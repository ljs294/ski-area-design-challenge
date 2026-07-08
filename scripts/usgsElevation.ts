// USGS Elevation Point Query Service (EPQS) — used ONLY by the one-off
// preset-capture script (downloadPresetTerrain.ts), as a fallback data
// source for a specific preset. The live in-app picker (src/elevation.ts)
// stays on Open-Meteo, unchanged — this is a separate government service
// (US coverage only, free, no API key) that happened to tolerate real
// concurrency well where Open-Meteo did not, when Open-Meteo got heavily
// throttled during development. Single point per request, so this fetcher
// leans on concurrency rather than large batches.

export interface LatLonBounds {
  south: number;
  north: number;
  west: number;
  east: number;
}

export interface UsgsProgress {
  completed: number;
  total: number;
}

const CONCURRENCY = 16;
const MAX_RETRIES = 3;

async function fetchPoint(lat: number, lon: number): Promise<number> {
  let lastError: unknown;
  for (let attempt = 0; attempt <= MAX_RETRIES; attempt++) {
    try {
      const url = `https://epqs.nationalmap.gov/v1/json?x=${lon}&y=${lat}&wkid=4326&units=Meters&includeDate=false`;
      const res = await fetch(url);
      if (!res.ok) throw new Error(`USGS EPQS returned ${res.status}`);
      const data = await res.json();
      const value = parseFloat(data.value);
      if (!Number.isFinite(value)) throw new Error(`Invalid USGS EPQS value: ${data.value}`);
      return value;
    } catch (e) {
      lastError = e;
      if (attempt < MAX_RETRIES) await new Promise((resolve) => setTimeout(resolve, 500 * (attempt + 1)));
    }
  }
  throw lastError instanceof Error ? lastError : new Error('USGS EPQS point fetch failed');
}

/**
 * Sample a gridSize x gridSize elevation grid across the given bounds.
 * Row-major, row 0 = south edge, col 0 = west edge (matches src/elevation.ts's
 * convention so the output is a drop-in replacement).
 */
export async function fetchUsgsElevationGrid(
  bounds: LatLonBounds,
  gridSize: number,
  onProgress?: (progress: UsgsProgress) => void
): Promise<number[]> {
  const points: { lat: number; lon: number }[] = [];
  for (let r = 0; r < gridSize; r++) {
    const latFraction = r / (gridSize - 1);
    const lat = bounds.south + (bounds.north - bounds.south) * latFraction;
    for (let c = 0; c < gridSize; c++) {
      const lonFraction = c / (gridSize - 1);
      const lon = bounds.west + (bounds.east - bounds.west) * lonFraction;
      points.push({ lat, lon });
    }
  }

  const results: number[] = new Array(points.length);
  let completed = 0;
  let nextIndex = 0;

  async function worker(): Promise<void> {
    while (nextIndex < points.length) {
      const idx = nextIndex++;
      results[idx] = await fetchPoint(points[idx].lat, points[idx].lon);
      completed++;
      onProgress?.({ completed, total: points.length });
    }
  }

  const workerCount = Math.min(CONCURRENCY, points.length);
  await Promise.all(Array.from({ length: workerCount }, () => worker()));

  return results;
}
