import type { LatLonBounds } from '../types/geo';
import type { SavedTrailPart } from '../types';
import { lngLatToUnit, unitToLngLat } from '../geo';
import { traceContours } from '../marchingSquares';
import { maskToPolygons } from '../coverPolygons';
import type { GradeEnvelope, TerrainGradePolicy } from './terrainGradeProtocol';
import { designGradeLine, smoothTrailProfile, TRAVERSE_MAX_GRADE,
  TRAVERSE_MIN_GRADE } from './trailGradeLine';
import { MAX_FACE_SLOPE, MIN_BENCH_WIDTH_M, sectionSurfaceAt, solveCrossSection,
  type CrossSection, type SectionPolicy } from './trailCrossSection';

export { smoothTrailProfile };

export const TRAIL_CONTOUR_INTERVAL_M = 6.096;
export const TRAIL_CONTOUR_GRID_SIZE = 512;
export { MAX_FACE_SLOPE, MIN_BENCH_WIDTH_M };

/** Cross sections are solved this far apart along the run. Fine enough to
 * follow a switchback, coarse enough that a 2 km run stays a few hundred. */
const STATION_SPACING_M = 8;
const MAX_STATIONS_PER_PART = 800;
/** Roads have no painted shoulder to work in, so they alone may grade outside
 * their footprint — this many times the pavement width. */
const DEFAULT_ROAD_WIDTH_MULTIPLIER = 3;

export interface TerrainGradeInput extends TerrainGradePolicy {
  kind?: 'trail' | 'road';
  heights: ArrayLike<number>;
  gridSize: number;
  bounds: LatLonBounds;
  parts: SavedTrailPart[];
  brushWidthM: number;
  protectedPolygons?: [number, number][][][];
  baseElevationChecksum?: string;
  contourGridSize?: number;
  contourIntervalM?: number;
}

export interface TerrainGradeResult {
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
  contourSegments: Float32Array;
  /** The subset of `contourSegments` lying on ground the grade changed — what
   * the map highlights so the player can see the shape of the edit. */
  editedContourSegments: Float32Array;
  contourGridSize: number;
  contourIntervalM: number;
  gradedElevations: number[][];
  expandedPolygons: [number, number][][][];
  disturbancePolygons: [number, number][][][];
  cutM3: number;
  fillM3: number;
  balanceM3: number;
  /** Steepest natural cross slope the run had to cope with — the reason a
   * stretch costs what it costs, or gets left alone. */
  maxGroundCrossSlopePct: number;
  maxFaceSlopePct: number;
  /** Metres of run left at natural ground because the hillside there is steeper
   * than the face slope. Zero when the whole run graded. */
  ungradedLengthM: number;
  maxDisturbedWidthM: number;
  infeasibleLines: [number, number][][];
  baseElevationChecksum: string;
}

type XY = { x: number; y: number };
type HeapItem = { index: number; distanceM: number };

interface Station {
  point: XY;
  /** Unit normal; +offset lies on this side of the centreline. */
  normal: XY;
  chainageM: number;
  groundElevM: number;
  leftHalfWidthM: number;
  rightHalfWidthM: number;
}

interface Alignment {
  stations: Station[];
  /** True when stations run opposite to the stored centreline order, i.e. the
   * painted run was drawn bottom-up. */
  reversed: boolean;
  totalLengthM: number;
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

function bilinearHeight(
  heights: ArrayLike<number>,
  n: number,
  bounds: LatLonBounds,
  point: [number, number]
): number {
  const [u, v] = lngLatToUnit(point[0], point[1], bounds);
  const x = Math.max(0, Math.min(n - 1, u * (n - 1)));
  const y = Math.max(0, Math.min(n - 1, v * (n - 1)));
  const x0 = Math.floor(x), x1 = Math.min(n - 1, x0 + 1), tx = x - x0;
  const y0 = Math.floor(y), y1 = Math.min(n - 1, y0 + 1), ty = y - y0;
  const a = heights[y0 * n + x0] * (1 - tx) + heights[y0 * n + x1] * tx;
  const b = heights[y1 * n + x0] * (1 - tx) + heights[y1 * n + x1] * tx;
  return a * (1 - ty) + b * ty;
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

class MinHeap {
  private values: HeapItem[] = [];
  get size() { return this.values.length; }
  push(value: HeapItem) {
    const a = this.values;
    a.push(value);
    let i = a.length - 1;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (a[p].distanceM <= value.distanceM) break;
      a[i] = a[p];
      i = p;
    }
    a[i] = value;
  }
  pop(): HeapItem | undefined {
    const a = this.values;
    if (a.length === 0) return undefined;
    const first = a[0], last = a.pop()!;
    if (a.length) {
      let i = 0;
      for (;;) {
        const left = i * 2 + 1;
        if (left >= a.length) break;
        const right = left + 1;
        const child = right < a.length && a[right].distanceM < a[left].distanceM ? right : left;
        if (last.distanceM <= a[child].distanceM) break;
        a[i] = a[child];
        i = child;
      }
      a[i] = last;
    }
    return first;
  }
}

function polygonsFromMask(
  mask: Uint8Array,
  n: number,
  bounds: LatLonBounds
): [number, number][][][] {
  return maskToPolygons(mask, n, {
    blurRadius: 0,
    simplifyTol: 0.35,
    minAreaCells: 0.5,
  }).map((polygon) => {
    const project = (ring: [number, number][]) =>
      ring.map(([x, y]) => unitToLngLat(
        Math.max(0, Math.min(1, x / (n - 1))),
        Math.max(0, Math.min(1, y / (n - 1))),
        bounds
      ));
    return [project(polygon.outer), ...polygon.holes.map(project)];
  });
}

function markProtected(
  polygons: [number, number][][][],
  n: number,
  bounds: LatLonBounds
): Uint8Array {
  const mask = new Uint8Array(n * n);
  if (polygons.length === 0) return mask;
  for (const polygon of polygons) {
    const units = polygon.flat().map(([lng, lat]) => lngLatToUnit(lng, lat, bounds));
    if (units.length === 0) continue;
    const c0 = Math.max(0, Math.floor(Math.min(...units.map((p) => p[0])) * (n - 1)) - 1);
    const c1 = Math.min(n - 1, Math.ceil(Math.max(...units.map((p) => p[0])) * (n - 1)) + 1);
    const r0 = Math.max(0, Math.floor(Math.min(...units.map((p) => p[1])) * (n - 1)) - 1);
    const r1 = Math.min(n - 1, Math.ceil(Math.max(...units.map((p) => p[1])) * (n - 1)) + 1);
    for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
      // Four inset samples conservatively protect any cell substantially
      // intersecting an existing trail, not only cells whose centre is inside.
      for (const [dc, dr] of [[0, 0], [-0.45, -0.45], [0.45, -0.45],
        [0.45, 0.45], [-0.45, 0.45]] as [number, number][]) {
        const [lng, lat] = unitToLngLat(
          Math.max(0, Math.min(1, (c + dc) / (n - 1))),
          Math.max(0, Math.min(1, (r + dr) / (n - 1))),
          bounds
        );
        if (pointInPolygon(lng, lat, polygon)) {
          mask[r * n + c] = 1;
          break;
        }
      }
    }
  }
  // One-cell dilation protects every bilinear DEM vertex that can influence a
  // point inside the trail, even when a narrow polygon crosses between sample
  // centres.
  const conservative = mask.slice();
  for (let index = 0; index < mask.length; index++) if (mask[index]) {
    const r = Math.floor(index / n), c = index % n;
    for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
      const rr = r + dr, cc = c + dc;
      if (rr >= 0 && rr < n && cc >= 0 && cc < n) conservative[rr * n + cc] = 1;
    }
  }
  return conservative;
}

/** Distance from `origin` along `direction` to the nearest crossing of any ring
 * of `polygon`. `Infinity` when the ray escapes, which only happens if the
 * origin sits outside the painted footprint. */
function rayToPolygonEdge(origin: XY, direction: XY, rings: XY[][]): number {
  let best = Infinity;
  for (const ring of rings) {
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const a = ring[j], b = ring[i];
      const ex = b.x - a.x, ey = b.y - a.y;
      const denominator = direction.x * ey - direction.y * ex;
      if (Math.abs(denominator) < 1e-12) continue;
      const ox = a.x - origin.x, oy = a.y - origin.y;
      const t = (ox * ey - oy * ex) / denominator;
      const u = (ox * direction.y - oy * direction.x) / -denominator;
      if (t >= 0 && u >= 0 && u <= 1 && t < best) best = t;
    }
  }
  return best;
}

/**
 * Uniform stations along one painted part, oriented top-first so the profile
 * designer can treat the run as descending. Tangents come from a centred
 * difference across neighbouring stations, which keeps the section normal
 * stable through a switchback where a per-segment normal would flip.
 */
function buildAlignment(
  lineM: XY[],
  ringsM: XY[][],
  groundAtPoint: (point: XY) => number,
  fallbackHalfWidthM: number,
  maxHalfWidthM: number,
  spacingM: number
): Alignment | null {
  if (lineM.length < 2) return null;
  const cumulative = [0];
  for (let i = 1; i < lineM.length; i++) {
    cumulative.push(cumulative[i - 1] + Math.hypot(
      lineM[i].x - lineM[i - 1].x, lineM[i].y - lineM[i - 1].y));
  }
  const totalLengthM = cumulative[cumulative.length - 1];
  if (totalLengthM < 1e-6) return null;

  const count = Math.min(MAX_STATIONS_PER_PART,
    Math.max(3, Math.round(totalLengthM / spacingM) + 1));
  const points: XY[] = [];
  let segment = 0;
  for (let i = 0; i < count; i++) {
    const distance = (i / (count - 1)) * totalLengthM;
    while (segment < cumulative.length - 2 && cumulative[segment + 1] < distance) segment++;
    const span = cumulative[segment + 1] - cumulative[segment];
    const t = span > 0 ? (distance - cumulative[segment]) / span : 0;
    const a = lineM[segment], b = lineM[segment + 1];
    points.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
  }

  const ground = points.map(groundAtPoint);
  const reversed = ground[0] < ground[ground.length - 1];
  if (reversed) { points.reverse(); ground.reverse(); }

  const stations: Station[] = [];
  let chainageM = 0;
  for (let i = 0; i < points.length; i++) {
    if (i > 0) chainageM += Math.hypot(
      points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
    const a = points[Math.max(0, i - 1)], b = points[Math.min(points.length - 1, i + 1)];
    const tx = b.x - a.x, ty = b.y - a.y;
    const length = Math.hypot(tx, ty);
    const normal = length > 1e-9
      ? { x: -ty / length, y: tx / length } : { x: 0, y: 1 };
    const positive = rayToPolygonEdge(points[i], normal, ringsM);
    const negative = rayToPolygonEdge(points[i],
      { x: -normal.x, y: -normal.y }, ringsM);
    stations.push({
      point: points[i],
      normal,
      chainageM,
      groundElevM: ground[i],
      leftHalfWidthM: Math.min(maxHalfWidthM,
        Number.isFinite(negative) ? negative : fallbackHalfWidthM),
      rightHalfWidthM: Math.min(maxHalfWidthM,
        Number.isFinite(positive) ? positive : fallbackHalfWidthM),
    });
  }
  return { stations, reversed, totalLengthM };
}

export function gradeTerrainForTrail(input: TerrainGradeInput): TerrainGradeResult {
  const { gridSize: n, bounds, parts } = input;
  if (input.heights.length !== n * n) throw new Error('Elevation grid dimensions do not match.');
  const original = Float32Array.from(input.heights);
  const working = original.slice();
  const kind = input.kind ?? 'trail';
  const envelope: GradeEnvelope = input.envelope ??
    (kind === 'road' ? 'expand' : 'footprint');
  const maxFaceSlope = MAX_FACE_SLOPE;
  const minGrade = TRAVERSE_MIN_GRADE;
  const maxGrade = TRAVERSE_MAX_GRADE;
  const widthMultiplier = Math.max(1,
    input.maxWidthMultiplier ?? DEFAULT_ROAD_WIDTH_MULTIPLIER);

  const midLat = (bounds.north + bounds.south) / 2;
  const metersX = 111_320 * Math.cos(midLat * Math.PI / 180);
  const metersY = 111_320;
  const dxM = (bounds.east - bounds.west) * metersX / Math.max(1, n - 1);
  const dyM = (bounds.north - bounds.south) * metersY / Math.max(1, n - 1);
  const cellM = Math.max(0.25, Math.min(dxM, dyM));
  const toMeters = ([lng, lat]: [number, number]): XY => ({
    x: (lng - bounds.west) * metersX,
    y: (bounds.north - lat) * metersY,
  });
  const toLngLat = (point: XY): [number, number] => [
    bounds.west + point.x / metersX,
    bounds.north - point.y / metersY,
  ];
  const groundAtPoint = (point: XY) => bilinearHeight(original, n, bounds, toLngLat(point));

  const sectionPolicy: SectionPolicy = {
    maxFaceSlope,
    minBenchWidthM: Math.max(cellM,
      kind === 'road' ? input.brushWidthM : MIN_BENCH_WIDTH_M),
    stepM: cellM,
    // A road's running surface is exactly its pavement; everything wider is a
    // cut or fill face. A trail benches as wide as the paint allows.
    maxBenchWidthM: kind === 'road' ? input.brushWidthM : undefined,
  };

  const normalizedParts = parts.map((part) => ({
    ...part,
    centerlineElevM: part.centerlineElevM.length === part.centerline.length
      ? part.centerlineElevM.slice()
      : part.centerline.map((point) => bilinearHeight(original, n, bounds, point)),
  }));
  const holePolygons = normalizedParts.flatMap((part) =>
    part.polygon.slice(1).map((ring) => [ring]));
  const protectedMask = markProtected(
    [...(input.protectedPolygons ?? []), ...holePolygons],
    n,
    bounds
  );

  // ---- Alignment, profile and cross sections, per part ---------------------

  const alignments: (Alignment | null)[] = [];
  const profiles: number[][] = [];
  const sections: CrossSection[][] = [];
  const tieWeights: number[][] = [];
  const gradedElevations: number[][] = [];

  for (const part of normalizedParts) {
    const lineM = part.centerline.map(toMeters);
    const ringsM = part.polygon.map((ring) => ring.map(toMeters));
    const maxHalfWidthM = envelope === 'expand'
      ? input.brushWidthM * widthMultiplier / 2 : Infinity;
    const alignment = buildAlignment(lineM, ringsM, groundAtPoint,
      input.brushWidthM / 2, maxHalfWidthM, STATION_SPACING_M);
    alignments.push(alignment);
    if (!alignment) {
      profiles.push([]);
      sections.push([]);
      tieWeights.push([]);
      gradedElevations.push(part.centerlineElevM);
      continue;
    }

    const { stations } = alignment;
    if (envelope === 'expand') for (const station of stations) {
      // A road has no painted shoulder; give every station the full envelope.
      station.leftHalfWidthM = maxHalfWidthM;
      station.rightHalfWidthM = maxHalfWidthM;
    }
    const chainage = stations.map((station) => station.chainageM);
    const ground = stations.map((station) => station.groundElevM);
    const groundAtFor = (station: Station) => (offsetM: number) =>
      groundAtPoint({
        x: station.point.x + station.normal.x * offsetM,
        y: station.point.y + station.normal.y * offsetM,
      });

    const design = designGradeLine(chainage, ground, { minGrade, maxGrade });
    const solved = stations.map((station, i) => solveCrossSection(
      design.elevations[i], groundAtFor(station),
      { leftHalfWidthM: station.leftHalfWidthM, rightHalfWidthM: station.rightHalfWidthM },
      sectionPolicy));

    // The bench has to tie into the mountain lengthwise too, not just across —
    // at the top and bottom of the run, and at either edge of a stretch too
    // steep to grade. Without it those boundaries end in a wall.
    //
    // `tieWeight` is 0 at every such anchor and rises to 1 over the distance it
    // takes to lay the bench back down, which is just a chamfer distance along
    // the chainage: one forward sweep, one backward sweep.
    const tieDistanceFor = (i: number) => {
      const neighbour = i === 0 ? 1 : i - 1;
      const run = Math.abs(chainage[neighbour] - chainage[i]);
      // The ramp lays back at the face slope *net of* the grade line's own
      // descent, so the two together never build a face steeper than the cap.
      const longitudinal = run > 1e-6
        ? Math.abs(design.elevations[neighbour] - design.elevations[i]) / run : 0;
      const rate = Math.max(0.15, maxFaceSlope - longitudinal);
      const depth = Math.max(solved[i].cutDepthM, solved[i].fillHeightM);
      return Math.min(alignment.totalLengthM / 4, Math.max(cellM, depth / rate));
    };
    // An ungradeable station is never rasterized, so the ramp has to reach zero
    // at its gradeable *neighbour* — that is the last surface actually drawn.
    const last = stations.length - 1;
    const gradeable = (i: number) => solved[i]?.gradeable ?? false;
    const tieWeight: number[] = solved.map((_, i) =>
      gradeable(i) && i > 0 && i < last && gradeable(i - 1) && gradeable(i + 1) ? 1 : 0);
    for (const direction of [1, -1] as const) {
      for (let step = 1; step <= last; step++) {
        const i = direction === 1 ? step : last - step;
        if (tieWeight[i] === 0) continue;
        const run = Math.abs(chainage[i] - chainage[i - direction]);
        const distance = tieDistanceFor(i);
        tieWeight[i] = Math.min(tieWeight[i],
          tieWeight[i - direction] + (distance > 1e-6 ? run / distance : 1));
      }
    }

    profiles.push(design.elevations);
    sections.push(solved);
    tieWeights.push(tieWeight);

    // Report the designed elevation at the stored centreline vertices, so the
    // saved part keeps one elevation per painted vertex.
    let vertexDistance = 0;
    const vertexElevations: number[] = [];
    for (let i = 0; i < part.centerline.length; i++) {
      if (i > 0) vertexDistance += Math.hypot(
        lineM[i].x - lineM[i - 1].x, lineM[i].y - lineM[i - 1].y);
      const target = alignment.reversed
        ? alignment.totalLengthM - vertexDistance : vertexDistance;
      let station = 0;
      while (station < chainage.length - 2 && chainage[station + 1] < target) station++;
      const span = chainage[station + 1] - chainage[station];
      const t = span > 1e-9
        ? Math.max(0, Math.min(1, (target - chainage[station]) / span)) : 0;
      vertexElevations.push(design.elevations[station] * (1 - t) +
        design.elevations[station + 1] * t);
    }
    gradedElevations.push(vertexElevations);
  }

  // ---- Rasterize the corridor ---------------------------------------------

  const changed = new Uint8Array(n * n);
  const claimedOffset = new Float32Array(n * n).fill(Infinity);
  const footprint = new Uint8Array(n * n);
  const rowOf = (y: number) => y / metersY / (bounds.north - bounds.south) * (n - 1);
  const colOf = (x: number) => x / metersX / (bounds.east - bounds.west) * (n - 1);

  for (let partIndex = 0; partIndex < normalizedParts.length; partIndex++) {
    const alignment = alignments[partIndex];
    if (!alignment) continue;
    const { stations } = alignment;
    const solved = sections[partIndex];
    const profile = profiles[partIndex];
    const tieWeight = tieWeights[partIndex];
    const polygon = normalizedParts[partIndex].polygon;

    for (let i = 0; i < stations.length - 1; i++) {
      const a = stations[i], b = stations[i + 1];
      const solutionA = solved[i], solutionB = solved[i + 1];
      if (!solutionA.gradeable || !solutionB.gradeable) continue;
      const reach = Math.max(
        solutionA.daylightLeftM, solutionA.daylightRightM,
        solutionB.daylightLeftM, solutionB.daylightRightM) + cellM;
      const minX = Math.min(a.point.x, b.point.x) - reach;
      const maxX = Math.max(a.point.x, b.point.x) + reach;
      const minY = Math.min(a.point.y, b.point.y) - reach;
      const maxY = Math.max(a.point.y, b.point.y) + reach;
      const c0 = Math.max(0, Math.floor(colOf(minX)));
      const c1 = Math.min(n - 1, Math.ceil(colOf(maxX)));
      const r0 = Math.max(0, Math.floor(rowOf(minY)));
      const r1 = Math.min(n - 1, Math.ceil(rowOf(maxY)));
      const ex = b.point.x - a.point.x, ey = b.point.y - a.point.y;
      const lengthSquared = ex * ex + ey * ey;

      for (let r = r0; r <= r1; r++) for (let c = c0; c <= c1; c++) {
        const index = r * n + c;
        if (protectedMask[index]) continue;
        const [lng, lat] = unitToLngLat(c / (n - 1), r / (n - 1), bounds);
        const point = toMeters([lng, lat]);
        const t = lengthSquared > 0 ? Math.max(0, Math.min(1,
          ((point.x - a.point.x) * ex + (point.y - a.point.y) * ey) / lengthSquared)) : 0;
        const px = a.point.x + ex * t, py = a.point.y + ey * t;
        const nx = a.normal.x * (1 - t) + b.normal.x * t;
        const ny = a.normal.y * (1 - t) + b.normal.y * t;
        const normalLength = Math.hypot(nx, ny);
        if (normalLength < 1e-9) continue;
        const offsetM = ((point.x - px) * nx + (point.y - py) * ny) / normalLength;
        const distanceM = Math.hypot(point.x - px, point.y - py);
        if (distanceM > reach) continue;
        // Nearest centreline wins, so overlapping stations and parts resolve the
        // same way regardless of iteration order.
        if (distanceM >= claimedOffset[index] - 1e-9) continue;
        if (envelope === 'footprint' && !pointInPolygon(lng, lat, polygon)) continue;

        const section = interpolateSection(solutionA, solutionB, t);
        const benchElevM = profile[i] * (1 - t) + profile[i + 1] * t;
        const surface = sectionSurfaceAt(section, benchElevM, offsetM,
          original[index], sectionPolicy);
        if (surface === null || !Number.isFinite(surface)) continue;
        const blend = tieWeight[i] * (1 - t) + tieWeight[i + 1] * t;
        const tied = original[index] + (surface - original[index]) * blend;
        claimedOffset[index] = distanceM;
        footprint[index] = 1;
        working[index] = tied;
        changed[index] = Math.abs(tied - original[index]) >= 1e-5 ? 1 : 0;
      }
    }
  }

  // ---- Protect intersections ----------------------------------------------

  // Taper the proposed delta to zero before a protected cell. A multi-source
  // distance transform makes this independent of polygon winding and prevents
  // any overlap cell from entering the patch.
  if (protectedMask.some((value) => value !== 0)) {
    const distance = new Float64Array(n * n).fill(Infinity);
    const heap = new MinHeap();
    for (let i = 0; i < protectedMask.length; i++) if (protectedMask[i]) {
      distance[i] = 0;
      heap.push({ index: i, distanceM: 0 });
    }
    const diagonal = Math.hypot(dxM, dyM);
    const neighbors = [[-1, 0, dyM], [1, 0, dyM], [0, -1, dxM], [0, 1, dxM],
      [-1, -1, diagonal], [-1, 1, diagonal],
      [1, -1, diagonal], [1, 1, diagonal]] as const;
    while (heap.size) {
      const current = heap.pop()!;
      if (current.distanceM !== distance[current.index]) continue;
      const r = Math.floor(current.index / n), c = current.index % n;
      for (const [dr, dc, step] of neighbors) {
        const rr = r + dr, cc = c + dc;
        if (rr < 0 || rr >= n || cc < 0 || cc >= n) continue;
        const next = rr * n + cc, candidate = current.distanceM + step;
        if (candidate + 1e-9 >= distance[next]) continue;
        distance[next] = candidate;
        heap.push({ index: next, distanceM: candidate });
      }
    }
    for (let i = 0; i < working.length; i++) {
      if (protectedMask[i]) {
        working[i] = original[i];
        changed[i] = 0;
        continue;
      }
      if (!changed[i]) continue;
      const limit = maxFaceSlope * distance[i];
      const delta = working[i] - original[i];
      if (Math.abs(delta) > limit) {
        working[i] = original[i] + Math.sign(delta) * limit;
        if (Math.abs(working[i] - original[i]) < 1e-5) changed[i] = 0;
      }
    }
  }

  // ---- Volumes, diagnostics and outputs -----------------------------------

  let cutM3 = 0, fillM3 = 0;
  const cellAreaM2 = dxM * dyM;
  for (let i = 0; i < working.length; i++) {
    if (!changed[i]) continue;
    const delta = working[i] - original[i];
    const r = Math.floor(i / n), c = i % n;
    const areaWeight = (r === 0 || r === n - 1 ? 0.5 : 1) *
      (c === 0 || c === n - 1 ? 0.5 : 1);
    if (delta < 0) cutM3 += -delta * cellAreaM2 * areaWeight;
    else fillM3 += delta * cellAreaM2 * areaWeight;
  }

  const infeasibleLines: [number, number][][] = [];
  let maxGroundCrossSlope = 0;
  let maxDisturbedWidthM = 0;
  let ungradedLengthM = 0;
  let graded = false;
  for (let partIndex = 0; partIndex < normalizedParts.length; partIndex++) {
    const alignment = alignments[partIndex];
    if (!alignment) continue;
    const { stations } = alignment;
    const solved = sections[partIndex];
    for (let i = 0; i < stations.length; i++) {
      const station = stations[i];
      const section = solved[i];
      const sample = Math.max(cellM, Math.min(station.leftHalfWidthM,
        station.rightHalfWidthM));
      maxGroundCrossSlope = Math.max(maxGroundCrossSlope, Math.abs(
        groundAtPoint({
          x: station.point.x + station.normal.x * sample,
          y: station.point.y + station.normal.y * sample,
        }) - groundAtPoint({
          x: station.point.x - station.normal.x * sample,
          y: station.point.y - station.normal.y * sample,
        })) / (2 * sample));
      if (section.gradeable) {
        graded = true;
        maxDisturbedWidthM = Math.max(maxDisturbedWidthM,
          section.daylightLeftM + section.daylightRightM);
        continue;
      }
      // Too steep to bench: this stretch keeps its natural ground, and gets
      // marked on the map so the player can see what was left alone.
      const next = stations[Math.min(stations.length - 1, i + 1)];
      ungradedLengthM += Math.abs(next.chainageM - station.chainageM);
      infeasibleLines.push([toLngLat(station.point), toLngLat(next.point)]);
    }
  }

  const patch: [number, number][] = [];
  for (let i = 0; i < changed.length; i++) if (changed[i] &&
      Number.isFinite(working[i]) && Math.abs(working[i] - original[i]) >= 1e-5)
    patch.push([i, working[i]]);

  const disturbanceTraced = envelope === 'expand'
    ? polygonsFromMask(footprint, n, bounds) : [];
  const largestDisturbance = disturbanceTraced.length > 0
    ? [...disturbanceTraced].sort((a, b) => b[0].length - a[0].length)[0] : null;
  // Trails grade inside the paint, so the stored footprint never moves. Roads
  // have no paint and grow to whatever the cut and fill needed.
  const expandedPolygons = normalizedParts.map((part) =>
    envelope === 'footprint' ? part.polygon : largestDisturbance ?? part.polygon);
  // The painted run is what the player cleared; grading never leaves it, so the
  // clearing footprint is the paint itself. Roads have no paint, so they hand
  // back the traced disturbance instead.
  const disturbancePolygons = envelope === 'footprint'
    ? normalizedParts.map((part) => part.polygon)
    : disturbanceTraced;

  const contourGridSize = Math.min(input.contourGridSize ?? TRAIL_CONTOUR_GRID_SIZE, n);
  const contourIntervalM = input.contourIntervalM ?? TRAIL_CONTOUR_INTERVAL_M;
  const contours = traceContours(resampleGrid(working, n, contourGridSize),
    contourGridSize, 1, contourIntervalM);
  const editedMask = downsampleMask(changed, n, contourGridSize);
  const flatten = (segments: typeof contours) => Float32Array.from(
    segments.flatMap((s) => [s.x1, s.y1, s.x2, s.y2, s.level]));

  return {
    patchIndices: Uint32Array.from(patch.map(([index]) => index)),
    patchHeights: Float32Array.from(patch.map(([, value]) => value)),
    contourSegments: flatten(contours),
    // `traceContours` is called with mapSize 1, so segment coordinates are unit
    // [0,1] — scale them back onto the contour grid to test the edited mask.
    editedContourSegments: flatten(contours.filter((segment) => {
      const c = Math.round((segment.x1 + segment.x2) / 2 * (contourGridSize - 1));
      const r = Math.round((segment.y1 + segment.y2) / 2 * (contourGridSize - 1));
      return c >= 0 && c < contourGridSize && r >= 0 && r < contourGridSize &&
        editedMask[r * contourGridSize + c] === 1;
    })),
    contourGridSize,
    contourIntervalM,
    gradedElevations,
    expandedPolygons,
    disturbancePolygons,
    cutM3,
    fillM3,
    balanceM3: cutM3 - fillM3,
    maxGroundCrossSlopePct: maxGroundCrossSlope * 100,
    maxFaceSlopePct: graded ? maxFaceSlope * 100 : 0,
    ungradedLengthM,
    maxDisturbedWidthM,
    infeasibleLines,
    baseElevationChecksum: input.baseElevationChecksum ?? '',
  };
}

/**
 * Shrink the changed-cell mask onto the contour grid, then dilate by one cell.
 * A contour segment lies on a cell *edge*, so the isoline between a changed cell
 * and its untouched neighbour is a genuinely new line and belongs in the
 * highlight — without the dilation the outermost contour of every edit is
 * missed.
 */
function downsampleMask(mask: Uint8Array, srcSize: number, dstSize: number): Uint8Array {
  const coarse = new Uint8Array(dstSize * dstSize);
  const scale = (srcSize - 1) / Math.max(1, dstSize - 1);
  for (let i = 0; i < mask.length; i++) {
    if (!mask[i]) continue;
    const r = Math.min(dstSize - 1, Math.round(Math.floor(i / srcSize) / scale));
    const c = Math.min(dstSize - 1, Math.round((i % srcSize) / scale));
    coarse[r * dstSize + c] = 1;
  }
  const out = coarse.slice();
  for (let i = 0; i < coarse.length; i++) {
    if (!coarse[i]) continue;
    const r = Math.floor(i / dstSize), c = i % dstSize;
    for (let dr = -1; dr <= 1; dr++) for (let dc = -1; dc <= 1; dc++) {
      const rr = r + dr, cc = c + dc;
      if (rr >= 0 && rr < dstSize && cc >= 0 && cc < dstSize) out[rr * dstSize + cc] = 1;
    }
  }
  return out;
}

/** Blend two solved sections so the graded surface stays continuous between
 * stations instead of stepping at every section. */
function interpolateSection(a: CrossSection, b: CrossSection, t: number): CrossSection {
  const mix = (x: number, y: number) => x * (1 - t) + y * t;
  return {
    gradeable: true,
    benchLeftM: mix(a.benchLeftM, b.benchLeftM),
    benchRightM: mix(a.benchRightM, b.benchRightM),
    daylightLeftM: mix(a.daylightLeftM, b.daylightLeftM),
    daylightRightM: mix(a.daylightRightM, b.daylightRightM),
    cutDepthM: mix(a.cutDepthM, b.cutDepthM),
    fillHeightM: mix(a.fillHeightM, b.fillHeightM),
  };
}
