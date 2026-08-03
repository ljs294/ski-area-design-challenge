import { maskToPolygons } from './coverPolygons';
import { METERS_PER_DEGREE_LAT, unitToLngLat } from './geo';
import { traceContours } from './marchingSquares';
import type { TerrainRecord } from './types';

/**
 * The one earth-moving system the game builds with. Every structure that
 * reshapes ground — pond berms, dam embankments — describes itself as a
 * *design surface* over the elevation grid and hands it to `sweepEarthwork`,
 * so a cubic metre is measured the same way everywhere and the committed DEM
 * always equals the volumes the player was quoted.
 */

export type Point = [number, number];
export interface XY { x: number; y: number }

/** Crest sits this far above full pool so wind chop cannot overtop it. */
export const EARTHWORK_FREEBOARD_M = 0.6;
/** Crest width — enough for the truck that services the water body. */
export const EARTHWORK_CREST_WIDTH_M = 3.5;
/** Water-side embankment face, run:rise. Flat enough to hold saturated soil. */
export const WATER_FACE_SLOPE = 3;
/** Dry embankment face, run:rise. */
export const OUTER_FACE_SLOPE = 2;
/** Back slope where ground is cut away, run:rise. Matches the 1:1 cut face the
 * trail grader uses, so every excavation in the game reads the same way on the
 * contours. */
export const EARTHWORK_CUT_SLOPE = 1;
/** Hard stop on how far a toe or back slope may reach from the structure. */
export const MAX_EARTHWORK_REACH_M = 250;

const DEFAULT_CONTOUR_GRID_SIZE = 512;
const DEFAULT_CONTOUR_INTERVAL_M = 6.096;

export function validElevation(value: number): boolean {
  return Number.isFinite(value) && value > -1000 && value < 10000;
}

export interface TerrainMetrics {
  n: number;
  heights: ArrayLike<number>;
  bounds: NonNullable<TerrainRecord['bounds']>;
  metersX: number;
  metersY: number;
  dxM: number;
  dyM: number;
  /** Shortest cell edge — the natural step for walking a design surface. */
  cellM: number;
  cellAreaM2: number;
}

export function terrainMetrics(record: TerrainRecord): TerrainMetrics | null {
  const bounds = record.bounds, n = record.sampleGridSize;
  if (!bounds || n < 2 || record.sampleHeights.length !== n * n) return null;
  const midLat = (bounds.north + bounds.south) / 2;
  const metersX = METERS_PER_DEGREE_LAT * Math.cos(midLat * Math.PI / 180);
  const metersY = METERS_PER_DEGREE_LAT;
  const dxM = (bounds.east - bounds.west) * metersX / (n - 1);
  const dyM = (bounds.north - bounds.south) * metersY / (n - 1);
  if (!(dxM > 0) || !(dyM > 0)) return null;
  return { n, heights: record.sampleHeights, bounds, metersX, metersY, dxM, dyM,
    cellM: Math.max(0.25, Math.min(dxM, dyM)), cellAreaM2: dxM * dyM };
}

/** Project into the local metric frame design surfaces work in: x east of the
 * west edge, y south of the north edge, both in metres. */
export function localMeters(metrics: TerrainMetrics, [lng, lat]: Point): XY {
  return { x: (lng - metrics.bounds.west) * metrics.metersX,
    y: (metrics.bounds.north - lat) * metrics.metersY };
}

/** Inverse of `localMeters`. */
export function fromLocalMeters(metrics: TerrainMetrics, point: XY): Point {
  return [metrics.bounds.west + point.x / metrics.metersX,
    metrics.bounds.north - point.y / metrics.metersY];
}

export interface Box { minX: number; maxX: number; minY: number; maxY: number }

export function boxOf(points: XY[]): Box {
  return { minX: Math.min(...points.map((p) => p.x)), maxX: Math.max(...points.map((p) => p.x)),
    minY: Math.min(...points.map((p) => p.y)), maxY: Math.max(...points.map((p) => p.y)) };
}

export interface EarthworkDesign {
  /** Finished elevation at a point in the local frame. Called once per sample
   * the sweep considers, including samples whose ground is unreadable, so a
   * design can keep its own tallies here. */
  elevationAt(x: number, y: number, groundM: number): number;
  /** Cheap proof that a sample `outsideM` beyond the design's box is left
   * alone. `outsideM` is a lower bound on the true distance to the structure,
   * so this only has to be conservative. */
  untouchedBeyond?(outsideM: number, groundM: number): boolean;
}

export interface EarthworkSweep {
  cutM3: number;
  fillM3: number;
  maxFillM: number;
  maxCutM: number;
  disturbedCells: number;
  indices: number[];
  heights: number[];
  /** True when the pass changed a sample on a working-box edge that is not a
   * grid edge: a daylight line ran off the box and the reach has to grow. */
  clipped: boolean;
}

/**
 * Walk every DEM sample within `reachM` of `box`, ask the design for the
 * finished elevation, and total the earth that moves. Edge samples carry half
 * weight per axis because they stand for half a cell of ground.
 */
export function sweepEarthwork(metrics: TerrainMetrics, box: Box, reachM: number,
  design: EarthworkDesign): EarthworkSweep {
  const { n, dxM, dyM, cellAreaM2, heights } = metrics;
  const c0 = Math.max(0, Math.floor((box.minX - reachM) / dxM));
  const c1 = Math.min(n - 1, Math.ceil((box.maxX + reachM) / dxM));
  const r0 = Math.max(0, Math.floor((box.minY - reachM) / dyM));
  const r1 = Math.min(n - 1, Math.ceil((box.maxY + reachM) / dyM));
  const sweep: EarthworkSweep = { cutM3: 0, fillM3: 0, maxFillM: 0, maxCutM: 0,
    disturbedCells: 0, indices: [], heights: [], clipped: false };
  for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
    const index = r * n + c;
    const groundM = heights[index];
    const x = c * dxM, y = r * dyM;
    const outsideM = Math.hypot(Math.max(box.minX - x, 0, x - box.maxX),
      Math.max(box.minY - y, 0, y - box.maxY));
    if (outsideM > 0 && design.untouchedBeyond?.(outsideM, groundM)) continue;
    const designM = design.elevationAt(x, y, groundM);
    if (!validElevation(groundM)) continue;
    const deltaM = designM - groundM;
    if (!Number.isFinite(deltaM) || Math.abs(deltaM) < 1e-3) continue;
    const weight = (r === 0 || r === n - 1 ? 0.5 : 1) * (c === 0 || c === n - 1 ? 0.5 : 1);
    if (deltaM < 0) {
      sweep.cutM3 += -deltaM * cellAreaM2 * weight;
      sweep.maxCutM = Math.max(sweep.maxCutM, -deltaM);
    } else {
      sweep.fillM3 += deltaM * cellAreaM2 * weight;
      sweep.maxFillM = Math.max(sweep.maxFillM, deltaM);
    }
    sweep.disturbedCells++;
    sweep.indices.push(index);
    sweep.heights.push(designM);
    if ((c === c0 && c0 > 0) || (c === c1 && c1 < n - 1) ||
      (r === r0 && r0 > 0) || (r === r1 && r1 < n - 1)) sweep.clipped = true;
  }
  return sweep;
}

/**
 * Grow the working box until no edit touches its edge, so a toe running out
 * across falling ground is never silently truncated into a wall. `makeDesign`
 * is called once per attempt so a design carrying its own tallies starts each
 * pass clean; a sweep that is still `clipped` at the reach limit is the
 * caller's cue to refuse the structure.
 */
export function solveEarthwork(metrics: TerrainMetrics, box: Box, seedReachM: number,
  makeDesign: () => EarthworkDesign): EarthworkSweep {
  let reachM = Math.max(metrics.cellM, seedReachM);
  let sweep = sweepEarthwork(metrics, box, reachM, makeDesign());
  while (sweep.clipped && reachM < MAX_EARTHWORK_REACH_M) {
    reachM = Math.min(MAX_EARTHWORK_REACH_M, reachM * 2);
    sweep = sweepEarthwork(metrics, box, reachM, makeDesign());
  }
  return sweep;
}

/** Distance to a closed ring, negative inside it. */
export function signedDistanceM(ring: XY[], x: number, y: number): number {
  let best = Infinity;
  let inside = false;
  for (let i = 1; i < ring.length; i++) {
    const a = ring[i - 1], b = ring[i];
    const ex = b.x - a.x, ey = b.y - a.y;
    const lengthSquared = ex * ex + ey * ey;
    const t = lengthSquared > 0
      ? Math.max(0, Math.min(1, ((x - a.x) * ex + (y - a.y) * ey) / lengthSquared)) : 0;
    const dx = x - (a.x + ex * t), dy = y - (a.y + ey * t);
    const distanceSquared = dx * dx + dy * dy;
    if (distanceSquared < best) best = distanceSquared;
    if (a.y > y !== b.y > y && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
  }
  return (inside ? -1 : 1) * Math.sqrt(best);
}

export interface SegmentOffset {
  /** Distance to the segment, always positive. */
  distanceM: number;
  /** Which flank of the segment the point falls on: +1, -1, or 0 when on it. */
  side: 1 | -1 | 0;
  /** Position along the segment, clamped to [0, 1]. */
  t: number;
}

/** Where a point sits relative to a straight alignment. */
export function segmentOffset(a: XY, b: XY, x: number, y: number): SegmentOffset {
  const ex = b.x - a.x, ey = b.y - a.y;
  const lengthSquared = ex * ex + ey * ey;
  const t = lengthSquared > 0
    ? Math.max(0, Math.min(1, ((x - a.x) * ex + (y - a.y) * ey) / lengthSquared)) : 0;
  const cross = ex * (y - a.y) - ey * (x - a.x);
  return { distanceM: Math.hypot(x - (a.x + ex * t), y - (a.y + ey * t)),
    side: cross > 0 ? 1 : cross < 0 ? -1 : 0, t };
}

export function bilinearAt(heights: ArrayLike<number>, n: number,
  x: number, y: number): number | null {
  if (!(x >= 0) || !(y >= 0) || x > n - 1 || y > n - 1) return null;
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const values = [heights[y0 * n + x0], heights[y0 * n + x1],
    heights[y1 * n + x0], heights[y1 * n + x1]];
  if (!values.every(validElevation)) return null;
  return (values[0] * (1 - tx) + values[1] * tx) * (1 - ty) +
    (values[2] * (1 - tx) + values[3] * tx) * ty;
}

/** Ground elevation at a point in the local metric frame. */
export function groundAt(metrics: TerrainMetrics, x: number, y: number): number | null {
  return bilinearAt(metrics.heights, metrics.n, x / metrics.dxM, y / metrics.dyM);
}

export interface EarthworkTerrainPatch {
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
  contourSegments: number[];
  /** Just the contours the grade moves, for the pre-build highlight. */
  editedContourSegments: number[];
  contourGridSize: number;
  contourIntervalM: number;
  baseElevationChecksum: string;
  disturbancePolygons: Point[][][];
}

/**
 * Turn a set of edited elevations into the patch the terrain package commits:
 * re-traced contours, the subset of them the edit moved, and the disturbed
 * footprint the cover edit clears. Shared by every earth-moving tool so a
 * preview and its commit are always derived from the same arrays.
 */
export function earthworkTerrainPatch(record: TerrainRecord, patchIndices: Uint32Array,
  patchHeights: Float32Array): EarthworkTerrainPatch {
  const n = record.sampleGridSize;
  const patched = Float32Array.from(record.sampleHeights);
  const mask = new Uint8Array(n * n);
  for (let i = 0; i < patchIndices.length; i++) {
    patched[patchIndices[i]] = patchHeights[i];
    mask[patchIndices[i]] = 1;
  }
  const contourGridSize = Math.min(record.contourMetadata?.gridSize ??
    DEFAULT_CONTOUR_GRID_SIZE, n);
  const contourIntervalM = record.contourMetadata?.intervalM ?? DEFAULT_CONTOUR_INTERVAL_M;
  const traced = traceContours(resampleGrid(patched, n, contourGridSize),
    contourGridSize, 1, contourIntervalM);
  const flatten = (segments: typeof traced) =>
    segments.flatMap((s) => [s.x1, s.y1, s.x2, s.y2, s.level]);
  const editedMask = downsampleMask(patchIndices, n, contourGridSize);
  const bounds = record.bounds;
  const disturbancePolygons = bounds
    ? maskToPolygons(mask, n, { blurRadius: 0, blurIterations: 0, simplifyTol: 0.35,
      minAreaCells: 0.5 }).map((polygon) => [polygon.outer, ...polygon.holes].map((ringPoints) =>
        ringPoints.map(([x, y]) => unitToLngLat(Math.max(0, Math.min(1, x / (n - 1))),
          Math.max(0, Math.min(1, y / (n - 1))), bounds))))
    : [];
  return { patchIndices, patchHeights,
    contourSegments: flatten(traced),
    // `traceContours` runs with mapSize 1, so segment coordinates are unit
    // [0, 1] — scale them back onto the contour grid to test the edited mask.
    editedContourSegments: flatten(traced.filter((segment) => {
      const c = Math.round((segment.x1 + segment.x2) / 2 * (contourGridSize - 1));
      const r = Math.round((segment.y1 + segment.y2) / 2 * (contourGridSize - 1));
      return c >= 0 && c < contourGridSize && r >= 0 && r < contourGridSize &&
        editedMask[r * contourGridSize + c] === 1;
    })),
    contourGridSize, contourIntervalM,
    baseElevationChecksum: record.packageManifest?.elevationChecksum ?? '',
    disturbancePolygons };
}

/** Scatter the edited samples onto the contour grid, dilated by a cell so a
 * contour that shifts just outside the patch still reads as edited. */
function downsampleMask(patchIndices: ArrayLike<number>, n: number, size: number): Uint8Array {
  const mask = new Uint8Array(size * size);
  const scale = (size - 1) / Math.max(1, n - 1);
  for (let i = 0; i < patchIndices.length; i++) {
    const index = patchIndices[i];
    const r = Math.round(Math.floor(index / n) * scale);
    const c = Math.round((index % n) * scale);
    for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
      const ry = r + dr, cx = c + dc;
      if (ry >= 0 && ry < size && cx >= 0 && cx < size) mask[ry * size + cx] = 1;
    }
  }
  return mask;
}

function resampleGrid(source: Float32Array, sourceSize: number, size: number): number[] {
  if (sourceSize === size) return Array.from(source);
  const out = new Array<number>(size * size);
  const scale = (sourceSize - 1) / Math.max(1, size - 1);
  for (let r = 0; r < size; r++) {
    const y = r * scale, y0 = Math.floor(y), y1 = Math.min(sourceSize - 1, y0 + 1), ty = y - y0;
    for (let c = 0; c < size; c++) {
      const x = c * scale, x0 = Math.floor(x), x1 = Math.min(sourceSize - 1, x0 + 1), tx = x - x0;
      out[r * size + c] =
        (source[y0 * sourceSize + x0] * (1 - tx) + source[y0 * sourceSize + x1] * tx) * (1 - ty) +
        (source[y1 * sourceSize + x0] * (1 - tx) + source[y1 * sourceSize + x1] * tx) * ty;
    }
  }
  return out;
}
