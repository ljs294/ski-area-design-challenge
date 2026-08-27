import type { TerrainRecord } from './types/terrain';
import type { WeatherDataPackage } from './weather/weatherModel';
import { validateWeatherPackage } from './weatherStorageClient';
import { weatherTerrainBinding } from './weather/terrainBinding';

const DEFAULT_SERVICE_URL = 'http://127.0.0.1:8787';
const SOURCE_POLICY_VERSION = 'daymet-v4r1-merra2-ghcnh-v1';

export interface WeatherPackageRequest {
  schemaVersion: 1;
  terrainKey: string;
  terrainBinding: string;
  latitude: number;
  longitude: number;
  bounds: TerrainRecord['bounds'];
  areaSizeMeters: number;
  /** The hosted builder resolves coordinates to an explicit IANA zone. */
  timezone: 'auto';
  historicalStartYear: 1991;
  historicalEndYear: 2020;
  sourcePolicyVersion: string;
}

export type WeatherBuildStage =
  | 'queued'
  | 'validating'
  | 'daymet'
  | 'merra2'
  | 'ghcnh'
  | 'normalizing'
  | 'packing'
  | 'installing'
  | 'complete';

export interface WeatherBuildProgress {
  stage: WeatherBuildStage | string;
  completed: number;
  total?: number;
  message: string;
  updatedAt: string;
}

export interface WeatherBuildFailure {
  code: string;
  message: string;
  retryable: boolean;
  details?: unknown;
}

export interface WeatherBuildJob {
  id: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  request: WeatherPackageRequest;
  progress: WeatherBuildProgress;
  createdAt: string;
  updatedAt: string;
  result?: {
    contentHash: string;
    manifestUrl: string;
    chunkUrls: string[];
  };
  error?: WeatherBuildFailure;
}

export class WeatherServiceError extends Error {
  readonly code: string;
  readonly retryable: boolean;
  readonly details?: unknown;
  readonly status?: number;

  constructor(message: string, options: {
    code?: string;
    retryable?: boolean;
    details?: unknown;
    status?: number;
  } = {}) {
    super(message);
    this.name = 'WeatherServiceError';
    this.code = options.code ?? 'WEATHER_SERVICE_ERROR';
    this.retryable = options.retryable ?? false;
    this.details = options.details;
    this.status = options.status;
  }
}

function serviceUrl(): string {
  return import.meta.env.VITE_WEATHER_SERVICE_URL || DEFAULT_SERVICE_URL;
}

function endpoint(path: string): string {
  return `${serviceUrl().replace(/\/$/, '')}${path}`;
}

function resourceUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) return path;
  return path.startsWith('/') ? endpoint(path) : endpoint(`/${path}`);
}

function errorFromPayload(payload: unknown, status?: number): WeatherServiceError {
  const candidate = payload && typeof payload === 'object'
    ? (payload as { error?: Partial<WeatherBuildFailure> }).error
    : undefined;
  if (candidate?.message) {
    return new WeatherServiceError(candidate.message, {
      code: candidate.code,
      retryable: candidate.retryable,
      details: candidate.details,
      status,
    });
  }
  return new WeatherServiceError(
    status ? `Weather service returned ${status}.` : 'Weather service returned an invalid response.',
    { code: status ? `HTTP_${status}` : 'INVALID_RESPONSE', retryable: !!status && status >= 500, status },
  );
}

async function jsonResponse<T>(response: Response): Promise<T> {
  let payload: unknown = null;
  try { payload = await response.json(); } catch { /* handled below */ }
  if (!response.ok) throw errorFromPayload(payload, response.status);
  if (payload === null) throw errorFromPayload(null, response.status);
  return payload as T;
}

function weatherBuildJobFromPayload(payload: unknown): WeatherBuildJob | null {
  if (payload && typeof payload === 'object' &&
    typeof (payload as Partial<WeatherBuildJob>).id === 'string') return payload as WeatherBuildJob;
  const wrapped = payload && typeof payload === 'object' ? (payload as { job?: unknown }).job : undefined;
  if (wrapped && typeof wrapped === 'object' && typeof (wrapped as Partial<WeatherBuildJob>).id === 'string') {
    return wrapped as WeatherBuildJob;
  }
  return null;
}

/** The versioned request sent to the project-owned package builder. */
export function weatherPackageRequest(record: TerrainRecord): WeatherPackageRequest {
  return {
    schemaVersion: 1,
    terrainKey: record.key,
    terrainBinding: weatherTerrainBinding(record),
    latitude: record.latitude,
    longitude: record.longitude,
    bounds: record.bounds,
    areaSizeMeters: record.areaSizeMeters,
    // A coordinate-to-timezone boundary dataset is held by the builder. The
    // package manifest always records the resolved, concrete IANA identifier.
    timezone: 'auto',
    historicalStartYear: 1991,
    historicalEndYear: 2020,
    sourcePolicyVersion: SOURCE_POLICY_VERSION,
  };
}

export async function createWeatherPackageJob(
  record: TerrainRecord,
  signal?: AbortSignal,
): Promise<WeatherBuildJob> {
  try {
    const response = await fetch(endpoint('/v1/weather-package-jobs'), {
      method: 'POST', signal, headers: { 'content-type': 'application/json' },
      body: JSON.stringify(weatherPackageRequest(record)),
    });
    const payload = await jsonResponse<unknown>(response);
    const job = weatherBuildJobFromPayload(payload);
    if (!job || typeof job.id !== 'string') throw errorFromPayload(null, response.status);
    return job;
  } catch (error) {
    if (error instanceof WeatherServiceError || error instanceof DOMException) throw error;
    throw new WeatherServiceError(error instanceof Error ? error.message : 'Unable to start weather preparation.', {
      code: 'WEATHER_SERVICE_UNREACHABLE', retryable: true,
    });
  }
}

export async function loadWeatherPackageJob(id: string, signal?: AbortSignal): Promise<WeatherBuildJob> {
  const response = await fetch(endpoint(`/v1/weather-package-jobs/${encodeURIComponent(id)}`), { signal });
  const payload = await jsonResponse<unknown>(response);
  const job = weatherBuildJobFromPayload(payload);
  if (!job || typeof job.id !== 'string') throw errorFromPayload(null, response.status);
  return job;
}

export async function cancelWeatherPackageJob(id: string): Promise<WeatherBuildJob> {
  const response = await fetch(endpoint(`/v1/weather-package-jobs/${encodeURIComponent(id)}`), { method: 'DELETE' });
  const payload = await jsonResponse<unknown>(response);
  const job = weatherBuildJobFromPayload(payload);
  if (!job || typeof job.id !== 'string') throw errorFromPayload(null, response.status);
  return job;
}

/**
 * Poll a preparation job. The callback is deliberately UI-agnostic and is
 * also useful to Electron callers; only this explicit preparation path ever
 * reaches the network.
 */
export async function waitForWeatherPackageJob(
  id: string,
  options: { signal?: AbortSignal; onProgress?: (job: WeatherBuildJob) => void; pollMs?: number } = {},
): Promise<WeatherBuildJob> {
  const pollMs = Math.max(100, options.pollMs ?? 350);
  for (;;) {
    const job = await loadWeatherPackageJob(id, options.signal);
    options.onProgress?.(job);
    if (job.status === 'succeeded') return job;
    if (job.status === 'failed') {
      const failure = job.error ?? { code: 'PACKAGE_BUILD_FAILED', message: 'Weather package preparation failed.', retryable: false };
      throw new WeatherServiceError(failure.message, failure);
    }
    if (job.status === 'cancelled') {
      throw new WeatherServiceError('Weather package preparation was cancelled.', {
        code: 'PACKAGE_BUILD_CANCELLED', retryable: true,
      });
    }
    await new Promise<void>((resolve, reject) => {
      const timer = window.setTimeout(resolve, pollMs);
      const abort = () => {
        window.clearTimeout(timer);
        reject(options.signal?.reason ?? new DOMException('Aborted', 'AbortError'));
      };
      options.signal?.addEventListener('abort', abort, { once: true });
    });
  }
}

/**
 * Fetch a completed package through the compatibility endpoint. The service
 * answers from its completed canonical cache, so this does not invoke an
 * upstream provider a second time. Keeping this adapter lets older package
 * readers coexist while v2 binary chunk installation rolls out.
 */
export async function downloadWeatherPackage(record: TerrainRecord, signal?: AbortSignal): Promise<WeatherDataPackage> {
  try {
    const response = await fetch(endpoint('/v1/weather-packages'), {
      method: 'POST', signal, headers: { 'content-type': 'application/json' },
      body: JSON.stringify(weatherPackageRequest(record)),
    });
    const weatherPackage: unknown = await jsonResponse<unknown>(response);
    if (!validateWeatherPackage(weatherPackage)) {
      throw new WeatherServiceError('Weather service returned an invalid or incomplete package.', {
        code: 'INVALID_PACKAGE', retryable: false,
      });
    }
    if (weatherPackage.manifest.terrainBinding !== weatherTerrainBinding(record)) {
      throw new WeatherServiceError('Weather service returned a package for a different terrain map.', {
        code: 'TERRAIN_BINDING_MISMATCH', retryable: false,
      });
    }
    return weatherPackage;
  } catch (error) {
    if (error instanceof WeatherServiceError || error instanceof DOMException) throw error;
    throw new WeatherServiceError(error instanceof Error ? error.message : 'Unable to download the weather package.', {
      code: 'WEATHER_SERVICE_UNREACHABLE', retryable: true,
    });
  }
}

function base64FromBytes(bytes: Uint8Array): string {
  let binary = '';
  // Avoid spreading an entire yearly chunk into one call: browser argument
  // limits are far below the size of a real hourly archive.
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(bytes.length, offset + 0x8000)));
  }
  return btoa(binary);
}

/** Read only the immutable manifest plus binary chunks produced by a completed job. */
export async function downloadWeatherPackageFromJob(
  job: WeatherBuildJob,
  signal?: AbortSignal,
): Promise<WeatherDataPackage> {
  if (job.status !== 'succeeded' || !job.result?.manifestUrl) {
    throw new WeatherServiceError('Weather package is not ready to download.', {
      code: 'PACKAGE_NOT_READY', retryable: true,
    });
  }
  try {
    const manifestResponse = await fetch(resourceUrl(job.result.manifestUrl), { signal });
    const manifest = await jsonResponse<WeatherDataPackage['manifest']>(manifestResponse);
    const descriptors = manifest.chunks ?? [];
    if (manifest.schemaVersion !== 2 || descriptors.length === 0) {
      throw new WeatherServiceError('Weather builder did not return a v2 chunk manifest.', {
        code: 'INVALID_PACKAGE_MANIFEST', retryable: false,
      });
    }
    const chunks = await Promise.all(descriptors.map(async (descriptor, index) => {
      const url = job.result?.chunkUrls[index]
        ?? `/v1/weather-package-jobs/${encodeURIComponent(job.id)}/chunks/${encodeURIComponent(descriptor.id)}`;
      const response = await fetch(resourceUrl(url), { signal });
      if (!response.ok) {
        let payload: unknown = null;
        try { payload = await response.json(); } catch { /* typed below */ }
        throw errorFromPayload(payload, response.status);
      }
      const bytes = new Uint8Array(await response.arrayBuffer());
      if (bytes.byteLength !== descriptor.byteLength) {
        throw new WeatherServiceError(`Weather chunk ${descriptor.id} has an unexpected byte length.`, {
          code: 'CHUNK_LENGTH_MISMATCH', retryable: true,
        });
      }
      return { descriptor, dataBase64: base64FromBytes(bytes) };
    }));
    const weatherPackage: WeatherDataPackage = { manifest, chunks, historicalYears: [] };
    if (!validateWeatherPackage(weatherPackage)) {
      throw new WeatherServiceError('Weather builder returned an invalid immutable package.', {
        code: 'INVALID_PACKAGE', retryable: false,
      });
    }
    return weatherPackage;
  } catch (error) {
    if (error instanceof WeatherServiceError || error instanceof DOMException) throw error;
    throw new WeatherServiceError(error instanceof Error ? error.message : 'Unable to download immutable weather chunks.', {
      code: 'WEATHER_SERVICE_UNREACHABLE', retryable: true,
    });
  }
}

/** Build once, expose real progress, then read the immutable cached package. */
export async function prepareWeatherPackage(
  record: TerrainRecord,
  options: { signal?: AbortSignal; onProgress?: (job: WeatherBuildJob) => void } = {},
): Promise<WeatherDataPackage> {
  const job = await createWeatherPackageJob(record, options.signal);
  options.onProgress?.(job);
  const complete = await waitForWeatherPackageJob(job.id, options);
  const weatherPackage = await downloadWeatherPackageFromJob(complete, options.signal);
  if (weatherPackage.manifest.terrainBinding !== weatherTerrainBinding(record)) {
    throw new WeatherServiceError('Weather service returned a package for a different terrain map.', {
      code: 'TERRAIN_BINDING_MISMATCH', retryable: false,
    });
  }
  return weatherPackage;
}
