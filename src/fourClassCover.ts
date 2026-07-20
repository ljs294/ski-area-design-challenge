import type {
  SiteCoverGrid,
  TerrainCoverCode,
  TerrainCoverGrid,
  TerrainCoverProvenance,
  VectorFeatureSet,
} from './types';
import type { LatLonBounds } from './elevation';
import type { LidarCanopyGrid, NaipAcquisition } from './usgsTerrainCover';

export const TERRAIN_COVER_CODES = {
  forest: 1,
  alpine: 2,
  grassland: 3,
  water: 4,
  nodata: 255,
} as const satisfies Record<string, TerrainCoverCode>;

export const TERRAIN_COVER_LABELS: Record<number, string> = {
  1: 'Forest', 2: 'Alpine', 3: 'Grassland', 4: 'Water', 255: 'No data',
};

interface DeriveFourClassOptions {
  bounds: LatLonBounds;
  original: SiteCoverGrid;
  elevation: { heights: ArrayLike<number>; width: number; height: number };
  naip?: NaipAcquisition | null;
  lidar?: LidarCanopyGrid | null;
  vectors?: VectorFeatureSet;
  targetCellM?: number;
}

function dimensions(bounds: LatLonBounds, targetCellM: number): { width: number; height: number; cellSizeM: number } {
  const mid = (bounds.south + bounds.north) * Math.PI / 360;
  const widthM = Math.abs(bounds.east - bounds.west) * 111_320 * Math.cos(mid);
  const heightM = Math.abs(bounds.north - bounds.south) * 111_320;
  const width = Math.max(2, Math.min(4000, Math.round(widthM / targetCellM)));
  const height = Math.max(2, Math.min(4000, Math.round(heightM / targetCellM)));
  return { width, height, cellSizeM: Math.max(widthM / width, heightM / height) };
}

function indexAt(width: number, height: number, u: number, v: number): number {
  const col = Math.max(0, Math.min(width - 1, Math.floor(u * width)));
  const row = Math.max(0, Math.min(height - 1, Math.floor(v * height)));
  return row * width + col;
}

function sampleElevation(values: ArrayLike<number>, width: number, height: number, u: number, v: number): number {
  const x = Math.max(0, Math.min(width - 1, u * (width - 1)));
  const y = Math.max(0, Math.min(height - 1, v * (height - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(width - 1, x0 + 1), y1 = Math.min(height - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const a = values[y0 * width + x0], b = values[y0 * width + x1];
  const c = values[y1 * width + x0], d = values[y1 * width + x1];
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

function ndvi(naip: NaipAcquisition, u: number, v: number): number {
  const i = indexAt(naip.width, naip.height, u, v);
  const nir = naip.nir[i], red = naip.red[i];
  return (nir - red) / Math.max(1, nir + red);
}

function ndwi(naip: NaipAcquisition, u: number, v: number): number {
  const i = indexAt(naip.width, naip.height, u, v);
  const green = naip.green[i], nir = naip.nir[i];
  return (green - nir) / Math.max(1, green + nir);
}

function texture(naip: NaipAcquisition, u: number, v: number): number {
  const x = Math.max(0, Math.min(naip.width - 1, Math.floor(u * naip.width)));
  const y = Math.max(0, Math.min(naip.height - 1, Math.floor(v * naip.height)));
  let min = 255, max = 0;
  for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    const col = Math.max(0, Math.min(naip.width - 1, x + dx));
    const row = Math.max(0, Math.min(naip.height - 1, y + dy));
    const i = row * naip.width + col;
    const brightness = (naip.red[i] + naip.green[i] + naip.blue[i]) / 3;
    min = Math.min(min, brightness); max = Math.max(max, brightness);
  }
  return max - min;
}

function isWorldForest(code: number): boolean {
  return code === 10 || code === 95;
}

function pointInRing(lng: number, lat: number, ring: [number, number][]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i], [xj, yj] = ring[j];
    if ((yi > lat) !== (yj > lat) && lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

interface PreparedWaterPolygon { rings: [number, number][][]; bounds: [number, number, number, number] }

function prepareWater(vectors?: VectorFeatureSet): PreparedWaterPolygon[] {
  return (vectors?.waterPolygons ?? []).filter((polygon) => polygon.rings[0]?.length >= 3).map((polygon) => {
    const bounds: [number, number, number, number] = [Infinity, Infinity, -Infinity, -Infinity];
    for (const [lng, lat] of polygon.rings[0]) {
      bounds[0] = Math.min(bounds[0], lng); bounds[1] = Math.min(bounds[1], lat);
      bounds[2] = Math.max(bounds[2], lng); bounds[3] = Math.max(bounds[3], lat);
    }
    return { rings: polygon.rings, bounds };
  });
}

function isVectorWater(lng: number, lat: number, polygons: PreparedWaterPolygon[]): boolean {
  for (const polygon of polygons) {
    if (lng < polygon.bounds[0] || lng > polygon.bounds[2] || lat < polygon.bounds[1] || lat > polygon.bounds[3]) continue;
    if (!polygon.rings.length || !pointInRing(lng, lat, polygon.rings[0])) continue;
    if (!polygon.rings.slice(1).some((ring) => pointInRing(lng, lat, ring))) return true;
  }
  return false;
}

function boundaryCorridor(mask: Uint8Array, width: number, height: number, radius: number): Uint8Array {
  const corridor = new Uint8Array(mask.length);
  const queue: number[] = [];
  const distance = new Int16Array(mask.length).fill(-1);
  for (let row = 0; row < height; row++) for (let col = 0; col < width; col++) {
    const i = row * width + col;
    const boundary = (col > 0 && mask[i - 1] !== mask[i]) || (col + 1 < width && mask[i + 1] !== mask[i]) ||
      (row > 0 && mask[i - width] !== mask[i]) || (row + 1 < height && mask[i + width] !== mask[i]);
    if (boundary) { distance[i] = 0; corridor[i] = 1; queue.push(i); }
  }
  for (let head = 0; head < queue.length; head++) {
    const i = queue[head], d = distance[i];
    if (d >= radius) continue;
    const row = Math.floor(i / width), col = i % width;
    for (const next of [col > 0 ? i - 1 : -1, col + 1 < width ? i + 1 : -1, row > 0 ? i - width : -1, row + 1 < height ? i + width : -1]) {
      if (next < 0 || distance[next] >= 0) continue;
      distance[next] = d + 1; corridor[next] = 1; queue.push(next);
    }
  }
  return corridor;
}

function removeTinyComponents(mask: Uint8Array, width: number, height: number, minPixels: number, target: 0 | 1): void {
  const seen = new Uint8Array(mask.length);
  const component: number[] = [];
  for (let start = 0; start < mask.length; start++) {
    if (seen[start] || mask[start] !== target) continue;
    component.length = 0;
    const queue = [start]; seen[start] = 1;
    let touchesEdge = false;
    for (let head = 0; head < queue.length; head++) {
      const i = queue[head]; component.push(i);
      const row = Math.floor(i / width), col = i % width;
      if (row === 0 || col === 0 || row === height - 1 || col === width - 1) touchesEdge = true;
      for (const next of [col > 0 ? i - 1 : -1, col + 1 < width ? i + 1 : -1, row > 0 ? i - width : -1, row + 1 < height ? i + width : -1]) {
        if (next >= 0 && !seen[next] && mask[next] === target) { seen[next] = 1; queue.push(next); }
      }
    }
    if (component.length < minPixels && !(target === 0 && touchesEdge)) for (const i of component) mask[i] = target === 1 ? 0 : 1;
  }
}

type AspectCode = 0 | 1 | 2 | 3; // north, east, south, west

function aspectFor(elevation: Float32Array, width: number, height: number, row: number, col: number): AspectCode {
  const left = elevation[row * width + Math.max(0, col - 1)], right = elevation[row * width + Math.min(width - 1, col + 1)];
  const up = elevation[Math.max(0, row - 1) * width + col], down = elevation[Math.min(height - 1, row + 1) * width + col];
  const degrees = (Math.atan2(left - right, down - up) * 180 / Math.PI + 360) % 360;
  if (degrees >= 315 || degrees < 45) return 0;
  if (degrees < 135) return 1;
  if (degrees < 225) return 2;
  return 3;
}

function inferredTreeline(elevations: Float32Array, forest: Uint8Array, aspects: Uint8Array, filter?: AspectCode): number | null {
  let min = Infinity, max = -Infinity;
  for (let i = 0; i < elevations.length; i++) if (filter == null || aspects[i] === filter) {
    min = Math.min(min, elevations[i]); max = Math.max(max, elevations[i]);
  }
  if (!Number.isFinite(min) || max <= min) return null;
  const base = Math.floor(min / 25) * 25;
  const bins = Math.max(1, Math.floor((max - base) / 25) + 1);
  const total = new Uint32Array(bins), wooded = new Uint32Array(bins);
  for (let i = 0; i < elevations.length; i++) {
    if (filter != null && aspects[i] !== filter) continue;
    const bin = Math.max(0, Math.min(bins - 1, Math.floor((elevations[i] - base) / 25)));
    total[bin]++; if (forest[i]) wooded[bin]++;
  }
  let line: number | null = null;
  for (let bin = 0; bin < bins; bin++) {
    const enough = total[bin] >= 100;
    const fraction = enough ? wooded[bin] / total[bin] : 0;
    const prior = bin === 0 || total[bin - 1] < 100 ? fraction : wooded[bin - 1] / total[bin - 1];
    if (fraction >= 0.2 && prior >= 0.2) line = base + (bin + 1) * 25;
  }
  return line;
}

function elevationQuantile(elevations: Float32Array, quantile: number): number {
  let min = Infinity, max = -Infinity;
  for (const value of elevations) { min = Math.min(min, value); max = Math.max(max, value); }
  if (!Number.isFinite(min) || max <= min) return Number.isFinite(min) ? min : 0;
  const histogram = new Uint32Array(1024);
  for (const value of elevations) histogram[Math.min(1023, Math.floor(((value - min) / (max - min)) * 1024))]++;
  const target = Math.floor(elevations.length * quantile);
  let cumulative = 0;
  for (let bin = 0; bin < histogram.length; bin++) {
    cumulative += histogram[bin];
    if (cumulative >= target) return min + ((bin + 0.5) / histogram.length) * (max - min);
  }
  return max;
}

export function deriveFourClassCover(options: DeriveFourClassOptions): TerrainCoverGrid {
  const { bounds, original, naip, lidar, vectors } = options;
  const dims = dimensions(bounds, options.targetCellM ?? 2);
  const count = dims.width * dims.height;
  const elevations = new Float32Array(count);
  const forest = new Uint8Array(count);
  const water = new Uint8Array(count);
  const waterPolygons = prepareWater(vectors);
  for (let row = 0; row < dims.height; row++) for (let col = 0; col < dims.width; col++) {
    const i = row * dims.width + col;
    const u = (col + 0.5) / dims.width, v = (row + 0.5) / dims.height;
    const worldCode = original.data[indexAt(original.width, original.height, u, v)];
    elevations[i] = sampleElevation(options.elevation.heights, options.elevation.width, options.elevation.height, u, v);
    const vegetation = naip ? ndvi(naip, u, v) > 0.08 : true;
    if (lidar) {
      const height = lidar.maxHeightM[indexAt(lidar.width, lidar.height, u, v)];
      forest[i] = height >= 3 && vegetation ? 1 : 0;
    } else {
      forest[i] = isWorldForest(worldCode) ? 1 : 0;
    }
    const lng = bounds.west + u * (bounds.east - bounds.west);
    const lat = bounds.north - v * (bounds.north - bounds.south);
    // Imagery is deliberately not an independent water detector: dark roofs,
    // snow shadow, and paved surfaces can all mimic water spectrally. Seed the
    // mask from redistribution-safe hydrography, then refine only its edge.
    water[i] = worldCode === 80 || isVectorWater(lng, lat, waterPolygons) ? 1 : 0;
  }

  if (naip) {
    const waterCorridor = boundaryCorridor(water, dims.width, dims.height, Math.max(1, Math.round(6 / dims.cellSizeM)));
    for (let row = 0; row < dims.height; row++) for (let col = 0; col < dims.width; col++) {
      const i = row * dims.width + col;
      if (!waterCorridor[i]) continue;
      const u = (col + 0.5) / dims.width, v = (row + 0.5) / dims.height;
      water[i] = ndwi(naip, u, v) > 0.18 && ndvi(naip, u, v) < 0.05 ? 1 : 0;
    }
    const corridor = boundaryCorridor(forest, dims.width, dims.height, Math.max(1, Math.round(6 / dims.cellSizeM)));
    for (let row = 0; row < dims.height; row++) for (let col = 0; col < dims.width; col++) {
      const i = row * dims.width + col;
      if (!corridor[i] || water[i]) continue;
      const u = (col + 0.5) / dims.width, v = (row + 0.5) / dims.height;
      const vegetation = ndvi(naip, u, v);
      const roughness = texture(naip, u, v);
      if (forest[i] && vegetation < -0.02) forest[i] = 0;
      else if (!forest[i] && vegetation > 0.12 && roughness >= 8) forest[i] = 1;
    }
  }
  const minimumPixels = Math.max(1, Math.ceil(16 / (dims.cellSizeM * dims.cellSizeM)));
  removeTinyComponents(forest, dims.width, dims.height, minimumPixels, 1);
  removeTinyComponents(forest, dims.width, dims.height, minimumPixels, 0);

  const aspectValues = new Uint8Array(count);
  for (let row = 0; row < dims.height; row++) for (let col = 0; col < dims.width; col++) {
    aspectValues[row * dims.width + col] = aspectFor(elevations, dims.width, dims.height, row, col);
  }
  const all = inferredTreeline(elevations, forest, aspectValues) ?? elevationQuantile(elevations, 0.7);
  const treeline = {
    site: all,
    north: inferredTreeline(elevations, forest, aspectValues, 0) ?? all,
    east: inferredTreeline(elevations, forest, aspectValues, 1) ?? all,
    south: inferredTreeline(elevations, forest, aspectValues, 2) ?? all,
    west: inferredTreeline(elevations, forest, aspectValues, 3) ?? all,
  };
  const aspectTreelines = [treeline.north, treeline.east, treeline.south, treeline.west];
  const data = new Uint8Array(count);
  for (let i = 0; i < count; i++) {
    data[i] = water[i] ? TERRAIN_COVER_CODES.water
      : forest[i] ? TERRAIN_COVER_CODES.forest
      : elevations[i] >= aspectTreelines[aspectValues[i]] ? TERRAIN_COVER_CODES.alpine
      : TERRAIN_COVER_CODES.grassland;
  }

  const provenance: TerrainCoverProvenance = {
    processingVersion: 'four-class-v1', confidence: lidar && naip ? 'high' : 'reduced',
    method: lidar && naip ? 'lidar-naip' : lidar ? 'lidar-worldcover' : naip ? 'naip-worldcover' : 'worldcover-fallback',
    attribution: [
      ...(lidar ? ['USGS 3DEP lidar point cloud'] : []),
      ...(naip ? ['USDA/USGS NAIP orthoimagery'] : []),
      'ESA WorldCover 2021 / Contains modified Copernicus Sentinel data',
    ],
    lidar: lidar ? {
      projectId: lidar.projectId, acquisitionYear: lidar.acquisitionYear, resolutionM: lidar.cellSizeM,
      downloadedBytes: lidar.downloadedBytes, license: 'us-government-public-domain',
    } : undefined,
    naip: naip ? {
      sceneIds: naip.sceneIds, sceneNames: naip.sceneNames, acquisitionYear: naip.acquisitionYear,
      agency: naip.agency, resolutionM: naip.resolutionM, license: 'us-government-public-domain',
    } : undefined,
    worldCover: { vintage: '2021', license: 'cc-by-4.0' },
  };
  const vintage = [lidar?.acquisitionYear, naip?.acquisitionYear, 2021].filter((year): year is number => !!year).sort().join('/');
  return { bounds, width: dims.width, height: dims.height, cellSizeM: dims.cellSizeM, data, complete: true, nodataCount: 0, source: 'usgs-four-class-v1', vintage, treelineM: treeline, provenance };
}

export function isFourClassGrid(grid: { source: string }): grid is TerrainCoverGrid {
  return grid.source === 'usgs-four-class-v1';
}
