/**
 * Phase 2 condition state for the guest simulation.
 *
 * This module intentionally knows nothing about the map renderer, weather
 * service, or the Phase 1 engine.  A caller supplies one record per network
 * edge and receives an owned, revisioned snapshot.  Updates are normalized
 * and sorted before they are applied, so replaying the same update set in a
 * different arrival order produces the same result.
 */

import { eventCalendarChecksum } from './eventCalendar.ts';
import type { SimulatedSecond } from './contracts.ts';

export const GUEST_CONDITION_DOMAIN_VERSION = 1 as const;
export const GUEST_SIMULATION_CONDITION_VERSION = GUEST_CONDITION_DOMAIN_VERSION;
export type GuestConditionDomainVersion = typeof GUEST_CONDITION_DOMAIN_VERSION;

export type ConditionRevision = number;
export type GroomingStatus = 'ungroomed' | 'partial' | 'groomed';
export type SnowQualityBand = 'poor' | 'fair' | 'good' | 'excellent';
export type CoverageStatus = 'bare' | 'thin' | 'adequate' | 'deep';
export type CrowdingLevel = 'empty' | 'light' | 'moderate' | 'crowded' | 'severe';
export type TerrainCharacter = 'bare' | 'icy' | 'groomed' | 'packed' | 'powder' | 'variable';

export interface GroomingSummary {
  readonly revision: ConditionRevision;
  /** 0 means no grooming; 1 means freshly/full groomed. */
  readonly quality: number;
  readonly status: GroomingStatus;
}

export interface SnowQualitySummary {
  readonly revision: ConditionRevision;
  /** 0 is unusable/icy snow; 1 is excellent snow. */
  readonly quality: number;
  readonly band: SnowQualityBand;
}

export interface CoverageSummary {
  readonly revision: ConditionRevision;
  /** Fraction of the edge that has skiable snow, in [0, 1]. */
  readonly fraction: number;
  /** Optional physical depth used by providers that have it. */
  readonly depthCm: number;
  readonly status: CoverageStatus;
}

export interface CrowdingSummary {
  readonly revision: ConditionRevision;
  /** Occupancy divided by capacity, intentionally not capped at 1. */
  readonly ratio: number;
  readonly level: CrowdingLevel;
}

export interface OccupancySummary {
  readonly revision: ConditionRevision;
  readonly guests: number;
  readonly capacity: number;
  /** Occupancy divided by capacity, intentionally not capped at 1. */
  readonly fraction: number;
  readonly crowding: CrowdingSummary;
}

export interface ConditionEdgeSnapshot {
  readonly edgeId: string;
  readonly revision: ConditionRevision;
  readonly baseDifficulty: number;
  readonly grooming: GroomingSummary;
  readonly snowQuality: SnowQualitySummary;
  readonly coverage: CoverageSummary;
  readonly occupancy: OccupancySummary;
  readonly crowding: CrowdingSummary;
  readonly effectiveDifficulty: number;
  readonly comfort: number;
  readonly terrainCharacter: TerrainCharacter;
}

export interface ConditionSnapshot {
  readonly version: GuestConditionDomainVersion;
  readonly revision: ConditionRevision;
  readonly tick: SimulatedSecond;
  readonly edges: readonly ConditionEdgeSnapshot[];
  readonly checksum: string;
}

export interface GroomingInput {
  readonly quality?: number;
  readonly score?: number;
  readonly status?: GroomingStatus;
}

export interface SnowQualityInput {
  readonly quality?: number;
  readonly score?: number;
  readonly band?: SnowQualityBand;
}

export interface CoverageInput {
  readonly fraction?: number;
  readonly coverageFraction?: number;
  readonly depthCm?: number;
  readonly status?: CoverageStatus;
}

export interface OccupancyInput {
  readonly guests?: number;
  readonly count?: number;
  readonly capacity?: number;
}

export interface ConditionEdgeInput {
  readonly edgeId?: string;
  /** `id` is accepted as a convenient adapter boundary alias. */
  readonly id?: string;
  readonly revision?: ConditionRevision;
  readonly baseDifficulty?: number;
  /** `difficulty` is accepted as an adapter boundary alias. */
  readonly difficulty?: number;
  readonly grooming?: number | GroomingInput;
  readonly snowQuality?: number | SnowQualityInput;
  readonly coverage?: number | CoverageInput;
  readonly occupancy?: number | OccupancyInput;
  readonly capacity?: number;
}

export interface ConditionSnapshotInput {
  readonly version?: GuestConditionDomainVersion;
  readonly revision?: ConditionRevision;
  readonly tick?: SimulatedSecond;
  readonly edges: readonly ConditionEdgeInput[];
}

export interface ConditionUpdate {
  readonly edgeId: string;
  /** Optimistic-concurrency guard for the edge being updated. */
  readonly expectedRevision?: ConditionRevision;
  readonly expectedEdgeRevision?: ConditionRevision;
  /** Explicit next edge revision; otherwise current edge revision + 1. */
  readonly revision?: ConditionRevision;
  /** Optional source order. Updates are sorted by this before their payload. */
  readonly sequence?: number;
  readonly updateOrder?: number;
  readonly grooming?: number | GroomingInput;
  readonly snowQuality?: number | SnowQualityInput;
  readonly coverage?: number | CoverageInput;
  readonly occupancy?: number | OccupancyInput;
  readonly capacity?: number;
  readonly baseDifficulty?: number;
}

export interface ConditionUpdateOptions {
  readonly tick?: SimulatedSecond;
  readonly expectedRevision?: ConditionRevision;
}

export interface ConditionAwareRouteSegment {
  readonly edgeId: string;
  readonly effectiveDifficulty: number;
  readonly comfort: number;
  readonly crowding: number;
  readonly coverage: number;
  readonly snowQuality: number;
  readonly grooming: number;
  readonly terrainCharacter: TerrainCharacter;
}

export interface ConditionAwareRouteScoringInputs {
  readonly edgeIds: readonly string[];
  readonly segments: readonly ConditionAwareRouteSegment[];
  readonly effectiveDifficulty: number;
  readonly minimumComfort: number;
  readonly averageComfort: number;
  readonly averageCrowding: number;
  readonly maximumCrowding: number;
  readonly coverage: number;
}

export interface RouteScorePreferences {
  readonly ability?: number;
  readonly targetDifficulty?: number;
  readonly comfortDemand?: number;
  readonly hardcoreTerrainPreference?: number;
  readonly crowdingSensitivity?: number;
}

export interface ConditionAwareRouteScore extends ConditionAwareRouteScoringInputs {
  readonly compatibility: number;
  readonly score: number;
  readonly canProceed: boolean;
}

export interface EffectiveDifficultyInput {
  readonly baseDifficulty: number;
  readonly grooming: number;
  readonly snowQuality: number;
  readonly coverage: number;
  readonly crowding?: number;
}

/** Compatibility aliases for adapters that use the shorter domain names. */
export type EdgeConditionSnapshot = ConditionEdgeSnapshot;
export type GuestConditionSnapshot = ConditionSnapshot;
export type EdgeConditionUpdate = ConditionUpdate;

export type ConditionValidationCode =
  | 'invalid-snapshot' | 'invalid-edge' | 'invalid-update' | 'stale-revision'
  | 'unknown-edge' | 'tick-regression' | 'checksum-mismatch';

export class ConditionValidationError extends RangeError {
  readonly code: ConditionValidationCode;

  constructor(code: ConditionValidationCode, message: string) {
    super(message);
    this.name = 'ConditionValidationError';
    this.code = code;
  }
}

function freezeArray<T>(values: readonly T[]): readonly T[] { return Object.freeze([...values]); }

function freezeObject<T extends object>(value: T): Readonly<T> { return Object.freeze(value); }

function assertRecord(value: unknown, label: string): asserts value is Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ConditionValidationError('invalid-edge', `${label} must be an object`);
  }
}

function assertText(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new ConditionValidationError('invalid-edge', `${label} must be a non-empty string`);
  }
}

function assertRevision(value: unknown, label: string): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new ConditionValidationError('invalid-snapshot', `${label} must be a non-negative safe integer`);
  }
}

function assertTick(value: unknown, label: string): asserts value is SimulatedSecond {
  if (!Number.isSafeInteger(value) || (value as number) < 0) {
    throw new ConditionValidationError('invalid-snapshot', `${label} must be a non-negative simulated second`);
  }
}

function assertFinite(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new ConditionValidationError('invalid-edge', `${label} must be finite`);
  }
}

function assertUnit(value: unknown, label: string): asserts value is number {
  assertFinite(value, label);
  if ((value as number) < 0 || (value as number) > 1) {
    throw new ConditionValidationError('invalid-edge', `${label} must be within [0, 1]`);
  }
}

function assertPositive(value: unknown, label: string): asserts value is number {
  assertFinite(value, label);
  if ((value as number) <= 0) throw new ConditionValidationError('invalid-edge', `${label} must be positive`);
}

function assertNonNegative(value: unknown, label: string): asserts value is number {
  assertFinite(value, label);
  if ((value as number) < 0) throw new ConditionValidationError('invalid-edge', `${label} must be non-negative`);
}

function compareText(left: string, right: string): number { return left < right ? -1 : left > right ? 1 : 0; }

function numberOr(value: unknown, fallback: number, label: string): number {
  if (value === undefined) return fallback;
  assertFinite(value, label);
  return value;
}

function objectField(value: unknown, keys: readonly string[], fallback: number, label: string): number {
  if (value === undefined) return fallback;
  if (typeof value === 'number') return numberOr(value, fallback, label);
  assertRecord(value, label);
  for (const key of keys) if (value[key] !== undefined) return numberOr(value[key], fallback, `${label}.${key}`);
  return fallback;
}

function groomingStatus(quality: number): GroomingStatus {
  return quality >= 0.66 ? 'groomed' : quality >= 0.33 ? 'partial' : 'ungroomed';
}

function snowBand(quality: number): SnowQualityBand {
  return quality >= 0.75 ? 'excellent' : quality >= 0.5 ? 'good' : quality >= 0.25 ? 'fair' : 'poor';
}

function coverageStatus(fraction: number): CoverageStatus {
  return fraction <= 0 ? 'bare' : fraction < 0.5 ? 'thin' : fraction < 0.9 ? 'adequate' : 'deep';
}

function crowdingLevel(ratio: number): CrowdingLevel {
  return ratio <= 0 ? 'empty' : ratio < 0.35 ? 'light' : ratio < 0.7 ? 'moderate' : ratio < 1 ? 'crowded' : 'severe';
}

function normalizeGrooming(value: number | GroomingInput | undefined, revision: number): GroomingSummary {
  const quality = objectField(value, ['quality', 'score'], 0.5, 'grooming');
  assertUnit(quality, 'grooming quality');
  const suppliedStatus = typeof value === 'object' && value !== null ? value.status : undefined;
  if (suppliedStatus !== undefined && !['ungroomed', 'partial', 'groomed'].includes(suppliedStatus)) {
    throw new ConditionValidationError('invalid-edge', 'grooming status is invalid');
  }
  const status = suppliedStatus ?? groomingStatus(quality);
  if (suppliedStatus !== undefined && status !== groomingStatus(quality)) {
    throw new ConditionValidationError('invalid-edge', 'grooming status does not match quality');
  }
  return freezeObject({ revision, quality, status });
}

function normalizeSnowQuality(value: number | SnowQualityInput | undefined, revision: number): SnowQualitySummary {
  const quality = objectField(value, ['quality', 'score'], 0.5, 'snowQuality');
  assertUnit(quality, 'snow quality');
  const suppliedBand = typeof value === 'object' && value !== null ? value.band : undefined;
  if (suppliedBand !== undefined && !['poor', 'fair', 'good', 'excellent'].includes(suppliedBand)) {
    throw new ConditionValidationError('invalid-edge', 'snow quality band is invalid');
  }
  const band = suppliedBand ?? snowBand(quality);
  if (suppliedBand !== undefined && band !== snowBand(quality)) {
    throw new ConditionValidationError('invalid-edge', 'snow quality band does not match quality');
  }
  return freezeObject({ revision, quality, band });
}

function normalizeCoverage(value: number | CoverageInput | undefined, revision: number): CoverageSummary {
  const fraction = objectField(value, ['fraction', 'coverageFraction'], 1, 'coverage');
  const depthCm = typeof value === 'object' && value !== null ? numberOr(value.depthCm, 0, 'coverage depthCm') : 0;
  assertUnit(fraction, 'coverage fraction');
  assertNonNegative(depthCm, 'coverage depthCm');
  const suppliedStatus = typeof value === 'object' && value !== null ? value.status : undefined;
  if (suppliedStatus !== undefined && !['bare', 'thin', 'adequate', 'deep'].includes(suppliedStatus)) {
    throw new ConditionValidationError('invalid-edge', 'coverage status is invalid');
  }
  const status = suppliedStatus ?? coverageStatus(fraction);
  if (suppliedStatus !== undefined && status !== coverageStatus(fraction)) {
    throw new ConditionValidationError('invalid-edge', 'coverage status does not match fraction');
  }
  return freezeObject({ revision, fraction, depthCm, status });
}

function normalizeOccupancy(value: number | OccupancyInput | undefined, capacityValue: unknown, revision: number): OccupancySummary {
  const objectCapacity = typeof value === 'object' && value !== null ? value.capacity : undefined;
  const capacity = numberOr(objectCapacity ?? capacityValue, 1, 'occupancy capacity');
  const guests = objectField(value, ['guests', 'count'], 0, 'occupancy');
  assertNonNegative(guests, 'occupancy guests');
  assertPositive(capacity, 'occupancy capacity');
  if (!Number.isSafeInteger(guests)) throw new ConditionValidationError('invalid-edge', 'occupancy guests must be an integer');
  const fraction = guests / capacity;
  const crowding = freezeObject({ revision, ratio: fraction, level: crowdingLevel(fraction) });
  return freezeObject({ revision, guests, capacity, fraction, crowding });
}

function terrainCharacter(grooming: number, snowQuality: number, coverage: number): TerrainCharacter {
  if (coverage <= 0.05) return 'bare';
  if (snowQuality < 0.25) return 'icy';
  if (snowQuality >= 0.78 && grooming < 0.4) return 'powder';
  if (grooming >= 0.66) return 'groomed';
  if (grooming >= 0.35 && snowQuality >= 0.35) return 'packed';
  return 'variable';
}

function comfortFor(difficulty: number, grooming: number, snowQuality: number, coverage: number, crowding: number): number {
  return Math.max(0, Math.min(1, 0.55 + grooming * 0.16 + snowQuality * 0.2 + coverage * 0.12
    - Math.min(1, crowding) * 0.2 - difficulty * 0.14));
}

/** Compute the condition-adjusted difficulty used by route decisions. */
export function effectiveDifficulty(input: EffectiveDifficultyInput): number;
export function effectiveDifficulty(baseDifficulty: number, grooming: number, snowQuality: number, coverage: number, crowding?: number): number;
export function effectiveDifficulty(
  baseOrInput: number | EffectiveDifficultyInput,
  groomingValue?: number,
  snowQualityValue?: number,
  coverageValue?: number,
  crowdingValue = 0,
): number {
  const baseDifficulty = typeof baseOrInput === 'number' ? baseOrInput : baseOrInput.baseDifficulty;
  const grooming = typeof baseOrInput === 'number' ? groomingValue : baseOrInput.grooming;
  const snowQuality = typeof baseOrInput === 'number' ? snowQualityValue : baseOrInput.snowQuality;
  const coverage = typeof baseOrInput === 'number' ? coverageValue : baseOrInput.coverage;
  const crowding = typeof baseOrInput === 'number' ? crowdingValue : baseOrInput.crowding ?? 0;
  if (grooming === undefined || snowQuality === undefined || coverage === undefined) {
    throw new ConditionValidationError('invalid-edge', 'effective difficulty requires grooming, snow quality, and coverage');
  }
  assertUnit(baseDifficulty, 'base difficulty');
  assertUnit(grooming, 'grooming quality');
  assertUnit(snowQuality, 'snow quality');
  assertUnit(coverage, 'coverage fraction');
  assertNonNegative(crowding, 'crowding ratio');
  const penalty = (1 - grooming) * 0.12 + (1 - snowQuality) * 0.2 + (1 - coverage) * 0.3 + Math.min(1, crowding) * 0.08;
  return Math.max(0, Math.min(1, baseDifficulty + penalty));
}

function edgeSnapshot(input: ConditionEdgeInput, fallbackRevision: number): ConditionEdgeSnapshot {
  assertRecord(input, 'condition edge');
  const edgeInput = input as unknown as ConditionEdgeInput;
  const edgeId = edgeInput.edgeId ?? edgeInput.id;
  assertText(edgeId, 'edgeId');
  const revision = edgeInput.revision ?? fallbackRevision;
  assertRevision(revision, `edge ${edgeId} revision`);
  const baseDifficulty = numberOr(edgeInput.baseDifficulty ?? edgeInput.difficulty, 0.5, `edge ${edgeId} baseDifficulty`);
  assertUnit(baseDifficulty, `edge ${edgeId} baseDifficulty`);
  const grooming = normalizeGrooming(edgeInput.grooming, revision);
  const snowQuality = normalizeSnowQuality(edgeInput.snowQuality, revision);
  const coverage = normalizeCoverage(edgeInput.coverage, revision);
  const occupancy = normalizeOccupancy(edgeInput.occupancy, edgeInput.capacity, revision);
  const crowding = occupancy.crowding;
  const difficulty = effectiveDifficulty(baseDifficulty, grooming.quality, snowQuality.quality, coverage.fraction, crowding.ratio);
  const comfort = comfortFor(difficulty, grooming.quality, snowQuality.quality, coverage.fraction, crowding.ratio);
  return freezeObject({ edgeId, revision, baseDifficulty, grooming, snowQuality, coverage, occupancy, crowding,
    effectiveDifficulty: difficulty, comfort, terrainCharacter: terrainCharacter(grooming.quality, snowQuality.quality, coverage.fraction) });
}

function conditionProjection(snapshot: Omit<ConditionSnapshot, 'checksum'>): unknown {
  return { version: snapshot.version, revision: snapshot.revision, tick: snapshot.tick, edges: snapshot.edges };
}

/** Compute a stable checksum for a condition projection or snapshot. */
export function conditionSnapshotChecksum(snapshot: Pick<ConditionSnapshot, 'version' | 'revision' | 'tick' | 'edges'>): string {
  return eventCalendarChecksum(conditionProjection(snapshot as Omit<ConditionSnapshot, 'checksum'>));
}

export const conditionsChecksum = conditionSnapshotChecksum;

function snapshotFromEdges(version: GuestConditionDomainVersion, revision: number, tick: number, edges: readonly ConditionEdgeSnapshot[]): ConditionSnapshot {
  const ordered = edges.slice().sort((left, right) => compareText(left.edgeId, right.edgeId));
  const base = { version, revision, tick, edges: freezeArray(ordered) } satisfies Omit<ConditionSnapshot, 'checksum'>;
  return freezeObject({ ...base, checksum: conditionSnapshotChecksum(base) });
}

/** Create an owned, sorted, immutable condition snapshot. */
export function createConditionSnapshot(input: ConditionSnapshotInput): ConditionSnapshot {
  assertRecord(input, 'condition snapshot');
  const version = input.version ?? GUEST_CONDITION_DOMAIN_VERSION;
  if (version !== GUEST_CONDITION_DOMAIN_VERSION) throw new ConditionValidationError('invalid-snapshot', `unsupported condition version ${String(version)}`);
  const revision = input.revision ?? 0;
  const tick = input.tick ?? 0;
  assertRevision(revision, 'condition snapshot revision');
  assertTick(tick, 'condition snapshot tick');
  if (!Array.isArray(input.edges)) throw new ConditionValidationError('invalid-snapshot', 'condition snapshot edges must be an array');
  const edges = input.edges.map((edge) => edgeSnapshot(edge, revision));
  const seen = new Set<string>();
  for (const edge of edges) {
    if (seen.has(edge.edgeId)) throw new ConditionValidationError('invalid-edge', `duplicate condition edge ${edge.edgeId}`);
    seen.add(edge.edgeId);
  }
  return snapshotFromEdges(version, revision, tick, edges);
}

function updateOrder(update: ConditionUpdate): number {
  const value = update.sequence ?? update.updateOrder;
  if (value === undefined) return Number.MAX_SAFE_INTEGER;
  assertRevision(value, 'condition update sequence');
  return value;
}

function updatePayloadKey(update: ConditionUpdate): string {
  return JSON.stringify({ baseDifficulty: update.baseDifficulty, capacity: update.capacity, coverage: update.coverage,
    grooming: update.grooming, occupancy: update.occupancy, snowQuality: update.snowQuality });
}

/** Return updates in their canonical, replay-safe order. */
export function sortConditionUpdates(updates: readonly ConditionUpdate[]): readonly ConditionUpdate[] {
  if (!Array.isArray(updates)) throw new ConditionValidationError('invalid-update', 'condition updates must be an array');
  const copy = updates.slice();
  copy.forEach(assertConditionUpdate);
  return freezeArray(copy.sort((left, right) => compareText(left.edgeId, right.edgeId)
    || updateOrder(left) - updateOrder(right) || compareText(updatePayloadKey(left), updatePayloadKey(right))));
}

/** Validate an update before it reaches a mutable adapter. */
export function assertConditionUpdate(value: unknown): asserts value is ConditionUpdate {
  assertRecord(value, 'condition update');
  const update = value as unknown as ConditionUpdate;
  assertText(update.edgeId, 'condition update edgeId');
  if (update.expectedRevision !== undefined) assertRevision(update.expectedRevision, 'condition update expectedRevision');
  if (update.expectedEdgeRevision !== undefined) assertRevision(update.expectedEdgeRevision, 'condition update expectedEdgeRevision');
  if (update.revision !== undefined) assertRevision(update.revision, 'condition update revision');
  if (update.sequence !== undefined) assertRevision(update.sequence, 'condition update sequence');
  if (update.updateOrder !== undefined) assertRevision(update.updateOrder, 'condition update updateOrder');
  if (update.baseDifficulty !== undefined) assertUnit(update.baseDifficulty, 'condition update baseDifficulty');
  if (update.capacity !== undefined) assertPositive(update.capacity, 'condition update capacity');
  if (update.grooming !== undefined) normalizeGrooming(update.grooming, 0);
  if (update.snowQuality !== undefined) normalizeSnowQuality(update.snowQuality, 0);
  if (update.coverage !== undefined) normalizeCoverage(update.coverage, 0);
  if (update.occupancy !== undefined) normalizeOccupancy(update.occupancy, update.capacity, 0);
  if (update.grooming === undefined && update.snowQuality === undefined && update.coverage === undefined
    && update.occupancy === undefined && update.capacity === undefined && update.baseDifficulty === undefined) {
    throw new ConditionValidationError('invalid-update', `condition update ${update.edgeId} has no changed fields`);
  }
}

export function isConditionUpdate(value: unknown): value is ConditionUpdate {
  try { assertConditionUpdate(value); return true; } catch { return false; }
}

function patchedEdge(edge: ConditionEdgeSnapshot, update: ConditionUpdate): ConditionEdgeSnapshot {
  const nextRevision = update.revision ?? edge.revision + 1;
  if (nextRevision !== edge.revision + 1) throw new ConditionValidationError('stale-revision', `edge ${edge.edgeId} revision must advance by one`);
  const baseDifficulty = update.baseDifficulty ?? edge.baseDifficulty;
  // Every per-edge summary carries the edge revision.  Re-normalizing
  // unchanged values is deliberate: a capacity-only update is still a new
  // coherent edge observation, not a mixture of old and new revisions.
  const grooming = normalizeGrooming(update.grooming ?? edge.grooming, nextRevision);
  const snowQuality = normalizeSnowQuality(update.snowQuality ?? edge.snowQuality, nextRevision);
  const coverage = normalizeCoverage(update.coverage ?? edge.coverage, nextRevision);
  const occupancy = update.occupancy === undefined && update.capacity === undefined ? edge.occupancy
    : normalizeOccupancy(update.occupancy, update.capacity ?? edge.occupancy.capacity, nextRevision);
  const revisedOccupancy = occupancy.revision === nextRevision ? occupancy
    : normalizeOccupancy(occupancy, occupancy.capacity, nextRevision);
  const crowding = revisedOccupancy.crowding;
  const difficulty = effectiveDifficulty(baseDifficulty, grooming.quality, snowQuality.quality, coverage.fraction, crowding.ratio);
  const comfort = comfortFor(difficulty, grooming.quality, snowQuality.quality, coverage.fraction, crowding.ratio);
  return freezeObject({ edgeId: edge.edgeId, revision: nextRevision, baseDifficulty, grooming, snowQuality, coverage, occupancy: revisedOccupancy, crowding,
    effectiveDifficulty: difficulty, comfort, terrainCharacter: terrainCharacter(grooming.quality, snowQuality.quality, coverage.fraction) });
}

/** Apply a canonical batch and return a new immutable snapshot. */
export function applyConditionUpdates(snapshot: ConditionSnapshot, updates: readonly ConditionUpdate[], options: ConditionUpdateOptions | SimulatedSecond = {}): ConditionSnapshot {
  assertConditionSnapshot(snapshot);
  const updateList = sortConditionUpdates(updates);
  if (updateList.length === 0) return snapshot;
  const tick = typeof options === 'number' ? options : options.tick ?? snapshot.tick;
  assertTick(tick, 'condition update tick');
  if (tick < snapshot.tick) throw new ConditionValidationError('tick-regression', 'condition update tick cannot move backwards');
  if (typeof options !== 'number' && options.expectedRevision !== undefined && options.expectedRevision !== snapshot.revision) {
    throw new ConditionValidationError('stale-revision', `expected condition revision ${options.expectedRevision}, got ${snapshot.revision}`);
  }
  const byId = new Map(snapshot.edges.map((edge) => [edge.edgeId, edge]));
  for (const update of updateList) {
    const current = byId.get(update.edgeId);
    if (!current) throw new ConditionValidationError('unknown-edge', `unknown condition edge ${update.edgeId}`);
    const expected = update.expectedRevision ?? update.expectedEdgeRevision;
    if (expected !== undefined && expected !== current.revision) {
      throw new ConditionValidationError('stale-revision', `expected edge ${update.edgeId} revision ${expected}, got ${current.revision}`);
    }
    byId.set(update.edgeId, patchedEdge(current, update));
  }
  const nextGlobalRevision = snapshot.revision + 1;
  assertRevision(nextGlobalRevision, 'next condition snapshot revision');
  return snapshotFromEdges(snapshot.version, nextGlobalRevision, tick, [...byId.values()]);
}

export const updateConditionSnapshot = applyConditionUpdates;
export const applyConditionBatch = applyConditionUpdates;

function validateSummaryRevision(edge: ConditionEdgeSnapshot): void {
  for (const [label, summary] of [['grooming', edge.grooming], ['snowQuality', edge.snowQuality], ['coverage', edge.coverage],
    ['occupancy', edge.occupancy], ['crowding', edge.crowding]] as const) {
    if (summary.revision !== edge.revision) throw new ConditionValidationError('invalid-edge', `${edge.edgeId} ${label} revision is inconsistent`);
  }
}

/** Strong runtime validation, including the deterministic checksum. */
export function assertConditionSnapshot(value: unknown): asserts value is ConditionSnapshot {
  assertRecord(value, 'condition snapshot');
  if (value.version !== GUEST_CONDITION_DOMAIN_VERSION) throw new ConditionValidationError('invalid-snapshot', 'unsupported condition snapshot version');
  assertRevision(value.revision, 'condition snapshot revision');
  assertTick(value.tick, 'condition snapshot tick');
  if (!Array.isArray(value.edges) || typeof value.checksum !== 'string' || value.checksum.length === 0) {
    throw new ConditionValidationError('invalid-snapshot', 'condition snapshot envelope is invalid');
  }
  const seen = new Set<string>();
  let priorEdgeId: string | undefined;
  for (const rawEdge of value.edges) {
    assertRecord(rawEdge, 'condition edge');
    const edge = rawEdge as unknown as ConditionEdgeSnapshot;
    assertText(edge.edgeId, 'condition edge edgeId');
    if (priorEdgeId !== undefined && compareText(priorEdgeId, edge.edgeId) >= 0) {
      throw new ConditionValidationError('invalid-snapshot', 'condition edges must be sorted by edgeId');
    }
    priorEdgeId = edge.edgeId;
    if (seen.has(edge.edgeId)) throw new ConditionValidationError('invalid-edge', `duplicate condition edge ${edge.edgeId}`);
    seen.add(edge.edgeId);
    assertRevision(edge.revision, `edge ${edge.edgeId} revision`);
    assertUnit(edge.baseDifficulty, `edge ${edge.edgeId} baseDifficulty`);
    assertRecord(edge.grooming, `edge ${edge.edgeId} grooming`);
    assertRecord(edge.snowQuality, `edge ${edge.edgeId} snowQuality`);
    assertRecord(edge.coverage, `edge ${edge.edgeId} coverage`);
    assertRecord(edge.occupancy, `edge ${edge.edgeId} occupancy`);
    assertRecord(edge.crowding, `edge ${edge.edgeId} crowding`);
    validateSummaryRevision(edge);
    assertUnit(edge.grooming.quality, `edge ${edge.edgeId} grooming quality`);
    if (!['ungroomed', 'partial', 'groomed'].includes(edge.grooming.status)
      || edge.grooming.status !== groomingStatus(edge.grooming.quality)) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} grooming summary is inconsistent`);
    }
    assertUnit(edge.snowQuality.quality, `edge ${edge.edgeId} snow quality`);
    if (!['poor', 'fair', 'good', 'excellent'].includes(edge.snowQuality.band)
      || edge.snowQuality.band !== snowBand(edge.snowQuality.quality)) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} snow quality summary is inconsistent`);
    }
    assertUnit(edge.coverage.fraction, `edge ${edge.edgeId} coverage fraction`);
    assertNonNegative(edge.coverage.depthCm, `edge ${edge.edgeId} coverage depthCm`);
    if (!['bare', 'thin', 'adequate', 'deep'].includes(edge.coverage.status)
      || edge.coverage.status !== coverageStatus(edge.coverage.fraction)) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} coverage summary is inconsistent`);
    }
    assertNonNegative(edge.occupancy.guests, `edge ${edge.edgeId} occupancy guests`);
    assertPositive(edge.occupancy.capacity, `edge ${edge.edgeId} occupancy capacity`);
    if (!Number.isSafeInteger(edge.occupancy.guests)
      || edge.occupancy.fraction !== edge.occupancy.guests / edge.occupancy.capacity) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} occupancy summary is inconsistent`);
    }
    assertNonNegative(edge.crowding.ratio, `edge ${edge.edgeId} crowding ratio`);
    if (edge.crowding.ratio !== edge.occupancy.fraction
      || !['empty', 'light', 'moderate', 'crowded', 'severe'].includes(edge.crowding.level)
      || edge.crowding.level !== crowdingLevel(edge.crowding.ratio)) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} crowding summary is inconsistent`);
    }
    assertUnit(edge.effectiveDifficulty, `edge ${edge.edgeId} effective difficulty`);
    assertUnit(edge.comfort, `edge ${edge.edgeId} comfort`);
    const difficulty = effectiveDifficulty(edge.baseDifficulty, edge.grooming.quality, edge.snowQuality.quality,
      edge.coverage.fraction, edge.crowding.ratio);
    if (edge.effectiveDifficulty !== difficulty || edge.comfort !== comfortFor(difficulty, edge.grooming.quality,
      edge.snowQuality.quality, edge.coverage.fraction, edge.crowding.ratio)
      || edge.terrainCharacter !== terrainCharacter(edge.grooming.quality, edge.snowQuality.quality, edge.coverage.fraction)) {
      throw new ConditionValidationError('invalid-edge', `edge ${edge.edgeId} derived attributes are inconsistent`);
    }
  }
  const expected = conditionSnapshotChecksum({ version: value.version, revision: value.revision, tick: value.tick, edges: value.edges });
  if (value.checksum !== expected) throw new ConditionValidationError('checksum-mismatch', 'condition snapshot checksum does not match payload');
}

export function isConditionSnapshot(value: unknown): value is ConditionSnapshot {
  try { assertConditionSnapshot(value); return true; } catch { return false; }
}

export const validateConditionSnapshot = assertConditionSnapshot;

/** Produce the condition fields consumed by a route scorer. */
export function conditionAwareRouteScoringInputs(snapshot: ConditionSnapshot, edgeIds: readonly string[]): ConditionAwareRouteScoringInputs {
  assertConditionSnapshot(snapshot);
  if (!Array.isArray(edgeIds) || edgeIds.length === 0) throw new ConditionValidationError('unknown-edge', 'a route must contain at least one edge');
  const byId = new Map(snapshot.edges.map((edge) => [edge.edgeId, edge]));
  const seen = new Set<string>();
  const segments = edgeIds.map((edgeId) => {
    assertText(edgeId, 'route edgeId');
    if (seen.has(edgeId)) throw new ConditionValidationError('invalid-edge', `route repeats edge ${edgeId}`);
    seen.add(edgeId);
    const edge = byId.get(edgeId);
    if (!edge) throw new ConditionValidationError('unknown-edge', `unknown route edge ${edgeId}`);
    return freezeObject({ edgeId, effectiveDifficulty: edge.effectiveDifficulty, comfort: edge.comfort,
      crowding: edge.crowding.ratio, coverage: edge.coverage.fraction, snowQuality: edge.snowQuality.quality,
      grooming: edge.grooming.quality, terrainCharacter: edge.terrainCharacter });
  });
  const effective = segments.reduce((sum, segment) => sum + segment.effectiveDifficulty, 0) / segments.length;
  const comforts = segments.map((segment) => segment.comfort);
  const crowding = segments.map((segment) => segment.crowding);
  return freezeObject({ edgeIds: freezeArray(edgeIds), segments: freezeArray(segments), effectiveDifficulty: effective,
    minimumComfort: Math.min(...comforts), averageComfort: comforts.reduce((sum, value) => sum + value, 0) / comforts.length,
    averageCrowding: crowding.reduce((sum, value) => sum + value, 0) / crowding.length, maximumCrowding: Math.max(...crowding),
    coverage: segments.reduce((sum, segment) => sum + segment.coverage, 0) / segments.length });
}

export const getConditionAwareRouteScoringInputs = conditionAwareRouteScoringInputs;
export const routeConditionInputs = conditionAwareRouteScoringInputs;

/** Score a route using condition-aware difficulty, comfort, and crowding. */
export function scoreConditionAwareRoute(snapshot: ConditionSnapshot, edgeIds: readonly string[], preferences: RouteScorePreferences = {}): ConditionAwareRouteScore {
  const inputs = conditionAwareRouteScoringInputs(snapshot, edgeIds);
  const ability = preferences.ability ?? 1;
  const target = preferences.targetDifficulty ?? ability;
  const comfortDemand = preferences.comfortDemand ?? 0.5;
  const hardcore = preferences.hardcoreTerrainPreference ?? 0.5;
  const crowdingSensitivity = preferences.crowdingSensitivity ?? 0.5;
  assertUnit(ability, 'route preference ability');
  assertUnit(target, 'route preference target difficulty');
  assertUnit(comfortDemand, 'route preference comfort demand');
  assertUnit(hardcore, 'route preference hardcore terrain preference');
  assertUnit(crowdingSensitivity, 'route preference crowding sensitivity');
  const compatibility = Math.max(0, Math.min(1, 1 - Math.max(0, inputs.effectiveDifficulty - ability) * 2));
  const difficultyFit = Math.max(0, 1 - Math.abs(inputs.effectiveDifficulty - target));
  const comfortFit = inputs.averageComfort * (0.5 + comfortDemand * 0.5);
  const terrainFit = inputs.segments.reduce((sum, segment) => sum + (segment.terrainCharacter === 'powder' || segment.terrainCharacter === 'icy' ? hardcore : 1 - hardcore), 0) / inputs.segments.length;
  const score = Math.max(0, Math.min(1, compatibility * (difficultyFit * 0.4 + comfortFit * 0.4
    + terrainFit * 0.2) * (1 - Math.min(1, inputs.averageCrowding) * crowdingSensitivity * 0.35)));
  // Ability mismatch is a scored preference and Phase 4 hazard input, not a
  // hard routing barrier. Only materially uncovered terrain blocks travel.
  return freezeObject({ ...inputs, compatibility, score, canProceed: inputs.coverage >= 0.2 });
}

export const evaluateConditionAwareRoute = scoreConditionAwareRoute;

/** A small stateful facade for worker/adaptor owners that prefer commands. */
export class ConditionDomain {
  private current: ConditionSnapshot;

  constructor(input: ConditionSnapshotInput | ConditionSnapshot) {
    this.current = isConditionSnapshot(input) ? input : createConditionSnapshot(input);
  }

  get snapshot(): ConditionSnapshot { return this.current; }
  getSnapshot(): ConditionSnapshot { return this.current; }

  update(updates: readonly ConditionUpdate[], options: ConditionUpdateOptions | SimulatedSecond = {}): ConditionSnapshot {
    this.current = applyConditionUpdates(this.current, updates, options);
    return this.current;
  }

  apply(updates: readonly ConditionUpdate[], options: ConditionUpdateOptions | SimulatedSecond = {}): ConditionSnapshot {
    return this.update(updates, options);
  }
}

export function createConditionDomain(input: ConditionSnapshotInput | ConditionSnapshot): ConditionDomain {
  return new ConditionDomain(input);
}

export const createGuestConditionDomain = createConditionDomain;
