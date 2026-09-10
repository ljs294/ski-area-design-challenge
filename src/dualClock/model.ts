import type { NetworkEdge } from '../network';
import type { SnowGrid } from '../types/snow';
import type { SavedTrail } from '../types/trails';
import type { TerrainRecord } from '../types/terrain';
import type { ResolvedWeatherHour } from '../weather/weatherModel';

import type { SimulationSpeedProfile, DualCheckpoint, DualPortal, DualAmenity, GuestMovementConfig } from '../types/dualClock';
export type * from '../types/dualClock';

export const DUAL_SPEEDS = [1, 2, 4, 8, 16, 64] as const;
export const DEFAULT_GUEST_MOVEMENT: GuestMovementConfig = {
  version: 1,
  trailEntrySpacingMicroSeconds: 2,
  speedDomainTag: 'dual-clock-guest-speed-v1',
  speedNormalLimit: 3,
};
export const DEFAULT_DUAL_CONFIG: SimulationSpeedProfile = {
  version: 1, macroSecondsPerSecond: 40,
  microRates: { 1: 1, 2: 1.75, 4: 2.75, 8: 8, 16: 16, 64: 64 },
  representativeLimit: 3000, winterWeeks: 24, openingHour: 8, closingHour: 16,
  guestMovement: DEFAULT_GUEST_MOVEMENT,
  wear: { referenceWidthM: 30, referenceSlopeDeg: 20, lossM: 0.000005,
    packedPasses: 200, hardPasses: 800, warningDepthM: 0.1, warningFraction: 0.2, clearFraction: 0.1 },
};
export interface ResortSimulationInput {
  revision: number; edges: readonly NetworkEdge[]; trails: readonly SavedTrail[];
  portal: DualPortal | null; dailyDemand: number; ticketPriceCents: number; amenities: readonly DualAmenity[];
  dailyDemandByWeekday?: readonly number[];
}
export interface DualInitialization {
  seed: string; at: string; timezone: string; snow: SnowGrid | null;
  terrain: TerrainRecord | null; weather: readonly ResolvedWeatherHour[]; resort: ResortSimulationInput;
  checkpoint?: DualCheckpoint; config?: SimulationSpeedProfile;
}
