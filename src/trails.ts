import { haversineMeters } from './geo';
import type { SavedTrail, SavedTrailPart, TrailDifficulty, TrailStatus } from './types';

// Pure ski-run helpers: difficulty grading, spine geometry stats, and the
// hydration shield for GameSave.trails. No DOM / fetch here — the brush→polygon
// rasterization lives in src/app/trailBrush.ts, and terrain sampling in the
// MapView. Everything here is a pure function of stored geometry, so it is
// cheap to unit test and safe to recompute on every load.

const M_TO_FT = 3.28084;

export const TRAIL_DIFFICULTIES: TrailDifficulty[] = ['green', 'blue', 'black', 'red'];

/** Human labels. "Red" is this app's expert band (≥37°), matching the slope
 *  legend in terrainProtocols.ts rather than the North-American "double black". */
export const DIFFICULTY_LABELS: Record<TrailDifficulty, string> = {
  green: 'Green',
  blue: 'Blue',
  black: 'Black',
  red: 'Expert',
};

/** Trail-map symbol per grade (● ■ ◆ ◆◆) — the classic circle/square/diamond. */
export const DIFFICULTY_SYMBOL: Record<TrailDifficulty, string> = {
  green: '●',
  blue: '■',
  black: '◆',
  red: '◆◆',
};

/** Fill/label colors, matched to the slope-overlay bands in terrainProtocols.ts
 *  so a run's grade reads the same as the shaded terrain under it. */
export const DIFFICULTY_COLORS: Record<TrailDifficulty, string> = {
  green: '#43a047', // rgb(67,160,71)
  blue: '#1e88e5', // rgb(30,136,229)
  black: '#212121', // rgb(33,33,33)
  red: '#d32f2f', // rgb(211,47,47)
};

// Upper slope-angle bound (exclusive) for each grade. Mirrors SLOPE_BANDS in
// terrainProtocols.ts; kept here as plain data so this module stays DOM-free.
const DIFFICULTY_BANDS: { max: number; d: TrailDifficulty }[] = [
  { max: 16, d: 'green' },
  { max: 24, d: 'blue' },
  { max: 37, d: 'black' },
  { max: Infinity, d: 'red' },
];

function bandFor(slopeDeg: number): TrailDifficulty {
  for (const b of DIFFICULTY_BANDS) if (slopeDeg < b.max) return b.d;
  return 'red';
}

/**
 * Sustained pitch counts three times as much as the steepest single segment.
 */
export function difficultyForSlopes(avgSlopeDeg: number, maxSlopeDeg: number): TrailDifficulty {
  return bandFor((3 * avgSlopeDeg + maxSlopeDeg) / 4);
}

export interface TrailStats {
  /** Slope length: sum of 3D segment lengths; horizontal-only when elevations
   *  are unknown. */
  lengthM: number;
  /** max − min station elevation along the spine; null while unresolved. */
  verticalM: number | null;
  /** Run-length-weighted mean pitch (degrees); 0 when elevations are unknown. */
  avgSlopeDeg: number;
  /** Steepest segment pitch (degrees); 0 when elevations are unknown. */
  maxSlopeDeg: number;
}

/**
 * Length/vertical/slope of a run from its spine and (optionally) the elevations
 * sampled at each station. When elevations are missing or mismatched, falls
 * back to horizontal length only with zero slope.
 */
export function trailStats(spine: [number, number][], elevM: number[]): TrailStats {
  if (spine.length < 2) {
    return { lengthM: 0, verticalM: null, avgSlopeDeg: 0, maxSlopeDeg: 0 };
  }
  const haveElev = elevM.length === spine.length && elevM.every((e) => Number.isFinite(e));

  let lengthM = 0;
  let weightedSlope = 0; // Σ slopeDeg · horizontalRun
  let horizTotal = 0;
  let maxSlopeDeg = 0;

  for (let i = 0; i < spine.length - 1; i++) {
    const horiz = haversineMeters(spine[i], spine[i + 1]);
    if (!haveElev) {
      lengthM += horiz;
      horizTotal += horiz;
      continue;
    }
    const dz = elevM[i + 1] - elevM[i];
    lengthM += Math.hypot(horiz, dz);
    const slopeDeg = horiz > 0 ? (Math.atan(Math.abs(dz) / horiz) * 180) / Math.PI : 0;
    weightedSlope += slopeDeg * horiz;
    horizTotal += horiz;
    if (slopeDeg > maxSlopeDeg) maxSlopeDeg = slopeDeg;
  }

  const verticalM = haveElev ? Math.max(...elevM) - Math.min(...elevM) : null;
  const avgSlopeDeg = haveElev && horizTotal > 0 ? weightedSlope / horizTotal : 0;
  return { lengthM, verticalM, avgSlopeDeg, maxSlopeDeg };
}

export function trailPartsStats(parts: SavedTrailPart[]): TrailStats {
  let lengthM = 0;
  let weightedSlope = 0;
  let horizTotal = 0;
  let maxSlopeDeg = 0;
  let allResolved = parts.length > 0;
  const elevations: number[] = [];
  for (const part of parts) {
    const resolved = part.centerlineElevM.length === part.centerline.length &&
      part.centerlineElevM.every(Number.isFinite);
    if (!resolved) allResolved = false;
    if (resolved) elevations.push(...part.centerlineElevM);
    for (let i = 1; i < part.centerline.length; i++) {
      const horiz = haversineMeters(part.centerline[i - 1], part.centerline[i]);
      const dz = resolved ? part.centerlineElevM[i] - part.centerlineElevM[i - 1] : 0;
      const slope = resolved && horiz > 0 ? Math.atan(Math.abs(dz) / horiz) * 180 / Math.PI : 0;
      lengthM += resolved ? Math.hypot(horiz, dz) : horiz;
      weightedSlope += slope * horiz;
      horizTotal += horiz;
      maxSlopeDeg = Math.max(maxSlopeDeg, slope);
    }
  }
  return {
    lengthM,
    verticalM: allResolved && elevations.length > 0
      ? Math.max(...elevations) - Math.min(...elevations) : null,
    avgSlopeDeg: allResolved && horizTotal > 0 ? weightedSlope / horizTotal : 0,
    maxSlopeDeg,
  };
}

export function trailAreaM2(parts: Pick<SavedTrailPart, 'polygon'>[]): number {
  let total = 0;
  for (const { polygon } of parts) {
    if (polygon.length === 0) continue;
    const latRef = polygon[0].reduce((sum, p) => sum + p[1], 0) / polygon[0].length;
    const metersLng = 111_320 * Math.cos(latRef * Math.PI / 180);
    const ringArea = (ring: [number, number][]) => {
      let sum = 0;
      for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        sum += ring[j][0] * metersLng * ring[i][1] * 111_320 -
          ring[i][0] * metersLng * ring[j][1] * 111_320;
      }
      return Math.abs(sum) / 2;
    };
    total += ringArea(polygon[0]);
    for (let i = 1; i < polygon.length; i++) total -= ringArea(polygon[i]);
  }
  return Math.max(0, total);
}

/** Reorder the spine (and its elevations) so station 0 is the highest point —
 *  runs descend, so top-first is the natural direction. No-op when unresolved. */
export function orientTopToBottom(
  spine: [number, number][],
  elevM: number[]
): { spine: [number, number][]; elevM: number[] } {
  if (elevM.length !== spine.length || spine.length < 2) return { spine, elevM };
  if (elevM[0] < elevM[elevM.length - 1]) {
    return { spine: [...spine].reverse(), elevM: [...elevM].reverse() };
  }
  return { spine, elevM };
}

// ---- Hydration shield ------------------------------------------------------

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

function isRing(r: unknown): r is [number, number][] {
  return Array.isArray(r) && r.length >= 4 && r.every(isLngLat);
}

/**
 * Hydration shield for `GameSave.trails`: drops anything that isn't a valid
 * painted run and recomputes cached length/vertical/slope/difficulty from the
 * stored geometry so they can never drift from it. `difficulty` is honored if
 * valid, else recomputed from the slopes.
 */
export function sanitizeTrails(raw: unknown[]): SavedTrail[] {
  const out: SavedTrail[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const t = item as Record<string, unknown>;
    if (typeof t.id !== 'string' || typeof t.name !== 'string') continue;
    const rawParts = Array.isArray(t.parts)
      ? t.parts
      : [{ polygon: t.polygon, centerline: t.spine, centerlineElevM: t.spineElevM }];
    const parts: SavedTrailPart[] = [];
    for (const rawPart of rawParts) {
      if (typeof rawPart !== 'object' || rawPart === null) continue;
      const p = rawPart as Record<string, unknown>;
      if (!Array.isArray(p.polygon) || p.polygon.length === 0 || !p.polygon.every(isRing)) continue;
      if (!Array.isArray(p.centerline) || p.centerline.length < 2 || !p.centerline.every(isLngLat)) continue;
      const centerline = p.centerline as [number, number][];
      const centerlineElevM = Array.isArray(p.centerlineElevM) &&
        p.centerlineElevM.length === centerline.length &&
        p.centerlineElevM.every((e) => typeof e === 'number' && Number.isFinite(e))
          ? p.centerlineElevM as number[] : [];
      parts.push({ polygon: p.polygon as [number, number][][], centerline, centerlineElevM });
    }
    if (parts.length === 0) continue;

    const brushWidthM =
      typeof t.brushWidthM === 'number' && Number.isFinite(t.brushWidthM) && t.brushWidthM > 0
        ? t.brushWidthM
        : DEFAULT_BRUSH_WIDTH_M;

    const stats = trailPartsStats(parts);
    const difficulty = difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg);
    const status: TrailStatus =
      t.status === 'planning' || t.status === 'complete' ? t.status : 'complete';

    out.push({
      id: t.id,
      name: t.name,
      parts,
      brushWidthM,
      areaM2: trailAreaM2(parts),
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      avgSlopeDeg: stats.avgSlopeDeg,
      maxSlopeDeg: stats.maxSlopeDeg,
      difficulty,
      status,
      createdAt: typeof t.createdAt === 'string' ? t.createdAt : new Date().toISOString(),
    });
  }
  return out;
}

/** First "Run N" not already taken. */
export function nextTrailName(existing: SavedTrail[]): string {
  const taken = new Set(existing.map((t) => t.name));
  for (let n = 1; ; n++) {
    const name = `Run ${n}`;
    if (!taken.has(name)) return name;
  }
}

// Brush width envelope (meters). Trails run from tight cat-tracks to wide
// boulevards; the default suits a typical intermediate run.
export const MIN_BRUSH_WIDTH_M = 8;
export const MAX_BRUSH_WIDTH_M = 120;
export const DEFAULT_BRUSH_WIDTH_M = 30;

export function fmtSlope(deg: number): string {
  return `${Math.round(deg)}°`;
}

export function fmtVertical(m: number | null, units: 'imperial' | 'metric'): string {
  if (m == null) return '—';
  return units === 'imperial'
    ? `${Math.round(m * M_TO_FT).toLocaleString()} ft`
    : `${Math.round(m).toLocaleString()} m`;
}

export function fmtArea(m2: number, units: 'imperial' | 'metric'): string {
  return units === 'imperial'
    ? `${(m2 / 4046.8564224).toFixed(m2 < 40_468 ? 1 : 0)} ac`
    : `${(m2 / 10_000).toFixed(m2 < 100_000 ? 1 : 0)} ha`;
}
