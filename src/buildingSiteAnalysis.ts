import { earthworkTerrainPatch, fromLocalMeters, localMeters, terrainMetrics,
  validElevation, type EarthworkTerrainPatch, type TerrainMetrics, type XY } from './earthwork';
import { maskToPolygons } from './coverPolygons';
import type { LatLonBounds } from './types/geo';
import type { TerrainRecord } from './types/terrain';
import type { BuildingFoundationMode } from './types/buildings';

/** Six feet of working apron around a flattened building pad. */
export const BUILDING_PAD_APRON_M = 1.8288;
/** Six inches of clearance between natural ground and a slope-foundation floor. */
export const SLOPE_FOUNDATION_CLEARANCE_M = 0.1524;
/** Cut faces use 1 horizontal to 1 vertical. */
export const BUILDING_CUT_SLOPE = 1;
/** Fill faces use 2 horizontal to 1 vertical. */
export const BUILDING_FILL_SLOPE = 2;
/** Keep a structure from asking the earthwork solver to reach indefinitely. */
export const BUILDING_MAX_EARTHWORK_REACH_M = 250;

export interface BuildingSiteDimensions {
  lengthM: number;
  widthM: number;
  eaveHeightM?: number;
}

/** The deliberately small, dependency-neutral input understood by the worker. */
export interface BuildingSiteInput {
  center: [number, number];
  bearingDeg: number;
  dimensions: BuildingSiteDimensions;
  foundationMode?: BuildingFoundationMode;
  /** Optional aliases make this usable by placement drafts before they are saved. */
  foundation?: BuildingFoundationMode | {
    mode?: BuildingFoundationMode;
    kind?: BuildingFoundationMode;
  };
  heights: ArrayLike<number>;
  gridSize: number;
  bounds: LatLonBounds;
  baseElevationChecksum?: string;
  terrainRevision?: string | number;
  buildingGeometryKey?: string;
  contourGridSize?: number;
  contourIntervalM?: number;
}

/** Draft-only geometry accepted by the record overload below. */
export type BuildingSiteDraftInput = Pick<BuildingSiteInput,
  'center' | 'bearingDeg' | 'dimensions' | 'foundationMode' | 'foundation'>;

export interface BuildingSiteAnalysisOptions {
  terrainRevision?: string | number;
  baseElevationChecksum?: string;
  contourGridSize?: number;
  contourIntervalM?: number;
  buildingGeometryKey?: string;
}

export interface BuildingSiteTerrainSample {
  point: [number, number];
  elevationM: number;
}

export interface BuildingEarthworkEstimate {
  cutM3: number;
  fillM3: number;
  balanceM3: number;
}

export interface BuildingSiteAnalysisResult {
  /** Identity copied into the review state and later confirmation request. */
  geometryKey: string;
  terrainRevision: string | number | undefined;
  baseElevationChecksum: string;
  foundationMode: BuildingFoundationMode;
  bearingDeg: number;
  center: [number, number];
  dimensions: BuildingSiteDimensions;
  /** The level finished floor used by the pump node and mesh. */
  finishedFloorElevationM: number;
  /** Short alias used by older controllers. */
  finishedFloorM: number;
  /** Elevations at the fixed clockwise perimeter samples in slope mode. */
  perimeterSamples: BuildingSiteTerrainSample[];
  perimeterElevationsM: number[];
  /** The oriented building footprint and, for flattening, the six-foot apron. */
  footprintRing: [number, number][];
  padRing: [number, number][];
  apronRing: [number, number][];
  /** Shared terrain patch/contour contract. Empty patch means no terrain edit. */
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
  contourSegments: number[];
  editedContourSegments: number[];
  contourGridSize: number;
  contourIntervalM: number;
  disturbancePolygons: [number, number][][][];
  earthwork: BuildingEarthworkEstimate;
  /** A flattened site commits an elevation edit; a slope site does not. */
  terrainGraded: boolean;
  /** The exact elevation to use for the owned centre pump. */
  pumpNodeElevationM: number;
  /** Stable aliases useful to commit adapters. */
  terrainPatch: EarthworkTerrainPatch;
  foundation: {
    kind: BuildingFoundationMode;
    mode: BuildingFoundationMode;
    finishedFloorElevationM: number;
    perimeterSamples: BuildingSiteTerrainSample[];
    perimeterGroundElevationsM: number[];
    terrainGraded: boolean;
    earthwork: BuildingEarthworkEstimate;
  };
}

export type BuildingSiteAnalysisOutcome =
  | { ok: true; result: BuildingSiteAnalysisResult }
  | { ok: false; error: string };

interface NormalizedInput {
  center: [number, number];
  bearingDeg: number;
  dimensions: BuildingSiteDimensions;
  foundationMode: BuildingFoundationMode;
  heights: ArrayLike<number>;
  gridSize: number;
  bounds: LatLonBounds;
  baseElevationChecksum: string;
  terrainRevision: string | number | undefined;
  geometryKey: string;
  contourGridSize: number;
  contourIntervalM: number;
}

interface OrientedFrame {
  center: XY;
  along: XY;
  across: XY;
}

function normalizeBearing(deg: number): number {
  const value = deg % 360;
  return value < 0 ? value + 360 : value;
}

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function resolveFoundationMode(input: BuildingSiteInput): BuildingFoundationMode {
  const value = typeof input.foundation === 'string'
    ? input.foundation : input.foundation?.mode ?? input.foundation?.kind;
  return input.foundationMode === 'slope' || value === 'slope' ? 'slope' : 'flattened';
}

function makeGeometryKey(input: BuildingSiteInput, mode: BuildingFoundationMode): string {
  if (typeof input.buildingGeometryKey === 'string' && input.buildingGeometryKey.length > 0)
    return input.buildingGeometryKey;
  const value = JSON.stringify([input.center, normalizeBearing(input.bearingDeg), input.dimensions, mode]);
  let hash = 0x811c9dc5;
  for (let i = 0; i < value.length; i++) {
    hash ^= value.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `building-site-${hash.toString(16).padStart(8, '0')}`;
}

function normalize(input: BuildingSiteInput): NormalizedInput | string {
  const mode = resolveFoundationMode(input);
  if (!Array.isArray(input.center) || input.center.length !== 2 ||
      !finite(input.center[0]) || !finite(input.center[1])) return 'Building center is invalid.';
  if (!finite(input.bearingDeg)) return 'Building bearing is invalid.';
  const { lengthM, widthM } = input.dimensions ?? {};
  if (!finite(lengthM) || !finite(widthM) || lengthM <= 0 || widthM <= 0)
    return 'Building dimensions must be positive.';
  if (!Number.isInteger(input.gridSize) || input.gridSize < 2 ||
      input.heights.length !== input.gridSize * input.gridSize)
    return 'Elevation grid dimensions do not match.';
  if (!input.bounds || !finite(input.bounds.west) || !finite(input.bounds.east) ||
      !finite(input.bounds.south) || !finite(input.bounds.north) ||
      !(input.bounds.east > input.bounds.west) || !(input.bounds.north > input.bounds.south))
    return 'Terrain coverage is unavailable for this building.';
  if (mode === 'flattened' && !finite(input.dimensions.eaveHeightM ?? 0)) {
    // Eave height is not used by site grading, but if a draft supplies it then
    // a malformed value should not be allowed to become a persisted building.
    if (input.dimensions.eaveHeightM !== undefined) return 'Building height is invalid.';
  }
  const metrics = terrainMetrics({
    bounds: input.bounds,
    sampleGridSize: input.gridSize,
    sampleHeights: input.heights,
  } as TerrainRecord);
  if (!metrics) return 'Terrain coverage is unavailable for this building.';
  const contourGridSize = Math.max(2, Math.min(
    input.contourGridSize ?? Math.min(512, input.gridSize), input.gridSize));
  return {
    center: [input.center[0], input.center[1]],
    bearingDeg: normalizeBearing(input.bearingDeg),
    dimensions: { ...input.dimensions },
    foundationMode: mode,
    heights: input.heights,
    gridSize: input.gridSize,
    bounds: input.bounds,
    baseElevationChecksum: input.baseElevationChecksum ?? '',
    terrainRevision: input.terrainRevision,
    geometryKey: makeGeometryKey(input, mode),
    contourGridSize,
    contourIntervalM: input.contourIntervalM ?? 6.096,
  };
}

function frame(metrics: TerrainMetrics, input: NormalizedInput): OrientedFrame {
  const angle = input.bearingDeg * Math.PI / 180;
  // localMeters' y axis points south, so clockwise-from-north points [sin,-cos].
  const along = { x: Math.sin(angle), y: -Math.cos(angle) };
  const across = { x: Math.cos(angle), y: Math.sin(angle) };
  return { center: localMeters(metrics, input.center), along, across };
}

function add(a: XY, b: XY): XY { return { x: a.x + b.x, y: a.y + b.y }; }
function scale(a: XY, value: number): XY { return { x: a.x * value, y: a.y * value }; }

interface OrientedFrameWithMetrics extends OrientedFrame { metrics: TerrainMetrics }

function frameWithMetrics(metrics: TerrainMetrics, input: NormalizedInput): OrientedFrameWithMetrics {
  return { ...frame(metrics, input), metrics };
}

function localCorners(frameValue: OrientedFrame, lengthM: number, widthM: number): XY[] {
  const halfL = lengthM / 2, halfW = widthM / 2;
  return [
    add(add(frameValue.center, scale(frameValue.along, halfL)), scale(frameValue.across, -halfW)),
    add(add(frameValue.center, scale(frameValue.along, halfL)), scale(frameValue.across, halfW)),
    add(add(frameValue.center, scale(frameValue.along, -halfL)), scale(frameValue.across, halfW)),
    add(add(frameValue.center, scale(frameValue.along, -halfL)), scale(frameValue.across, -halfW)),
  ];
}

function geographicRing(metrics: TerrainMetrics, frameValue: OrientedFrame,
  lengthM: number, widthM: number): [number, number][] {
  const ring = localCorners(frameValue, lengthM, widthM);
  ring.push(ring[0]);
  return ring.map((point) => fromLocalMeters(metrics, point));
}

function withinTerrain(metrics: TerrainMetrics, point: XY): boolean {
  return point.x >= -1e-7 && point.y >= -1e-7 &&
    point.x <= (metrics.bounds.east - metrics.bounds.west) * metrics.metersX + 1e-7 &&
    point.y <= (metrics.bounds.north - metrics.bounds.south) * metrics.metersY + 1e-7;
}

function sampleAt(metrics: TerrainMetrics, point: XY): number | null {
  const x = point.x / metrics.dxM, y = point.y / metrics.dyM;
  if (x < 0 || y < 0 || x > metrics.n - 1 || y > metrics.n - 1) return null;
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(metrics.n - 1, x0 + 1), y1 = Math.min(metrics.n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const values = [metrics.heights[y0 * metrics.n + x0], metrics.heights[y0 * metrics.n + x1],
    metrics.heights[y1 * metrics.n + x0], metrics.heights[y1 * metrics.n + x1]];
  if (!values.every(validElevation)) return null;
  return (values[0] * (1 - tx) + values[1] * tx) * (1 - ty) +
    (values[2] * (1 - tx) + values[3] * tx) * ty;
}

function median(values: number[]): number | null {
  if (!values.length) return null;
  const ordered = values.slice().sort((a, b) => a - b);
  const middle = Math.floor(ordered.length / 2);
  return ordered.length % 2 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
}

function rectangleCoordinates(frameValue: OrientedFrame, point: XY): { alongM: number; acrossM: number } {
  const dx = point.x - frameValue.center.x, dy = point.y - frameValue.center.y;
  return { alongM: dx * frameValue.along.x + dy * frameValue.along.y,
    acrossM: dx * frameValue.across.x + dy * frameValue.across.y };
}

function distanceToRect(frameValue: OrientedFrame, point: XY, lengthM: number, widthM: number): number {
  const local = rectangleCoordinates(frameValue, point);
  return Math.hypot(Math.max(Math.abs(local.alongM) - lengthM / 2, 0),
    Math.max(Math.abs(local.acrossM) - widthM / 2, 0));
}

function insideRect(frameValue: OrientedFrame, point: XY, lengthM: number, widthM: number): boolean {
  const local = rectangleCoordinates(frameValue, point);
  return Math.abs(local.alongM) <= lengthM / 2 + 1e-6 &&
    Math.abs(local.acrossM) <= widthM / 2 + 1e-6;
}

function perimeterSamples(metrics: TerrainMetrics, frameValue: OrientedFrame,
  lengthM: number, widthM: number): BuildingSiteTerrainSample[] {
  const halfL = lengthM / 2, halfW = widthM / 2;
  // Four corners and four edge midpoints, always clockwise.
  const local = [
    add(add(frameValue.center, scale(frameValue.along, halfL)), scale(frameValue.across, -halfW)),
    add(frameValue.center, scale(frameValue.along, halfL)),
    add(add(frameValue.center, scale(frameValue.along, halfL)), scale(frameValue.across, halfW)),
    add(frameValue.center, scale(frameValue.across, halfW)),
    add(add(frameValue.center, scale(frameValue.along, -halfL)), scale(frameValue.across, halfW)),
    add(frameValue.center, scale(frameValue.along, -halfL)),
    add(add(frameValue.center, scale(frameValue.along, -halfL)), scale(frameValue.across, -halfW)),
    add(frameValue.center, scale(frameValue.across, -halfW)),
  ];
  return local.map((point) => ({
    point: fromLocalMeters(metrics, point),
    elevationM: sampleAt(metrics, point) ?? Number.NaN,
  }));
}

function cellWeight(metrics: TerrainMetrics, row: number, column: number): number {
  return (row === 0 || row === metrics.n - 1 ? 0.5 : 1) *
    (column === 0 || column === metrics.n - 1 ? 0.5 : 1);
}

function changedPolygons(metrics: TerrainMetrics, mask: Uint8Array): [number, number][][][] {
  return maskToPolygons(mask, metrics.n, { blurRadius: 0, blurIterations: 0,
    simplifyTol: 0.35, minAreaCells: 0.5 }).map((polygon) =>
    [polygon.outer, ...polygon.holes].map((ring) => ring.map(([x, y]) =>
      fromLocalMeters(metrics, { x: x * metrics.dxM, y: y * metrics.dyM }))));
}

function emptyTerrainPatch(input: NormalizedInput): EarthworkTerrainPatch {
  return {
    patchIndices: new Uint32Array(), patchHeights: new Float32Array(),
    contourSegments: [], editedContourSegments: [],
    contourGridSize: input.contourGridSize, contourIntervalM: input.contourIntervalM,
    baseElevationChecksum: input.baseElevationChecksum, disturbancePolygons: [],
  };
}

function failure(error: string): BuildingSiteAnalysisOutcome { return { ok: false, error }; }

/**
 * Analyze the terrain beneath one level rectangular building.
 *
 * The implementation intentionally operates on the DEM alone. It can therefore
 * run in a worker and remains deterministic for a given geometry key, terrain
 * revision, and elevation checksum.
 */
function analyzeBuildingSiteInput(input: BuildingSiteInput): BuildingSiteAnalysisOutcome {
  const normalized = normalize(input);
  if (typeof normalized === 'string') return failure(normalized);
  const metrics = terrainMetrics({
    bounds: normalized.bounds, sampleGridSize: normalized.gridSize,
    sampleHeights: normalized.heights,
  } as TerrainRecord);
  if (!metrics) return failure('Terrain coverage is unavailable for this building.');
  const frameValue = frameWithMetrics(metrics, normalized);
  const footprintLocal = localCorners(frameValue, normalized.dimensions.lengthM, normalized.dimensions.widthM);
  const apronLength = normalized.dimensions.lengthM +
    (normalized.foundationMode === 'flattened' ? BUILDING_PAD_APRON_M * 2 : 0);
  const apronWidth = normalized.dimensions.widthM +
    (normalized.foundationMode === 'flattened' ? BUILDING_PAD_APRON_M * 2 : 0);
  const padLocal = localCorners(frameValue, apronLength, apronWidth);
  if (footprintLocal.some((point) => !withinTerrain(metrics, point)))
    return failure('The entire building footprint must remain inside prepared terrain.');
  if (normalized.foundationMode === 'flattened' && padLocal.some((point) => !withinTerrain(metrics, point)))
    return failure('The flattened pad and its apron must remain inside prepared terrain.');

  const footprintSamples = footprintLocal.map((point) => sampleAt(metrics, point));
  if (footprintSamples.some((value) => value == null))
    return failure('The building footprint does not have usable elevation data.');

  const perimeter = perimeterSamples(metrics, frameValue,
    normalized.dimensions.lengthM, normalized.dimensions.widthM);
  if (normalized.foundationMode === 'slope' && perimeter.some((sample) => !validElevation(sample.elevationM)))
    return failure('All eight slope-foundation perimeter samples need usable elevation data.');

  const footprintRing = footprintLocal.map((point) => fromLocalMeters(metrics, point));
  footprintRing.push(footprintRing[0]);
  const padRing = padLocal.map((point) => fromLocalMeters(metrics, point));
  padRing.push(padRing[0]);
  const emptyPatch = emptyTerrainPatch(normalized);

  if (normalized.foundationMode === 'slope') {
    const elevations = perimeter.map((sample) => sample.elevationM);
    const finishedFloorElevationM = Math.max(...elevations) + SLOPE_FOUNDATION_CLEARANCE_M;
    const foundationEarthwork = { cutM3: 0, fillM3: 0, balanceM3: 0 };
    const result: BuildingSiteAnalysisResult = {
      geometryKey: normalized.geometryKey,
      terrainRevision: normalized.terrainRevision,
      baseElevationChecksum: normalized.baseElevationChecksum,
      foundationMode: 'slope', bearingDeg: normalized.bearingDeg,
      center: normalized.center, dimensions: normalized.dimensions,
      finishedFloorElevationM, finishedFloorM: finishedFloorElevationM,
      perimeterSamples: perimeter, perimeterElevationsM: elevations,
      footprintRing, padRing: footprintRing.slice(), apronRing: footprintRing.slice(),
      patchIndices: emptyPatch.patchIndices, patchHeights: emptyPatch.patchHeights,
      contourSegments: emptyPatch.contourSegments,
      editedContourSegments: emptyPatch.editedContourSegments,
      contourGridSize: emptyPatch.contourGridSize, contourIntervalM: emptyPatch.contourIntervalM,
      disturbancePolygons: [[footprintRing]], earthwork: foundationEarthwork,
      terrainGraded: false, pumpNodeElevationM: finishedFloorElevationM,
      terrainPatch: emptyPatch,
      foundation: { kind: 'slope', mode: 'slope', finishedFloorElevationM,
        perimeterSamples: perimeter, perimeterGroundElevationsM: elevations,
        terrainGraded: false, earthwork: foundationEarthwork },
    };
    return { ok: true, result };
  }

  // Median of valid DEM samples whose vertices lie on the six-foot-expanded pad.
  const padElevations: number[] = [];
  let padSampleCount = 0;
  for (let row = 0; row < metrics.n; row++) for (let column = 0; column < metrics.n; column++) {
    const point = { x: column * metrics.dxM, y: row * metrics.dyM };
    if (!insideRect(frameValue, point, apronLength, apronWidth)) continue;
    padSampleCount++;
    const value = metrics.heights[row * metrics.n + column];
    if (validElevation(value)) padElevations.push(value);
  }
  // Small buildings can fall between DEM vertices. Their center and perimeter
  // samples still provide a deterministic datum, while a missing value on a
  // sampled pad vertex remains an invalid site.
  if (!padElevations.length) {
    const fallback = [sampleAt(metrics, frameValue.center), ...perimeter.map((sample) => sample.elevationM)]
      .filter((value): value is number => value != null && validElevation(value));
    padElevations.push(...fallback);
  }
  if (!padElevations.length || (padSampleCount > 0 && padElevations.length < padSampleCount))
    return failure('The flattened pad does not have complete usable elevation data.');
  const finishedFloorElevationM = median(padElevations);
  if (finishedFloorElevationM == null) return failure('The flattened pad elevation could not be determined.');

  const patchIndexList: number[] = [], patchHeightList: number[] = [];
  const changedMask = new Uint8Array(metrics.n * metrics.n);
  let cutM3 = 0, fillM3 = 0;
  let truncated = false;
  const workingReach = BUILDING_MAX_EARTHWORK_REACH_M;
  for (let row = 0; row < metrics.n; row++) for (let column = 0; column < metrics.n; column++) {
    const index = row * metrics.n + column;
    const groundM = metrics.heights[index];
    if (!validElevation(groundM)) {
      // A missing sample beneath the building/apron cannot be graded around.
      const point = { x: column * metrics.dxM, y: row * metrics.dyM };
      // Samples in the earthwork reach are needed to prove that each face
      // daylights. Treating a hole as natural ground would otherwise produce a
      // deceptively valid patch around an unreadable DEM cell.
      if (distanceToRect(frameValue, point, apronLength, apronWidth) <= workingReach)
        return failure('The flattened pad does not have complete usable elevation data.');
      continue;
    }
    const point = { x: column * metrics.dxM, y: row * metrics.dyM };
    const distanceM = distanceToRect(frameValue, point, apronLength, apronWidth);
    const targetAtDistanceM = insideRect(frameValue, point, apronLength, apronWidth)
      ? finishedFloorElevationM
      : groundM > finishedFloorElevationM
        ? Math.min(groundM, finishedFloorElevationM + distanceM / BUILDING_CUT_SLOPE)
        : Math.max(groundM, finishedFloorElevationM - distanceM / BUILDING_FILL_SLOPE);
    // A face that is still above/below natural ground at the reach limit would
    // become a vertical truncation if we stopped rasterizing here. Refuse the
    // whole site, even when the terrain boundary is farther than the final
    // working sample and therefore has not been added to the patch.
    if (distanceM > workingReach && Math.abs(targetAtDistanceM - groundM) >= 1e-4 &&
        (column === 0 || row === 0 || column === metrics.n - 1 || row === metrics.n - 1))
      truncated = true;
    const targetM = distanceM <= workingReach ? targetAtDistanceM : groundM;
    const deltaM = targetM - groundM;
    if (Math.abs(deltaM) < 1e-4) continue;
    if (column === 0 || row === 0 || column === metrics.n - 1 || row === metrics.n - 1)
      truncated = true;
    changedMask[index] = 1;
    patchIndexList.push(index); patchHeightList.push(targetM);
    const volume = Math.abs(deltaM) * metrics.cellAreaM2 * cellWeight(metrics, row, column);
    if (deltaM < 0) cutM3 += volume; else fillM3 += volume;
  }
  if (truncated)
    return failure('The flattened grade cannot daylight inside the available terrain.');

  const patchIndices = Uint32Array.from(patchIndexList);
  const patchHeights = Float32Array.from(patchHeightList);
  const terrainPatch = patchIndices.length
    ? earthworkTerrainPatch({ bounds: normalized.bounds, sampleGridSize: normalized.gridSize,
      sampleHeights: normalized.heights, contourMetadata: {
        gridSize: normalized.contourGridSize, intervalM: normalized.contourIntervalM,
      }, packageManifest: { elevationChecksum: normalized.baseElevationChecksum },
    } as TerrainRecord, patchIndices, patchHeights)
    : emptyPatch;
  const apronRing = padRing;
  // A full pad is disturbed even when its natural datum already matches the
  // median; this is the geometry consumed by best-effort cover clearing.
  const disturbancePolygons = changedPolygons(metrics, changedMask);
  if (!disturbancePolygons.length) disturbancePolygons.push([apronRing]);
  else disturbancePolygons.unshift([apronRing]);
  const earthwork = { cutM3, fillM3, balanceM3: cutM3 - fillM3 };
  const result: BuildingSiteAnalysisResult = {
    geometryKey: normalized.geometryKey,
    terrainRevision: normalized.terrainRevision,
    baseElevationChecksum: normalized.baseElevationChecksum,
    foundationMode: 'flattened', bearingDeg: normalized.bearingDeg,
    center: normalized.center, dimensions: normalized.dimensions,
    finishedFloorElevationM, finishedFloorM: finishedFloorElevationM,
    perimeterSamples: perimeter, perimeterElevationsM: perimeter.map((sample) => sample.elevationM),
    footprintRing, padRing, apronRing,
    patchIndices, patchHeights,
    contourSegments: terrainPatch.contourSegments,
    editedContourSegments: terrainPatch.editedContourSegments,
    contourGridSize: terrainPatch.contourGridSize,
    contourIntervalM: terrainPatch.contourIntervalM,
    disturbancePolygons, earthwork, terrainGraded: true,
    pumpNodeElevationM: finishedFloorElevationM,
    terrainPatch: { ...terrainPatch, disturbancePolygons },
    foundation: { kind: 'flattened', mode: 'flattened', finishedFloorElevationM,
      perimeterSamples: perimeter, perimeterGroundElevationsM: perimeter.map((sample) => sample.elevationM),
      terrainGraded: true, earthwork },
  };
  return { ok: true, result };
}

/**
 * Analyze either a worker-shaped request or a hydrated TerrainRecord plus a
 * placement draft. The second form mirrors the existing dam/pond analysis
 * APIs and keeps callers from rebuilding the worker payload for deterministic
 * unit tests and synchronous fallbacks.
 */
export function analyzeBuildingSite(input: BuildingSiteInput): BuildingSiteAnalysisOutcome;
export function analyzeBuildingSite(record: TerrainRecord, draft: BuildingSiteDraftInput,
  options?: BuildingSiteAnalysisOptions): BuildingSiteAnalysisOutcome;
export function analyzeBuildingSite(
  inputOrRecord: BuildingSiteInput | TerrainRecord,
  draft?: BuildingSiteDraftInput,
  options: BuildingSiteAnalysisOptions = {},
): BuildingSiteAnalysisOutcome {
  if ('gridSize' in inputOrRecord && 'heights' in inputOrRecord) {
    return analyzeBuildingSiteInput(inputOrRecord as BuildingSiteInput);
  }
  const bounds = inputOrRecord.bounds;
  if (!draft || !bounds) return failure('Terrain coverage is unavailable for this building.');
  const record = inputOrRecord as TerrainRecord;
  return analyzeBuildingSiteInput({
    ...draft,
    heights: record.sampleHeights,
    gridSize: record.sampleGridSize,
    bounds,
    baseElevationChecksum: options.baseElevationChecksum ?? record.packageManifest?.elevationChecksum ?? '',
    terrainRevision: options.terrainRevision,
    buildingGeometryKey: options.buildingGeometryKey,
    contourGridSize: options.contourGridSize,
    contourIntervalM: options.contourIntervalM,
  });
}

/** Friendly aliases used by controller code and by the worker protocol tests. */
export const analyzePumpHouseSite = analyzeBuildingSite;
export const analyzeBuildingSiteTerrain = analyzeBuildingSite;

/** Expose geometry helpers without making the worker depend on a renderer. */
export function buildingFootprintRing(input: Pick<BuildingSiteInput, 'center' | 'bearingDeg' | 'dimensions' | 'bounds'>): [number, number][] {
  const metrics = terrainMetrics({ bounds: input.bounds, sampleGridSize: 2,
    sampleHeights: [0, 0, 0, 0] } as TerrainRecord);
  if (!metrics) return [];
  const local = frameWithMetrics(metrics, { ...input,
    heights: [0, 0, 0, 0], gridSize: 2, bounds: input.bounds,
    foundationMode: 'slope',
    baseElevationChecksum: '', terrainRevision: undefined, geometryKey: '',
    contourGridSize: 2, contourIntervalM: 6.096,
  });
  return geographicRing(metrics, local, input.dimensions.lengthM, input.dimensions.widthM);
}
