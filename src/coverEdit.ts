// Clearing ground cover under a drawn ski lift. When a chairlift is confirmed,
// the trees beneath and beside the cable are, in reality, felled to open a lift
// corridor. These pure helpers turn a two-point lift line into a cleared strip
// of grassland: a corridor polygon (with gently irregular, hand-cleared edges),
// stamped into the analytical cover grid and appended to the persisted vector
// display geometry. Nothing here re-vectorizes the whole grid — building the
// ring is O(corridor), so it is cheap enough to run on every lift confirm (see
// coverDisplay.ts for why a full re-trace is not).

import type { LatLonBounds } from './elevation';
import type { CoverGrid } from './types';
import { isFourClassGrid, TERRAIN_COVER_CODES } from './fourClassCover';
import { METERS_PER_DEGREE_LAT, lngLatToUnit, unitToLngLat } from './geo';

/** Half-width of the cleared corridor on each side of the lift line (24 m total). */
export const LIFT_CLEAR_HALF_WIDTH_M = 12;
/** Peak ±wobble applied to the corridor edge so it never reads as a ruler line. */
export const LIFT_CLEAR_JITTER_M = 2;

const WATER_CODES = new Set<number>([TERRAIN_COVER_CODES.water, 80]);
const NODATA_CODE = TERRAIN_COVER_CODES.nodata; // 255, shared by both schemes

type LngLat = [number, number];
type Meters = { x: number; y: number };

/** Grassland class code for whichever scheme this grid uses: 3 four-class, 30 ESA. */
export function grasslandCodeFor(grid: CoverGrid): number {
  return isFourClassGrid(grid) ? TERRAIN_COVER_CODES.grassland : 30;
}

// ---- Deterministic smooth edge jitter -------------------------------------

function hash32(str: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < str.length; i++) {
    h ^= str.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * A smooth, deterministic value-noise function of distance-along-line (metres),
 * bounded to ±amplitude. Random node values every `wavelengthM`, smoothstep
 * interpolated between them, so the corridor edge undulates organically but the
 * same seed always reproduces the same wobble (stable across reloads).
 */
function makeEdgeNoise(seed: string, amplitude: number, wavelengthM: number, lengthM: number): (s: number) => number {
  const rnd = mulberry32(hash32(seed));
  const nodeCount = Math.ceil(lengthM / wavelengthM) + 2;
  const nodes: number[] = [];
  for (let i = 0; i < nodeCount; i++) nodes.push((rnd() * 2 - 1) * amplitude);
  return (s: number) => {
    const x = s / wavelengthM;
    const i = Math.floor(x);
    const f = x - i;
    const a = nodes[Math.min(i, nodeCount - 1)];
    const b = nodes[Math.min(i + 1, nodeCount - 1)];
    const t = f * f * (3 - 2 * f); // smoothstep
    return a + (b - a) * t;
  };
}

// ---- Corridor ring ---------------------------------------------------------

const CENTERLINE_STEP_M = 4; // spacing of edge samples down the line
const JITTER_WAVELENGTH_M = 14; // how quickly the edge wobble varies
const CAP_ARC_STEPS = 6; // points per rounded end cap

export interface CorridorOptions {
  halfWidthM: number;
  jitterM: number;
  /** Seed string (use the lift id) — makes the edge wobble deterministic. */
  seed: string;
}

/**
 * Build the cleared-corridor ring for a straight two-point lift line, returned
 * as a closed lng/lat polygon (first point repeated at the end). The line is
 * sampled every few metres and offset left/right by (halfWidth ± jitter)
 * perpendicular to its direction, with rounded caps at each terminal — so the
 * result reads as a hand-cleared swath rather than a hard rectangle.
 */
export function liftCorridorRing(points: [LngLat, LngLat], _bounds: LatLonBounds, opts: CorridorOptions): LngLat[] {
  const { halfWidthM, jitterM, seed } = opts;
  const [a, b] = points;
  const lat0 = a[1];
  const lng0 = a[0];
  const mPerLat = METERS_PER_DEGREE_LAT;
  const mPerLng = METERS_PER_DEGREE_LAT * Math.cos((lat0 * Math.PI) / 180);
  const toMeters = (p: LngLat): Meters => ({ x: (p[0] - lng0) * mPerLng, y: (p[1] - lat0) * mPerLat });
  const toLngLat = (m: Meters): LngLat => [lng0 + m.x / mPerLng, lat0 + m.y / mPerLat];

  const bm = toMeters(b);
  const length = Math.hypot(bm.x, bm.y);
  if (length < 1e-6) {
    // Degenerate line: fall back to a small square so callers still get a ring.
    const r = halfWidthM;
    return [
      toLngLat({ x: -r, y: -r }), toLngLat({ x: r, y: -r }),
      toLngLat({ x: r, y: r }), toLngLat({ x: -r, y: r }), toLngLat({ x: -r, y: -r }),
    ];
  }
  const dir = { x: bm.x / length, y: bm.y / length };
  const perp = { x: -dir.y, y: dir.x };

  const leftNoise = makeEdgeNoise(`${seed}:L`, jitterM, JITTER_WAVELENGTH_M, length);
  const rightNoise = makeEdgeNoise(`${seed}:R`, jitterM, JITTER_WAVELENGTH_M, length);

  const steps = Math.max(1, Math.ceil(length / CENTERLINE_STEP_M));
  const centers: Meters[] = [];
  const wLeft: number[] = [];
  const wRight: number[] = [];
  for (let i = 0; i <= steps; i++) {
    const s = (i / steps) * length;
    centers.push({ x: dir.x * s, y: dir.y * s });
    wLeft.push(Math.max(1, halfWidthM + leftNoise(s)));
    wRight.push(Math.max(1, halfWidthM + rightNoise(s)));
  }

  const offset = (c: Meters, w: number, sign: number): Meters => ({ x: c.x + perp.x * w * sign, y: c.y + perp.y * w * sign });

  // Semicircular cap around `center`, radius `w`, on the hemisphere facing `axis`
  // (a unit vector). Sweeps from the +perp edge to the -perp edge through `axis`.
  const cap = (center: Meters, w: number, axis: Meters): Meters[] => {
    const pts: Meters[] = [];
    for (let k = 1; k < CAP_ARC_STEPS; k++) {
      const theta = (Math.PI / 2) - (k / CAP_ARC_STEPS) * Math.PI; // +π/2 → -π/2
      pts.push({
        x: center.x + w * (Math.cos(theta) * axis.x + Math.sin(theta) * perp.x),
        y: center.y + w * (Math.cos(theta) * axis.y + Math.sin(theta) * perp.y),
      });
    }
    return pts;
  };

  const ring: Meters[] = [];
  // Left edge, start → end.
  for (let i = 0; i <= steps; i++) ring.push(offset(centers[i], wLeft[i], +1));
  // Forward cap at the far terminal (left → right through +dir).
  ring.push(...cap(centers[steps], (wLeft[steps] + wRight[steps]) / 2, dir));
  // Right edge, end → start.
  for (let i = steps; i >= 0; i--) ring.push(offset(centers[i], wRight[i], -1));
  // Backward cap at the near terminal. The ring arrives here from the right edge
  // (-perp) and must close to the left edge (+perp), so this cap sweeps the
  // opposite way to the forward one — reverse it, otherwise the arc folds back
  // across itself into a bowtie at the bottom terminal.
  ring.push(...cap(centers[0], (wLeft[0] + wRight[0]) / 2, { x: -dir.x, y: -dir.y }).reverse());

  const lngLatRing = ring.map(toLngLat);
  lngLatRing.push(lngLatRing[0]); // close
  return lngLatRing;
}

// ---- Point-in-ring (even-odd) ---------------------------------------------

function pointInRing(lng: number, lat: number, ring: LngLat[]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i];
    const [xj, yj] = ring[j];
    const intersects = yi > lat !== yj > lat && lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi;
    if (intersects) inside = !inside;
  }
  return inside;
}

// ---- Grid stamp ------------------------------------------------------------

/**
 * Stamp the corridor into the analytical cover grid: every land cell whose
 * centre falls inside `ring` becomes grassland. Water and no-data cells are left
 * untouched (so `complete`/`nodataCount` stay valid). Returns a NEW grid with a
 * copied data buffer, plus the number of cells actually changed.
 */
export function stampCorridorIntoGrid(grid: CoverGrid, ring: LngLat[]): { grid: CoverGrid; changed: number } {
  const code = grasslandCodeFor(grid);
  const data = Uint8Array.from(grid.data);
  const { bounds, width, height } = grid;

  // Only visit cells inside the ring's bounding box (a small window).
  let minU = 1;
  let minV = 1;
  let maxU = 0;
  let maxV = 0;
  for (const [lng, lat] of ring) {
    const [u, v] = lngLatToUnit(lng, lat, bounds);
    minU = Math.min(minU, u);
    maxU = Math.max(maxU, u);
    minV = Math.min(minV, v);
    maxV = Math.max(maxV, v);
  }
  const col0 = Math.max(0, Math.floor(minU * width) - 1);
  const col1 = Math.min(width - 1, Math.ceil(maxU * width) + 1);
  const row0 = Math.max(0, Math.floor(minV * height) - 1);
  const row1 = Math.min(height - 1, Math.ceil(maxV * height) + 1);

  let changed = 0;
  for (let row = row0; row <= row1; row++) {
    const v = (row + 0.5) / height;
    for (let col = col0; col <= col1; col++) {
      const u = (col + 0.5) / width;
      const [lng, lat] = unitToLngLat(u, v, bounds);
      if (!pointInRing(lng, lat, ring)) continue;
      const idx = row * width + col;
      const current = data[idx];
      if (WATER_CODES.has(current) || current === NODATA_CODE || current === code) continue;
      data[idx] = code;
      changed++;
    }
  }

  return { grid: { ...grid, data } as CoverGrid, changed };
}

// ---- Display-geometry append ----------------------------------------------

/**
 * Append the corridor as one grassland polygon to the packed display-geometry
 * stream (see coverDisplay.ts for the encoding). Coordinates are normalized to
 * the 0..1 unit square via `lngLatToUnit`, exactly what `coverDisplayToGeoJSON`
 * decodes back through `bounds`. Returns a new array; the input is not mutated.
 */
export function appendCorridorToDisplayGeometry(geometry: number[], ring: LngLat[], bounds: LatLonBounds, code: number): number[] {
  const out = geometry.slice();
  out.push(code, 1, ring.length); // code, ringCount = 1 (outer, no holes), pointCount
  for (const [lng, lat] of ring) {
    const [u, v] = lngLatToUnit(lng, lat, bounds);
    out.push(u, v);
  }
  return out;
}
