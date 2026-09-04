import { createDailyGuestRoster, GuestSimulationEngine } from '../guestSimulation/engine';
import { workerEnvironment, type GuestSimulationWorkerRequest, type GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';
import { decodeGuestSimulationReplayState, encodeGuestSimulationReplayState } from '../guestSimulation/replayPersistence';
import { replayStateFromGuestSimulationEngine, restoreGuestSimulationEngine } from '../guestSimulation/enginePersistence';
import { planDailyArrivals, type DemandScenarioInputV1 } from '../guestSimulation/demand';
import { blendedReputationScore } from '../guestSimulation/phase3Economy';
import { buildGuestRenderFrame } from '../guestSimulation/guestRenderFrame';
import type {
  GuestSimulationAdvancePerformance,
  GuestSimulationCompactAdvanceRequest,
  GuestSimulationCompactAdvancedResponse,
  GuestSimulationLegacyAdvancedResponse,
  GuestSimulationWorkerErrorResponse,
  GuestSimulationCheckpointResponse,
  GuestSimulationWorkerCompatibilityResponse,
  GuestSimulationSnapshotResponse,
  GuestSimulationRenderFrame,
  GuestSimulationSummaryDelta,
  GuestSimulationEnvironmentUpdateRequest,
  GuestSimulationEnvironmentUpdatedResponse,
  GuestSimulationTopologyUpdateRequest,
  GuestSimulationTopologyUpdatedResponse,
} from './guestSimulationWorkerProtocol';
import type { ConditionSnapshot } from '../guestSimulation/conditions';
import type { GuestSimulationEnvironmentSnapshot } from '../guestSimulation/contracts';

const DEFAULT_DEMAND_BUCKET_SECONDS = 10 * 60;
const DEFAULT_DEMAND_MAX_GUESTS = 50_000;
const DEFAULT_DEMAND_MAX_PARTIES = 20_000;

function isSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value);
}

function nowMs(): number {
  return typeof performance === 'undefined' ? Date.now() : performance.now();
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

interface PendingEnvironmentUpdate {
  readonly effectiveSecond: number;
  readonly operationsRevision: number;
  readonly weatherRevision: number;
  readonly environment?: GuestSimulationEnvironmentSnapshot;
  readonly conditionSnapshot?: ConditionSnapshot;
}

/** Stateful worker body. Its only clock input is an explicit game-time command. */
export class GuestSimulationWorkerEngine {
  private engine: GuestSimulationEngine | null = null;
  private lastSequence = -1;
  /** Compact time is the integer second acknowledged by the compatibility engine. */
  private compactSecond = 0;
  private operationsRevision = 0;
  private weatherRevision = 0;
  private pendingEnvironmentUpdates: PendingEnvironmentUpdate[] = [];

  /**
   * Overloads preserve the old snapshot-returning call for existing adapters
   * while exposing a precise compact response to the new request shape.
  */
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'initialize' }>): GuestSimulationWorkerCompatibilityResponse;
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'restore' }>): GuestSimulationWorkerCompatibilityResponse;
  handle(request: { readonly requestId: string; readonly sequence: number; readonly type: 'snapshot' }): GuestSimulationSnapshotResponse;
  handle(request: { readonly requestId: string; readonly sequence: number; readonly type: 'checkpoint' }): GuestSimulationCheckpointResponse;
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'advance'; toTick: number }>): GuestSimulationLegacyAdvancedResponse | GuestSimulationWorkerErrorResponse;
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'advance'; targetSecond: number }>): GuestSimulationCompactAdvancedResponse | GuestSimulationWorkerErrorResponse;
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'updateEnvironment' }>): GuestSimulationEnvironmentUpdatedResponse | GuestSimulationWorkerErrorResponse;
  handle(request: Extract<GuestSimulationWorkerRequest, { type: 'topology-update' }>): GuestSimulationTopologyUpdatedResponse | GuestSimulationWorkerErrorResponse;
  handle(request: GuestSimulationWorkerRequest): GuestSimulationWorkerResponse;

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
        if ((request.operationsRevision !== undefined && (!isSafeInteger(request.operationsRevision) || request.operationsRevision < 0))
          || (request.weatherRevision !== undefined && (!isSafeInteger(request.weatherRevision) || request.weatherRevision < 0))) {
          return { type: 'error', requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
            message: 'Operations and weather revisions must be non-negative safe integers.' };
        }
        this.engine = new GuestSimulationEngine({ network: request.network, roster, runId: request.runId,
          environment: workerEnvironment(request), conditionSnapshot: request.conditionSnapshot,
          phase3: { dayId: `${request.seed}:${request.startTick}`,
            ticketPriceCents: request.demand?.ticketPriceCents ?? 10_000,
            outcomeWeight: request.demand?.outcomeWeight,
            demandForecast: planned?.forecast, demandRealization: planned?.realization,
            openingReputation: request.openingReputation }, phase5to7: request.phase5to7 });
        this.compactSecond = request.startTick;
        this.operationsRevision = request.operationsRevision ?? 0;
        this.weatherRevision = request.weatherRevision ?? 0;
        this.pendingEnvironmentUpdates = [];
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
        this.compactSecond = this.engine.currentTick;
        // Revision sidecars are not part of the legacy replay payload. A
        // restored compatibility checkpoint therefore starts at its known
        // topology with fresh coordinator revisions.
        this.operationsRevision = 0;
        this.weatherRevision = 0;
        this.pendingEnvironmentUpdates = [];
        return { type: 'ready', requestId: request.requestId, sequence: request.sequence, snapshot: this.engine.snapshot() };
      }
      if (!this.engine) return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'not-initialized', message: 'Guest simulation is not initialized.' };
      if (request.type === 'topology-update') return this.updateTopology(request);
      if (request.type === 'updateEnvironment') return this.updateEnvironment(request);
      // Compact advances intentionally avoid the rich snapshot path.  The
      // projection is built only after the bounded engine advance below.
      if ('targetSecond' in request) return this.compactAdvance(request);
      if (request.type === 'inspectGuest') return { type: 'guest', requestId: request.requestId, sequence: request.sequence,
        guestId: request.guestId, committedSecond: this.compactSecond, guest: this.engine.getGuest(request.guestId) ?? null };

      const snapshot = this.engine.snapshot();
      if (request.type === 'advance' && 'toTick' in request && (request.expectedEnvironmentRevision !== snapshot.environmentRevision ||
        request.expectedTopologyRevision !== snapshot.topologyRevision)) {
        return { type: 'error', requestId: request.requestId, sequence: request.sequence,
          code: 'stale-revision', message: 'Guest simulation environment or topology revision is stale.' };
      }
      if (request.type === 'checkpoint') return { type: 'checkpoint', requestId: request.requestId, sequence: request.sequence,
        snapshot, bytes: encodeGuestSimulationReplayState(replayStateFromGuestSimulationEngine(this.engine)),
        committedSecond: this.compactSecond };
      if (request.type === 'advance' && request.conditionSnapshot && request.conditionSnapshot.tick > request.toTick) return { type: 'error',
        requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
        message: 'Condition snapshot tick cannot be later than the requested target tick.' };
      if (request.type === 'advance' && request.conditionSnapshot) this.engine.applyConditionSnapshot(request.conditionSnapshot);
      if (request.type === 'advance') {
        const next = this.engine.advanceTo(request.toTick);
        // Keep mixed legacy/compact callers coherent during the rollout. The
        // legacy engine is integer-second based, so it can only move this
        // coordinator forward to its committed integer tick.
        this.compactSecond = Math.max(this.compactSecond, next.tick);
        return { type: 'advanced', requestId: request.requestId, sequence: request.sequence, snapshot: next };
      }
      return { type: 'snapshot', requestId: request.requestId, sequence: request.sequence, snapshot };
    } catch (error) {
      if (error instanceof GuestSimulationWorkerRevisionError) return { type: 'error', requestId: request.requestId,
        sequence: request.sequence, code: 'stale-revision', message: error.message };
      if (error instanceof RangeError) return { type: 'error', requestId: request.requestId,
        sequence: request.sequence, code: 'invalid-request', message: error.message };
      return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'simulation-failed', message: error instanceof Error ? error.message : 'Guest simulation failed.' };
    }
  }

  private compactAdvance(request: GuestSimulationCompactAdvanceRequest): GuestSimulationCompactAdvancedResponse {
    const engine = this.engine;
    if (!engine) {
      // This branch is normally handled before dispatch, but retaining the
      // guard keeps this helper safe if it is reused by another adapter.
      throw new Error('Guest simulation is not initialized.');
    }
    if (!Number.isFinite(request.targetSecond) || request.targetSecond < 0) {
      throw new RangeError('targetSecond must be a finite non-negative simulated second.');
    }
    if (!Number.isFinite(request.maxCpuMs) || request.maxCpuMs <= 0) {
      throw new RangeError('maxCpuMs must be a positive finite number.');
    }
    for (const [name, revision] of [['topology', request.topologyRevision],
      ['operations', request.operationsRevision], ['weather', request.weatherRevision]] as const) {
      if (!isSafeInteger(revision) || revision < 0) throw new RangeError(`${name} revision must be a non-negative safe integer.`);
    }
    if (request.topologyRevision !== engine.topologyRevision
      || request.operationsRevision !== this.operationsRevision
      || request.weatherRevision !== this.weatherRevision) {
      throw new GuestSimulationWorkerRevisionError(
        `Guest simulation revision is stale (topology ${request.topologyRevision}/${engine.topologyRevision}, `
        + `operations ${request.operationsRevision}/${this.operationsRevision}, weather ${request.weatherRevision}/${this.weatherRevision}).`,
      );
    }
    if (request.targetSecond < this.compactSecond) throw new RangeError('targetSecond cannot move simulation time backwards.');

    const start = nowMs();
    const budgetMs = Math.min(8, request.maxCpuMs);
    // The domain engine is integer-second based.  Never acknowledge a
    // fractional target: retain it as backlog until the next integer second
    // is accumulated, so checkpoints and guest state share one timestamp.
    const candidateTarget = Math.min(request.targetSecond, this.compactSecond + 60);
    const engineTarget = Math.max(engine.currentTick, Math.floor(candidateTarget));
    this.applyPendingEnvironmentUpdates(engineTarget);
    const advance = engineTarget > engine.currentTick
      ? engine.advanceToBudget(engineTarget, budgetMs)
      : { tick: engine.currentTick, eventsProcessed: 0, cpuMs: 0, budgetExceeded: false, reachedTarget: true };
    this.compactSecond = advance.tick;
    const renderFrame = buildGuestRenderFrame(engine.compactRenderProjection()) as GuestSimulationRenderFrame;
    const cpuMs = Math.max(advance.cpuMs, Math.max(0, nowMs() - start));
    const metrics = engine.getMetrics();
    const performanceReport: GuestSimulationAdvancePerformance = {
      cpuMs, workerCpuMs: cpuMs, workerP95Ms: cpuMs, budgetMs,
      eventsProcessed: advance.eventsProcessed,
      budgetExceeded: advance.budgetExceeded || cpuMs > budgetMs,
    };
    const summaryDelta: GuestSimulationSummaryDelta = {
      committedSecond: this.compactSecond, population: metrics.population,
      scheduled: metrics.scheduled, arrived: metrics.arrived,
      active: metrics.active, departed: metrics.departed,
    };
    return { type: 'advanced', requestId: request.requestId, sequence: request.sequence,
      committedSecond: this.compactSecond, backlogSeconds: Math.max(0, request.targetSecond - this.compactSecond),
      renderFrame, summaryDelta, performance: performanceReport,
      topologyRevision: engine.topologyRevision, operationsRevision: this.operationsRevision,
      weatherRevision: this.weatherRevision };
  }

  /** Queue an environment revision without advancing speculative time. */
  private updateTopology(request: GuestSimulationTopologyUpdateRequest): GuestSimulationTopologyUpdatedResponse {
    const engine = this.engine;
    if (!engine) throw new Error('Guest simulation is not initialized.');
    if (!isSafeInteger(request.topologyRevision) || request.topologyRevision < 0) {
      throw new RangeError('topology revision must be a non-negative safe integer.');
    }
    if (request.topologyRevision === engine.topologyRevision) {
      throw new GuestSimulationWorkerRevisionError(
        `Topology revision ${request.topologyRevision} is already applied.`,
      );
    }
    if (engine.currentTick !== this.compactSecond) {
      throw new GuestSimulationWorkerRevisionError('Topology replacement must pause at the last committed simulation second.');
    }
    const migration = engine.replaceTopology(request.network, request.topologyRevision);
    // Pending condition/environment payloads reference the old edge set and
    // must never be applied after a topology replacement.
    this.pendingEnvironmentUpdates = [];
    const metrics = engine.getMetrics();
    return { type: 'topology-updated', requestId: request.requestId, sequence: request.sequence,
      committedSecond: this.compactSecond, migration,
      renderFrame: buildGuestRenderFrame(engine.compactRenderProjection()) as GuestSimulationRenderFrame,
      summaryDelta: { committedSecond: this.compactSecond, population: metrics.population,
        scheduled: metrics.scheduled, arrived: metrics.arrived, active: metrics.active, departed: metrics.departed } };
  }

  /** Queue an environment revision without advancing speculative time. */
  private updateEnvironment(request: GuestSimulationEnvironmentUpdateRequest): GuestSimulationEnvironmentUpdatedResponse {
    const engine = this.engine;
    if (!engine) throw new Error('Guest simulation is not initialized.');
    if (!isSafeInteger(request.effectiveSecond) || request.effectiveSecond <= this.compactSecond) {
      throw new GuestSimulationWorkerRevisionError('Environment updates must target a future simulation second.');
    }
    for (const [name, revision] of [['topology', request.topologyRevision],
      ['operations', request.operationsRevision], ['weather', request.weatherRevision]] as const) {
      if (!isSafeInteger(revision) || revision < 0) throw new RangeError(`${name} revision must be a non-negative safe integer.`);
    }
    if (request.topologyRevision !== engine.topologyRevision) {
      throw new GuestSimulationWorkerRevisionError('Environment update topology revision is stale.');
    }
    if (request.expectedOperationsRevision !== undefined && request.expectedOperationsRevision !== this.operationsRevision) {
      throw new GuestSimulationWorkerRevisionError(`Expected operations revision ${request.expectedOperationsRevision}, got ${this.operationsRevision}.`);
    }
    if (request.expectedWeatherRevision !== undefined && request.expectedWeatherRevision !== this.weatherRevision) {
      throw new GuestSimulationWorkerRevisionError(`Expected weather revision ${request.expectedWeatherRevision}, got ${this.weatherRevision}.`);
    }
    if (request.operationsRevision < this.operationsRevision || request.weatherRevision < this.weatherRevision) {
      throw new GuestSimulationWorkerRevisionError(
        `Environment update revision is stale (operations ${request.operationsRevision}/${this.operationsRevision}, `
        + `weather ${request.weatherRevision}/${this.weatherRevision}).`,
      );
    }
    if (request.environment && (request.environment.tick !== request.effectiveSecond
      || request.environment.topologyRevision !== request.topologyRevision
      || request.environment.environmentRevision <= engine.environmentRevision)) {
      throw new GuestSimulationWorkerRevisionError('Environment update payload is stale or has a mismatched effective second.');
    }
    const latestQueuedConditionRevision = this.pendingEnvironmentUpdates.reduce((latest, update) =>
      Math.max(latest, update.conditionSnapshot?.revision ?? -1), -1);
    if (request.conditionSnapshot && (request.conditionSnapshot.tick !== request.effectiveSecond
      || request.conditionSnapshot.revision <= Math.max(engine.conditionRevision, latestQueuedConditionRevision))) {
      throw new GuestSimulationWorkerRevisionError('Condition update payload is stale or has a mismatched effective second.');
    }
    const previous = this.pendingEnvironmentUpdates[this.pendingEnvironmentUpdates.length - 1];
    if (previous && request.effectiveSecond < previous.effectiveSecond) {
      throw new GuestSimulationWorkerRevisionError('Environment updates must be submitted in effective-second order.');
    }
    this.pendingEnvironmentUpdates.push({ effectiveSecond: request.effectiveSecond,
      operationsRevision: request.operationsRevision, weatherRevision: request.weatherRevision,
      ...(request.environment ? { environment: request.environment } : {}),
      ...(request.conditionSnapshot ? { conditionSnapshot: request.conditionSnapshot } : {}) });
    this.operationsRevision = request.operationsRevision;
    this.weatherRevision = request.weatherRevision;
    return { type: 'environment-updated', requestId: request.requestId, sequence: request.sequence,
      effectiveSecond: request.effectiveSecond, committedSecond: this.compactSecond,
      topologyRevision: engine.topologyRevision, operationsRevision: this.operationsRevision,
      weatherRevision: this.weatherRevision };
  }

  /** Apply all due revisions immediately before their timestamp's events. */
  private applyPendingEnvironmentUpdates(targetSecond: number): void {
    const engine = this.engine;
    if (!engine) return;
    while (this.pendingEnvironmentUpdates.length > 0) {
      const update = this.pendingEnvironmentUpdates[0]!;
      if (update.effectiveSecond > targetSecond) break;
      this.pendingEnvironmentUpdates.shift();
      if (update.effectiveSecond <= engine.currentTick) {
        throw new GuestSimulationWorkerRevisionError('An environment update became stale before its effective second.');
      }
      if (update.environment) engine.applyEnvironmentSnapshot(update.environment);
      if (update.conditionSnapshot) engine.applyConditionSnapshot(update.conditionSnapshot);
    }
  }
}

/** Internal marker used to preserve a clear stale-revision failure boundary. */
class GuestSimulationWorkerRevisionError extends Error {
  constructor(message: string) { super(message); this.name = 'GuestSimulationWorkerRevisionError'; }
}
