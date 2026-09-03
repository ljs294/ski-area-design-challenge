/**
 * Phase 4 ski-patrol response domain.
 *
 * This module is deliberately independent from React, workers, Electron, and
 * the GameSave contract.  A worker owns one `PatrolSimulation` and feeds it
 * monotonically sequenced commands.  The simulation returns an immutable
 * snapshot after every command, which makes the boundary convenient for a
 * worker protocol without making the worker part of the domain.
 */

import { eventCalendarChecksum } from './eventCalendar.ts';
import type { GuestId, SimulatedSecond } from './contracts.ts';

export const PATROL_DOMAIN_VERSION = 1 as const;
export const PATROL_PROTOCOL_VERSION = 1 as const;

export type PatrolSeverity = 'info' | 'minor' | 'major' | 'critical';
export type PatrolIncidentStatus =
  | 'queued'
  | 'dispatched'
  | 'on-scene'
  | 'transporting'
  | 'auto-rescue'
  | 'resolved'
  | 'unreachable'
  | 'failed'
  | 'cancelled';
export type PatrolResponderStatus = 'available' | 'assigned';
export type PatrolDestinationKind = 'medical' | 'base-area' | 'transport';

export type PatrolNodeKind =
  | 'station'
  | 'trail'
  | 'junction'
  | 'lift-base'
  | 'lift-top'
  | 'portal'
  | 'destination';

export interface PatrolGraphNode {
  readonly id: string;
  readonly kind: PatrolNodeKind;
}

/** Edges are directed; callers that need two-way travel provide two edges. */
export interface PatrolGraphEdge {
  readonly id: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly travelSeconds: SimulatedSecond;
  readonly open?: boolean;
}

export interface PatrolGraph {
  readonly nodes: readonly PatrolGraphNode[];
  readonly edges: readonly PatrolGraphEdge[];
}

export interface PatrolStation {
  readonly id: string;
  readonly nodeId: string;
  readonly responderIds: readonly string[];
  readonly openFromTick?: SimulatedSecond;
  readonly openUntilTick?: SimulatedSecond;
}

/**
 * Capacity is expressed in integer response units.  A normal responder has a
 * capacity of one.  The unit representation also supports a future
 * snowmobile/medical-team responder without changing dispatch accounting.
 */
export interface PatrolResponder {
  readonly id: string;
  readonly stationId: string;
  readonly capacityUnits: number;
}

export interface PatrolRescueDestination {
  readonly id: string;
  readonly nodeId: string;
  readonly kind: PatrolDestinationKind;
}

export interface PatrolFallbackOptions {
  readonly enabled?: boolean;
  /** Used when no fallback source node is supplied. */
  readonly travelSeconds: SimulatedSecond;
  readonly sourceNodeId?: string;
  /** Optional fixed destination. Otherwise the shortest reachable one wins. */
  readonly destinationId?: string;
}

export interface PatrolSimulationOptions {
  readonly graph: PatrolGraph;
  readonly stations: readonly PatrolStation[];
  readonly responders: readonly PatrolResponder[];
  readonly destinations: readonly PatrolRescueDestination[];
  readonly startTick?: SimulatedSecond;
  readonly fallback?: PatrolFallbackOptions;
  /** If true, a response without a route to a destination fails explicitly. */
  readonly requireRescueDestination?: boolean;
  readonly maxIncidents?: number;
}

export interface PatrolPath {
  readonly nodeIds: readonly string[];
  readonly edgeIds: readonly string[];
  readonly travelSeconds: SimulatedSecond;
}

export interface PatrolResponderAssignment {
  readonly responderId: string;
  readonly units: number;
}

export type PatrolDispatchSource = 'station' | 'auto-rescue';

export interface PatrolIncident {
  readonly id: string;
  readonly guestId: GuestId;
  readonly nodeId: string;
  readonly reportedTick: SimulatedSecond;
  readonly severity: PatrolSeverity;
  readonly requiredResponderUnits: number;
  readonly serviceSeconds: SimulatedSecond;
  readonly status: PatrolIncidentStatus;
  readonly reason?: string;
  readonly dispatchId?: string;
  readonly rescueDestinationId?: string;
}

export interface PatrolDispatch {
  readonly id: string;
  readonly incidentId: string;
  readonly source: PatrolDispatchSource;
  readonly stationId: string | null;
  readonly responderAssignments: readonly PatrolResponderAssignment[];
  readonly responsePath: PatrolPath | null;
  readonly responseSeconds: SimulatedSecond;
  readonly dispatchedTick: SimulatedSecond;
  readonly onSceneTick: SimulatedSecond;
  readonly transportStartTick: SimulatedSecond;
  readonly completeTick: SimulatedSecond;
  readonly destinationId: string | null;
  readonly destinationPath: PatrolPath | null;
  readonly failureReason?: string;
  readonly completed: boolean;
}

export interface PatrolResponderState {
  readonly id: string;
  readonly stationId: string;
  readonly capacityUnits: number;
  readonly busyUnits: number;
  readonly status: PatrolResponderStatus;
  readonly incidentIds: readonly string[];
}

export interface PatrolMetrics {
  readonly incidentCount: number;
  readonly queuedCount: number;
  readonly activeDispatchCount: number;
  readonly resolvedCount: number;
  readonly failedCount: number;
  readonly unreachableCount: number;
  readonly autoRescueCount: number;
  readonly responderCapacityUnits: number;
  readonly assignedResponderUnits: number;
  readonly availableResponderUnits: number;
  readonly responderCapacityConserved: boolean;
}

export interface PatrolSnapshot {
  readonly version: typeof PATROL_DOMAIN_VERSION;
  readonly protocolVersion: typeof PATROL_PROTOCOL_VERSION;
  readonly tick: SimulatedSecond;
  readonly sequence: number;
  readonly incidents: readonly PatrolIncident[];
  readonly dispatches: readonly PatrolDispatch[];
  readonly responders: readonly PatrolResponderState[];
  readonly queue: readonly string[];
  readonly metrics: PatrolMetrics;
  readonly checksum: string;
}

export interface PatrolReportIncidentCommand {
  readonly version: typeof PATROL_PROTOCOL_VERSION;
  readonly type: 'report-incident';
  readonly requestId: string;
  readonly sequence: number;
  readonly incidentId: string;
  readonly guestId: GuestId;
  readonly nodeId: string;
  readonly tick: SimulatedSecond;
  readonly severity: PatrolSeverity;
  readonly requiredResponderUnits?: number;
  readonly serviceSeconds?: SimulatedSecond;
}

export interface PatrolAdvanceCommand {
  readonly version: typeof PATROL_PROTOCOL_VERSION;
  readonly type: 'advance';
  readonly requestId: string;
  readonly sequence: number;
  readonly tick: SimulatedSecond;
}

export interface PatrolCancelCommand {
  readonly version: typeof PATROL_PROTOCOL_VERSION;
  readonly type: 'cancel-incident';
  readonly requestId: string;
  readonly sequence: number;
  readonly incidentId: string;
}

export type PatrolCommand = PatrolReportIncidentCommand | PatrolAdvanceCommand | PatrolCancelCommand;

export type PatrolErrorCode =
  | 'invalid-command'
  | 'stale-command'
  | 'duplicate-incident'
  | 'unknown-incident'
  | 'invalid-tick'
  | 'capacity-limit'
  | 'unknown-node';

export interface PatrolCommandError {
  readonly code: PatrolErrorCode;
  readonly message: string;
  readonly retryable: boolean;
}

export interface PatrolEvent {
  readonly id: string;
  readonly tick: SimulatedSecond;
  readonly incidentId: string;
  readonly type: 'status-changed' | 'dispatch-created' | 'dispatch-completed';
  readonly status?: PatrolIncidentStatus;
  readonly dispatchId?: string;
  readonly reason?: string;
}

export interface PatrolCommandResult {
  readonly ok: boolean;
  readonly requestId: string;
  readonly sequence: number;
  readonly type: PatrolCommand['type'];
  readonly snapshot: PatrolSnapshot;
  readonly events: readonly PatrolEvent[];
  readonly error?: PatrolCommandError;
}

interface MutableIncident extends PatrolIncident {
  status: PatrolIncidentStatus;
  reason?: string;
  dispatchId?: string;
  rescueDestinationId?: string;
}

interface MutableDispatch extends PatrolDispatch {
  completed: boolean;
}

interface MutableResponder {
  readonly definition: PatrolResponder;
  busyUnits: number;
  readonly incidentIds: Set<string>;
}

interface CandidateStation {
  readonly station: PatrolStation;
  readonly path: PatrolPath;
}

interface AdjacencyEntry {
  readonly edge: PatrolGraphEdge;
  readonly to: string;
}

const DEFAULT_FALLBACK: PatrolFallbackOptions = Object.freeze({ enabled: true, travelSeconds: 300 });
const DEFAULT_MAX_INCIDENTS = 10_000;

function assertNonEmpty(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new TypeError(`${label} must be a non-empty string`);
}

function assertTick(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

function assertPositiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${label} must be a positive safe integer`);
}

function assertOptionalWindow(from: number | undefined, until: number | undefined, label: string): void {
  if (from !== undefined) assertTick(from, `${label}.openFromTick`);
  if (until !== undefined) assertTick(until, `${label}.openUntilTick`);
  if (from !== undefined && until !== undefined && until <= from) {
    throw new RangeError(`${label}.openUntilTick must be greater than openFromTick`);
  }
}

function isOpenAt(openFromTick: number | undefined, openUntilTick: number | undefined, tick: number): boolean {
  return (openFromTick === undefined || tick >= openFromTick)
    && (openUntilTick === undefined || tick < openUntilTick);
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function comparePath(left: PatrolPath, right: PatrolPath): number {
  if (left.travelSeconds !== right.travelSeconds) return left.travelSeconds - right.travelSeconds;
  const leftKey = left.edgeIds.join('\u0000');
  const rightKey = right.edgeIds.join('\u0000');
  return compareText(leftKey, rightKey);
}

function defaultRequiredUnits(severity: PatrolSeverity): number {
  return severity === 'critical' ? 2 : 1;
}

function defaultServiceSeconds(severity: PatrolSeverity): SimulatedSecond {
  switch (severity) {
    case 'info': return 30;
    case 'minor': return 60;
    case 'major': return 120;
    case 'critical': return 180;
  }
}

function freezePath(path: PatrolPath | null): PatrolPath | null {
  if (!path) return null;
  return Object.freeze({
    nodeIds: Object.freeze([...path.nodeIds]),
    edgeIds: Object.freeze([...path.edgeIds]),
    travelSeconds: path.travelSeconds,
  });
}

function freezeIncident(incident: MutableIncident): PatrolIncident {
  return Object.freeze({
    id: incident.id,
    guestId: incident.guestId,
    nodeId: incident.nodeId,
    reportedTick: incident.reportedTick,
    severity: incident.severity,
    requiredResponderUnits: incident.requiredResponderUnits,
    serviceSeconds: incident.serviceSeconds,
    status: incident.status,
    ...(incident.reason === undefined ? {} : { reason: incident.reason }),
    ...(incident.dispatchId === undefined ? {} : { dispatchId: incident.dispatchId }),
    ...(incident.rescueDestinationId === undefined ? {} : { rescueDestinationId: incident.rescueDestinationId }),
  });
}

function freezeDispatch(dispatch: MutableDispatch): PatrolDispatch {
  return Object.freeze({
    ...dispatch,
    responderAssignments: Object.freeze(dispatch.responderAssignments.map((assignment) => Object.freeze({ ...assignment }))),
    responsePath: freezePath(dispatch.responsePath),
    destinationPath: freezePath(dispatch.destinationPath),
  });
}

function checksumSnapshot(snapshot: Omit<PatrolSnapshot, 'checksum'>): string {
  return eventCalendarChecksum(snapshot);
}

/** Validate a topology before it crosses into a worker. */
export function assertPatrolSimulationOptions(options: PatrolSimulationOptions): void {
  if (!options || typeof options !== 'object') throw new TypeError('patrol options are required');
  const { graph } = options;
  if (!graph || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) throw new TypeError('patrol graph is required');
  if (graph.nodes.length > 10_000 || graph.edges.length > 25_000) throw new RangeError('patrol graph exceeds bounded limits');
  const nodeIds = new Set<string>();
  for (const node of graph.nodes) {
    assertNonEmpty(node.id, 'graph node id');
    if (nodeIds.has(node.id)) throw new RangeError(`duplicate patrol graph node ${node.id}`);
    nodeIds.add(node.id);
  }
  const edgeIds = new Set<string>();
  for (const edge of graph.edges) {
    assertNonEmpty(edge.id, 'graph edge id');
    if (edgeIds.has(edge.id)) throw new RangeError(`duplicate patrol graph edge ${edge.id}`);
    edgeIds.add(edge.id);
    if (!nodeIds.has(edge.fromNodeId) || !nodeIds.has(edge.toNodeId)) throw new RangeError(`edge ${edge.id} references an unknown node`);
    assertPositiveInteger(edge.travelSeconds, `edge ${edge.id}.travelSeconds`);
  }
  const stationIds = new Set<string>();
  const responderIds = new Set<string>();
  const stationResponderIds = new Set<string>();
  for (const station of options.stations) {
    assertNonEmpty(station.id, 'station id');
    if (stationIds.has(station.id)) throw new RangeError(`duplicate patrol station ${station.id}`);
    stationIds.add(station.id);
    if (!nodeIds.has(station.nodeId)) throw new RangeError(`station ${station.id} references an unknown node`);
    assertOptionalWindow(station.openFromTick, station.openUntilTick, `station ${station.id}`);
    if (station.responderIds.length === 0) throw new RangeError(`station ${station.id} must have at least one responder`);
    for (const responderId of station.responderIds) {
      assertNonEmpty(responderId, 'station responder id');
      if (stationResponderIds.has(responderId)) throw new RangeError(`responder ${responderId} is assigned to multiple stations`);
      stationResponderIds.add(responderId);
    }
  }
  for (const responder of options.responders) {
    assertNonEmpty(responder.id, 'responder id');
    if (responderIds.has(responder.id)) throw new RangeError(`duplicate patrol responder ${responder.id}`);
    responderIds.add(responder.id);
    if (!stationIds.has(responder.stationId)) throw new RangeError(`responder ${responder.id} references an unknown station`);
    if (!stationResponderIds.has(responder.id)) throw new RangeError(`responder ${responder.id} is not listed by its station`);
    assertPositiveInteger(responder.capacityUnits, `responder ${responder.id}.capacityUnits`);
  }
  for (const station of options.stations) {
    for (const responderId of station.responderIds) {
      if (!responderIds.has(responderId)) throw new RangeError(`station ${station.id} references an unknown responder ${responderId}`);
    }
  }
  const destinationIds = new Set<string>();
  for (const destination of options.destinations) {
    assertNonEmpty(destination.id, 'destination id');
    if (destinationIds.has(destination.id)) throw new RangeError(`duplicate patrol destination ${destination.id}`);
    destinationIds.add(destination.id);
    if (!nodeIds.has(destination.nodeId)) throw new RangeError(`destination ${destination.id} references an unknown node`);
  }
  if (options.startTick !== undefined) assertTick(options.startTick, 'startTick');
  if (options.maxIncidents !== undefined) assertPositiveInteger(options.maxIncidents, 'maxIncidents');
  if (options.fallback) {
    assertPositiveInteger(options.fallback.travelSeconds, 'fallback.travelSeconds');
    if (options.fallback.sourceNodeId !== undefined && !nodeIds.has(options.fallback.sourceNodeId)) {
      throw new RangeError(`fallback references an unknown source node ${options.fallback.sourceNodeId}`);
    }
    if (options.fallback.destinationId !== undefined && !destinationIds.has(options.fallback.destinationId)) {
      throw new RangeError(`fallback references an unknown destination ${options.fallback.destinationId}`);
    }
  }
}

/**
 * Deterministic Dijkstra pathfinder.  Equal-cost paths are resolved by edge
 * ids, so two workers with the same topology always select the same route.
 */
export function findPatrolPath(graph: PatrolGraph, fromNodeId: string, toNodeId: string): PatrolPath | null {
  const knownNodes = new Set(graph.nodes.map((node) => node.id));
  if (!knownNodes.has(fromNodeId) || !knownNodes.has(toNodeId)) return null;
  if (fromNodeId === toNodeId) return Object.freeze({ nodeIds: Object.freeze([fromNodeId]), edgeIds: Object.freeze([]), travelSeconds: 0 });
  const adjacency = new Map<string, AdjacencyEntry[]>();
  for (const edge of graph.edges) {
    if (edge.open === false) continue;
    const list = adjacency.get(edge.fromNodeId) ?? [];
    list.push({ edge, to: edge.toNodeId });
    adjacency.set(edge.fromNodeId, list);
  }
  for (const list of adjacency.values()) list.sort((left, right) => compareText(left.edge.id, right.edge.id));
  interface Label { nodeId: string; path: PatrolPath; }
  const best = new Map<string, PatrolPath>();
  const open: Label[] = [{ nodeId: fromNodeId, path: { nodeIds: [fromNodeId], edgeIds: [], travelSeconds: 0 } }];
  best.set(fromNodeId, open[0].path);
  while (open.length > 0) {
    open.sort((left, right) => {
      const pathOrder = comparePath(left.path, right.path);
      return pathOrder !== 0 ? pathOrder : compareText(left.nodeId, right.nodeId);
    });
    const current = open.shift()!;
    if (current.nodeId === toNodeId) return freezePath(current.path);
    const currentBest = best.get(current.nodeId);
    if (!currentBest || comparePath(current.path, currentBest) !== 0) continue;
    for (const { edge, to } of adjacency.get(current.nodeId) ?? []) {
      if (current.path.nodeIds.includes(to)) continue;
      const next: PatrolPath = {
        nodeIds: [...current.path.nodeIds, to],
        edgeIds: [...current.path.edgeIds, edge.id],
        travelSeconds: current.path.travelSeconds + edge.travelSeconds,
      };
      const previous = best.get(to);
      if (!previous || comparePath(next, previous) < 0) {
        best.set(to, next);
        open.push({ nodeId: to, path: next });
      }
    }
  }
  return null;
}

/** Pure command-driven patrol simulation. */
export class PatrolSimulation {
  readonly graph: PatrolGraph;
  readonly stations: readonly PatrolStation[];
  readonly responders: readonly PatrolResponder[];
  readonly destinations: readonly PatrolRescueDestination[];
  readonly fallback: PatrolFallbackOptions;
  readonly requireRescueDestination: boolean;
  readonly maxIncidents: number;

  private currentTick: SimulatedSecond;
  private lastSequence = -1;
  private readonly incidentsById = new Map<string, MutableIncident>();
  private readonly dispatchesById = new Map<string, MutableDispatch>();
  private readonly respondersById = new Map<string, MutableResponder>();
  private readonly stationById = new Map<string, PatrolStation>();
  private readonly destinationById = new Map<string, PatrolRescueDestination>();
  private readonly graphNodeIds: ReadonlySet<string>;
  private readonly events: PatrolEvent[] = [];
  private dispatchOrdinal = 0;

  constructor(options: PatrolSimulationOptions) {
    assertPatrolSimulationOptions(options);
    this.graph = Object.freeze({
      nodes: Object.freeze(options.graph.nodes.map((node) => Object.freeze({ ...node }))),
      edges: Object.freeze(options.graph.edges.map((edge) => Object.freeze({ ...edge }))),
    });
    this.graphNodeIds = new Set(this.graph.nodes.map((node) => node.id));
    this.stations = Object.freeze([...options.stations].sort((left, right) => compareText(left.id, right.id)));
    this.responders = Object.freeze([...options.responders].sort((left, right) => compareText(left.id, right.id)));
    this.destinations = Object.freeze([...options.destinations].sort((left, right) => compareText(left.id, right.id)));
    this.currentTick = options.startTick ?? 0;
    this.fallback = Object.freeze({ ...DEFAULT_FALLBACK, ...(options.fallback ?? {}) });
    this.requireRescueDestination = options.requireRescueDestination ?? true;
    this.maxIncidents = options.maxIncidents ?? DEFAULT_MAX_INCIDENTS;
    for (const station of this.stations) this.stationById.set(station.id, station);
    for (const destination of this.destinations) this.destinationById.set(destination.id, destination);
    for (const responder of this.responders) {
      this.respondersById.set(responder.id, { definition: responder, busyUnits: 0, incidentIds: new Set() });
    }
  }

  get tick(): SimulatedSecond { return this.currentTick; }

  /** Apply one worker-safe, monotonic command and return an immutable result. */
  apply(command: PatrolCommand): PatrolCommandResult {
    const eventsStart = this.events.length;
    const requestId = typeof command?.requestId === 'string' ? command.requestId : '';
    const sequence = Number.isSafeInteger(command?.sequence) ? command.sequence : -1;
    const type = command?.type ?? 'advance';
    const error = this.validateCommand(command);
    if (error) {
      return this.result(false, requestId, sequence, type, eventsStart, error);
    }
    this.lastSequence = command.sequence;
    switch (command.type) {
      case 'report-incident': this.report(command); break;
      case 'advance': this.advanceTo(command.tick); break;
      case 'cancel-incident': this.cancel(command); break;
    }
    return this.result(true, command.requestId, command.sequence, command.type, eventsStart);
  }

  snapshot(): PatrolSnapshot {
    const incidents = [...this.incidentsById.values()].sort((left, right) => compareText(left.id, right.id)).map(freezeIncident);
    const dispatches = [...this.dispatchesById.values()].sort((left, right) => compareText(left.id, right.id)).map(freezeDispatch);
    const responders = this.responderStates();
    const queue = [...this.incidentsById.values()]
      .filter((incident) => incident.status === 'queued')
      .sort((left, right) => left.reportedTick - right.reportedTick || compareText(left.id, right.id))
      .map((incident) => incident.id);
    const assignedResponderUnits = responders.reduce((sum, responder) => sum + responder.busyUnits, 0);
    const responderCapacityUnits = responders.reduce((sum, responder) => sum + responder.capacityUnits, 0);
    const metrics: PatrolMetrics = Object.freeze({
      incidentCount: incidents.length,
      queuedCount: incidents.filter((incident) => incident.status === 'queued').length,
      activeDispatchCount: dispatches.filter((dispatch) => !dispatch.completed).length,
      resolvedCount: incidents.filter((incident) => incident.status === 'resolved').length,
      failedCount: incidents.filter((incident) => incident.status === 'failed').length,
      unreachableCount: incidents.filter((incident) => incident.status === 'unreachable').length,
      autoRescueCount: dispatches.filter((dispatch) => dispatch.source === 'auto-rescue').length,
      responderCapacityUnits,
      assignedResponderUnits,
      availableResponderUnits: responderCapacityUnits - assignedResponderUnits,
      responderCapacityConserved: assignedResponderUnits >= 0 && assignedResponderUnits <= responderCapacityUnits
        && responders.every((responder) => responder.busyUnits >= 0 && responder.busyUnits <= responder.capacityUnits),
    });
    const projection: Omit<PatrolSnapshot, 'checksum'> = {
      version: PATROL_DOMAIN_VERSION,
      protocolVersion: PATROL_PROTOCOL_VERSION,
      tick: this.currentTick,
      sequence: this.lastSequence,
      incidents,
      dispatches,
      responders,
      queue: Object.freeze(queue),
      metrics,
    };
    return Object.freeze({ ...projection, checksum: checksumSnapshot(projection) });
  }

  private validateCommand(command: PatrolCommand): PatrolCommandError | undefined {
    if (!command || typeof command !== 'object' || command.version !== PATROL_PROTOCOL_VERSION) {
      return { code: 'invalid-command', message: 'unsupported patrol command version', retryable: false };
    }
    if (typeof command.requestId !== 'string' || command.requestId.length === 0 || !Number.isSafeInteger(command.sequence) || command.sequence < 0) {
      return { code: 'invalid-command', message: 'requestId and sequence are required', retryable: false };
    }
    if (command.sequence <= this.lastSequence) {
      return { code: 'stale-command', message: `command sequence ${command.sequence} is not newer than ${this.lastSequence}`, retryable: false };
    }
    if (command.type === 'advance') {
      if (!Number.isSafeInteger(command.tick) || command.tick < this.currentTick) {
        return { code: 'invalid-tick', message: `advance tick must be at least ${this.currentTick}`, retryable: false };
      }
    } else if (command.type === 'report-incident') {
      if (this.incidentsById.has(command.incidentId)) return { code: 'duplicate-incident', message: `incident ${command.incidentId} already exists`, retryable: false };
      if (this.incidentsById.size >= this.maxIncidents) return { code: 'capacity-limit', message: 'patrol incident bound has been reached', retryable: false };
      if (!this.graphNodeIds.has(command.nodeId)) return { code: 'unknown-node', message: `incident node ${command.nodeId} is unknown`, retryable: false };
      if (!Number.isSafeInteger(command.tick) || command.tick < this.currentTick) return { code: 'invalid-tick', message: `incident tick must be at least ${this.currentTick}`, retryable: false };
      if (!['info', 'minor', 'major', 'critical'].includes(command.severity)) return { code: 'invalid-command', message: 'invalid incident severity', retryable: false };
      const units = command.requiredResponderUnits ?? defaultRequiredUnits(command.severity);
      if (!Number.isSafeInteger(units) || units <= 0) return { code: 'capacity-limit', message: 'required responder units must be positive', retryable: false };
      if (command.serviceSeconds !== undefined && (!Number.isSafeInteger(command.serviceSeconds) || command.serviceSeconds <= 0)) return { code: 'invalid-command', message: 'serviceSeconds must be positive', retryable: false };
    } else if (command.type === 'cancel-incident') {
      if (!this.incidentsById.has(command.incidentId)) return { code: 'unknown-incident', message: `incident ${command.incidentId} is unknown`, retryable: false };
    } else {
      return { code: 'invalid-command', message: 'unknown patrol command type', retryable: false };
    }
    return undefined;
  }

  private result(ok: boolean, requestId: string, sequence: number, type: PatrolCommand['type'], eventsStart: number, error?: PatrolCommandError): PatrolCommandResult {
    const result: PatrolCommandResult = {
      ok,
      requestId,
      sequence,
      type,
      snapshot: this.snapshot(),
      events: Object.freeze(this.events.slice(eventsStart).map((event) => Object.freeze({ ...event }))),
      ...(error === undefined ? {} : { error }),
    };
    return Object.freeze(result);
  }

  private report(command: PatrolReportIncidentCommand): void {
    const incident: MutableIncident = {
      id: command.incidentId,
      guestId: command.guestId,
      nodeId: command.nodeId,
      reportedTick: command.tick,
      severity: command.severity,
      requiredResponderUnits: command.requiredResponderUnits ?? defaultRequiredUnits(command.severity),
      serviceSeconds: command.serviceSeconds ?? defaultServiceSeconds(command.severity),
      status: 'queued',
    };
    this.incidentsById.set(incident.id, incident);
    if (incident.reportedTick <= this.currentTick) this.processAtCurrentTick();
  }

  private cancel(command: PatrolCancelCommand): void {
    const incident = this.incidentsById.get(command.incidentId)!;
    if (incident.status === 'resolved' || incident.status === 'failed' || incident.status === 'unreachable' || incident.status === 'cancelled') return;
    if (incident.dispatchId) {
      const dispatch = this.dispatchesById.get(incident.dispatchId);
      if (dispatch && !dispatch.completed) {
        dispatch.completed = true;
        this.releaseDispatch(dispatch);
      }
    }
    this.transition(incident, 'cancelled', 'cancelled-by-command');
  }

  private advanceTo(targetTick: SimulatedSecond): void {
    while (this.currentTick < targetTick) {
      const next = Math.min(targetTick, this.nextInternalTick(targetTick));
      this.currentTick = next;
      this.processAtCurrentTick();
      if (next === targetTick) break;
    }
    this.processAtCurrentTick();
  }

  private nextInternalTick(targetTick: SimulatedSecond): SimulatedSecond {
    let next = targetTick;
    for (const incident of this.incidentsById.values()) {
      if (!incident.dispatchId) continue;
      const dispatch = this.dispatchesById.get(incident.dispatchId);
      if (!dispatch || dispatch.completed) continue;
      for (const candidate of [dispatch.onSceneTick, dispatch.transportStartTick, dispatch.completeTick]) {
        if (candidate > this.currentTick && candidate < next) next = candidate;
      }
    }
    for (const incident of this.incidentsById.values()) {
      if (incident.status === 'queued' && incident.reportedTick > this.currentTick && incident.reportedTick < next) next = incident.reportedTick;
    }
    return next;
  }

  private processAtCurrentTick(): void {
    // Completion precedes dispatch at a shared tick, releasing capacity first.
    for (const dispatch of [...this.dispatchesById.values()].sort((left, right) => compareText(left.id, right.id))) {
      if (dispatch.completed) continue;
      const incident = this.incidentsById.get(dispatch.incidentId);
      if (!incident || incident.status === 'cancelled') continue;
      if (this.currentTick >= dispatch.completeTick) {
        if (dispatch.failureReason) {
          this.transition(incident, 'failed', dispatch.failureReason);
        } else {
          this.transition(incident, 'resolved', 'rescue-complete');
        }
        dispatch.completed = true;
        this.releaseDispatch(dispatch);
        this.emit({ id: `${dispatch.id}:completed`, tick: this.currentTick, incidentId: incident.id, type: 'dispatch-completed', dispatchId: dispatch.id, status: incident.status });
        continue;
      }
      if (this.currentTick >= dispatch.transportStartTick && dispatch.destinationPath && incident.status === 'on-scene') {
        this.transition(incident, 'transporting', 'transport-started');
      }
      if (this.currentTick >= dispatch.onSceneTick && incident.status === 'dispatched') {
        if (dispatch.failureReason) this.transition(incident, 'on-scene', dispatch.failureReason);
        else this.transition(incident, 'on-scene', 'responder-on-scene');
      }
    }
    this.dispatchQueuedIncidents();
  }

  private dispatchQueuedIncidents(): void {
    const queued = [...this.incidentsById.values()]
      .filter((incident) => incident.status === 'queued' && incident.reportedTick <= this.currentTick)
      .sort((left, right) => left.reportedTick - right.reportedTick || compareText(left.id, right.id));
    for (const incident of queued) {
      const result = this.tryDispatch(incident);
      if (result === 'capacity') break;
    }
  }

  private tryDispatch(incident: MutableIncident): 'dispatched' | 'capacity' | 'unreachable' {
    const openStations = this.stations.filter((station) => isOpenAt(station.openFromTick, station.openUntilTick, this.currentTick));
    const candidates: CandidateStation[] = [];
    for (const station of openStations) {
      const path = findPatrolPath(this.graph, station.nodeId, incident.nodeId);
      if (path) candidates.push({ station, path });
    }
    candidates.sort((left, right) => comparePath(left.path, right.path) || compareText(left.station.id, right.station.id));
    const availableCandidates = candidates.filter((candidate) => this.availableCapacity(candidate.station) >= incident.requiredResponderUnits);
    if (availableCandidates.length === 0 && candidates.length > 0) return 'capacity';
    if (availableCandidates.length === 0) {
      return this.dispatchAutoRescue(incident) ? 'dispatched' : 'unreachable';
    }
    const candidate = availableCandidates[0];
    const assignments = this.allocate(candidate.station, incident.requiredResponderUnits, incident.id);
    if (!assignments) return 'capacity';
    const destination = this.selectDestination(incident.nodeId, undefined);
    const dispatchId = `patrol-dispatch-${String(this.dispatchOrdinal++).padStart(8, '0')}`;
    const onSceneTick = this.currentTick + candidate.path.travelSeconds;
    const transportSeconds = destination?.path.travelSeconds ?? 0;
    const transportStartTick = onSceneTick + incident.serviceSeconds;
    const completeTick = transportStartTick + transportSeconds;
    const failureReason = destination ? undefined : (this.requireRescueDestination ? 'no-reachable-rescue-destination' : undefined);
    const dispatch: MutableDispatch = {
      id: dispatchId,
      incidentId: incident.id,
      source: 'station',
      stationId: candidate.station.id,
      responderAssignments: assignments,
      responsePath: candidate.path,
      responseSeconds: candidate.path.travelSeconds,
      dispatchedTick: this.currentTick,
      onSceneTick,
      transportStartTick,
      completeTick,
      destinationId: destination?.destination.id ?? null,
      destinationPath: destination?.path ?? null,
      ...(failureReason === undefined ? {} : { failureReason }),
      completed: false,
    };
    this.dispatchesById.set(dispatch.id, dispatch);
    incident.dispatchId = dispatch.id;
    incident.rescueDestinationId = destination?.destination.id;
    this.transition(incident, 'dispatched', 'station-dispatched');
    this.emit({ id: `${dispatch.id}:created`, tick: this.currentTick, incidentId: incident.id, type: 'dispatch-created', dispatchId: dispatch.id, status: incident.status });
    return 'dispatched';
  }

  private dispatchAutoRescue(incident: MutableIncident): boolean {
    if (this.fallback.enabled === false) {
      this.transition(incident, 'unreachable', 'no-reachable-patrol-station');
      return false;
    }
    const fallbackPath = this.fallback.sourceNodeId === undefined ? null : findPatrolPath(this.graph, this.fallback.sourceNodeId, incident.nodeId);
    const responseSeconds = fallbackPath?.travelSeconds ?? this.fallback.travelSeconds;
    const destination = this.selectDestination(incident.nodeId, this.fallback.destinationId);
    const dispatchId = `patrol-auto-rescue-${String(this.dispatchOrdinal++).padStart(8, '0')}`;
    const onSceneTick = this.currentTick + responseSeconds;
    const transportStartTick = onSceneTick + incident.serviceSeconds;
    const completeTick = transportStartTick + (destination?.path.travelSeconds ?? 0);
    const failureReason = destination ? undefined : (this.requireRescueDestination ? 'no-reachable-rescue-destination' : undefined);
    const dispatch: MutableDispatch = {
      id: dispatchId,
      incidentId: incident.id,
      source: 'auto-rescue',
      stationId: null,
      responderAssignments: Object.freeze([]),
      responsePath: fallbackPath,
      responseSeconds,
      dispatchedTick: this.currentTick,
      onSceneTick,
      transportStartTick,
      completeTick,
      destinationId: destination?.destination.id ?? null,
      destinationPath: destination?.path ?? null,
      ...(failureReason === undefined ? {} : { failureReason }),
      completed: false,
    };
    this.dispatchesById.set(dispatch.id, dispatch);
    incident.dispatchId = dispatch.id;
    incident.rescueDestinationId = destination?.destination.id;
    this.transition(incident, 'auto-rescue', 'no-reachable-patrol-station');
    this.emit({ id: `${dispatch.id}:created`, tick: this.currentTick, incidentId: incident.id, type: 'dispatch-created', dispatchId: dispatch.id, status: incident.status, reason: 'fallback' });
    return true;
  }

  private selectDestination(fromNodeId: string, destinationId: string | undefined): { destination: PatrolRescueDestination; path: PatrolPath } | null {
    if (destinationId !== undefined) {
      const destination = this.destinationById.get(destinationId);
      if (!destination) return null;
      const path = findPatrolPath(this.graph, fromNodeId, destination.nodeId);
      return path ? { destination, path } : null;
    }
    const candidates: { destination: PatrolRescueDestination; path: PatrolPath }[] = [];
    for (const destination of this.destinations) {
      const path = findPatrolPath(this.graph, fromNodeId, destination.nodeId);
      if (path) candidates.push({ destination, path });
    }
    candidates.sort((left, right) => comparePath(left.path, right.path) || compareText(left.destination.id, right.destination.id));
    return candidates[0] ?? null;
  }

  private availableCapacity(station: PatrolStation): number {
    return station.responderIds.reduce((sum, responderId) => {
      const responder = this.respondersById.get(responderId)!;
      return sum + responder.definition.capacityUnits - responder.busyUnits;
    }, 0);
  }

  private allocate(station: PatrolStation, units: number, incidentId: string): readonly PatrolResponderAssignment[] | null {
    if (this.availableCapacity(station) < units) return null;
    let remaining = units;
    const assignments: PatrolResponderAssignment[] = [];
    for (const responderId of [...station.responderIds].sort(compareText)) {
      const responder = this.respondersById.get(responderId)!;
      const free = responder.definition.capacityUnits - responder.busyUnits;
      const assigned = Math.min(free, remaining);
      if (assigned <= 0) continue;
      responder.busyUnits += assigned;
      responder.incidentIds.add(incidentId);
      assignments.push(Object.freeze({ responderId, units: assigned }));
      remaining -= assigned;
      if (remaining === 0) break;
    }
    if (remaining !== 0) throw new Error('patrol responder allocation invariant violated');
    return Object.freeze(assignments);
  }

  private releaseDispatch(dispatch: MutableDispatch): void {
    for (const assignment of dispatch.responderAssignments) {
      const responder = this.respondersById.get(assignment.responderId);
      if (!responder) continue;
      responder.busyUnits -= assignment.units;
      responder.incidentIds.delete(dispatch.incidentId);
      if (responder.busyUnits < 0) throw new Error('patrol responder capacity underflow');
    }
  }

  private responderStates(): readonly PatrolResponderState[] {
    return Object.freeze([...this.respondersById.values()].sort((left, right) => compareText(left.definition.id, right.definition.id)).map((responder) => Object.freeze({
      id: responder.definition.id,
      stationId: responder.definition.stationId,
      capacityUnits: responder.definition.capacityUnits,
      busyUnits: responder.busyUnits,
      status: responder.busyUnits > 0 ? 'assigned' : 'available',
      incidentIds: Object.freeze([...responder.incidentIds].sort(compareText)),
    })));
  }

  private transition(incident: MutableIncident, status: PatrolIncidentStatus, reason: string): void {
    if (incident.status === status && incident.reason === reason) return;
    incident.status = status;
    incident.reason = reason;
    this.emit({ id: `${incident.id}:${status}:${this.currentTick}`, tick: this.currentTick, incidentId: incident.id, type: 'status-changed', status, reason });
  }

  private emit(event: PatrolEvent): void {
    this.events.push(Object.freeze(event));
  }
}

export function createPatrolSimulation(options: PatrolSimulationOptions): PatrolSimulation {
  return new PatrolSimulation(options);
}
