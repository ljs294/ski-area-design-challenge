import type { GuestSimulationEnvironmentSnapshot } from '../guestSimulation/contracts';
import type { GuestSimulationEngineSnapshot, GuestSimulationNetwork } from '../guestSimulation/engine';
import type { ConditionSnapshot } from '../guestSimulation/conditions';
import type { ReputationProfile } from '../guestSimulation/phase3Economy';

interface RequestBase { readonly requestId: string; readonly sequence: number }

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
}

export type GuestSimulationWorkerRequest =
  | (RequestBase & { readonly type: 'initialize'; readonly runId: string; readonly seed: string;
      /** Legacy fixed roster input. Ignored when `demand` is supplied. */
      readonly guestCount?: number; readonly demand?: GuestSimulationWorkerDemandInput;
      readonly network: GuestSimulationNetwork;
      readonly startTick: number; readonly endTick: number;
      readonly environmentRevision: number; readonly topologyRevision: number;
      readonly openingReputation?: ReputationProfile;
      readonly conditionSnapshot?: ConditionSnapshot })
  | (RequestBase & { readonly type: 'restore'; readonly bytes: Uint8Array; readonly expectedTopologyRevision: number })
  | (RequestBase & { readonly type: 'advance'; readonly toTick: number;
      readonly expectedEnvironmentRevision: number; readonly expectedTopologyRevision: number;
      readonly conditionSnapshot?: ConditionSnapshot })
  | (RequestBase & { readonly type: 'snapshot' | 'checkpoint' });

export type GuestSimulationWorkerResponse =
  | { readonly type: 'ready' | 'advanced' | 'snapshot'; readonly requestId: string; readonly sequence: number;
      readonly snapshot: GuestSimulationEngineSnapshot }
  | { readonly type: 'checkpoint'; readonly requestId: string; readonly sequence: number;
      readonly snapshot: GuestSimulationEngineSnapshot; readonly bytes: Uint8Array }
  | { readonly type: 'error'; readonly requestId: string; readonly sequence: number;
      readonly code: 'not-initialized' | 'stale-sequence' | 'stale-revision' | 'invalid-request' | 'simulation-failed';
      readonly message: string };

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
