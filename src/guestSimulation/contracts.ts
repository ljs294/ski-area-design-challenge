/**
 * Dependency-neutral contracts for the guest simulation.
 *
 * The guest simulation deliberately uses integer seconds as its only clock
 * primitive.  Keeping these contracts free of React, Electron, MapLibre, and
 * the save format lets the simulation run in a worker or on the server later.
 */

export const GUEST_SIMULATION_CONTRACT_VERSION = 1 as const;
export const GUEST_SIMULATION_PROTOCOL_VERSION = 1 as const;

export type GuestSimulationContractVersion = typeof GUEST_SIMULATION_CONTRACT_VERSION;
export type GuestSimulationProtocolVersion = typeof GUEST_SIMULATION_PROTOCOL_VERSION;

/** A simulation tick is always a whole, non-negative simulated second. */
export type SimulatedSecond = number;
export type SimulatedTick = SimulatedSecond;

export type GuestId = string;
export type PartyId = string;
export type GuestPortalId = string;
export type IncidentId = string;
export type ThoughtEventId = string;
export type ProtocolSequence = number;
export type EnvironmentRevision = number;
export type TopologyRevision = number;

export type GuestExperience = 'beginner' | 'intermediate' | 'advanced' | 'expert';
export type GuestAgeBand = 'child' | 'teen' | 'adult' | 'senior';
export type PartyKind = 'individual' | 'family' | 'friends' | 'club' | 'school';

export interface GuestPreferences {
  /** Compatibility UI band; behavior should use the continuous ability field. */
  readonly experience: GuestExperience;
  readonly abilityBand: GuestExperience;
  readonly ability: number;
  readonly ageBand: GuestAgeBand;
  readonly wantsLessons: boolean;
  readonly budgetCents: number;
  readonly economicSegment: 'budget' | 'standard' | 'premium' | 'luxury';
  readonly tripCashCents: number;
  readonly riskTolerance: number;
  readonly comfortDemand: number;
  readonly hardcoreTerrainPreference: number;
  readonly priceSensitivity: number;
  readonly frugality: number;
  readonly patience: number;
  readonly varietySeeking: number;
}

/** An individual visitor. `futurePartyId` is intentionally nullable. */
export interface Guest {
  readonly id: GuestId;
  readonly partyId: PartyId;
  readonly ordinal: number;
  readonly arrivalTick: SimulatedSecond;
  readonly plannedDepartureTick: SimulatedSecond | null;
  readonly portalId: GuestPortalId;
  readonly preferences: GuestPreferences;
  /** Reserved seam for a later party-merging or future-booking feature. */
  readonly futurePartyId: PartyId | null;
}

/** A party is the unit used for shared decisions and arrival waves. */
export interface Party {
  readonly id: PartyId;
  readonly guestIds: readonly GuestId[];
  readonly size: number;
  readonly kind: PartyKind;
  readonly heavyGroup: boolean;
  readonly arrivalTick: SimulatedSecond;
  readonly plannedDepartureTick: SimulatedSecond | null;
  /** Reserved seam for a later party-merging or future-booking feature. */
  readonly futurePartyId: PartyId | null;
}

export type DemandWaveKind = 'weekday' | 'weekend' | 'holiday';

export interface DemandWave {
  readonly id: string;
  readonly kind: DemandWaveKind;
  /** Inclusive start of the half-open [startTick, endTick) interval. */
  readonly startTick: SimulatedSecond;
  /** Exclusive end of the half-open [startTick, endTick) interval. */
  readonly endTick: SimulatedSecond;
  readonly guestCount: number;
  readonly partyCount: number;
}

/** Arrival demand is explicit and reproducible; it is not a random callback. */
export interface DemandPlan {
  readonly version: GuestSimulationContractVersion;
  readonly seed: string;
  readonly guestCount: number;
  readonly partyCount: number;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  readonly waves: readonly DemandWave[];
  readonly heavyGroupCount: number;
}

/**
 * A portal is specifically a guest entrance.  It is not a generic map point:
 * `direction` and `accepts` make the inbound semantics unambiguous to worker
 * and UI consumers.
 */
export interface GuestPortal {
  readonly version: GuestSimulationContractVersion;
  readonly id: GuestPortalId;
  readonly kind: 'guest-entrance';
  readonly type: 'guest-entrance';
  readonly semantics: 'guest-entrance';
  readonly direction: 'inbound';
  readonly accepts: 'guests';
  readonly label: string;
  readonly capacityGuestsPerTick: number;
  /** Inclusive start of the half-open operating interval. */
  readonly openFromTick: SimulatedSecond;
  /** Exclusive end of the half-open operating interval. */
  readonly openUntilTick: SimulatedSecond;
}

export type RunConditionTrend = 'improving' | 'stable' | 'deteriorating';
export type RunConditionStatus = 'excellent' | 'good' | 'fair' | 'poor' | 'severe';

/** Conditions that affect guest decisions at one exact simulation tick. */
export interface RunConditionSnapshot {
  readonly version: GuestSimulationContractVersion;
  readonly tick: SimulatedSecond;
  readonly status: RunConditionStatus;
  readonly trend: RunConditionTrend;
  readonly temperatureC: number;
  readonly windKph: number;
  readonly visibilityKm: number;
  readonly precipitationMm: number;
  readonly snowfallCm: number;
  readonly terrainOpenFraction: number;
  readonly liftOpenFraction: number;
  readonly trailOpenFraction: number;
}

export type IncidentKind =
  | 'portal-closure'
  | 'lift-closure'
  | 'trail-closure'
  | 'weather'
  | 'capacity'
  | 'safety'
  | 'closure-storm';
export type IncidentSeverity = 'info' | 'minor' | 'major' | 'critical';

export interface Incident {
  readonly version: GuestSimulationContractVersion;
  readonly id: IncidentId;
  readonly kind: IncidentKind;
  readonly severity: IncidentSeverity;
  /** Inclusive start of the half-open incident interval. */
  readonly startTick: SimulatedSecond;
  /** Exclusive end, or null while the incident remains active. */
  readonly endTick: SimulatedSecond | null;
  readonly affectedPortalId: GuestPortalId | null;
  readonly affectedResourceId: string | null;
  readonly message: string;
}

export type ThoughtEventKind =
  | 'arrived'
  | 'queueing'
  | 'riding'
  | 'skiing'
  | 'waiting'
  | 'concerned'
  | 'leaving';
export type ThoughtSentiment = 'positive' | 'neutral' | 'negative';

/** A bounded, inspectable explanation of a guest state transition. */
export interface ThoughtEvent {
  readonly version: GuestSimulationContractVersion;
  readonly id: ThoughtEventId;
  readonly tick: SimulatedSecond;
  readonly guestId: GuestId;
  readonly partyId: PartyId | null;
  readonly kind: ThoughtEventKind;
  readonly sentiment: ThoughtSentiment;
  readonly text: string;
  /** Stable machine-readable explanation used by aggregate guest views. */
  readonly reasonCode?: string;
}

export type GuestStateStatus =
  | 'scheduled'
  | 'arriving'
  | 'choosing'
  | 'travelling-to-lift'
  | 'lift-queue'
  | 'lift-ride'
  | 'skiing'
  | 'appraising'
  | 'departing'
  | 'departed';

/** States reserved for later facility, incident, and lodging systems. */
export type GuestFutureStateStatus =
  | 'facility-queue'
  | 'facility-service'
  | 'regrouping'
  | 'incident'
  | 'patrol-response'
  | 'lodging'
  | 'road-travel';

export type GuestSimulationStateStatus = GuestStateStatus | GuestFutureStateStatus;

export interface GuestState extends Guest {
  readonly status: GuestSimulationStateStatus;
  readonly currentPortalId: GuestPortalId | null;
  readonly currentResourceId: string | null;
  readonly satisfaction: number;
}

export interface PartyState extends Party {
  readonly status: 'arriving' | 'active' | 'departed';
}

/** The environment is separate from guest state so conditions can be replayed. */
export interface GuestSimulationEnvironmentSnapshot {
  readonly version: GuestSimulationContractVersion;
  readonly tick: SimulatedSecond;
  readonly environmentRevision: EnvironmentRevision;
  readonly topologyRevision: TopologyRevision;
  readonly operating: boolean;
  readonly conditions: RunConditionSnapshot;
  readonly portals: readonly GuestPortal[];
  readonly incidents: readonly Incident[];
}

/** Short alias used by consumers that do not need the longer domain name. */
export type EnvironmentSnapshot = GuestSimulationEnvironmentSnapshot;

export interface GuestSimulationSnapshot {
  readonly version: GuestSimulationContractVersion;
  readonly protocolVersion: GuestSimulationProtocolVersion;
  readonly configVersion: number;
  readonly runId: string;
  readonly tick: SimulatedSecond;
  readonly sequence: ProtocolSequence;
  readonly environmentRevision: EnvironmentRevision;
  readonly topologyRevision: TopologyRevision;
  /** Deterministic authoritative-state checksum; the binary sidecar later adds SHA-256. */
  readonly checksum: string;
  readonly guests: readonly GuestState[];
  readonly parties: readonly PartyState[];
  readonly demandPlan: DemandPlan;
  readonly environment: GuestSimulationEnvironmentSnapshot;
  readonly incidents: readonly Incident[];
  readonly thoughtEvents: readonly ThoughtEvent[];
  /** Nullable until a later release supports dynamically formed parties. */
  readonly futureParty: Party | null;
}

export interface GuestSimulationError {
  readonly code: 'invalid-request' | 'invalid-snapshot' | 'unsupported-version' | 'simulation-failed' | 'stale-request' | 'stale-revision';
  readonly message: string;
  readonly retryable: boolean;
}

export interface GuestSimulationInitializeRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'initialize';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly runId: string;
  readonly config: import('./config.ts').GuestSimulationConfig;
  readonly demandPlan: DemandPlan;
  readonly environment: GuestSimulationEnvironmentSnapshot;
}

export interface GuestSimulationAdvanceRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'advance';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly expectedEnvironmentRevision: EnvironmentRevision;
  readonly expectedTopologyRevision: TopologyRevision;
  readonly toTick: SimulatedSecond;
}

export interface GuestSimulationSnapshotRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'snapshot';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
}

export interface GuestSimulationSaveRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'save';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
}

export interface GuestSimulationLoadRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'load';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly expectedEnvironmentRevision: EnvironmentRevision;
  readonly expectedTopologyRevision: TopologyRevision;
  readonly expectedRunId: string;
  readonly expectedConfigVersion: number;
  readonly expectedDemandSeed: string;
  readonly expectedSnapshotChecksum: string;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationCancelRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'cancel';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly cancelRequestId: string;
}

export interface GuestSimulationResetRequest {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'reset';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
}

/** Commands sent to a simulation worker/controller. */
export type GuestSimulationRequest =
  | GuestSimulationInitializeRequest
  | GuestSimulationAdvanceRequest
  | GuestSimulationSnapshotRequest
  | GuestSimulationSaveRequest
  | GuestSimulationLoadRequest
  | GuestSimulationCancelRequest
  | GuestSimulationResetRequest;

export interface GuestSimulationReadyResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'ready';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationAdvancedResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'advanced';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly fromTick: SimulatedSecond;
  readonly toTick: SimulatedSecond;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationSnapshotResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'snapshot';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationSavedResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'saved';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationLoadedResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'loaded';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly snapshot: GuestSimulationSnapshot;
}

export interface GuestSimulationResetResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'reset';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
}

export interface GuestSimulationCancelledResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'cancelled';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly cancelledRequestId: string;
}

export interface GuestSimulationErrorResponse {
  readonly version: GuestSimulationProtocolVersion;
  readonly type: 'error';
  readonly requestId: string;
  readonly sequence: ProtocolSequence;
  readonly error: GuestSimulationError;
}

/** Messages returned by a simulation worker/controller. */
export type GuestSimulationResponse =
  | GuestSimulationReadyResponse
  | GuestSimulationAdvancedResponse
  | GuestSimulationSnapshotResponse
  | GuestSimulationSavedResponse
  | GuestSimulationLoadedResponse
  | GuestSimulationCancelledResponse
  | GuestSimulationResetResponse
  | GuestSimulationErrorResponse;

/** Friendly aliases for integrations that call the two sides command/message. */
export type GuestSimulationCommand = GuestSimulationRequest;
export type GuestSimulationMessage = GuestSimulationResponse;

export function isSimulatedSecond(value: unknown): value is SimulatedSecond {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

export function assertSimulatedSecond(value: unknown, label = 'simulated second'): asserts value is SimulatedSecond {
  if (!isSimulatedSecond(value)) {
    throw new RangeError(`${label} must be a finite, non-negative integer`);
  }
}

export function asSimulatedSecond(value: number, label = 'simulated second'): SimulatedSecond {
  assertSimulatedSecond(value, label);
  return value;
}

export function isProtocolSequence(value: unknown): value is ProtocolSequence {
  return Number.isSafeInteger(value) && (value as number) >= 0;
}

export function isSimulationRevision(value: unknown): value is EnvironmentRevision | TopologyRevision {
  return Number.isSafeInteger(value) && (value as number) >= 0;
}

export function isHalfOpenTickInterval(startTick: unknown, endTick: unknown): boolean {
  return isSimulatedSecond(startTick) && isSimulatedSecond(endTick) && endTick > startTick;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

function isEnvironmentSnapshot(value: unknown): value is GuestSimulationEnvironmentSnapshot {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const environment = value as Partial<GuestSimulationEnvironmentSnapshot>;
  return environment.version === GUEST_SIMULATION_CONTRACT_VERSION
    && isSimulatedSecond(environment.tick)
    && isSimulationRevision(environment.environmentRevision)
    && isSimulationRevision(environment.topologyRevision)
    && typeof environment.operating === 'boolean'
    && Array.isArray(environment.portals)
    && environment.portals.every((portal) => isHalfOpenTickInterval(portal.openFromTick, portal.openUntilTick))
    && Array.isArray(environment.incidents)
    && environment.incidents.every((incident) => isSimulatedSecond(incident.startTick)
      && (incident.endTick === null || isHalfOpenTickInterval(incident.startTick, incident.endTick)));
}

export function isGuestSimulationSnapshot(value: unknown): value is GuestSimulationSnapshot {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const snapshot = value as Partial<GuestSimulationSnapshot>;
  const demandPlan = snapshot.demandPlan;
  return snapshot.version === GUEST_SIMULATION_CONTRACT_VERSION
    && snapshot.protocolVersion === GUEST_SIMULATION_PROTOCOL_VERSION
    && Number.isSafeInteger(snapshot.configVersion) && (snapshot.configVersion as number) > 0
    && isNonEmptyString(snapshot.runId)
    && isSimulatedSecond(snapshot.tick)
    && isProtocolSequence(snapshot.sequence)
    && isSimulationRevision(snapshot.environmentRevision)
    && isSimulationRevision(snapshot.topologyRevision)
    && isNonEmptyString(snapshot.checksum)
    && Array.isArray(snapshot.guests)
    && Array.isArray(snapshot.parties)
    && Array.isArray(snapshot.incidents)
    && Array.isArray(snapshot.thoughtEvents)
    && isEnvironmentSnapshot(snapshot.environment)
    && snapshot.environment.environmentRevision === snapshot.environmentRevision
    && snapshot.environment.topologyRevision === snapshot.topologyRevision
    && snapshot.environment.tick === snapshot.tick
    && demandPlan?.version === GUEST_SIMULATION_CONTRACT_VERSION
    && isNonEmptyString(demandPlan.seed)
    && isHalfOpenTickInterval(demandPlan.startTick, demandPlan.endTick)
    && Array.isArray(demandPlan.waves)
    && demandPlan.waves.every((wave) => isHalfOpenTickInterval(wave.startTick, wave.endTick)
      && wave.startTick >= demandPlan.startTick && wave.endTick <= demandPlan.endTick);
}

/** Validate the scalar/revision envelope before a worker handles a command. */
export function isGuestSimulationRequest(value: unknown): value is GuestSimulationRequest {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const request = value as Partial<GuestSimulationRequest> & Record<string, unknown>;
  if (request.version !== GUEST_SIMULATION_PROTOCOL_VERSION
    || !isNonEmptyString(request.requestId) || !isProtocolSequence(request.sequence)) return false;
  switch (request.type) {
    case 'initialize':
      return isNonEmptyString(request.runId) && isEnvironmentSnapshot(request.environment)
        && request.demandPlan?.version === GUEST_SIMULATION_CONTRACT_VERSION;
    case 'advance':
      return isSimulatedSecond(request.toTick)
        && isSimulationRevision(request.expectedEnvironmentRevision)
        && isSimulationRevision(request.expectedTopologyRevision);
    case 'snapshot':
    case 'save':
    case 'reset':
      return true;
    case 'load':
      return isSimulationRevision(request.expectedEnvironmentRevision)
        && isSimulationRevision(request.expectedTopologyRevision)
        && isNonEmptyString(request.expectedRunId)
        && Number.isSafeInteger(request.expectedConfigVersion) && (request.expectedConfigVersion as number) > 0
        && isNonEmptyString(request.expectedDemandSeed)
        && isNonEmptyString(request.expectedSnapshotChecksum)
        && isGuestSimulationSnapshot(request.snapshot)
        && request.snapshot.runId === request.expectedRunId
        && request.snapshot.configVersion === request.expectedConfigVersion
        && request.snapshot.demandPlan.seed === request.expectedDemandSeed
        && request.snapshot.checksum === request.expectedSnapshotChecksum;
    case 'cancel':
      return isNonEmptyString(request.cancelRequestId);
    default:
      return false;
  }
}

export function assertGuestSimulationRequest(value: unknown): asserts value is GuestSimulationRequest {
  if (!isGuestSimulationRequest(value)) throw new RangeError('Invalid guest simulation request');
}

export function isNormalizedUnitInterval(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1;
}

export function assertNormalizedUnitInterval(value: unknown, label = 'normalized value'): asserts value is number {
  if (!isNormalizedUnitInterval(value)) throw new RangeError(`${label} must be finite and within [0, 1]`);
}
