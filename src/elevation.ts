// Fetches a fixed-size real-world elevation grid from the free Open-Meteo
// elevation API (SRTM-derived, ~90m accuracy, no API key required).
//
// The endpoint enforces a low, undocumented burst rate limit — empirically,
// under sustained load it did not reliably sustain more than roughly half a
// dozen 100-point requests before 429ing, and prolonged heavy use can also
// surface plain 503s (the provider transiently overloaded), not just 429s.
// A download is a one-time, per-resort cost, so this trades speed for
// reliability: requests are sequential and deliberately paced
// (INTER_BATCH_DELAY_MS) so the download generally never hits the limit in
// the first place, rather than relying on reactive backoff to dig out of
// it. The reactive backoff below (for both 429 and 5xx) still exists as a
// safety net for contention, not as the primary strategy — a full download
// is expected to take a couple of minutes, which is fine since the user
// only pays this cost once per spot.

export const SAMPLE_GRID_SIZE = 64;

const BATCH_SIZE = 100; // the API's own hard max coordinates per request
const MAX_CONCURRENT_BATCHES = 1;
const INTER_BATCH_DELAY_MS = 3500;
const INTER_BATCH_JITTER_MS = 500;

const MAX_GENERIC_RETRIES = 2; // network errors, malformed responses — genuinely unexpected
const GENERIC_RETRY_BASE_MS = 300;

// 429 (rate limited) and 5xx (the provider is transiently overloaded — which
// under sustained load turned out to include plain 503s, not just 429s) are
// both treated as recoverable and get the same generous backoff/retry
// budget, since a paced download is meant to ride these out rather than
// give up on them.
const MAX_RECOVERABLE_RETRIES = 12;
const RECOVERABLE_BACKOFF_BASE_MS = 5000;
const RECOVERABLE_BACKOFF_MAX_MS = 30000;

export interface LatLonBounds {
  south: number;
  north: number;
  west: number;
  east: number;
}

export interface ElevationProgress {
  completedBatches: number;
  totalBatches: number;
  /** True once at least one batch has hit a recoverable (429/5xx) error and is backing off. */
  rateLimited: boolean;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function fetchBatch(
  latBatch: number[],
  lonBatch: number[],
  onRecoverableError?: () => void
): Promise<number[]> {
  let genericAttempt = 0;
  let recoverableAttempt = 0;

  for (;;) {
    try {
      const latString = latBatch.join(',');
      const lonString = lonBatch.join(',');
      const response = await fetch(
        `https://api.open-meteo.com/v1/elevation?latitude=${latString}&longitude=${lonString}`
      );

      const isRecoverable = response.status === 429 || response.status >= 500;
      if (isRecoverable) {
        if (recoverableAttempt >= MAX_RECOVERABLE_RETRIES) {
          throw new Error(`Elevation API returned ${response.status} after repeated retries`);
        }
        onRecoverableError?.();
        const backoff = Math.min(
          RECOVERABLE_BACKOFF_MAX_MS,
          RECOVERABLE_BACKOFF_BASE_MS * 2 ** recoverableAttempt
        );
        const jitter = Math.random() * 500;
        recoverableAttempt++;
        await sleep(backoff + jitter);
        continue;
      }

      if (!response.ok) throw new Error(`Elevation API returned ${response.status}`);

      const data = await response.json();
      if (!data || !Array.isArray(data.elevation) || data.elevation.length !== latBatch.length) {
        throw new Error('Invalid elevation API response shape');
      }
      return data.elevation;
    } catch (e) {
      if (genericAttempt >= MAX_GENERIC_RETRIES) throw e;
      genericAttempt++;
      await sleep(GENERIC_RETRY_BASE_MS * genericAttempt);
    }
  }
}

/**
 * Sample a fixed SAMPLE_GRID_SIZE x SAMPLE_GRID_SIZE elevation grid across
 * the given bounds. Grid is row-major, row 0 = south edge, col 0 = west edge
 * (matches the existing renderer convention).
 */
export async function fetchElevationGrid(
  bounds: LatLonBounds,
  onProgress?: (progress: ElevationProgress) => void
): Promise<number[]> {
  const resolution = SAMPLE_GRID_SIZE;
  const points: { lat: number; lon: number }[] = [];

  for (let r = 0; r < resolution; r++) {
    const latFraction = r / (resolution - 1);
    const lat = bounds.south + (bounds.north - bounds.south) * latFraction;
    for (let c = 0; c < resolution; c++) {
      const lonFraction = c / (resolution - 1);
      const lon = bounds.west + (bounds.east - bounds.west) * lonFraction;
      points.push({ lat, lon });
    }
  }

  const totalBatches = Math.ceil(points.length / BATCH_SIZE);
  const results: number[][] = new Array(totalBatches);
  let completedBatches = 0;
  let nextBatchIndex = 0;
  let rateLimited = false;

  async function worker(): Promise<void> {
    while (nextBatchIndex < totalBatches) {
      const batchIdx = nextBatchIndex++;
      const start = batchIdx * BATCH_SIZE;
      const end = Math.min(start + BATCH_SIZE, points.length);
      const slice = points.slice(start, end);

      if (batchIdx > 0) {
        // Deliberate pacing: spread requests out so the provider's burst
        // limit generally never triggers, rather than hitting it and
        // recovering reactively.
        await sleep(INTER_BATCH_DELAY_MS + Math.random() * INTER_BATCH_JITTER_MS);
      }

      results[batchIdx] = await fetchBatch(
        slice.map((p) => p.lat),
        slice.map((p) => p.lon),
        () => {
          rateLimited = true;
          onProgress?.({ completedBatches, totalBatches, rateLimited });
        }
      );
      completedBatches++;
      onProgress?.({ completedBatches, totalBatches, rateLimited });
    }
  }

  const workerCount = Math.min(MAX_CONCURRENT_BATCHES, totalBatches);
  await Promise.all(Array.from({ length: workerCount }, () => worker()));

  return results.flat();
}
