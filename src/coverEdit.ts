// Clearing ground cover under drawn ski infrastructure. When a chairlift or a
// trail is confirmed, the trees beneath it are, in reality, felled to open a
// cleared strip of grassland. Both reduce to the same shape — a list of polygons
// (each an outer ring plus optional tree-island holes) — which these pure helpers
// stamp into the analytical cover grid and append to the persisted vector display
// geometry. A lift generates its corridor polygon (`liftCorridorRing`); a trail
// supplies its painted footprint directly. Nothing here re-vectorizes the whole
// grid — the work is O(footprint), cheap enough to run on every confirm (see
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

// A cleared area is a polygon: one outer ring followed by optional holes (tree
// islands left standing). A lift emits a single-ring polygon; a trail emits one
// polygon per painted part, each with its holes. Rings are closed lng/lat loops.
export type Ring = LngLat[];
export type Polygon = Ring[]; // [outer, ...holes]

// ---- Point-in-polygon (even-odd across all rings) -------------------------

// Even-odd test over every ring of the polygon at once: a point inside the outer
// ring but also inside a hole flips parity twice → counted as outside, so holes
// (tree islands) are excluded for free.
function pointInPolygon(lng: number, lat: number, polygon: Polygon): boolean {
  let inside = false;
  for (const ring of polygon) {
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [xi, yi] = ring[i];
      const [xj, yj] = ring[j];
      const intersects = yi > lat !== yj > lat && lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi;
      if (intersects) inside = !inside;
    }
  }
  return inside;
}

// ---- Deterministic edge jitter for an existing ring -----------------------

/** Ensure a ring is closed (first point repeated at the end). */
function closeRing(ring: Ring): Ring {
  if (ring.length === 0) return ring;
  const [fx, fy] = ring[0];
  const [lx, ly] = ring[ring.length - 1];
  return fx === lx && fy === ly ? ring : [...ring, ring[0]];
}

/**
 * Perturb a closed ring's vertices along their local normal by smooth,
 * deterministic value-noise (±amplitudeM), so a brush-traced or generated edge
 * wobbles organically like a hand-cleared boundary rather than following the
 * exact outline. Reuses the same `makeEdgeNoise` infrastructure as the lift
 * corridor, parameterised by perimeter distance. Endpoints stay identical so the
 * ring remains closed; the same seed always reproduces the same wobble.
 */
export function jitterRing(ring: Ring, amplitudeM: number, seed: string): Ring {
  const closed = closeRing(ring);
  const pts = closed.slice(0, -1); // unique vertices (drop the closing duplicate)
  if (pts.length < 3 || amplitudeM <= 0) return closed;

  const lng0 = pts[0][0];
  const lat0 = pts[0][1];
  const mPerLat = METERS_PER_DEGREE_LAT;
  const mPerLng = METERS_PER_DEGREE_LAT * Math.cos((lat0 * Math.PI) / 180);
  const toM = (p: LngLat): Meters => ({ x: (p[0] - lng0) * mPerLng, y: (p[1] - lat0) * mPerLat });
  const toLL = (m: Meters): LngLat => [lng0 + m.x / mPerLng, lat0 + m.y / mPerLat];
  const mpts = pts.map(toM);

  // Cumulative perimeter distance to each vertex (for the noise parameter).
  const n = mpts.length;
  const cum: number[] = new Array(n);
  let perimeter = 0;
  for (let i = 0; i < n; i++) {
    cum[i] = perimeter;
    const a = mpts[i];
    const b = mpts[(i + 1) % n];
    perimeter += Math.hypot(b.x - a.x, b.y - a.y);
  }
  const noise = makeEdgeNoise(seed, amplitudeM, JITTER_WAVELENGTH_M, perimeter);

  const out: LngLat[] = [];
  for (let i = 0; i < n; i++) {
    const prev = mpts[(i - 1 + n) % n];
    const next = mpts[(i + 1) % n];
    const tx = next.x - prev.x;
    const ty = next.y - prev.y;
    const len = Math.hypot(tx, ty) || 1;
    // Consistent left-normal of the local tangent; the signed noise wobbles the
    // edge in and out. Consistency (not true outwardness) is what keeps it smooth.
    const nx = -ty / len;
    const ny = tx / len;
    const d = noise(cum[i]);
    out.push(toLL({ x: mpts[i].x + nx * d, y: mpts[i].y + ny * d }));
  }
  out.push(out[0]); // close
  return out;
}

/** Jitter every ring of a polygon (outer + each hole) with a distinct seed. */
export function jitterPolygon(polygon: Polygon, amplitudeM: number, seed: string): Polygon {
  return polygon.map((ring, i) => jitterRing(ring, amplitudeM, `${seed}:r${i}`));
}

// ---- Grid stamp ------------------------------------------------------------

/**
 * Stamp cleared polygons into the analytical cover grid: every land cell whose
 * centre falls inside a polygon (outer ring, minus any holes) becomes grassland.
 * Water and no-data cells are left untouched (so `complete`/`nodataCount` stay
 * valid). Returns a NEW grid with a copied data buffer, plus the number of cells
 * actually changed.
 */
export function stampPolygonsIntoGrid(grid: CoverGrid, polygons: Polygon[]): { grid: CoverGrid; changed: number } {
  const code = grasslandCodeFor(grid);
  const data = Uint8Array.from(grid.data);
  const { bounds, width, height } = grid;

  let changed = 0;
  for (const polygon of polygons) {
    const outer = polygon[0];
    if (!outer || outer.length < 3) continue;

    // Only visit cells inside the outer ring's bounding box (a small window).
    let minU = 1;
    let minV = 1;
    let maxU = 0;
    let maxV = 0;
    for (const [lng, lat] of outer) {
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

    for (let row = row0; row <= row1; row++) {
      const v = (row + 0.5) / height;
      for (let col = col0; col <= col1; col++) {
        const u = (col + 0.5) / width;
        const [lng, lat] = unitToLngLat(u, v, bounds);
        if (!pointInPolygon(lng, lat, polygon)) continue;
        const idx = row * width + col;
        const current = data[idx];
        if (WATER_CODES.has(current) || current === NODATA_CODE || current === code) continue;
        data[idx] = code;
        changed++;
      }
    }
  }

  return { grid: { ...grid, data } as CoverGrid, changed };
}
