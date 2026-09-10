import type { SnowGrid } from './snow';

export type MacroSecond = number & { readonly __clock: 'macro' };
export type MicroSecond = number & { readonly __clock: 'micro' };
export type DualSpeed = 1 | 2 | 4 | 8 | 16 | 64;
/** Additive, versioned knobs for individually simulated guest movement. */
export interface GuestMovementConfig {
  version: 1;
  /** Spacing between successive guests at one trail entrance, in movement-clock seconds. */
  trailEntrySpacingMicroSeconds: number;
  /** Keyed-randomness domain kept separate from all other guest decisions. */
  speedDomainTag: string;
  /** Absolute truncation bound for the standard-normal speed sample. */
  speedNormalLimit: number;
}
export interface SimulationSpeedProfile {
  version: 1;
  macroSecondsPerSecond: number;
  microRates: Record<DualSpeed, number>;
  representativeLimit: number;
  winterWeeks: number;
  openingHour: number;
  closingHour: number;
  guestMovement?: GuestMovementConfig;
  wear: { referenceWidthM: number; referenceSlopeDeg: number; lossM: number;
    packedPasses: number; hardPasses: number; warningDepthM: number; warningFraction: number; clearFraction: number };
}

export interface DualClockSnapshot {
  revision: number; macroSecond: MacroSecond; microSecond: MicroSecond;
  at: string; timezone: string; winterStart: string; winterEnd: string;
  speed: DualSpeed; paused: boolean; season: 'winter' | 'summer';
}
export type AdvanceDestination = 'opening' | 'day' | 'week' | 'month' | 'season' | 'winter';
export interface AdvanceRequest { destination: AdvanceDestination; target: string }
export interface AdvanceProgress { request: AdvanceRequest; startedAt: string; fraction: number;
  baseline: { admitted: number; runs: number; revenueCents: number };
  state: 'running' | 'suspended' | 'completed' | 'cancelled'; }
export interface OperationalSignal {
  id: string; entityId: string; entityKind: 'trail' | 'lift' | 'resort'; title: string; message: string;
  severity: 'critical' | 'opportunity' | 'advisory'; at: string; acknowledged: boolean; resolved: boolean;
}
export interface OperationJob {
  id: string; equipmentIds: string[]; routeIds: string[]; priority: number; startsAt: string;
  prerequisites: string[]; automationRuleId?: string;
  requiredCapabilities: string[];
  schedule: { recurrence?: 'daily' | 'weekly'; endsAt?: string };
  resources: { resourceId: string; reserved: number; consumed: number }[];
  progress: number; state: 'scheduled' | 'running' | 'interrupted' | 'completed' | 'cancelled';
  effects: ({ kind: 'surface'; trailId: string; exposureReset: number } |
    { kind: 'snow-transfer'; fromCell: number; toCell: number; volumeM3: number } |
    { kind: 'infrastructure-state'; entityId: string; state: 'open' | 'closed' })[];
}
export interface AdmissionEntitlement { id: string; guestOrCohortId: string; productId: string;
  admittedAt: string; paidCents: number; validUntil: string }
export interface DualPortal { id: string; nodeId: string; lngLat: readonly [number, number]; capacityPerMinute: number }
export interface DualAmenity { id: string; nodeId: string; priceCents: number; capacityPerHour: number;
  inventory: number; opens: number; closes: number; label: string; accessSeconds: number; serviceSeconds: number;
  need: 'hunger' | 'thirst'; relief: Partial<Record<'hunger' | 'thirst' | 'warmth' | 'fatigue' | 'restroom', number>> }

export interface TrafficExposureDelta { interval: number; edgeId: string; skierMeters: number }
/** Lift queue telemetry is checkpointed with aggregate flow so every lift entrypoint
 * can use the same committed view. New fields are optional when reading an early
 * schema-17 checkpoint; the engine normalizes them before publishing. */
export interface LiftFlowSnapshot {
  guests: number;
  waitSeconds: number;
  boarded: number;
  riders?: number;
  serviceAvailable?: boolean;
  serviceUnavailableReason?: 'missing' | 'no-descent' | 'closed' | 'capacity';
  dailyBoarded?: number;
  dailyAccuracy?: 'complete' | 'partial';
  trackingSince?: string;
}
export interface SnowAddResult {
  requestedMeters: number;
  affectedCells: number;
  clippedCells: number;
}
export interface AggregateFlowSnapshot {
  admitted: number; active: number; departed: number; turnedAway: number;
  ticketRevenueCents: number; amenityRevenueCents: number; completedRuns: number;
  queues: Record<string, LiftFlowSnapshot>;
  trails: Record<string, { guests: number; passages: number }>;
}
export interface GuestActivity { at: string; text: string }
export interface GuestMotion { routeId: string; lane: number; progress: number; duration: number }
export interface GuestInspectionSnapshot {
  id: string; groupId: string; status: 'walking' | 'lift-queue' | 'trail-queue' | 'lift-ride' | 'skiing' | 'resting' | 'departed';
  nextPlan: string; thought: string; satisfaction: number; runs: number; spendingCents: number;
  trackingBeganAt: string; completedAt?: string; history: GuestActivity[];
}
export interface RepresentativeGuest extends GuestInspectionSnapshot {
  needs: Record<'hunger' | 'thirst' | 'warmth' | 'fatigue' | 'restroom', number>;
  needsSecond: number; personalBudgetCents: number; amenityId?: string;
  laneEdgeId?: string;
  lastPosition?: [number, number];
  lastRestRun?: number;
  /** Stable visit personality; assigned once from the dedicated speed domain. */
  speedZ?: number;
  /** Set while waiting at a trail entrance. */
  trailQueueKey?: string;
  trailQueueArrivalMicroSecond?: number;
  trailQueueReleaseMicroSecond?: number;
  /** The previous trail id lets split segments continue without a second wait. */
  lastTrailId?: string;
  /** Synthetic walking transfer that bridges a same-trail junction gap. */
  transferTrailContinuationId?: string;
  edgeId: string | null; route: string[]; routeIndex: number; nodeId: string;
  started: number; due: number; admissionDay: string; ability: number; ordinal: number;
}
export interface MacroCohort {
  id: number; admissionId: string; count: number; nodeId: string; edgeId: string | null; due: number; started: number;
  status: 'choosing' | 'queue' | 'travel' | 'amenity'; leaveAt: number; ability: number; runs: number; budgetCents: number;
}
/** Existing journeys retain their geometry when the player edits the network. */
export interface TransitRoute {
  id: string; kind: 'trail' | 'lift' | 'path'; from: string; to: string;
  trailId: string; trailName: string; liftName: string; lengthM: number; travelTimeS: number;
  path: [number, number][]; lanes: [number, number][][]; distances: number[];
  footprint?: { indices: number[]; areas: number[]; areaM2: number; slopeDeg: number };
}
export interface TrailQueueEntry {
  guestId: string;
  arrivalMicroSecond: number;
  ordinal: number;
  releaseMicroSecond: number;
}
export interface TrailQueueState {
  trailId: string;
  entryNode: string;
  /** Next unreserved release instant, including the current actual-release cooldown. */
  nextReleaseMicroSecond: number;
  /** Last release that actually happened; future reservations do not become cooldown credits. */
  lastReleaseMicroSecond?: number;
  entries: TrailQueueEntry[];
}
export interface DualCheckpoint {
  version: 1; seed: string; config: SimulationSpeedProfile; clock: DualClockSnapshot; resortRevision: number; nextTicketPriceCents: number;
  snow: { bounds: SnowGrid['bounds']; width: number; height: number; depthM: number[]; surface: number[]; exposure: number[] } | null;
  flow: AggregateFlowSnapshot; cohorts: MacroCohort[]; guests: RepresentativeGuest[];
  dailyLedgers: { date: string; admissions: number; ticketRevenueCents: number; amenityRevenueCents: number }[];
  /** Additive schema-17 accounting. The date is local to the simulation clock. */
  dailyLiftBoardings?: { date: string; counts: Record<string, number>; accuracy: 'complete' | 'partial'; trackingSince?: string };
  snowLoss: { trafficM3: number; traceCutoffM3: number };
  transitRoutes: TransitRoute[];
  nextCohortId: number; nextGuestId: number; lastAdmissionMinute: number; lastWeatherHour: number;
  admissionResidual: number; serviceCredits: Record<string, number>; amenityCredits: Record<string, number>;
  amenityInventory: Record<string, number>; dailyPrices: Record<string, number>;
  signals: OperationalSignal[]; history: OperationalSignal[]; selectedGuestId: string | null; autoTrack: boolean;
  advance: AdvanceProgress | null; portal: DualPortal | null;
  /** Optional for early schema-17 checkpoints; normalized to an empty record on load. */
  trailQueues?: Record<string, TrailQueueState>;
  presentationMode?: 'individual' | 'aggregate';
}
export interface DualPublication {
  clock: DualClockSnapshot; flow: AggregateFlowSnapshot; signals: OperationalSignal[];
  guests: GuestInspectionSnapshot[]; selected: GuestInspectionSnapshot | null;
  autoTrack: boolean; advance: AdvanceProgress | null;
  points: { id: string; lng: number; lat: number; status: string; motion?: GuestMotion }[];
}
