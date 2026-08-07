// Clearing ground cover under drawn ski infrastructure. When a chairlift or a
// trail is confirmed, the trees beneath it are, in reality, felled to open a
// cleared strip of grassland. Both reduce to the same shape — a list of polygons
// (each an outer ring plus optional tree-island holes) — which these pure helpers
// stamp into the analytical cover grid before its vector display is regenerated.
// A lift generates its corridor polygon (`liftCorridorRing`); a trail
// supplies its painted footprint directly. Nothing here re-vectorizes the whole
// grid — the work is O(footprint), cheap enough to run on every confirm (see
// coverDisplay.ts for why a full re-trace is not).

import type { LatLonBounds } from './types/geo';
import type { CoverGrid } from './types/cover';
import { isFourClassGrid, TERRAIN_COVER_CODES } from './fourClassCover';
import { METERS_PER_DEGREE_LAT } from './geo';

/** Required clear width under a lift cable. */
export const LIFT_CLEAR_MIN_WIDTH_M = 50 * 0.3048;
/**
 * Grid-safe half-width on each side of the cable (16 m / 52.5 ft total).
 * Keeping this just above the 50 ft contract prevents 2 m raster rounding from
 * making a diagonal corridor visibly narrower than the requested minimum.
 */
export const LIFT_CLEAR_HALF_WIDTH_M = 8;
/** Maximum extra clearing on either lift edge; it is always applied outward. */
export const LIFT_CLEAR_NOISE_AMPLITUDE_M = 6;
/** Broad nominal scale of the lift's organic treeline variation. */
export const LIFT_CLEAR_NOISE_WAVELENGTH_M = 48;
/** Peak centered wobble retained for the subtle ski-run treeline treatment. */
export const TRAIL_CLEAR_JITTER_M = 2;
/** Compatibility alias for callers of the original centered-noise corridor API. */
export const LIFT_CLEAR_JITTER_M = TRAIL_CLEAR_JITTER_M;
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

function makeIrregularNoiseLayer(
  seed: string,
  wavelengthM: number,
  lengthM: number,
  outwardOnly: boolean
): (s: number) => number {
  const rnd = mulberry32(hash32(seed));
  const nodes: Array<{ distanceM: number; value: number }> = [{
    distanceM: 0,
    value: outwardOnly ? rnd() : rnd() * 2 - 1,
  }];
  const nominal = Math.max(1, wavelengthM);
  let distanceM = 0;
  while (distanceM <= lengthM) {
    // A nominal 48 m lift wavelength becomes irregular 40–60 m intervals.
    distanceM += nominal * (5 / 6 + rnd() * (5 / 12));
    nodes.push({
      distanceM,
      value: outwardOnly ? rnd() : rnd() * 2 - 1,
    });
  }

  return (s: number) => {
    const clamped = Math.max(0, Math.min(lengthM, s));
    let low = 0;
    let high = nodes.length - 1;
    while (low + 1 < high) {
      const mid = (low + high) >>> 1;
      if (nodes[mid].distanceM <= clamped) low = mid;
      else high = mid;
    }
    const a = nodes[low];
    const b = nodes[Math.min(nodes.length - 1, low + 1)];
    const span = Math.max(Number.EPSILON, b.distanceM - a.distanceM);
    const f = Math.max(0, Math.min(1, (clamped - a.distanceM) / span));
    // Quintic smootherstep gives value, slope, and curvature a clean join at
    // each unevenly-spaced random node, reading like a hand-drawn spline.
    const t = f * f * f * (f * (f * 6 - 15) + 10);
    return a.value + (b.value - a.value) * t;
  };
}

/**
 * Deterministic, bounded multi-scale value noise. Uneven node spacing removes
 * the repeated cadence of fixed-frequency noise; a slower secondary layer
 * groups small variations into broader natural sweeps.
 */
function makeOrganicEdgeNoise(
  seed: string,
  amplitude: number,
  wavelengthM: number,
  lengthM: number,
  outwardOnly = false
): (s: number) => number {
  if (!(amplitude > 0)) return () => 0;
  const primary = makeIrregularNoiseLayer(
    `${seed}:primary`, wavelengthM, lengthM, outwardOnly);
  const broad = makeIrregularNoiseLayer(
    `${seed}:broad`, wavelengthM * 2.7, lengthM, outwardOnly);
  return (s: number) => {
    const normalized = primary(s) * 0.72 + broad(s) * 0.28;
    const bounded = outwardOnly
      ? Math.max(0, Math.min(1, normalized))
      : Math.max(-1, Math.min(1, normalized));
    return bounded * amplitude;
  };
}

/** Smooth deterministic centered noise used by trail polygon edges. */
function makeEdgeNoise(seed: string, amplitude: number, wavelengthM: number, lengthM: number): (s: number) => number {
  return makeOrganicEdgeNoise(seed, amplitude, wavelengthM, lengthM, false);
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
  /** Nominal noise scale; actual node intervals vary around this value. */
  wavelengthM?: number;
  /** Use positive-only multi-scale noise so the guaranteed width never narrows. */
  outwardOrganic?: boolean;
  /** Seed string (use the lift id) — makes the edge wobble deterministic. */
  seed: string;
}

/**
 * Build the cleared-corridor ring for a straight two-point lift line, returned
 * as a closed lng/lat polygon (first point repeated at the end). The line is
 * sampled every few metres and offset left/right perpendicular to its direction,
 * with rounded caps at each terminal. Centered noise preserves the legacy API;
 * outward-organic noise treats `halfWidthM` as a hard minimum.
 */
export function liftCorridorRing(points: [LngLat, LngLat], _bounds: LatLonBounds, opts: CorridorOptions): LngLat[] {
  const { jitterM, seed } = opts;
  const halfWidthM = Math.max(LIFT_CLEAR_MIN_WIDTH_M / 2, opts.halfWidthM);
  const wavelengthM = opts.wavelengthM ?? JITTER_WAVELENGTH_M;
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

  const leftNoise = opts.outwardOrganic
    ? makeOrganicEdgeNoise(`${seed}:L`, jitterM, wavelengthM, length, true)
    : makeEdgeNoise(`${seed}:L`, jitterM, wavelengthM, length);
  const rightNoise = opts.outwardOrganic
    ? makeOrganicEdgeNoise(`${seed}:R`, jitterM, wavelengthM, length, true)
    : makeEdgeNoise(`${seed}:R`, jitterM, wavelengthM, length);

  const steps = Math.max(1, Math.ceil(length / CENTERLINE_STEP_M));
  const centers: Meters[] = [];
  const wLeft: number[] = [];
  const wRight: number[] = [];
  for (let i = 0; i <= steps; i++) {
    const s = (i / steps) * length;
    centers.push({ x: dir.x * s, y: dir.y * s });
    wLeft.push(Math.max(LIFT_CLEAR_MIN_WIDTH_M / 2, halfWidthM + leftNoise(s)));
    wRight.push(Math.max(LIFT_CLEAR_MIN_WIDTH_M / 2, halfWidthM + rightNoise(s)));
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

/** Production lift clearing: at least 50 ft wide with irregular outward noise. */
export function liftClearingRing(
  points: [LngLat, LngLat],
  bounds: LatLonBounds,
  seed: string
): LngLat[] {
  return liftCorridorRing(points, bounds, {
    halfWidthM: LIFT_CLEAR_HALF_WIDTH_M,
    jitterM: LIFT_CLEAR_NOISE_AMPLITUDE_M,
    wavelengthM: LIFT_CLEAR_NOISE_WAVELENGTH_M,
    outwardOrganic: true,
    seed,
  });
}

// A cleared area is a polygon: one outer ring followed by optional holes (tree
// islands left standing). A lift emits a single-ring polygon; a trail emits one
// polygon per painted part, each with its holes. Rings are closed lng/lat loops.
export type Ring = LngLat[];
export type Polygon = Ring[]; // [outer, ...holes]

/**
 * Dense, mixed-scale circular dabs whose exposed caps form an irregular
 * tree-crown line along an authored clearing edge.
 */
export interface BubbleBrushOptions {
  /** Stable infrastructure/part seed. Ring indices are appended internally. */
  seed: string;
}

/** One authored footprint, optionally followed by a bubbly edge-only pass. */
export interface CoverClearing {
  polygon: Polygon;
  edgeBrush?: BubbleBrushOptions;
}

export interface StampClearingsOptions {
  /**
   * Mutate an existing Uint8Array instead of copying it. Intended for the
   * worker's already-private transferred buffer; immutable callers omit this.
   */
  inPlace?: boolean;
}

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

type GridPoint = { x: number; y: number };
type ContinuousSpan = [number, number];

interface GridProjection {
  width: number;
  height: number;
  cellWidthM: number;
  cellHeightM: number;
  toGrid(point: LngLat): GridPoint;
  toMeters(point: LngLat): Meters;
  metersToGrid(point: Meters): GridPoint;
}

interface RingRaster {
  meterPoints: Meters[];
  /** Strict even-odd interior spans, in continuous grid coordinates. */
  spansByRow: Array<ContinuousSpan[] | undefined>;
  /** Exact horizontal boundaries retain the legacy inclusive-edge behavior. */
  horizontalByRow: Array<ContinuousSpan[] | undefined>;
}

interface BubbleDab {
  centerM: Meters;
  radiusM: number;
}

const PRIMARY_SPACING_MIN_M = 6;
const PRIMARY_SPACING_MAX_M = 13;
const PRIMARY_RADIUS_MIN_M = 6;
const PRIMARY_RADIUS_MAX_M = 12;
const PRIMARY_EXPOSURE_MIN_M = 3;
const PRIMARY_EXPOSURE_MAX_M = 8;
const ACCENT_CHANCE = 0.12;
const ACCENT_RADIUS_MIN_M = 12;
const ACCENT_RADIUS_MAX_M = 20;
const ACCENT_EXPOSURE_MIN_M = 6;
const ACCENT_EXPOSURE_MAX_M = 14;
const GRID_EPSILON = 1e-9;

function makeGridProjection(grid: CoverGrid): GridProjection {
  const { bounds, width, height } = grid;
  const centerLat = (bounds.north + bounds.south) / 2;
  const mPerLng = METERS_PER_DEGREE_LAT * Math.cos((centerLat * Math.PI) / 180);
  const totalWidthM = Math.max(Number.EPSILON, (bounds.east - bounds.west) * mPerLng);
  const totalHeightM = Math.max(Number.EPSILON, (bounds.north - bounds.south) * METERS_PER_DEGREE_LAT);
  const cellWidthM = totalWidthM / width;
  const cellHeightM = totalHeightM / height;
  return {
    width,
    height,
    cellWidthM,
    cellHeightM,
    toGrid: ([lng, lat]) => ({
      x: ((lng - bounds.west) / (bounds.east - bounds.west)) * width,
      y: ((bounds.north - lat) / (bounds.north - bounds.south)) * height,
    }),
    // Meter coordinates use north-up geometry so winding and normals have
    // their conventional signs. Grid rows are converted only while stamping.
    toMeters: ([lng, lat]) => ({
      x: (lng - bounds.west) * mPerLng,
      y: (lat - bounds.south) * METERS_PER_DEGREE_LAT,
    }),
    metersToGrid: ({ x, y }) => ({
      x: x / cellWidthM,
      y: height - y / cellHeightM,
    }),
  };
}

function distinctOpenRing<T extends LngLat | GridPoint | Meters>(
  points: T[],
  equal: (a: T, b: T) => boolean
): T[] {
  const out: T[] = [];
  for (const point of points) {
    if (out.length === 0 || !equal(point, out[out.length - 1])) out.push(point);
  }
  if (out.length > 1 && equal(out[0], out[out.length - 1])) out.pop();
  return out;
}

function appendSpan(
  rows: Array<ContinuousSpan[] | undefined>,
  row: number,
  left: number,
  right: number
): void {
  const spans = rows[row] ?? (rows[row] = []);
  spans.push([Math.min(left, right), Math.max(left, right)]);
}

/**
 * Rasterize a ring's scanline intersections once. Each non-horizontal edge
 * visits only the rows it actually crosses, avoiding rows × vertices work.
 */
function rasterizeRing(ring: Ring, projection: GridProjection): RingRaster | undefined {
  const lngLat = distinctOpenRing(closeRing(ring).slice(0, -1), (a, b) =>
    a[0] === b[0] && a[1] === b[1]);
  if (lngLat.length < 3) return undefined;
  const gridPoints = distinctOpenRing(lngLat.map(projection.toGrid), (a, b) =>
    a.x === b.x && a.y === b.y);
  const meterPoints = distinctOpenRing(lngLat.map(projection.toMeters), (a, b) =>
    a.x === b.x && a.y === b.y);
  if (gridPoints.length < 3 || meterPoints.length !== gridPoints.length) return undefined;

  const intersections: Array<number[] | undefined> = new Array(projection.height);
  const horizontalByRow: Array<ContinuousSpan[] | undefined> = new Array(projection.height);
  for (let i = 0; i < gridPoints.length; i++) {
    const a = gridPoints[i];
    const b = gridPoints[(i + 1) % gridPoints.length];
    if (Math.abs(a.y - b.y) < GRID_EPSILON) {
      const row = Math.round(a.y - 0.5);
      if (row >= 0 && row < projection.height &&
          Math.abs(a.y - (row + 0.5)) < GRID_EPSILON) {
        appendSpan(horizontalByRow, row, a.x, b.x);
      }
      continue;
    }
    const minY = Math.min(a.y, b.y);
    const maxY = Math.max(a.y, b.y);
    const row0 = Math.max(0, Math.ceil(minY - 0.5 - GRID_EPSILON));
    const row1 = Math.min(
      projection.height - 1,
      Math.ceil(maxY - 0.5 - GRID_EPSILON) - 1
    );
    for (let row = row0; row <= row1; row++) {
      const y = row + 0.5;
      const x = a.x + ((y - a.y) / (b.y - a.y)) * (b.x - a.x);
      const xs = intersections[row] ?? (intersections[row] = []);
      xs.push(x);
    }
  }

  const spansByRow: Array<ContinuousSpan[] | undefined> = new Array(projection.height);
  for (let row = 0; row < projection.height; row++) {
    const xs = intersections[row];
    if (!xs || xs.length < 2) continue;
    xs.sort((a, b) => a - b);
    const spans: ContinuousSpan[] = [];
    for (let i = 0; i + 1 < xs.length; i += 2) spans.push([xs[i], xs[i + 1]]);
    spansByRow[row] = spans;
  }
  return { meterPoints, spansByRow, horizontalByRow };
}

function cellSpan([left, right]: ContinuousSpan): [number, number] {
  return [
    Math.ceil(left - 0.5 - GRID_EPSILON),
    Math.ceil(right - 0.5 - GRID_EPSILON) - 1,
  ];
}

function distributeClosedIntervals(
  perimeterM: number,
  scale: number,
  rnd: () => number
): number[] {
  const minSpacing = PRIMARY_SPACING_MIN_M * scale;
  const maxSpacing = PRIMARY_SPACING_MAX_M * scale;
  const targetSpacing = (minSpacing + maxSpacing) / 2;
  const minCount = Math.max(1, Math.ceil(perimeterM / maxSpacing));
  const maxCount = Math.max(minCount, Math.floor(perimeterM / minSpacing));
  const count = Math.max(minCount, Math.min(maxCount, Math.round(perimeterM / targetSpacing)));
  const intervals = new Array<number>(count).fill(minSpacing);
  const capacity = maxSpacing - minSpacing;
  let remaining = Math.max(0, perimeterM - minSpacing * count);
  const weights = Array.from({ length: count }, () => 0.25 + rnd());
  const active = new Set(intervals.map((_, index) => index));

  // Capped weighted distribution keeps every closed-ring gap within the
  // requested 6–13 m range while making their pattern visibly non-periodic.
  while (remaining > 1e-7 && active.size > 0) {
    let weightTotal = 0;
    for (const index of active) weightTotal += weights[index];
    let consumed = 0;
    for (const index of [...active]) {
      const available = capacity - (intervals[index] - minSpacing);
      const addition = Math.min(available, remaining * weights[index] / weightTotal);
      intervals[index] += addition;
      consumed += addition;
      if (available - addition <= 1e-7) active.delete(index);
    }
    if (consumed <= 1e-9) break;
    remaining -= consumed;
  }
  if (remaining > 0) intervals[intervals.length - 1] += remaining;
  return intervals;
}

function ringBubbleDabs(
  raster: RingRaster,
  hole: boolean,
  seed: string
): BubbleDab[] {
  const points = raster.meterPoints;
  const segmentLengths = new Array<number>(points.length);
  const cumulative = new Array<number>(points.length + 1);
  cumulative[0] = 0;
  let signedArea2 = 0;
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    segmentLengths[i] = length;
    cumulative[i + 1] = cumulative[i] + length;
    signedArea2 += a.x * b.y - b.x * a.y;
  }
  const perimeterM = cumulative[cumulative.length - 1];
  const areaM2 = Math.abs(signedArea2) / 2;
  if (perimeterM < 1 || areaM2 < 0.25) return [];

  // Small rings retain the same relative texture without being overwhelmed by
  // full-sized dabs. Normal infrastructure rings reach scale 1 at 50 m.
  const scale = Math.min(1, perimeterM / 50);
  const rnd = mulberry32(hash32(seed));
  const intervals = distributeClosedIntervals(perimeterM, scale, rnd);
  const phase = rnd() * intervals[intervals.length - 1];
  const stations: number[] = [];
  let distance = phase;
  for (let i = 0; i < intervals.length; i++) {
    stations.push(distance % perimeterM);
    distance += intervals[i];
  }
  stations.sort((a, b) => a - b);

  const ringInteriorSign = signedArea2 >= 0 ? 1 : -1;
  const clearingSideSign = hole ? -ringInteriorSign : ringInteriorSign;
  const holeExposureCap = hole ? Math.sqrt(areaM2 / Math.PI) * 0.6 : Infinity;
  const dabs: BubbleDab[] = [];
  let segment = 0;
  for (const station of stations) {
    while (segment + 1 < cumulative.length - 1 &&
           station >= cumulative[segment + 1] - 1e-9) segment++;
    const a = points[segment];
    const b = points[(segment + 1) % points.length];
    const segmentLength = segmentLengths[segment];
    if (segmentLength <= 1e-9) continue;
    const t = Math.max(0, Math.min(1, (station - cumulative[segment]) / segmentLength));
    const boundary = {
      x: a.x + (b.x - a.x) * t,
      y: a.y + (b.y - a.y) * t,
    };
    const tx = (b.x - a.x) / segmentLength;
    const ty = (b.y - a.y) / segmentLength;
    const normal = { x: -ty * clearingSideSign, y: tx * clearingSideSign };

    const accent = rnd() < ACCENT_CHANCE;
    const radiusMin = accent ? ACCENT_RADIUS_MIN_M : PRIMARY_RADIUS_MIN_M;
    const radiusMax = accent ? ACCENT_RADIUS_MAX_M : PRIMARY_RADIUS_MAX_M;
    const exposureMin = accent ? ACCENT_EXPOSURE_MIN_M : PRIMARY_EXPOSURE_MIN_M;
    const exposureMax = accent ? ACCENT_EXPOSURE_MAX_M : PRIMARY_EXPOSURE_MAX_M;
    const radiusM = (radiusMin + rnd() * (radiusMax - radiusMin)) * scale;
    const desiredExposureM =
      (exposureMin + rnd() * (exposureMax - exposureMin)) * scale;
    const exposureM = Math.max(
      0,
      Math.min(desiredExposureM, radiusM - 0.5 * scale, holeExposureCap)
    );
    if (exposureM <= 0) continue;
    const insetM = radiusM - exposureM;
    dabs.push({
      centerM: {
        x: boundary.x + normal.x * insetM,
        y: boundary.y + normal.y * insetM,
      },
      radiusM,
    });
  }
  return dabs;
}

/**
 * Stamp authored polygons plus optional mixed-scale bubble linework. The base
 * polygon is always filled first. Outer-ring dabs write only outside the
 * authored outer ring; hole-ring dabs write only inside that original hole.
 */
export function stampClearingsIntoGrid(
  grid: CoverGrid,
  clearings: CoverClearing[],
  options: StampClearingsOptions = {}
): { grid: CoverGrid; changed: number } {
  const code = grasslandCodeFor(grid);
  const data = options.inPlace && grid.data instanceof Uint8Array
    ? grid.data
    : Uint8Array.from(grid.data);
  const projection = makeGridProjection(grid);
  let changed = 0;

  const fill = (row: number, col0: number, col1: number) => {
    const first = Math.max(0, col0);
    const last = Math.min(projection.width - 1, col1);
    for (let col = first; col <= last; col++) {
      const idx = row * projection.width + col;
      const current = data[idx];
      if (WATER_CODES.has(current) || current === NODATA_CODE || current === code) continue;
      data[idx] = code;
      changed++;
    }
  };

  const stampDab = (dab: BubbleDab, raster: RingRaster, hole: boolean) => {
    const center = projection.metersToGrid(dab.centerM);
    const radiusRows = dab.radiusM / projection.cellHeightM;
    const row0 = Math.max(
      0,
      Math.ceil(center.y - radiusRows - 0.5 - GRID_EPSILON)
    );
    const row1 = Math.min(
      projection.height - 1,
      Math.floor(center.y + radiusRows - 0.5 + GRID_EPSILON)
    );
    for (let row = row0; row <= row1; row++) {
      const dyM = (row + 0.5 - center.y) * projection.cellHeightM;
      const remainingM2 = dab.radiusM * dab.radiusM - dyM * dyM;
      if (remainingM2 < -GRID_EPSILON) continue;
      const halfWidthCols =
        Math.sqrt(Math.max(0, remainingM2)) / projection.cellWidthM;
      const disk0 = Math.ceil(center.x - halfWidthCols - 0.5 - GRID_EPSILON);
      const disk1 = Math.floor(center.x + halfWidthCols - 0.5 + GRID_EPSILON);
      if (disk1 < disk0) continue;
      const mask = raster.spansByRow[row];
      if (hole) {
        // A tree-island brush can erode only its own original hole.
        if (!mask) continue;
        for (const span of mask) {
          const [mask0, mask1] = cellSpan(span);
          fill(row, Math.max(disk0, mask0), Math.min(disk1, mask1));
        }
      } else {
        // The authored outer interior was filled already. Restricting this pass
        // to its complement avoids redundant writes and can never touch holes.
        if (!mask || mask.length === 0) {
          fill(row, disk0, disk1);
          continue;
        }
        let cursor = disk0;
        for (const span of mask) {
          const [mask0, mask1] = cellSpan(span);
          if (mask0 > cursor) fill(row, cursor, Math.min(disk1, mask0 - 1));
          cursor = Math.max(cursor, mask1 + 1);
          if (cursor > disk1) break;
        }
        if (cursor <= disk1) fill(row, cursor, disk1);
      }
    }
  };

  for (const clearing of clearings) {
    const outer = clearing.polygon[0];
    if (!outer || outer.length < 3) continue;
    const rasterEntries = clearing.polygon
      .map((ring) => rasterizeRing(ring, projection));
    if (!rasterEntries[0]) continue;
    const rasters = rasterEntries
      .filter((raster): raster is RingRaster => raster !== undefined);

    // Pair all ring intersections in x order to retain even-odd hole behavior.
    for (let row = 0; row < projection.height; row++) {
      const endpoints: number[] = [];
      for (const raster of rasters) {
        for (const span of raster.spansByRow[row] ?? []) endpoints.push(span[0], span[1]);
      }
      endpoints.sort((a, b) => a - b);
      for (let i = 0; i + 1 < endpoints.length; i += 2) {
        fill(row, ...cellSpan([endpoints[i], endpoints[i + 1]]));
      }
      // Preserve the previous inclusive treatment for exact horizontal edges.
      for (const raster of rasters) {
        for (const [left, right] of raster.horizontalByRow[row] ?? []) {
          fill(
            row,
            Math.ceil(left - 0.5 - GRID_EPSILON),
            Math.floor(right - 0.5 + GRID_EPSILON)
          );
        }
      }
    }

    if (!clearing.edgeBrush) continue;
    for (let ringIndex = 0; ringIndex < rasterEntries.length; ringIndex++) {
      const raster = rasterEntries[ringIndex];
      if (!raster) continue;
      const hole = ringIndex > 0;
      const dabs = ringBubbleDabs(
        raster,
        hole,
        `${clearing.edgeBrush.seed}:r${ringIndex}`
      );
      for (const dab of dabs) stampDab(dab, raster, hole);
    }
  }

  return { grid: { ...grid, data } as CoverGrid, changed };
}

/**
 * Compatibility wrapper for callers that want only the authored polygons,
 * without a bubbly edge pass.
 */
export function stampPolygonsIntoGrid(
  grid: CoverGrid,
  polygons: Polygon[]
): { grid: CoverGrid; changed: number } {
  return stampClearingsIntoGrid(
    grid,
    polygons.map((polygon) => ({ polygon }))
  );
}
