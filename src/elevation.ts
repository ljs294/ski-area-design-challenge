// Fetches a real-world elevation grid from the USGS 3DEP ImageServer's
// `exportImage` endpoint — a free, no-key government service that returns
// an entire resampled elevation raster (at whatever grid size you request)
// in a single HTTP call, as a GeoTIFF, automatically mosaicked from
// whatever the best locally-available source is (down to 1m LiDAR in many
// mountain/recreational areas, no finer than ~10m nationally elsewhere).
//
// The service's *documented* limit is maxImageWidth/Height: 8000, but that
// is not the real reliability ceiling — empirically, requests above
// roughly 2000-2500px on a side started intermittently failing with
// 500/504 errors (probably server-side mosaic/resample cost, not a hard
// cap), while 2000px reliably completed in ~12s. So the grid dimension is
// capped well under the documented limit, with shrink-and-retry as a
// safety net for the still-observed variance at that size.
import { fromArrayBuffer } from 'geotiff';

const EXPORT_IMAGE_URL = 'https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage';

// Empirically reliable ceiling (see comment above) — not the service's
// documented 8000px max, which fails in practice well before that.
const MAX_GRID_DIMENSION = 2000;
const MIN_GRID_DIMENSION = 500;
const MAX_RETRIES = 2;
const RETRY_BASE_MS = 500;

export interface LatLonBounds {
  south: number;
  north: number;
  west: number;
  east: number;
}

export interface ElevationProgress {
  phase: 'fetching' | 'decoding';
}

/**
 * Grid size to request. Deliberately NOT scaled down for smaller areas —
 * smaller areas just end up with finer real-world spacing at the same
 * pixel budget (e.g. ~1m at 2km vs ~4m at 8km), which is exactly what we
 * want: the ceiling here is about output raster size, not physical area.
 */
export function sampleGridSizeFor(_areaSizeMeters: number): number {
  return MAX_GRID_DIMENSION;
}

// Rough bounding boxes for CONUS, Alaska, and Hawaii — generous enough to
// avoid rejecting valid coastal/border selections, without pulling in a
// full geo/timezone dependency just for this check. USGS 3DEP is US-only.
const US_COVERAGE_BOXES: LatLonBounds[] = [
  { south: 24, north: 50, west: -125, east: -66 }, // contiguous US
  { south: 51, north: 72, west: -170, east: -129 }, // Alaska
  { south: 18, north: 23, west: -161, east: -154 }, // Hawaii
];

function boundsWithinBox(bounds: LatLonBounds, box: LatLonBounds): boolean {
  return (
    bounds.south >= box.south &&
    bounds.north <= box.north &&
    bounds.west >= box.west &&
    bounds.east <= box.east
  );
}

export function isUsCoverage(bounds: LatLonBounds): boolean {
  return US_COVERAGE_BOXES.some((box) => boundsWithinBox(bounds, box));
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function exportImageUrl(bounds: LatLonBounds, gridSize: number): string {
  const bbox = `${bounds.west},${bounds.south},${bounds.east},${bounds.north}`;
  return (
    `${EXPORT_IMAGE_URL}?bbox=${bbox}&bboxSR=4326&imageSR=4326` +
    `&size=${gridSize},${gridSize}&format=tiff&pixelType=F32&noData=-9999` +
    `&interpolation=RSP_BilinearInterpolation&f=image`
  );
}

/**
 * Fetch at the given grid size, shrinking (halving) and retrying on
 * server-side failures (5xx) — these show up intermittently at the larger
 * end of MAX_GRID_DIMENSION even though smaller requests over the same
 * area succeed, so a smaller request is a meaningfully different (more
 * likely to succeed) attempt, not just a repeat.
 */
async function fetchWithShrink(
  bounds: LatLonBounds,
  gridSize: number,
  onProgress?: (progress: ElevationProgress) => void
): Promise<ArrayBuffer> {
  let genericAttempt = 0;

  for (;;) {
    try {
      onProgress?.({ phase: 'fetching' });
      const response = await fetch(exportImageUrl(bounds, gridSize));
      if (response.ok) return await response.arrayBuffer();

      if (response.status >= 500 && gridSize > MIN_GRID_DIMENSION) {
        gridSize = Math.max(MIN_GRID_DIMENSION, Math.round(gridSize / 2));
        continue;
      }
      throw new Error(`USGS elevation service returned ${response.status}`);
    } catch (e) {
      if (genericAttempt >= MAX_RETRIES) throw e;
      genericAttempt++;
      await sleep(RETRY_BASE_MS * genericAttempt);
    }
  }
}

/**
 * Fetch an elevation grid (meters) across the given bounds in a single
 * request (shrinking and retrying under the hood if the service falters at
 * the requested size). Grid is row-major, row 0 = north edge, col 0 = west
 * edge — this matches the raw GeoTIFF raster's natural orientation (row 0 =
 * north) exactly, which is also what the renderer expects (row 0 maps to
 * canvas y=0, the top of the screen — so north stays up).
 */
export async function fetchElevationGrid(
  bounds: LatLonBounds,
  areaSizeMeters: number,
  onProgress?: (progress: ElevationProgress) => void
): Promise<number[]> {
  if (!isUsCoverage(bounds)) {
    throw new Error(
      'Elevation data is currently only available for locations within the United States.'
    );
  }

  const arrayBuffer = await fetchWithShrink(bounds, sampleGridSizeFor(areaSizeMeters), onProgress);

  onProgress?.({ phase: 'decoding' });
  const tiff = await fromArrayBuffer(arrayBuffer);
  const image = await tiff.getImage();
  const rasters = await image.readRasters();
  const band = rasters[0] as unknown as ArrayLike<number>;

  return Array.from(band);
}
