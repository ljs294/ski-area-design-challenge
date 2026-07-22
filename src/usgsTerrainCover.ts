import { fromArrayBuffer } from 'geotiff';
import type { LatLonBounds } from './elevation';

const NAIP_SERVICE = 'https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer';
const MAX_SOURCE_BYTES = 250 * 1024 * 1024;
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
