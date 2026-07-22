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

export interface ElevationGrid {
  /** Row-major heights (meters), row 0 = north edge. */
  heights: number[];
  /**
   * The raster's TRUE geographic extent, read from the returned GeoTIFF's own
   * georeferencing — NOT the requested bbox. ArcGIS `exportImage` snaps the
   * output extent to the requested pixel-`size` aspect ratio: ask for a square
   * grid over a bbox that is a rectangle in degrees (which a square-in-meters
   * site always is off the equator) and the service silently expands the
   * shorter axis, returning a taller/wider area than requested. Trusting the
   * request instead of this value is what mis-registered every downstream layer
   * (hillshade, contours, 3D mesh) against the satellite imagery.
   */
  bounds: LatLonBounds;
  /** Raster dimensions actually returned (may differ from the requested size). */
  width: number;
  height: number;
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

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.reject(new DOMException('Elevation download cancelled', 'AbortError'));
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      reject(new DOMException('Elevation download cancelled', 'AbortError'));
    }, { once: true });
  });
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
  onProgress?: (progress: ElevationProgress) => void,
  signal?: AbortSignal
): Promise<ArrayBuffer> {
  let genericAttempt = 0;

  for (;;) {
    try {
      onProgress?.({ phase: 'fetching' });
      const response = await fetch(exportImageUrl(bounds, gridSize), { signal });
      if (response.ok) return await response.arrayBuffer();

      if (response.status >= 500 && gridSize > MIN_GRID_DIMENSION) {
        gridSize = Math.max(MIN_GRID_DIMENSION, Math.round(gridSize / 2));
        continue;
      }
      throw new Error(`USGS elevation service returned ${response.status}`);
    } catch (e) {
      if (signal?.aborted || (e instanceof DOMException && e.name === 'AbortError')) throw e;
      if (genericAttempt >= MAX_RETRIES) {
        // A bare fetch TypeError ("Failed to fetch") is opaque — name the stage
        // and the likely cause so the failure is actionable, not mysterious.
        if (e instanceof TypeError) {
          throw new Error('Could not reach the USGS elevation service (network error). Check your internet connection and try again.');
        }
        throw e;
      }
      genericAttempt++;
      await sleep(RETRY_BASE_MS * genericAttempt, signal);
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
 *
 * Returns the true extent the service actually rendered (see
 * `ElevationGrid.bounds`), which every caller must adopt as the site's
 * canonical bounds so the elevation, ground cover, contours, and satellite
 * all share one footprint.
 */
export async function fetchElevationGrid(
  bounds: LatLonBounds,
  areaSizeMeters: number,
  onProgress?: (progress: ElevationProgress) => void,
  signal?: AbortSignal
): Promise<ElevationGrid> {
  if (!isUsCoverage(bounds)) {
    throw new Error(
      'Elevation data is currently only available for locations within the United States.'
    );
  }

  const arrayBuffer = await fetchWithShrink(bounds, sampleGridSizeFor(areaSizeMeters), onProgress, signal);

  onProgress?.({ phase: 'decoding' });
  const tiff = await fromArrayBuffer(arrayBuffer);
  const image = await tiff.getImage();
  const rasters = await image.readRasters();
  const band = rasters[0] as unknown as ArrayLike<number>;

  // [minX, minY, maxX, maxY] in the image SR (4326) — the authoritative extent,
  // not the requested bbox. For a north-up 4326 raster this is [W, S, E, N].
  const [west, south, east, north] = image.getBoundingBox();
  return {
    heights: Array.from(band),
    bounds: { west, south, east, north },
    width: image.getWidth(),
    height: image.getHeight(),
  };
}

// --- Offline perimeter ring ----------------------------------------------
//
// A medium-resolution elevation ring fetched around the high-res core so the 3D
// view shows real neighbouring relief (hillshaded like the core) instead of a
// cliff at the property line, terminating in a clean floating-clip edge. One
// extra USGS export at capture time, embedded in the package JSON (a few MB, no
// binary sidecar). Elevation only — ground cover / slope / aspect stay clamped
// to the play box. See TerrainRecord.surround.

/** How far the perimeter ring extends past the property line, in metres per
 *  side. The rendered map is the play box plus this margin on all four edges —
 *  real neighbouring relief for context, with the camera still locked to the box. */
export const PERIMETER_MARGIN_M = 3000;
/** Grid dimension for the perimeter ring. ~1024² gives ~10 m spacing over a
 *  box+3 km extent — coarse enough to stay a few MB as JSON (no binary sidecar),
 *  fine enough that the shaded relief reads like the core, not a blurry blob. */
export const PERIMETER_GRID_SIZE = 1024;
/** USGS 3DEP returns this for cells with no coverage (e.g. across a border or
 *  offshore). Stored verbatim and treated as transparent when sampling. */
export const SURROUND_NODATA = -9999;

/** Grow a bbox outward by a fixed distance (metres) on every side. Longitude
 *  degrees-per-metre is taken at the box centre latitude — good to well under a
 *  pixel over a few km, and the exact extent is re-derived from the returned
 *  raster anyway (see fetchElevationBuffer). */
export function expandBoundsByMeters(b: LatLonBounds, meters: number): LatLonBounds {
  const dLat = meters / 111320;
  const cy = (b.south + b.north) / 2;
  const dLon = meters / (111320 * Math.max(Math.cos((cy * Math.PI) / 180), 1e-6));
  return { west: b.west - dLon, east: b.east + dLon, south: b.south - dLat, north: b.north + dLat };
}

export interface SurroundGrid {
  heights: number[];
  bounds: LatLonBounds;
  width: number;
  height: number;
}

/**
 * Fetch the perimeter ring around a core extent — the play box grown by
 * PERIMETER_MARGIN_M on every side, sampled at PERIMETER_GRID_SIZE. Best-effort:
 * any failure (network, service error, out-of-coverage) resolves to null so a
 * resort is never blocked on its cosmetic ring — it just renders without one.
 * Unlike the core fetch this does not shrink-and-retry: a single ~1024² export
 * is still well below the size that stresses the service.
 */
export async function fetchElevationBuffer(
  coreBounds: LatLonBounds,
  signal?: AbortSignal
): Promise<SurroundGrid | null> {
  const requested = expandBoundsByMeters(coreBounds, PERIMETER_MARGIN_M);
  try {
    let response: Response | null = null;
    for (let attempt = 0; attempt <= MAX_RETRIES; attempt++) {
      try {
        response = await fetch(exportImageUrl(requested, PERIMETER_GRID_SIZE), { signal });
        if (response.ok) break;
        response = null;
      } catch (e) {
        if (signal?.aborted || (e instanceof DOMException && e.name === 'AbortError')) throw e;
      }
      if (attempt < MAX_RETRIES) await sleep(RETRY_BASE_MS * (attempt + 1), signal);
    }
    if (!response) return null;

    const tiff = await fromArrayBuffer(await response.arrayBuffer());
    const image = await tiff.getImage();
    const rasters = await image.readRasters();
    const band = rasters[0] as unknown as ArrayLike<number>;
    const [west, south, east, north] = image.getBoundingBox();
    // Store to the decimetre: this ring embeds in the record JSON, and at ~10 m
    // horizontal spacing sub-decimetre vertical precision is invisible while
    // trimming the serialized array by roughly a third. Nodata (-9999) is exact.
    return {
      heights: Array.from(band, (h) => Math.round(h * 10) / 10),
      bounds: { west, south, east, north },
      width: image.getWidth(),
      height: image.getHeight(),
    };
  } catch (e) {
    if (signal?.aborted || (e instanceof DOMException && e.name === 'AbortError')) throw e;
    console.error('Perimeter elevation ring fetch failed; resort will render without one:', e);
    return null;
  }
}
