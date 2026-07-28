import type { LatLonBounds } from '../elevation';
import type { SavedTrailPart } from '../types';
import { lngLatToUnit, unitToLngLat } from '../geo';
import { traceContours } from '../marchingSquares';

export const TRAIL_CONTOUR_INTERVAL_M = 6.096;
export const TRAIL_CONTOUR_GRID_SIZE = 512;

export interface TerrainGradeInput {
  heights: ArrayLike<number>;
  gridSize: number;
  bounds: LatLonBounds;
  parts: SavedTrailPart[];
  brushWidthM: number;
  baseElevationChecksum?: string;
  contourGridSize?: number;
  contourIntervalM?: number;
}

export interface TerrainGradeResult {
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
  contourSegments: Float32Array;
  contourGridSize: number;
  contourIntervalM: number;
  gradedElevations: number[][];
  baseElevationChecksum: string;
}

type XY = { x: number; y: number };
type EdgeSegment = { a: XY; b: XY };

/** One light, zero-phase smoothing pass. It deliberately imposes no downhill
 * invariant: real rolls, flats, and local rises remain part of the run. */
export function smoothTrailProfile(elevations: number[]): number[] {
  if (elevations.length < 3) return elevations.slice();
  const weights = [1, 2, 3, 2, 1];
  const result = elevations.map((value, i) => {
    if (i === 0 || i === elevations.length - 1) return value;
    let sum = 0;
    let weight = 0;
    for (let offset = -2; offset <= 2; offset++) {
      const station = i + offset;
      if (station < 0 || station >= elevations.length) continue;
      const w = weights[offset + 2];
      sum += elevations[station] * w;
      weight += w;
    }
    return weight > 0 ? sum / weight : value;
  });
  return result;
}

function pointInPolygon(lng: number, lat: number, polygon: [number, number][][]): boolean {
  let inside = false;
  for (const ring of polygon) {
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [xi, yi] = ring[i], [xj, yj] = ring[j];
      if (yi > lat !== yj > lat &&
          lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi) inside = !inside;
    }
  }
  return inside;
}

function nearestTarget(point: XY, line: XY[], graded: number[]) {
  let bestD2 = Infinity;
  let targetElevation = 0;
  let segmentIndex = -1;
  for (let i = 1; i < line.length; i++) {
    const a = line[i - 1], b = line[i];
    const dx = b.x - a.x, dy = b.y - a.y;
    const denom = dx * dx + dy * dy;
    const t = denom === 0 ? 0 : Math.max(0, Math.min(1,
      ((point.x - a.x) * dx + (point.y - a.y) * dy) / denom));
    const px = a.x + dx * t, py = a.y + dy * t;
    const d2 = (point.x - px) ** 2 + (point.y - py) ** 2;
    if (d2 < bestD2) {
      bestD2 = d2;
      targetElevation = graded[i - 1] * (1 - t) + graded[i] * t;
      segmentIndex = i - 1;
    }
  }
  return { distanceM: Math.sqrt(bestD2), targetElevation, segmentIndex };
}

function distanceToSegment(point: XY, segment: EdgeSegment): number {
  const dx = segment.b.x - segment.a.x;
  const dy = segment.b.y - segment.a.y;
  const denom = dx * dx + dy * dy;
  const t = denom === 0 ? 0 : Math.max(0, Math.min(1,
    ((point.x - segment.a.x) * dx + (point.y - segment.a.y) * dy) / denom));
  return Math.hypot(point.x - (segment.a.x + dx * t), point.y - (segment.a.y + dy * t));
}

/** Spatial bins only contain boundary segments close enough to influence the
 * inside shoulder. An empty query therefore means "fully graded interior". */
function boundaryDistanceIndex(
  polygon: [number, number][][],
  toMeters: (point: [number, number]) => XY,
  shoulderM: number
) {
  const binSize = Math.max(1, shoulderM * 2);
  const bins = new Map<string, EdgeSegment[]>();
  const binKey = (x: number, y: number) => `${x}:${y}`;
  for (const ring of polygon) {
    for (let i = 0; i < ring.length; i++) {
      const segment = { a: toMeters(ring[i]), b: toMeters(ring[(i + 1) % ring.length]) };
      const x0 = Math.floor((Math.min(segment.a.x, segment.b.x) - shoulderM) / binSize);
      const x1 = Math.floor((Math.max(segment.a.x, segment.b.x) + shoulderM) / binSize);
      const y0 = Math.floor((Math.min(segment.a.y, segment.b.y) - shoulderM) / binSize);
      const y1 = Math.floor((Math.max(segment.a.y, segment.b.y) + shoulderM) / binSize);
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
        const key = binKey(x, y);
        const bucket = bins.get(key);
        if (bucket) bucket.push(segment);
        else bins.set(key, [segment]);
      }
    }
  }
  return (point: XY): number => {
    const bucket = bins.get(binKey(Math.floor(point.x / binSize), Math.floor(point.y / binSize)));
    if (!bucket) return Infinity;
    let distance = Infinity;
    for (const segment of bucket) distance = Math.min(distance, distanceToSegment(point, segment));
    return distance;
  };
}

function resampleGrid(src: Float32Array, srcSize: number, dstSize: number): number[] {
  if (srcSize === dstSize) return Array.from(src);
  const out = new Array<number>(dstSize * dstSize);
  const scale = (srcSize - 1) / Math.max(1, dstSize - 1);
  for (let r = 0; r < dstSize; r++) {
    const y = r * scale, y0 = Math.floor(y), y1 = Math.min(srcSize - 1, y0 + 1), ty = y - y0;
    for (let c = 0; c < dstSize; c++) {
      const x = c * scale, x0 = Math.floor(x), x1 = Math.min(srcSize - 1, x0 + 1), tx = x - x0;
      const a = src[y0 * srcSize + x0], b = src[y0 * srcSize + x1];
      const d = src[y1 * srcSize + x0], e = src[y1 * srcSize + x1];
      out[r * dstSize + c] = (a * (1 - tx) + b * tx) * (1 - ty) +
        (d * (1 - tx) + e * tx) * ty;
    }
  }
  return out;
}

export function gradeTerrainForTrail(input: TerrainGradeInput): TerrainGradeResult {
  const { gridSize: n, bounds, parts } = input;
  if (input.heights.length !== n * n) throw new Error('Elevation grid dimensions do not match.');
  // Worker requests transfer ownership of their Float32 buffer, so it is safe
  // to edit in place there; plain-array callers (tests) still get isolation.
  const working = input.heights instanceof Float32Array
    ? input.heights : Float32Array.from(input.heights);
  const gradedElevations = parts.map((part) => smoothTrailProfile(part.centerlineElevM));
  type Candidate = { value: number; distanceM: number; partIndex: number; segmentIndex: number };
  const candidates = new Map<number, Candidate>();
  const midLat = (bounds.north + bounds.south) / 2;
  const metersX = 111_320 * Math.cos(midLat * Math.PI / 180);
  const metersY = 111_320;
  const toMeters = ([lng, lat]: [number, number]): XY => ({
    x: (lng - bounds.west) * metersX,
    y: (bounds.north - lat) * metersY,
  });
  const gridCellDiagonalM = Math.hypot(
    (bounds.east - bounds.west) * metersX / Math.max(1, n - 1),
    (bounds.north - bounds.south) * metersY / Math.max(1, n - 1)
  );
  const shoulderM = Math.max(0.01, Math.min(input.brushWidthM * 0.15,
    Math.max(gridCellDiagonalM, input.brushWidthM * 0.08)));

  parts.forEach((part, partIndex) => {
    if (part.centerline.length < 2 || part.centerlineElevM.length !== part.centerline.length) return;
    const line = part.centerline.map(toMeters);
    const graded = gradedElevations[partIndex];
    const distanceToBoundary = boundaryDistanceIndex(part.polygon, toMeters, shoulderM);
    const points = part.polygon.flat();
    if (points.length === 0) return;
    const units = points.map(([lng, lat]) => lngLatToUnit(lng, lat, bounds));
    const c0 = Math.max(0, Math.floor(Math.min(...units.map((p) => p[0])) * (n - 1)) - 1);
    const c1 = Math.min(n - 1, Math.ceil(Math.max(...units.map((p) => p[0])) * (n - 1)) + 1);
    const r0 = Math.max(0, Math.floor(Math.min(...units.map((p) => p[1])) * (n - 1)) - 1);
    const r1 = Math.min(n - 1, Math.ceil(Math.max(...units.map((p) => p[1])) * (n - 1)) + 1);
    for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
      const [lng, lat] = unitToLngLat(c / (n - 1), r / (n - 1), bounds);
      if (!pointInPolygon(lng, lat, part.polygon)) continue;
      const point = toMeters([lng, lat]);
      const nearest = nearestTarget(point, line, graded);
      if (!Number.isFinite(nearest.targetElevation) || nearest.segmentIndex < 0) continue;
      const edgeT = Math.max(0, Math.min(1, distanceToBoundary(point) / shoulderM));
      const weight = edgeT * edgeT * (3 - 2 * edgeT);
      if (weight <= 0) continue;
      const index = r * n + c;
      const value = working[index] + (nearest.targetElevation - working[index]) * weight;
      if (!Number.isFinite(value) || Math.abs(value - working[index]) < 1e-5) continue;
      const current = candidates.get(index);
      if (!current || nearest.distanceM < current.distanceM - 1e-9 ||
          (Math.abs(nearest.distanceM - current.distanceM) <= 1e-9 &&
            (partIndex < current.partIndex ||
              (partIndex === current.partIndex && nearest.segmentIndex < current.segmentIndex)))) {
        candidates.set(index, {
          value, distanceM: nearest.distanceM, partIndex, segmentIndex: nearest.segmentIndex,
        });
      }
    }
  });

  const sortedPatch = [...candidates.entries()].sort((a, b) => a[0] - b[0]);
  const indices = new Uint32Array(sortedPatch.length);
  const values = new Float32Array(sortedPatch.length);
  sortedPatch.forEach(([index, candidate], i) => {
    working[index] = candidate.value;
    indices[i] = index;
    values[i] = candidate.value;
  });
  const contourGridSize = Math.min(input.contourGridSize ?? TRAIL_CONTOUR_GRID_SIZE, n);
  const contourIntervalM = input.contourIntervalM ?? TRAIL_CONTOUR_INTERVAL_M;
  const contours = traceContours(resampleGrid(working, n, contourGridSize),
    contourGridSize, 1, contourIntervalM);
  return {
    patchIndices: indices,
    patchHeights: values,
    contourSegments: Float32Array.from(contours.flatMap((s) =>
      [s.x1, s.y1, s.x2, s.y2, s.level])),
    contourGridSize,
    contourIntervalM,
    gradedElevations,
    baseElevationChecksum: input.baseElevationChecksum ?? '',
  };
}
