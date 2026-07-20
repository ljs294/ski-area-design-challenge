import { parse } from '@loaders.gl/core';
import { LASLoader } from '@loaders.gl/las';
import { fromArrayBuffer } from 'geotiff';
import type { LatLonBounds } from './elevation';

const NAIP_SERVICE = 'https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer';
const LIDAR_RESOURCES = 'https://raw.githubusercontent.com/hobuinc/usgs-lidar/master/boundaries/resources.geojson';
const MAX_SOURCE_BYTES = 250 * 1024 * 1024;
const MAX_LIDAR_POINTS = 16_000_000;
const CONUS_STATES = new Set([
  'AL','AZ','AR','CA','CO','CT','DE','FL','GA','ID','IL','IN','IA','KS','KY','LA','ME','MD','MA','MI','MN','MS','MO',
  'MT','NE','NV','NH','NJ','NM','NY','NC','ND','OH','OK','OR','PA','RI','SC','SD','TN','TX','UT','VT','VA','WA','WV','WI','WY',
]);

interface NaipAttributes {
  OBJECTID: number;
  Name: string;
  State: string;
  Year: number;
  agency: string;
  resolution_value: number;
  resolution_units: string;
  band_count: number;
}

export interface NaipAcquisition {
  bounds: LatLonBounds;
  width: number;
  height: number;
  red: Uint8Array;
  green: Uint8Array;
  blue: Uint8Array;
  nir: Uint8Array;
  jpeg?: Uint8Array;
  sceneIds: number[];
  sceneNames: string[];
  acquisitionYear: number;
  agency: 'USDA' | 'USGS';
  resolutionM: number;
}

export interface BareEarthGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  heights: ArrayLike<number>;
}

export interface LidarCanopyGrid {
  bounds: LatLonBounds;
  width: number;
  height: number;
  cellSizeM: number;
  maxHeightM: Float32Array;
  projectId: string;
  acquisitionYear?: number;
  downloadedBytes: number;
}

interface ResourceFeature {
  properties: { name: string; url: string; count: number };
  geometry: { type: string; coordinates: unknown };
}

interface EptMetadata {
  bounds: [number, number, number, number, number, number];
  span: number;
  dataType: 'laszip';
}

function abortError(): DOMException {
  return new DOMException('Terrain-cover preparation cancelled', 'AbortError');
}

async function fetchJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(`Public terrain-data service returned ${response.status}.`);
  return response.json() as Promise<T>;
}

function metersFor(bounds: LatLonBounds): { width: number; height: number } {
  const mid = (bounds.south + bounds.north) * Math.PI / 360;
  return {
    width: Math.abs(bounds.east - bounds.west) * 111_320 * Math.cos(mid),
    height: Math.abs(bounds.north - bounds.south) * 111_320,
  };
}

function dimensionsFor(bounds: LatLonBounds, cellM: number, cap = 4000): { width: number; height: number; cellSizeM: number } {
  const meters = metersFor(bounds);
  const width = Math.max(2, Math.min(cap, Math.round(meters.width / cellM)));
  const height = Math.max(2, Math.min(cap, Math.round(meters.height / cellM)));
  return { width, height, cellSizeM: Math.max(meters.width / width, meters.height / height) };
}

function naipQueryUrl(bounds: LatLonBounds): string {
  const params = new URLSearchParams({
    f: 'json', where: 'Category=1',
    geometry: `${bounds.west},${bounds.south},${bounds.east},${bounds.north}`,
    geometryType: 'esriGeometryEnvelope', inSR: '4326', spatialRel: 'esriSpatialRelIntersects',
    outFields: 'OBJECTID,Name,State,Year,agency,resolution_value,resolution_units,band_count',
    returnGeometry: 'false', resultRecordCount: '50', orderByFields: 'Year DESC',
  });
  return `${NAIP_SERVICE}/query?${params}`;
}

function lockedMosaic(ids: number[]): string {
  return JSON.stringify({ mosaicMethod: 'esriMosaicLockRaster', lockRasterIds: ids, ascending: false });
}

async function makeJpeg(width: number, height: number, bands: Uint8Array[]): Promise<Uint8Array | undefined> {
  if (typeof document === 'undefined') return undefined;
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d');
  if (!context) return undefined;
  const image = context.createImageData(width, height);
  const [red, green, blue] = bands;
  for (let pixel = 0, out = 0; pixel < red.length; pixel++, out += 4) {
    image.data[out] = red[pixel];
    image.data[out + 1] = green[pixel];
    image.data[out + 2] = blue[pixel];
    image.data[out + 3] = 255;
  }
  context.putImageData(image, 0, 0);
  const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.84));
  return blob ? new Uint8Array(await blob.arrayBuffer()) : undefined;
}

/** Download a scene-locked, four-band public-domain NAIP crop. */
export async function fetchNaipAcquisition(
  bounds: LatLonBounds,
  preferredYear?: number,
  signal?: AbortSignal
): Promise<NaipAcquisition | null> {
  try {
    const query = await fetchJson<{ features?: { attributes: NaipAttributes }[]; error?: unknown }>(naipQueryUrl(bounds), signal);
    const allowed = (query.features ?? []).map((feature) => feature.attributes).filter((scene) =>
      Number.isInteger(scene.OBJECTID) && scene.band_count >= 4 && CONUS_STATES.has(scene.State) &&
      (scene.agency === 'USDA' || scene.agency === 'USGS')
    );
    if (!allowed.length) return null;
    const years = [...new Set(allowed.map((scene) => scene.Year))];
    const year = preferredYear == null
      ? Math.max(...years)
      : years.sort((a, b) => Math.abs(a - preferredYear) - Math.abs(b - preferredYear) || b - a)[0];
    const scenes = allowed.filter((scene) => scene.Year === year);
    const dims = dimensionsFor(bounds, 2);
    const params = new URLSearchParams({
      bbox: `${bounds.west},${bounds.south},${bounds.east},${bounds.north}`,
      bboxSR: '4326', imageSR: '4326', size: `${dims.width},${dims.height}`,
      format: 'tiff', pixelType: 'U8', bandIds: '0,1,2,3', noData: '0',
      interpolation: 'RSP_BilinearInterpolation', mosaicRule: lockedMosaic(scenes.map((scene) => scene.OBJECTID)),
      adjustAspectRatio: 'false', f: 'image',
    });
    const response = await fetch(`${NAIP_SERVICE}/exportImage?${params}`, { signal });
    if (!response.ok) return null;
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > MAX_SOURCE_BYTES) return null;
    const tiff = await fromArrayBuffer(bytes);
    const image = await tiff.getImage();
    const rasters = await image.readRasters();
    if (rasters.length < 4) return null;
    const bands = Array.from(rasters).slice(0, 4).map((band) => Uint8Array.from(band as ArrayLike<number>));
    const [west, south, east, north] = image.getBoundingBox();
    return {
      bounds: { west, south, east, north }, width: image.getWidth(), height: image.getHeight(),
      red: bands[0], green: bands[1], blue: bands[2], nir: bands[3],
      jpeg: await makeJpeg(image.getWidth(), image.getHeight(), bands),
      sceneIds: scenes.map((scene) => scene.OBJECTID), sceneNames: scenes.map((scene) => scene.Name),
      acquisitionYear: year, agency: scenes.every((scene) => scene.agency === 'USDA') ? 'USDA' : 'USGS',
      resolutionM: Math.min(...scenes.map((scene) => scene.resolution_value || 1)),
    };
  } catch (error) {
    if (signal?.aborted) throw abortError();
    console.warn('[terrain-cover] NAIP unavailable; using licensed WorldCover fallback.', error);
    return null;
  }
}

function mercator(lng: number, lat: number): [number, number] {
  const x = lng * 20037508.342789244 / 180;
  const clamped = Math.max(-85.05112878, Math.min(85.05112878, lat));
  const y = Math.log(Math.tan((90 + clamped) * Math.PI / 360)) * 20037508.342789244 / Math.PI;
  return [x, y];
}

function coordinateEnvelope(value: unknown, envelope = [Infinity, Infinity, -Infinity, -Infinity]): number[] {
  if (!Array.isArray(value)) return envelope;
  if (value.length >= 2 && typeof value[0] === 'number' && typeof value[1] === 'number') {
    envelope[0] = Math.min(envelope[0], value[0]); envelope[1] = Math.min(envelope[1], value[1]);
    envelope[2] = Math.max(envelope[2], value[0]); envelope[3] = Math.max(envelope[3], value[1]);
  } else for (const child of value) coordinateEnvelope(child, envelope);
  return envelope;
}

function containsBounds(envelope: number[], bounds: LatLonBounds): boolean {
  return envelope[0] <= bounds.west && envelope[1] <= bounds.south && envelope[2] >= bounds.east && envelope[3] >= bounds.north;
}

let resourceIndexPromise: Promise<ResourceFeature[]> | null = null;
function resourceIndex(signal?: AbortSignal): Promise<ResourceFeature[]> {
  if (!resourceIndexPromise) {
    resourceIndexPromise = fetchJson<{ features: ResourceFeature[] }>(LIDAR_RESOURCES, signal)
      .then((data) => data.features)
      .catch((error) => {
        // An aborted terrain preparation must not poison later attempts in the
        // same application session.
        resourceIndexPromise = null;
        throw error;
      });
  }
  return resourceIndexPromise;
}

function acquisitionYear(name: string): number | undefined {
  const years = [...name.matchAll(/(?:19|20)\d{2}/g)].map((match) => Number(match[0]));
  return years.length ? Math.max(...years) : undefined;
}

function nodeBounds(root: EptMetadata['bounds'], key: string): [number, number, number, number] {
  const [depth, x, y] = key.split('-').map(Number);
  const scale = 2 ** depth;
  const width = (root[3] - root[0]) / scale;
  const height = (root[4] - root[1]) / scale;
  return [root[0] + x * width, root[1] + y * height, root[0] + (x + 1) * width, root[1] + (y + 1) * height];
}

function intersects(a: [number, number, number, number], b: [number, number, number, number]): boolean {
  return a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1];
}

async function selectedNodes(
  base: string,
  metadata: EptMetadata,
  query: [number, number, number, number],
  maxDepth: number,
  signal?: AbortSignal
): Promise<{ key: string; points: number }[]> {
  const selected = new Map<string, number>();
  const visited = new Set<string>();
  const visit = async (hierarchyKey: string): Promise<void> => {
    if (visited.has(hierarchyKey)) return;
    visited.add(hierarchyKey);
    const hierarchy = await fetchJson<Record<string, number>>(`${base}/ept-hierarchy/${hierarchyKey}.json`, signal);
    for (const [key, count] of Object.entries(hierarchy)) {
      const depth = Number(key.split('-')[0]);
      if (depth > maxDepth || !intersects(nodeBounds(metadata.bounds, key), query)) continue;
      if (count === -1) await visit(key);
      else if (count > 0) selected.set(key, count);
    }
  };
  await visit('0-0-0-0');
  return [...selected].map(([key, points]) => ({ key, points })).sort((a, b) => Number(a.key.split('-')[0]) - Number(b.key.split('-')[0]) || a.key.localeCompare(b.key));
}

function sampleBareEarth(grid: BareEarthGrid, x: number, y: number): number {
  const u = (x - grid.bounds.west) / (grid.bounds.east - grid.bounds.west);
  const v = (grid.bounds.north - y) / (grid.bounds.north - grid.bounds.south);
  const col = Math.max(0, Math.min(grid.width - 1, Math.round(u * (grid.width - 1))));
  const row = Math.max(0, Math.min(grid.height - 1, Math.round(v * (grid.height - 1))));
  return grid.heights[row * grid.width + col];
}

/** Stream progressive EPT nodes and rasterize maximum height above bare earth. */
export async function fetchLidarCanopyGrid(
  bounds: LatLonBounds,
  bareEarth: BareEarthGrid,
  signal?: AbortSignal
): Promise<LidarCanopyGrid | null> {
  try {
    const resources = await resourceIndex(signal);
    const candidates = resources
      .map((feature) => ({ feature, envelope: coordinateEnvelope(feature.geometry.coordinates), year: acquisitionYear(feature.properties.name) }))
      .filter(({ envelope }) => containsBounds(envelope, bounds))
      .sort((a, b) => (b.year ?? 0) - (a.year ?? 0) || a.feature.properties.count - b.feature.properties.count);
    const resource = candidates[0]?.feature;
    if (!resource) return null;
    const base = resource.properties.url.replace(/\/ept\.json$/, '');
    const metadata = await fetchJson<EptMetadata>(resource.properties.url, signal);
    if (metadata.dataType !== 'laszip') return null;
    const [westM, southM] = mercator(bounds.west, bounds.south);
    const [eastM, northM] = mercator(bounds.east, bounds.north);
    const query: [number, number, number, number] = [westM, southM, eastM, northM];
    const rootSpanM = Math.max(metadata.bounds[3] - metadata.bounds[0], metadata.bounds[4] - metadata.bounds[1]);
    let cellSizeM = 4;
    let depth = Math.max(0, Math.min(14, Math.ceil(Math.log2(rootSpanM / (cellSizeM * metadata.span)))));
    let nodes = await selectedNodes(base, metadata, query, depth, signal);
    while (nodes.reduce((sum, node) => sum + node.points, 0) > MAX_LIDAR_POINTS && depth > 0) {
      cellSizeM = 5;
      depth--;
      nodes = await selectedNodes(base, metadata, query, depth, signal);
    }
    const dims = dimensionsFor(bounds, cellSizeM);
    const heights = new Float32Array(dims.width * dims.height);
    let downloadedBytes = 0;
    let acceptedPoints = 0;
    for (let start = 0; start < nodes.length; start += 4) {
      if (signal?.aborted) throw abortError();
      const batch = nodes.slice(start, start + 4);
      const decoded = await Promise.all(batch.map(async (node) => {
        const response = await fetch(`${base}/ept-data/${node.key}.laz`, { signal });
        if (!response.ok) throw new Error(`USGS lidar tile returned ${response.status}.`);
        const buffer = await response.arrayBuffer();
        downloadedBytes += buffer.byteLength;
        if (downloadedBytes > MAX_SOURCE_BYTES) throw new Error('Lidar download budget exceeded.');
        return parse(buffer, LASLoader, { worker: false, las: { skip: 1 } }) as Promise<any>;
      }));
      for (const cloud of decoded) {
        const positions = cloud.attributes?.POSITION?.value as ArrayLike<number> | undefined;
        const classes = cloud.attributes?.classification?.value as ArrayLike<number> | undefined;
        if (!positions) continue;
        for (let point = 0; point + 2 < positions.length; point += 3) {
          const x = positions[point], y = positions[point + 1], z = positions[point + 2];
          if (x < westM || x > eastM || y < southM || y > northM) continue;
          const classification = classes?.[point / 3] ?? 1;
          if (classification === 7 || classification === 18) continue;
          const col = Math.max(0, Math.min(dims.width - 1, Math.floor(((x - westM) / (eastM - westM)) * dims.width)));
          const row = Math.max(0, Math.min(dims.height - 1, Math.floor(((northM - y) / (northM - southM)) * dims.height)));
          const lng = bounds.west + ((col + 0.5) / dims.width) * (bounds.east - bounds.west);
          const lat = bounds.north - ((row + 0.5) / dims.height) * (bounds.north - bounds.south);
          const aboveGround = z - sampleBareEarth(bareEarth, lng, lat);
          if (aboveGround >= 0 && aboveGround < 120) {
            acceptedPoints++;
            if (aboveGround > heights[row * dims.width + col]) heights[row * dims.width + col] = aboveGround;
          }
        }
      }
    }
    if (acceptedPoints === 0) return null;
    return {
      bounds, width: dims.width, height: dims.height, cellSizeM: dims.cellSizeM,
      maxHeightM: heights, projectId: resource.properties.name,
      acquisitionYear: acquisitionYear(resource.properties.name), downloadedBytes,
    };
  } catch (error) {
    if (signal?.aborted) throw abortError();
    console.warn('[terrain-cover] Progressive lidar unavailable; using imagery refinement.', error);
    return null;
  }
}
