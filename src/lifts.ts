import { haversineMeters } from './geo';
import type { ChairSize, LiftStatus, SavedLift } from './types';

// Fixed-grip chairlift operating envelope. Carriers grip the haul rope
// permanently, so the whole line runs at loading speed — 450 ft/min (2.286 m/s)
// is a typical full-speed line. Chairs are evenly spaced so one passes each
// terminal every `headwayS` seconds, which fixes the hourly capacity.
export const FIXED_GRIP_SPEC = {
  ropeSpeedMps: 2.286, // 450 ft/min line speed
  headwayS: 6, // one chair passes a terminal every 6 s
  chairSizes: [2, 3, 4] as ChairSize[],
  defaultChairSize: 2 as ChairSize,
};

const M_TO_FT = 3.28084;

/** Human names for each carrier size, keyed by seat count. */
export const CHAIR_LABELS: Record<ChairSize, string> = {
  2: 'Double',
  3: 'Triple',
  4: 'Quad',
};

/** Fixed-grip hourly capacity: one chair of `seats` every headwayS seconds
 *  (Double 1,200 / Triple 1,800 / Quad 2,400 pph). */
export function fixedGripCapacityPph(chairSize: ChairSize): number {
  return (chairSize * 3600) / FIXED_GRIP_SPEC.headwayS; // 600 × seats
}

export interface LiftStats {
  horizontalM: number;
  /** Slope length: hypot(horizontal, vertical); horizontal-only when elevations unknown. */
  lengthM: number;
  verticalM: number | null;
  /** Index into points of the top terminal, or null while elevations unresolved. */
  topIndex: 0 | 1 | null;
}

export function liftStats(
  points: [[number, number], [number, number]],
  elevs: [number | null, number | null]
): LiftStats {
  const horizontalM = haversineMeters(points[0], points[1]);
  const [a, b] = elevs;
  if (a == null || b == null) {
    return { horizontalM, lengthM: horizontalM, verticalM: null, topIndex: null };
  }
  const verticalM = Math.abs(b - a);
  return {
    horizontalM,
    lengthM: Math.hypot(horizontalM, verticalM),
    verticalM,
    topIndex: b >= a ? 1 : 0,
  };
}

/** Reorder so index 0 is the bottom terminal. No-op while elevations are unknown. */
export function orientBottomToTop(
  points: [[number, number], [number, number]],
  elevs: [number | null, number | null]
): { points: [[number, number], [number, number]]; elevs: [number | null, number | null] } {
  const [a, b] = elevs;
  if (a != null && b != null && a > b) {
    return { points: [points[1], points[0]], elevs: [b, a] };
  }
  return { points, elevs };
}

export interface FixedGripDerived {
  headwayS: number; // seconds between carriers passing a terminal
  carrierSpacingM: number;
  carriersOnLine: number; // both directions of the loop
  rideTimeS: number;
}

export function fixedGripDerived(lengthM: number): FixedGripDerived {
  const headwayS = FIXED_GRIP_SPEC.headwayS;
  const carrierSpacingM = headwayS * FIXED_GRIP_SPEC.ropeSpeedMps;
  return {
    headwayS,
    carrierSpacingM,
    carriersOnLine: Math.ceil((2 * lengthM) / carrierSpacingM),
    rideTimeS: lengthM / FIXED_GRIP_SPEC.ropeSpeedMps,
  };
}

function isLngLat(p: unknown): p is [number, number] {
  return (
    Array.isArray(p) &&
    p.length === 2 &&
    typeof p[0] === 'number' &&
    typeof p[1] === 'number' &&
    Number.isFinite(p[0]) &&
    Number.isFinite(p[1])
  );
}

/**
 * Hydration shield for `GameSave.lifts`: drops anything that isn't a valid
 * two-point fixed-grip lift and recomputes the cached length/vertical from the
 * stored geometry so they can never drift from it.
 */
export function sanitizeLifts(raw: unknown[]): SavedLift[] {
  const out: SavedLift[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const l = item as Record<string, unknown>;
    if (l.liftClass !== 'fixed-grip') continue;
    if (typeof l.id !== 'string' || typeof l.name !== 'string') continue;
    if (!Array.isArray(l.points) || l.points.length !== 2) continue;
    if (!isLngLat(l.points[0]) || !isLngLat(l.points[1])) continue;
    const points: [[number, number], [number, number]] = [l.points[0], l.points[1]];
    const rawElevs = Array.isArray(l.endpointElevM) ? l.endpointElevM : [null, null];
    const elevs: [number | null, number | null] = [
      typeof rawElevs[0] === 'number' && Number.isFinite(rawElevs[0]) ? rawElevs[0] : null,
      typeof rawElevs[1] === 'number' && Number.isFinite(rawElevs[1]) ? rawElevs[1] : null,
    ];
    // Non-members (incl. legacy Single = 1) fall back to the default Double.
    const chairSize = FIXED_GRIP_SPEC.chairSizes.includes(l.chairSize as ChairSize)
      ? (l.chairSize as ChairSize)
      : FIXED_GRIP_SPEC.defaultChairSize;
    // Legacy saves predate `status`; treat an already-built lift as complete.
    const status: LiftStatus = l.status === 'planning' || l.status === 'complete' ? l.status : 'complete';
    const stats = liftStats(points, elevs);
    out.push({
      id: l.id,
      name: l.name,
      liftClass: 'fixed-grip',
      points,
      endpointElevM: elevs,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      chairSize,
      status,
      createdAt: typeof l.createdAt === 'string' ? l.createdAt : new Date().toISOString(),
    });
  }
  return out;
}

/** First "Lift N" not already taken. */
export function nextLiftName(existing: SavedLift[]): string {
  const taken = new Set(existing.map((l) => l.name));
  for (let n = 1; ; n++) {
    const name = `Lift ${n}`;
    if (!taken.has(name)) return name;
  }
}

export function fmtDistance(m: number, units: 'imperial' | 'metric'): string {
  return units === 'imperial'
    ? `${Math.round(m * M_TO_FT).toLocaleString()} ft`
    : `${Math.round(m).toLocaleString()} m`;
}
