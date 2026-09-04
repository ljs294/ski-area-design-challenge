import { createDailyGuestRoster, GuestSimulationEngine } from '../guestSimulation/engine';
import { workerEnvironment, type GuestSimulationWorkerRequest, type GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';
import { decodeGuestSimulationReplayState, encodeGuestSimulationReplayState } from '../guestSimulation/replayPersistence';
import { replayStateFromGuestSimulationEngine, restoreGuestSimulationEngine } from '../guestSimulation/enginePersistence';
import { planDailyArrivals, type DemandScenarioInputV1 } from '../guestSimulation/demand';
import { blendedReputationScore } from '../guestSimulation/phase3Economy';

const DEFAULT_DEMAND_BUCKET_SECONDS = 10 * 60;
const DEFAULT_DEMAND_MAX_GUESTS = 50_000;
const DEFAULT_DEMAND_MAX_PARTIES = 20_000;

function isSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value);
}

function rosterFromWorkerRequest(request: Extract<GuestSimulationWorkerRequest, { type: 'initialize' }>){
  if (!request.demand) {
    const guestCount = request.guestCount;
    if (!isSafeInteger(guestCount) || guestCount <= 0 || guestCount > DEFAULT_DEMAND_MAX_GUESTS) {
      throw new RangeError('Guest count is outside supported bounds.');
    }
    return { roster: createDailyGuestRoster({ seed: request.seed, guestCount,
      portals: request.network.portals, startTick: request.startTick, endTick: request.endTick }) };
  }
  const demand = request.demand;
  const scenarioInput: DemandScenarioInputV1 = {
    seed: request.seed,
    startTick: request.startTick,
    endTick: request.endTick,
    bucketSeconds: demand.bucketSeconds ?? DEFAULT_DEMAND_BUCKET_SECONDS,
    dayType: demand.dayType,
    basePotentialGuests: demand.basePotentialGuests,
    ticketPriceCents: demand.ticketPriceCents,
    referencePriceCents: demand.referencePriceCents,
    reputation: request.openingReputation
      ? blendedReputationScore(request.openingReputation) / 10_000 : demand.reputation,
    resortValue: demand.resortValue,
    operatingFraction: demand.operatingFraction,
    conditionFactor: demand.conditionFactor,
    availableCapacityGuests: demand.availableCapacityGuests,
    maxGuests: demand.maxGuests ?? DEFAULT_DEMAND_MAX_GUESTS,
    maxParties: demand.maxParties ?? DEFAULT_DEMAND_MAX_PARTIES,
  };
  const planned = planDailyArrivals(scenarioInput);
  // The demand plan is authoritative for arrival shape. The roster factory
  // still owns stable guest/party identity and preference generation.
  return { roster: createDailyGuestRoster({ seed: request.seed, guestCount: planned.realization.guestCount,
    portals: request.network.portals, startTick: request.startTick, endTick: request.endTick,
    demandPlan: planned.demandPlan }), planned };
}

/** Stateful worker body. Its only clock input is an explicit game-time command. */
export class GuestSimulationWorkerEngine {
  private engine: GuestSimulationEngine | null = null;
  private lastSequence = -1;

  handle(request: GuestSimulationWorkerRequest): GuestSimulationWorkerResponse {
    if (!Number.isSafeInteger(request.sequence) || request.sequence <= this.lastSequence) {
      return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'stale-sequence', message: 'Guest simulation request sequence is stale.' };
    }
    this.lastSequence = request.sequence;
    try {
      if (request.type === 'initialize') {
        const legacyGuestCount = request.guestCount;
        if (!isSafeInteger(request.startTick) || request.startTick < 0
          || !isSafeInteger(request.endTick) || request.endTick <= request.startTick
          || (!request.demand && (!isSafeInteger(legacyGuestCount) || legacyGuestCount <= 0 || legacyGuestCount > DEFAULT_DEMAND_MAX_GUESTS))) {
          return { type: 'error', requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
            message: 'Guest count or simulation horizon is outside supported bounds.' };
        }
        const { roster, planned } = rosterFromWorkerRequest(request);
        this.engine = new GuestSimulationEngine({ network: request.network, roster, runId: request.runId,
          environment: workerEnvironment(request), conditionSnapshot: request.conditionSnapshot,
          phase3: { dayId: `${request.seed}:${request.startTick}`,
            ticketPriceCents: request.demand?.ticketPriceCents ?? 10_000,
            demandForecast: planned?.forecast, demandRealization: planned?.realization,
            openingReputation: request.openingReputation }, phase5to7: request.phase5to7 });
        return { type: 'ready', requestId: request.requestId, sequence: request.sequence,
          snapshot: this.engine.snapshot() };
      }
      if (request.type === 'restore') {
        if (!(request.bytes instanceof Uint8Array) || request.bytes.byteLength > 128 * 1024 * 1024) return { type: 'error',
          requestId: request.requestId, sequence: request.sequence, code: 'invalid-request', message: 'Invalid guest checkpoint payload.' };
        const state = decodeGuestSimulationReplayState(request.bytes);
        if (state.snapshot.topologyRevision !== request.expectedTopologyRevision) return { type: 'error', requestId: request.requestId,
          sequence: request.sequence, code: 'stale-revision', message: 'Guest checkpoint topology does not match the resort.' };
        this.engine = restoreGuestSimulationEngine(state);
        return { type: 'ready', requestId: request.requestId, sequence: request.sequence, snapshot: this.engine.snapshot() };
      }
      if (!this.engine) return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'not-initialized', message: 'Guest simulation is not initialized.' };
      const snapshot = this.engine.snapshot();
      if (request.type === 'advance' && (request.expectedEnvironmentRevision !== snapshot.environmentRevision ||
        request.expectedTopologyRevision !== snapshot.topologyRevision)) {
        return { type: 'error', requestId: request.requestId, sequence: request.sequence,
          code: 'stale-revision', message: 'Guest simulation environment or topology revision is stale.' };
      }
      if (request.type === 'checkpoint') return { type: 'checkpoint', requestId: request.requestId, sequence: request.sequence,
        snapshot, bytes: encodeGuestSimulationReplayState(replayStateFromGuestSimulationEngine(this.engine)) };
      if (request.type === 'advance' && request.conditionSnapshot && request.conditionSnapshot.tick > request.toTick) return { type: 'error',
        requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
        message: 'Condition snapshot tick cannot be later than the requested target tick.' };
      if (request.type === 'advance' && request.conditionSnapshot) this.engine.applyConditionSnapshot(request.conditionSnapshot);
      const next = request.type === 'advance' ? this.engine.advanceTo(request.toTick) : snapshot;
      return { type: request.type === 'advance' ? 'advanced' : 'snapshot', requestId: request.requestId,
        sequence: request.sequence, snapshot: next };
    } catch (error) {
      return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'simulation-failed', message: error instanceof Error ? error.message : 'Guest simulation failed.' };
    }
  }
}
