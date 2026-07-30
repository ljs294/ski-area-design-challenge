// Clearing ground cover under drawn ski infrastructure. When a chairlift or a
// trail is confirmed, the trees beneath it are, in reality, felled to open a
// cleared strip of grassland. Both reduce to the same shape — a list of polygons
// (each an outer ring plus optional tree-island holes) — which these pure helpers
// stamp into the analytical cover grid before its vector display is regenerated.
// A lift generates its corridor polygon (`liftCorridorRing`); a trail
// supplies its painted footprint directly. Nothing here re-vectorizes the whole
// grid — the work is O(footprint), cheap enough to run on every confirm (see
// coverDisplay.ts for why a full re-trace is not).

import type { LatLonBounds } from './elevation';
import type { CoverGrid } from './types';
import { isFourClassGrid, TERRAIN_COVER_CODES } from './fourClassCover';
import { METERS_PER_DEGREE_LAT } from './geo';

/** Half-width of the cleared corridor on each side of the lift line (24 m total). */
export const LIFT_CLEAR_HALF_WIDTH_M = 12;
/** Peak ±wobble applied to the corridor edge so it never reads as a ruler line. */
export const LIFT_CLEAR_JITTER_M = 2;
/** Peak outward displacement of a lift or ski run's cut treeline. */
export const TRAIL_CLEAR_BUBBLE_AMPLITUDE_M = 24;
/** Approximate distance between broad lobes along a cut treeline. */
export const TRAIL_CLEAR_BUBBLE_WAVELENGTH_M = 72;
/** Maximum boundary segment length before bubble displacement is applied. */
export const TRAIL_CLEAR_BUBBLE_STEP_M = 4;

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

/**
 * Periodic value noise for a closed ring. Normalized nodes span the requested
 * displacement range; smoothstep makes both the value and first derivative meet
 * cleanly at the closing seam.
 */
function makePeriodicEdgeNoise(
  seed: string,
  amplitude: number,
  wavelengthM: number,
  perimeterM: number,
  outwardOnly: boolean
): (s: number) => number {
  const nodeCount = Math.max(3, Math.round(perimeterM / Math.max(1, wavelengthM)));
  const rnd = mulberry32(hash32(seed));
  const spacing = perimeterM / nodeCount;
  if (outwardOnly) {
    // Partition the ring into deliberately uneven intervals. Normalizing the
    // random widths keeps the ring exactly periodic while letting neighbouring
    // bays range from tight pockets to long, merged bulges.
    const widthWeights = Array.from(
      { length: nodeCount },
      () => 0.62 + rnd() * 0.83
    );
    const weightTotal = widthWeights.reduce((sum, value) => sum + value, 0);
    const widths = widthWeights.map((value) => perimeterM * value / weightTotal);
    const starts = new Array<number>(nodeCount + 1);
    starts[0] = 0;
    for (let i = 0; i < nodeCount; i++) starts[i + 1] = starts[i] + widths[i];
    starts[nodeCount] = perimeterM;

    // Most valleys nearly return to the authored edge. A minority stay raised
    // so adjacent lobes flow together into the larger tree-line bubbles visible
    // in hand-drawn masterplans.
    const troughs = Array.from({ length: nodeCount }, () => {
      const mergedShoulder = rnd() < 0.24;
      return amplitude * (mergedShoulder
        ? 0.12 + rnd() * 0.10
        : 0.01 + rnd() * 0.07);
    });
    const heights = Array.from(
      { length: nodeCount },
      () => amplitude * (0.74 + rnd() * 0.26)
    );
    const peaks = Array.from(
      { length: nodeCount },
      () => 0.41 + rnd() * 0.18
    );
    const smoothstep = (t: number) => t * t * (3 - 2 * t);
    return (s: number) => {
      const wrapped = ((s % perimeterM) + perimeterM) % perimeterM;
      let low = 0;
      let high = nodeCount;
      while (low + 1 < high) {
        const mid = (low + high) >>> 1;
        if (starts[mid] <= wrapped) low = mid;
        else high = mid;
      }
      const i = Math.min(nodeCount - 1, low);
      const f = (wrapped - starts[i]) / widths[i];
      const peak = peaks[i];
      const nextTrough = troughs[(i + 1) % nodeCount];
      const baseline = troughs[i] +
        (nextTrough - troughs[i]) * smoothstep(Math.max(0, Math.min(1, f)));
      // sin² traces a continuously rounded arch instead of the two nearly
      // straight ramps produced by a half-by-half easing curve. A small seeded
      // peak shift keeps it asymmetric while the 4 m resampling supplies enough
      // intermediate curvature for vector simplification to retain the bubble.
      const phase = f <= peak
        ? 0.5 * f / peak
        : 0.5 + 0.5 * (f - peak) / (1 - peak);
      const arch = Math.sin(Math.PI * phase) ** 2;
      return baseline +
        (heights[i] - Math.max(troughs[i], nextTrough)) * arch;
    };
  }

  const nodes = Array.from({ length: nodeCount }, () => rnd());
  const min = Math.min(...nodes);
  const max = Math.max(...nodes);
  const span = max - min;
  for (let i = 0; i < nodes.length; i++) {
    const normalized = span > 0 ? (nodes[i] - min) / span : 0;
    nodes[i] = (normalized * 2 - 1) * amplitude;
  }

  return (s: number) => {
    const wrapped = ((s % perimeterM) + perimeterM) % perimeterM;
    const x = wrapped / spacing;
    const floorX = Math.floor(x);
    const i = floorX % nodeCount;
    const f = x - floorX;
    const a = nodes[i];
    const b = nodes[(i + 1) % nodeCount];
    const t = f * f * (3 - 2 * f);
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

export interface RingJitterOptions {
  amplitudeM: number;
  /** Approximate distance between noise lobes. Defaults to the lift wobble. */
  wavelengthM?: number;
  /** Densify the boundary to at most this segment length before jittering. */
  maxSegmentM?: number;
  /** Join noise seamlessly around the closed perimeter. */
  periodic?: boolean;
  /** Move the edge only away from the polygon interior. */
  outwardOnly?: boolean;
}

// ---- Deterministic edge jitter for an existing ring -----------------------

/** Ensure a ring is closed (first point repeated at the end). */
function closeRing(ring: Ring): Ring {
  if (ring.length === 0) return ring;
  const [fx, fy] = ring[0];
  const [lx, ly] = ring[ring.length - 1];
  return fx === lx && fy === ly ? ring : [...ring, ring[0]];
}

function densifyClosedRing(points: Meters[], maxSegmentM: number): Meters[] {
  if (!(maxSegmentM > 0)) return points;
  const out: Meters[] = [];
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    const steps = Math.max(1, Math.ceil(length / maxSegmentM));
    out.push(a);
    for (let step = 1; step < steps; step++) {
      const t = step / steps;
      out.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
    }
  }
  return out;
}

/**
 * Perturb a closed ring's vertices along their local normal by smooth,
 * deterministic value-noise, so a generated edge reads like a hand-cleared
 * boundary. The options form can resample, join noise periodically, and force
 * every lobe outward. The same seed always reproduces the same boundary.
 */
function jitterRingInternal(
  ring: Ring,
  amplitudeOrOptions: number | RingJitterOptions,
  seed: string,
  hole: boolean
): Ring {
  const closed = closeRing(ring);
  const options: Required<RingJitterOptions> = typeof amplitudeOrOptions === 'number'
    ? {
        amplitudeM: amplitudeOrOptions,
        wavelengthM: JITTER_WAVELENGTH_M,
        maxSegmentM: 0,
        periodic: false,
        outwardOnly: false,
      }
    : {
        amplitudeM: amplitudeOrOptions.amplitudeM,
        wavelengthM: amplitudeOrOptions.wavelengthM ?? JITTER_WAVELENGTH_M,
        maxSegmentM: amplitudeOrOptions.maxSegmentM ?? 0,
        periodic: amplitudeOrOptions.periodic ?? false,
        outwardOnly: amplitudeOrOptions.outwardOnly ?? false,
      };
  const pts = closed.slice(0, -1).filter((point, i, all) =>
    i === 0 || point[0] !== all[i - 1][0] || point[1] !== all[i - 1][1]);
  if (pts.length < 3 || options.amplitudeM <= 0) return closed;

  const lng0 = pts[0][0];
  const lat0 = pts[0][1];
  const mPerLat = METERS_PER_DEGREE_LAT;
  const mPerLng = METERS_PER_DEGREE_LAT * Math.cos((lat0 * Math.PI) / 180);
  const toM = (p: LngLat): Meters => ({ x: (p[0] - lng0) * mPerLng, y: (p[1] - lat0) * mPerLat });
  const toLL = (m: Meters): LngLat => [lng0 + m.x / mPerLng, lat0 + m.y / mPerLat];
  const mpts = densifyClosedRing(pts.map(toM), options.maxSegmentM);

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
  if (perimeter <= 0) return closed;
  // Small tree islands taper toward zero displacement instead of being
  // overwhelmed by a full-size four-metre lobe.
  const amplitudeM = Math.min(options.amplitudeM, perimeter / 12);
  const noise = options.periodic
    ? makePeriodicEdgeNoise(seed, amplitudeM, options.wavelengthM, perimeter, options.outwardOnly)
    : makeEdgeNoise(seed, amplitudeM, options.wavelengthM, perimeter);
  let signedArea = 0;
  for (let i = 0; i < n; i++) {
    const a = mpts[i];
    const b = mpts[(i + 1) % n];
    signedArea += a.x * b.y - b.x * a.y;
  }
  // A CCW ring's interior is on its left, so its geometric outward normal is
  // right. Hole rings represent excluded area and therefore invert that normal:
  // expanding the cleared polygon cuts inward into the tree island.
  const outwardNormalSign = (signedArea >= 0 ? -1 : 1) * (hole ? -1 : 1);

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
    const direction = options.outwardOnly ? outwardNormalSign : 1;
    out.push(toLL({
      x: mpts[i].x + nx * d * direction,
      y: mpts[i].y + ny * d * direction,
    }));
  }
  out.push(out[0]); // close
  return out;
}

export function jitterRing(
  ring: Ring,
  amplitudeOrOptions: number | RingJitterOptions,
  seed: string
): Ring {
  return jitterRingInternal(ring, amplitudeOrOptions, seed, false);
}

/** Jitter every ring of a polygon (outer + each hole) with a distinct seed. */
export function jitterPolygon(
  polygon: Polygon,
  amplitudeOrOptions: number | RingJitterOptions,
  seed: string
): Polygon {
  return polygon.map((ring, i) =>
    jitterRingInternal(ring, amplitudeOrOptions, `${seed}:r${i}`, i > 0));
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
    const rings = polygon.map((ring) => ring.map(([lng, lat]) => ({
      x: ((lng - bounds.west) / (bounds.east - bounds.west)) * width,
      y: ((bounds.north - lat) / (bounds.north - bounds.south)) * height,
    })));
    let minY = Infinity;
    let maxY = -Infinity;
    for (const point of rings[0]) {
      minY = Math.min(minY, point.y);
      maxY = Math.max(maxY, point.y);
    }
    const row0 = Math.max(0, Math.ceil(minY - 0.5 - 1e-9));
    const row1 = Math.min(height - 1, Math.ceil(maxY - 0.5 - 1e-9) - 1);
    for (let row = row0; row <= row1; row++) {
      const y = row + 0.5;
      const intersections: number[] = [];
      const horizontalBoundaries: [number, number][] = [];
      for (const ring of rings) {
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
          const a = ring[j];
          const b = ring[i];
          if (Math.abs(a.y - y) < 1e-9 && Math.abs(b.y - y) < 1e-9) {
            horizontalBoundaries.push([Math.min(a.x, b.x), Math.max(a.x, b.x)]);
            continue;
          }
          if ((a.y > y) === (b.y > y)) continue;
          intersections.push(a.x + ((y - a.y) / (b.y - a.y)) * (b.x - a.x));
        }
      }
      const fill = (col0: number, col1: number) => {
        for (let col = Math.max(0, col0); col <= Math.min(width - 1, col1); col++) {
          const idx = row * width + col;
          const current = data[idx];
          if (WATER_CODES.has(current) || current === NODATA_CODE || current === code) continue;
          data[idx] = code;
          changed++;
        }
      };
      intersections.sort((a, b) => a - b);
      for (let span = 0; span + 1 < intersections.length; span += 2) {
        // A tiny grid-coordinate epsilon stabilizes centers that land exactly
        // on an edge after lng/lat round-tripping: left is inclusive, right is
        // exclusive, matching the even-odd point test.
        const col0 = Math.ceil(intersections[span] - 0.5 - 1e-9);
        const col1 = Math.ceil(intersections[span + 1] - 0.5 - 1e-9) - 1;
        fill(col0, col1);
      }
      // Treat a cell center exactly on a horizontal polygon edge as covered.
      // Besides matching the previous point test, this keeps an authored 13 m
      // corridor from losing a whole one-metre row to floating-point rounding.
      for (const [left, right] of horizontalBoundaries) {
        fill(Math.ceil(left - 0.5 - 1e-9), Math.floor(right - 0.5 + 1e-9));
      }
    }
  }

  return { grid: { ...grid, data } as CoverGrid, changed };
}
