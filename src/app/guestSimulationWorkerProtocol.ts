import type { GuestSimulationEnvironmentSnapshot } from '../guestSimulation/contracts';
import type { GuestSimulationEngineSnapshot, GuestSimulationNetwork, GuestTopologyMigrationResult } from '../guestSimulation/engine';
import type { ConditionSnapshot } from '../guestSimulation/conditions';
import type { ReputationProfile } from '../guestSimulation/phase3Economy';
import type { RemainingPhasesInput } from '../guestSimulation/remainingPhasesRuntime';

export interface RequestBase { readonly requestId: string; readonly sequence: number }

/**
 * Optional Phase 3 market inputs.  Keeping this boundary free of the full
 * demand module's implementation types makes the worker request stable and
 * straightforward to clone through structured messaging.
 */
export interface GuestSimulationWorkerDemandInput {
  readonly dayType: 'weekday' | 'weekend' | 'holiday';
  readonly basePotentialGuests: number;
  readonly ticketPriceCents: number;
  readonly referencePriceCents: number;
  readonly reputation: number;
  readonly resortValue: number;
  readonly operatingFraction?: number;
  readonly conditionFactor?: number;
  readonly availableCapacityGuests: number;
  readonly maxGuests?: number;
  readonly maxParties?: number;
  readonly bucketSeconds?: number;
  /** Uniform multiplier used only for additive composite-week outcomes. */
  readonly outcomeWeight?: number;
}

/**
 * The compact render contract deliberately uses columns instead of one rich
 * object per guest.  Four 32-bit columns are 16 bytes per guest, leaving room
 * under the 24-byte wire budget while remaining directly consumable by a
 * custom WebGL layer.
 */
export interface GuestSimulationRenderFrame {
  readonly ids: Uint32Array;
  /** Alias retained for callers that name the column after the domain field. */
  readonly guestIds: Uint32Array;
  readonly edgeIndices: Int32Array;
  readonly progress: Float32Array;
  readonly statusFlags: Uint32Array;
  readonly bytesPerGuest: 16;
  readonly byteLength: number;
}

export interface GuestSimulationAdvancePerformance {
  /** CPU time spent in this worker slice, in real milliseconds. */
  readonly cpuMs: number;
  /** Explicit alias for status surfaces and diagnostics. */
  readonly workerCpuMs: number;
  /** A one-sample p95 estimate; a coordinator may replace it with a window. */
  readonly workerP95Ms: number;
  readonly budgetMs: number;
  readonly eventsProcessed: number;
  readonly budgetExceeded: boolean;
}

export interface GuestSimulationSummaryDelta {
  readonly committedSecond: number;
  readonly population: number;
  readonly scheduled: number;
  readonly arrived: number;
  readonly active: number;
  readonly departed: number;
}

export interface GuestSimulationLegacyAdvanceRequest extends RequestBase {
  readonly type: 'advance';
  readonly toTick: number;
  readonly expectedEnvironmentRevision: number;
  readonly expectedTopologyRevision: number;
  readonly conditionSnapshot?: ConditionSnapshot;
}

export interface GuestSimulationCompactAdvanceRequest extends RequestBase {
  readonly type: 'advance';
  readonly targetSecond: number;
  readonly maxCpuMs: number;
  readonly topologyRevision: number;
  readonly operationsRevision: number;
  readonly weatherRevision: number;
}

/** Atomic topology replacement at the worker's last committed second. */
export interface GuestSimulationTopologyUpdateRequest extends RequestBase {
  readonly type: 'topology-update';
  readonly network: GuestSimulationNetwork;
  readonly topologyRevision: number;
}

/**
 * A revision update is queued by the worker and becomes observable at
 * `effectiveSecond`.  Keeping this separate from `advance` means a weather
 * edit can arrive while the render target is already being coalesced without
 * making the advance request carry a speculative state snapshot.
 */
export interface GuestSimulationEnvironmentUpdateRequest extends RequestBase {
  readonly type: 'updateEnvironment';
  readonly effectiveSecond: number;
  readonly topologyRevision: number;
  readonly operationsRevision: number;
  readonly weatherRevision: number;
  readonly expectedOperationsRevision?: number;
  readonly expectedWeatherRevision?: number;
  /** Full operation state is optional; conditions are the common weather path. */
  readonly environment?: GuestSimulationEnvironmentSnapshot;
  readonly conditionSnapshot?: ConditionSnapshot;
}

export type GuestSimulationWorkerRequest =
  | (RequestBase & { readonly type: 'initialize'; readonly runId: string; readonly seed: string;
      /** Legacy fixed roster input. Ignored when `demand` is supplied. */
      readonly guestCount?: number; readonly demand?: GuestSimulationWorkerDemandInput;
      readonly network: GuestSimulationNetwork;
      readonly startTick: number; readonly endTick: number;
      readonly environmentRevision: number; readonly topologyRevision: number;
      /** Revisions introduced by the composite-week coordinator. */
      readonly operationsRevision?: number; readonly weatherRevision?: number;
      readonly openingReputation?: ReputationProfile;
      readonly phase5to7?: RemainingPhasesInput;
      readonly conditionSnapshot?: ConditionSnapshot })
  | (RequestBase & { readonly type: 'restore'; readonly bytes: Uint8Array; readonly expectedTopologyRevision: number })
  /** Existing integer-tick request. Kept until all callers migrate. */
  | GuestSimulationLegacyAdvanceRequest
  /** Continuous coordinator request. One response advances at most 60 sim seconds. */
  | GuestSimulationCompactAdvanceRequest
  | GuestSimulationTopologyUpdateRequest
  | GuestSimulationEnvironmentUpdateRequest
  | (RequestBase & { readonly type: 'snapshot' | 'checkpoint' })
  | (RequestBase & { readonly type: 'inspectGuest'; readonly guestId: string });

export interface GuestSimulationReadyResponse {
  readonly type: 'ready'; readonly requestId: string; readonly sequence: number;
  readonly snapshot: GuestSimulationEngineSnapshot;
}

export interface GuestSimulationLegacyAdvancedResponse {
  readonly type: 'advanced'; readonly requestId: string; readonly sequence: number;
  readonly snapshot: GuestSimulationEngineSnapshot;
}

export interface GuestSimulationCompactAdvancedResponse {
  readonly type: 'advanced'; readonly requestId: string; readonly sequence: number;
  readonly committedSecond: number; readonly backlogSeconds: number;
  readonly renderFrame: GuestSimulationRenderFrame;
  readonly summaryDelta?: GuestSimulationSummaryDelta;
  readonly performance: GuestSimulationAdvancePerformance;
  readonly topologyRevision: number;
  readonly operationsRevision: number;
  readonly weatherRevision: number;
}

export interface GuestSimulationTopologyUpdatedResponse {
  readonly type: 'topology-updated'; readonly requestId: string; readonly sequence: number;
  readonly committedSecond: number; readonly migration: GuestTopologyMigrationResult;
  readonly renderFrame: GuestSimulationRenderFrame; readonly summaryDelta: GuestSimulationSummaryDelta;
}

export interface GuestSimulationEnvironmentUpdatedResponse {
  readonly type: 'environment-updated'; readonly requestId: string; readonly sequence: number;
  readonly effectiveSecond: number; readonly committedSecond: number;
  readonly topologyRevision: number; readonly operationsRevision: number; readonly weatherRevision: number;
}

export interface GuestSimulationSnapshotResponse {
  readonly type: 'snapshot'; readonly requestId: string; readonly sequence: number;
  readonly snapshot: GuestSimulationEngineSnapshot;
}

export interface GuestSimulationCheckpointResponse {
  readonly type: 'checkpoint'; readonly requestId: string; readonly sequence: number;
  readonly snapshot: GuestSimulationEngineSnapshot; readonly bytes: Uint8Array; readonly committedSecond?: number;
}

export interface GuestSimulationGuestResponse {
  readonly type: 'guest'; readonly requestId: string; readonly sequence: number;
  readonly guestId: string; readonly committedSecond: number;
  readonly guest: import('../guestSimulation/contracts').GuestState | null;
}

export interface GuestSimulationWorkerErrorResponse {
  readonly type: 'error'; readonly requestId: string; readonly sequence: number;
  readonly code: 'not-initialized' | 'stale-sequence' | 'stale-revision' | 'invalid-request' | 'simulation-failed';
  readonly message: string;
}

/** Existing rich response surface, intentionally excluding compact advances. */
export type GuestSimulationWorkerCompatibilityResponse =
  | GuestSimulationReadyResponse
  | GuestSimulationLegacyAdvancedResponse
  | GuestSimulationSnapshotResponse
  | GuestSimulationCheckpointResponse
  | GuestSimulationGuestResponse
  | GuestSimulationWorkerErrorResponse;

export type GuestSimulationWorkerResponse =
  | GuestSimulationReadyResponse
  | GuestSimulationLegacyAdvancedResponse
  | GuestSimulationCompactAdvancedResponse
  | GuestSimulationTopologyUpdatedResponse
  | GuestSimulationEnvironmentUpdatedResponse
  | GuestSimulationSnapshotResponse
  | GuestSimulationCheckpointResponse
  | GuestSimulationGuestResponse
  | GuestSimulationWorkerErrorResponse;

export function workerEnvironment(
  request: Extract<GuestSimulationWorkerRequest, { type: 'initialize' }>,
): GuestSimulationEnvironmentSnapshot {
  return Object.freeze({ version: 1, tick: request.startTick,
    environmentRevision: request.environmentRevision, topologyRevision: request.topologyRevision,
    operating: true, portals: request.network.portals, incidents: [],
    conditions: Object.freeze({ version: 1, tick: request.startTick, status: 'good', trend: 'stable',
      temperatureC: -3, windKph: 8, visibilityKm: 25, precipitationMm: 0, snowfallCm: 0,
      terrainOpenFraction: 1, liftOpenFraction: 1, trailOpenFraction: 1 }) });
}
