/**
 * Map-layer adapter for the guest condition domain.
 *
 * `SkiNetwork` is the authoritative topology and `SnowGrid` is the optional
 * weather/snow state. This module only translates those sources into the
 * dependency-neutral condition input; it does not mutate either source or own
 * a condition domain. Callers that need an immutable snapshot should use
 * `conditionSnapshotFromSkiNetwork`, which delegates normalization, ordering,
 * freezing, and checksum generation to `createConditionSnapshot`.
 *
 * There is no grooming authoring surface yet. Until one exists, descent edges
 * use the explicit, deterministic `DEFAULT_DESCENT_GROOMING_QUALITY` (partial
 * grooming, 0.5). Connector paths and lifts use fully groomed safe defaults so
 * they do not make a route look harder merely because they are not ski runs.
 * Network open/closed state is intentionally not copied: the current
 * ConditionSnapshot contract has no closed field, so routing continues to use
 * the network/guest-domain closure inputs alongside this snapshot.
 */

import type { NetworkEdge, SkiNetwork } from '../network';
import { sampleSnowGrid } from '../snow';
import type { SnowGrid } from '../types/snow';
import type { TrailDifficulty } from '../types/trails';
import type { SimulatedSecond } from '../guestSimulation/contracts.ts';
import {
  createConditionSnapshot,
  type ConditionEdgeInput,
  type ConditionRevision,
  type ConditionSnapshot,
  type ConditionSnapshotInput,
} from '../guestSimulation/conditions.ts';

/** Normalized difficulty ratings shared with the guest network adapter. */
export const TRAIL_DIFFICULTY_RATINGS: Readonly<Record<TrailDifficulty, number>> = Object.freeze({
  green: 0.2,
  blue: 0.45,
  black: 0.7,
  red: 0.92,
});

/** Deterministic fallback while run grooming is not authored in the UI. */
export const DEFAULT_DESCENT_GROOMING_QUALITY = 0.5;

/** Connectors and lifts are safe transit surfaces, not skiable runs. */
export const DEFAULT_SAFE_GROOMING_QUALITY = 1;
export const DEFAULT_SAFE_SNOW_QUALITY = 1;
export const DEFAULT_SAFE_COVERAGE_FRACTION = 1;

/** A 2 cm threshold avoids treating trace numerical snow as skiable coverage. */
export const DEFAULT_COVERAGE_THRESHOLD_M = 0.02;

/** Uniform station count used for deterministic descent sampling. */
export const DEFAULT_DESCENT_SAMPLE_COUNT = 9;

/**
 * Surface-quality factors for the compact SnowGrid surface codes. Depth is
 * applied separately, so deep powder is better than trace powder while icy
 * surfaces remain poor regardless of depth.
 */
export const SNOW_SURFACE_QUALITY: Readonly<Record<number, number>> = Object.freeze({
  1: 0.96, // P  powder
  2: 0.9, // PP packed powder
  3: 0.94, // MG machine groomed
  4: 0.68, // HP hard packed
  5: 0.16, // IS icy surface
  6: 0.84, // CO corn snow
  7: 0.42, // FG frozen granular
  8: 0.64, // LG loose granular
  9: 0.58, // SC spring conditions
  10: 0.5, // WG wet granular
  11: 0.78, // WP wet powder
});

export interface GuestConditionAdapterOptions {
  /** Domain revision for this coherent source observation. Defaults to 0. */
  readonly revision?: ConditionRevision;
  /** Simulation tick associated with the observation. Defaults to 0. */
  readonly tick?: SimulatedSecond;
  /** Number of evenly spaced stations per descent edge, clamped to 2..65. */
  readonly sampleCount?: number;
  /** Fallback descent grooming while grooming authoring is unavailable. */
  readonly descentGroomingQuality?: number;
  /** Snow depth in metres required for a sample to count as covered. */
  readonly coverageThresholdM?: number;
}

export const DEFAULT_GUEST_CONDITION_ADAPTER_OPTIONS: Readonly<Required<GuestConditionAdapterOptions>> = Object.freeze({
  revision: 0,
  tick: 0,
  sampleCount: DEFAULT_DESCENT_SAMPLE_COUNT,
  descentGroomingQuality: DEFAULT_DESCENT_GROOMING_QUALITY,
  coverageThresholdM: DEFAULT_COVERAGE_THRESHOLD_M,
});

interface LngLatPoint { readonly lng: number; readonly lat: number; }

interface SnowSummary {
  readonly coverageFraction: number;
  readonly depthCm: number;
  readonly quality: number;
}

function compareEdgeIds(left: NetworkEdge, right: NetworkEdge): number {
  return left.id < right.id ? -1 : left.id > right.id ? 1 : 0;
}

function assertFinite(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new RangeError(`${label} must be finite`);
}

function assertUnit(value: number, label: string): void {
  assertFinite(value, label);
  if (value < 0 || value > 1) throw new RangeError(`${label} must be within [0, 1]`);
}

function assertNonNegative(value: number, label: string): void {
  assertFinite(value, label);
  if (value < 0) throw new RangeError(`${label} must be non-negative`);
}

function assertRevision(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

function sampleCount(value: number | undefined): number {
  const resolved = value ?? DEFAULT_DESCENT_SAMPLE_COUNT;
  if (!Number.isSafeInteger(resolved) || resolved < 2) throw new RangeError('condition descent sampleCount must be an integer >= 2');
  return Math.min(65, resolved);
}

function pointDistance(left: LngLatPoint, right: LngLatPoint): number {
  const meanLat = ((left.lat + right.lat) / 2) * Math.PI / 180;
  const dx = (right.lng - left.lng) * Math.cos(meanLat);
  const dy = right.lat - left.lat;
  return Math.hypot(dx, dy);
}

function validPoint(value: readonly number[]): value is [number, number] {
  return value.length >= 2 && Number.isFinite(value[0]) && Number.isFinite(value[1]);
}

/** Uniformly sample a polyline by its deterministic cumulative station length. */
function samplePath(path: readonly [number, number][], count: number): readonly [number, number][] {
  const points = path.filter(validPoint).map(([lng, lat]) => [lng, lat] as [number, number]);
  if (points.length === 0) return [];
  if (points.length === 1) return Array.from({ length: count }, () => [points[0]![0], points[0]![1]] as [number, number]);

  const cumulative = [0];
  for (let index = 1; index < points.length; index += 1) {
    const previous = points[index - 1]!;
    const current = points[index]!;
    cumulative.push(cumulative[index - 1]! + pointDistance({ lng: previous[0], lat: previous[1] }, { lng: current[0], lat: current[1] }));
  }
  const total = cumulative[cumulative.length - 1]!;
  if (total <= 0) return Array.from({ length: count }, () => [points[0]![0], points[0]![1]] as [number, number]);

  const samples: [number, number][] = [];
  for (let sample = 0; sample < count; sample += 1) {
    const target = total * sample / (count - 1);
    let segment = 0;
    while (segment < cumulative.length - 2 && cumulative[segment + 1]! < target) segment += 1;
    const start = points[segment]!;
    const end = points[segment + 1]!;
    const span = cumulative[segment + 1]! - cumulative[segment]!;
    const ratio = span > 0 ? (target - cumulative[segment]!) / span : 0;
    samples.push([start[0] + (end[0] - start[0]) * ratio, start[1] + (end[1] - start[1]) * ratio]);
  }
  return samples;
}

function depthQuality(depthM: number, surface: number): number {
  if (!(depthM > 0) || !(surface > 0)) return 0;
  const surfaceQuality = SNOW_SURFACE_QUALITY[surface] ?? 0.5;
  // 30 cm is the point at which additional base depth no longer improves the
  // quality score. The blend keeps thin but real snow distinguishable.
  const depthFactor = Math.min(1, Math.max(0, depthM) / 0.3);
  return surfaceQuality * (0.65 + depthFactor * 0.35);
}

function snowSummary(edge: Extract<NetworkEdge, { kind: 'trail' }>, snow: SnowGrid | null | undefined,
  countValue: number, thresholdM: number): SnowSummary {
  if (!snow) {
    return { coverageFraction: DEFAULT_SAFE_COVERAGE_FRACTION, depthCm: 0, quality: 0.5 };
  }
  const points = samplePath(edge.path, countValue);
  if (points.length === 0) return { coverageFraction: 0, depthCm: 0, quality: 0 };

  let covered = 0;
  let totalDepthM = 0;
  let totalQuality = 0;
  for (const [lng, lat] of points) {
    const sample = sampleSnowGrid(snow, lng, lat);
    const depthM = sample && Number.isFinite(sample.depthM) ? Math.max(0, sample.depthM) : 0;
    const surface = sample && Number.isFinite(sample.surface) ? sample.surface : 0;
    if (depthM >= thresholdM && surface > 0) covered += 1;
    totalDepthM += depthM;
    totalQuality += depthQuality(depthM, surface);
  }
  return { coverageFraction: covered / points.length, depthCm: totalDepthM / points.length * 100,
    quality: totalQuality / points.length };
}

function difficultyRating(difficulty: TrailDifficulty): number {
  return TRAIL_DIFFICULTY_RATINGS[difficulty];
}

function safeSurface(edge: Extract<NetworkEdge, { kind: 'path' | 'lift' }>): Pick<ConditionEdgeInput, 'grooming' | 'snowQuality' | 'coverage'> {
  // Keep the argument explicit: paths and lifts have no SnowGrid-derived run
  // condition and should not inherit a trail's sampled winter state.
  void edge;
  return { grooming: DEFAULT_SAFE_GROOMING_QUALITY, snowQuality: DEFAULT_SAFE_SNOW_QUALITY,
    coverage: { fraction: DEFAULT_SAFE_COVERAGE_FRACTION, depthCm: 0 } };
}

function edgeInput(edge: NetworkEdge, snow: SnowGrid | null | undefined, options: Required<GuestConditionAdapterOptions>): ConditionEdgeInput {
  if (edge.kind === 'trail') {
    const summary = snowSummary(edge, snow, options.sampleCount, options.coverageThresholdM);
    return { edgeId: edge.id, baseDifficulty: difficultyRating(edge.difficulty),
      grooming: options.descentGroomingQuality, snowQuality: summary.quality,
      coverage: { fraction: summary.coverageFraction, depthCm: summary.depthCm }, occupancy: { guests: 0, capacity: 1 } };
  }
  const surface = safeSurface(edge);
  return { edgeId: edge.id, baseDifficulty: edge.kind === 'path' ? 0.1 : 0,
    ...surface, occupancy: { guests: 0, capacity: 1 } };
}

function resolveOptions(options: GuestConditionAdapterOptions | undefined): Required<GuestConditionAdapterOptions> {
  const merged = { ...DEFAULT_GUEST_CONDITION_ADAPTER_OPTIONS, ...options };
  assertRevision(merged.revision, 'condition adapter revision');
  assertRevision(merged.tick, 'condition adapter tick');
  merged.sampleCount = sampleCount(merged.sampleCount);
  assertUnit(merged.descentGroomingQuality, 'condition descent grooming quality');
  assertNonNegative(merged.coverageThresholdM, 'condition coverage threshold');
  return merged;
}

/** Build canonical condition input from a network and optional snow grid. */
export function conditionSnapshotInputFromSkiNetwork(network: SkiNetwork, snow?: SnowGrid | null,
  options?: GuestConditionAdapterOptions): ConditionSnapshotInput {
  const resolved = resolveOptions(options);
  const edges = network.edges.slice().sort(compareEdgeIds).map((edge) => edgeInput(edge, snow, resolved));
  return { version: 1, revision: resolved.revision, tick: resolved.tick, edges };
}

/** Build the immutable, sorted, checksummed condition snapshot for the network. */
export function conditionSnapshotFromSkiNetwork(network: SkiNetwork, snow?: SnowGrid | null,
  options?: GuestConditionAdapterOptions): ConditionSnapshot {
  return createConditionSnapshot(conditionSnapshotInputFromSkiNetwork(network, snow, options));
}

/** Descriptive aliases for callers that prefer “build” or “create” wording. */
export const buildGuestConditionSnapshot = conditionSnapshotFromSkiNetwork;
export const createGuestConditionSnapshot = conditionSnapshotFromSkiNetwork;
export const buildGuestConditionSnapshotInput = conditionSnapshotInputFromSkiNetwork;
