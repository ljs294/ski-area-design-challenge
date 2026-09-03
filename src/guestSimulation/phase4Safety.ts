/** Engine-facing Phase 4 safety orchestration. Pure injury and patrol rules remain in their own domains. */
import type { GuestId, SimulatedSecond } from './contracts.ts';
import type { GuestSimulationNetwork } from './engineSupport.ts';
import { evaluateTraversalInjury, type InjuryIncident, type InjuryTraversalInput,
  type InjuryTraversalResult } from './injury.ts';
import { createPatrolSimulation, PATROL_PROTOCOL_VERSION, type PatrolIncidentStatus,
  type PatrolSeverity, type PatrolSimulationOptions, type PatrolSnapshot } from './patrol.ts';

export interface SafetyTraversalRecord {
  readonly id: string;
  readonly guestId: GuestId;
  readonly edgeId: string;
  readonly ordinal: number;
  readonly entryTick: SimulatedSecond;
  readonly completionTick: SimulatedSecond;
  readonly outcome: 'pending' | 'normal' | 'injury';
  readonly evaluation: InjuryTraversalResult;
}

export interface GuestSafetyIncidentRecord {
  readonly id: string;
  readonly guestId: GuestId;
  readonly partyId: string;
  readonly edgeAnchor: string;
  readonly progressQ16: number;
  readonly severity: InjuryIncident['severity'];
  readonly createdTick: SimulatedSecond;
  readonly status: PatrolIncidentStatus;
  readonly assignedPatrolId: string | null;
  readonly resolution: 'patrol' | 'auto-rescue' | 'unreachable' | null;
  readonly hazard: number;
  readonly probability: number;
  readonly draw: number;
  readonly reasonVector: InjuryIncident['reasonVector'];
}

export interface PartyIncidentState {
  readonly partyId: string;
  readonly injuredGuestIds: readonly GuestId[];
  readonly policy: 'continue';
  readonly activeIncidentId: string | null;
  readonly revision: number;
}

export interface SafetyMetrics {
  readonly traversalsStarted: number;
  readonly traversalsCompleted: number;
  readonly incidentCount: number;
  readonly activeIncidents: number;
  readonly resolvedIncidents: number;
  readonly failedIncidents: number;
  readonly incidentsBySeverity: Readonly<Record<InjuryIncident['severity'], number>>;
  readonly safetyRate: number;
  /** Additive signal for Phase 3; it is deliberately not a demand input yet. */
  readonly safetyReputationSignal: number;
}

export interface Phase4SafetySnapshot {
  readonly traversals: readonly SafetyTraversalRecord[];
  readonly guestIncidents: readonly GuestSafetyIncidentRecord[];
  readonly partyIncidents: readonly PartyIncidentState[];
  readonly patrol: PatrolSnapshot;
  readonly metrics: SafetyMetrics;
}

function patrolOptions(network: GuestSimulationNetwork, startTick: SimulatedSecond): PatrolSimulationOptions {
  const portalNode = network.portalConnections[0]?.nodeId ?? network.nodes[0]?.id;
  if (!portalNode) throw new RangeError('Phase 4 patrol requires at least one network node');
  const graphEdges = network.edges.flatMap((edge) => edge.closed ? [] : [
    { id: `patrol:${edge.id}:forward`, fromNodeId: edge.fromNodeId, toNodeId: edge.toNodeId,
      travelSeconds: Math.max(1, edge.travelSeconds) },
    { id: `patrol:${edge.id}:reverse`, fromNodeId: edge.toNodeId, toNodeId: edge.fromNodeId,
      travelSeconds: Math.max(1, edge.travelSeconds) },
  ]);
  const responderIds = ['default-patrol-1', 'default-patrol-2'] as const;
  return {
    graph: { nodes: network.nodes.map((node) => ({ id: node.id, kind: node.kind })), edges: graphEdges },
    stations: [{ id: 'default-patrol-base', nodeId: portalNode, responderIds }],
    responders: responderIds.map((id) => ({ id, stationId: 'default-patrol-base', capacityUnits: 1 })),
    destinations: [{ id: 'guest-entrance-rescue', nodeId: portalNode, kind: 'base-area' }],
    startTick, fallback: { enabled: true, travelSeconds: 300, destinationId: 'guest-entrance-rescue' },
    requireRescueDestination: true,
  };
}

function severity(value: InjuryIncident['severity']): PatrolSeverity {
  return value === 'moderate' ? 'major' : value;
}

function terminal(status: PatrolIncidentStatus): boolean {
  return status === 'resolved' || status === 'failed' || status === 'unreachable' || status === 'cancelled';
}

export class Phase4SafetyRuntime {
  private readonly patrolSimulation;
  private readonly traversalsById = new Map<string, SafetyTraversalRecord>();
  private readonly incidentsById = new Map<string, Omit<GuestSafetyIncidentRecord, 'status' | 'assignedPatrolId' | 'resolution'>>();
  private readonly partyIncidentsById = new Map<string, PartyIncidentState>();
  private sequence = 0;

  constructor(network: GuestSimulationNetwork, startTick: SimulatedSecond) {
    this.patrolSimulation = createPatrolSimulation(patrolOptions(network, startTick));
  }

  evaluate(input: InjuryTraversalInput): InjuryTraversalResult {
    const evaluation = evaluateTraversalInjury(input);
    this.traversalsById.set(input.traversalId, Object.freeze({ id: input.traversalId, guestId: input.guestId,
      edgeId: input.runId, ordinal: input.decisionOrdinal, entryTick: input.entryTick,
      completionTick: input.entryTick + input.durationSeconds, outcome: 'pending', evaluation }));
    return evaluation;
  }

  markNormal(traversalId: string): void { this.setTraversalOutcome(traversalId, 'normal'); }

  reportInjury(incident: InjuryIncident, partyId: string, incidentNodeId: string): PatrolSnapshot {
    this.setTraversalOutcome(incident.traversalId, 'injury');
    const evaluation = this.traversalsById.get(incident.traversalId)?.evaluation;
    if (!evaluation) throw new RangeError(`missing traversal ${incident.traversalId}`);
    this.advance(incident.incidentTick);
    this.incidentsById.set(incident.id, Object.freeze({ id: incident.id, guestId: incident.guestId, partyId,
      edgeAnchor: incident.runId, progressQ16: Math.round(incident.positionFraction * 65_535),
      severity: incident.severity, createdTick: incident.incidentTick, hazard: evaluation.hazardScore,
      probability: evaluation.probability, draw: evaluation.entryDraw, reasonVector: incident.reasonVector }));
    const prior = this.partyIncidentsById.get(partyId);
    const injuredGuestIds = [...new Set([...(prior?.injuredGuestIds ?? []), incident.guestId])].sort();
    this.partyIncidentsById.set(partyId, Object.freeze({ partyId, injuredGuestIds: Object.freeze(injuredGuestIds),
      policy: 'continue', activeIncidentId: incident.id, revision: (prior?.revision ?? 0) + 1 }));
    const result = this.patrolSimulation.apply({ version: PATROL_PROTOCOL_VERSION, type: 'report-incident',
      requestId: `phase4-report-${this.sequence}`, sequence: this.sequence++, incidentId: incident.id,
      guestId: incident.guestId, nodeId: incidentNodeId, tick: incident.incidentTick,
      severity: severity(incident.severity) });
    if (!result.ok) throw new RangeError(result.error?.message ?? 'patrol rejected incident');
    return result.snapshot;
  }

  advance(tick: SimulatedSecond): PatrolSnapshot {
    if (tick > this.patrolSimulation.tick) {
      const result = this.patrolSimulation.apply({ version: PATROL_PROTOCOL_VERSION, type: 'advance',
        requestId: `phase4-advance-${this.sequence}`, sequence: this.sequence++, tick });
      if (!result.ok) throw new RangeError(result.error?.message ?? 'patrol advance failed');
    }
    return this.patrolSimulation.snapshot();
  }

  snapshot(tick: SimulatedSecond): Phase4SafetySnapshot {
    const patrol = this.advance(tick);
    const patrolById = new Map(patrol.incidents.map((incident) => [incident.id, incident]));
    const dispatchByIncident = new Map(patrol.dispatches.map((dispatch) => [dispatch.incidentId, dispatch]));
    const guestIncidents = [...this.incidentsById.values()].sort((a, b) => a.id.localeCompare(b.id)).map((incident) => {
      const response = patrolById.get(incident.id);
      const dispatch = dispatchByIncident.get(incident.id);
      return Object.freeze({ ...incident, status: response?.status ?? 'failed',
        assignedPatrolId: dispatch?.stationId ?? null,
        resolution: dispatch?.source === 'auto-rescue' ? 'auto-rescue'
          : response?.status === 'unreachable' ? 'unreachable' : dispatch ? 'patrol' : null });
    });
    for (const state of this.partyIncidentsById.values()) {
      if (state.activeIncidentId && terminal(patrolById.get(state.activeIncidentId)?.status ?? 'failed')) {
        this.partyIncidentsById.set(state.partyId, Object.freeze({ ...state, activeIncidentId: null, revision: state.revision + 1 }));
      }
    }
    const traversals = [...this.traversalsById.values()].sort((a, b) => a.id.localeCompare(b.id));
    const resolvedIncidents = guestIncidents.filter((incident) => incident.status === 'resolved').length;
    const failedIncidents = guestIncidents.filter((incident) => incident.status === 'failed'
      || incident.status === 'unreachable' || incident.status === 'cancelled').length;
    const activeIncidents = guestIncidents.length - resolvedIncidents - failedIncidents;
    const incidentsBySeverity = { minor: 0, moderate: 0, major: 0 };
    for (const incident of guestIncidents) incidentsBySeverity[incident.severity] += 1;
    const traversalsCompleted = traversals.filter((record) => record.outcome !== 'pending').length;
    const safetyRate = traversalsCompleted === 0 ? 1 : Math.max(0, 1 - guestIncidents.length / traversalsCompleted);
    const safetyReputationSignal = Math.max(-1, -(guestIncidents.length * 0.02 + failedIncidents * 0.05));
    return Object.freeze({ traversals: Object.freeze(traversals), guestIncidents: Object.freeze(guestIncidents),
      partyIncidents: Object.freeze([...this.partyIncidentsById.values()].sort((a, b) => a.partyId.localeCompare(b.partyId))),
      patrol, metrics: Object.freeze({ traversalsStarted: traversals.length, traversalsCompleted,
        incidentCount: guestIncidents.length, activeIncidents, resolvedIncidents, failedIncidents,
        incidentsBySeverity: Object.freeze(incidentsBySeverity), safetyRate, safetyReputationSignal }) });
  }

  private setTraversalOutcome(traversalId: string, outcome: 'normal' | 'injury'): void {
    const record = this.traversalsById.get(traversalId);
    if (!record || record.outcome !== 'pending') return;
    this.traversalsById.set(traversalId, Object.freeze({ ...record, outcome }));
  }
}

export function createPhase4SafetyRuntime(network: GuestSimulationNetwork, startTick: SimulatedSecond): Phase4SafetyRuntime {
  return new Phase4SafetyRuntime(network, startTick);
}
