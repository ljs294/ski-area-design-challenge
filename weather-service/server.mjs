import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PackageArtifactStore, SourceCache } from './lib/cache.mjs';
import { validateWeatherPackageRequest, packageRequestFingerprint } from './lib/contract.mjs';
import { decodeWeatherChunk } from './lib/codec.mjs';
import { WeatherPackageBuilder } from './lib/builder.mjs';
import { asWeatherServiceError, WeatherServiceError } from './lib/errors.mjs';
import { WeatherJobManager } from './lib/jobs.mjs';
import { createProviderSet } from './lib/providers.mjs';
import { WeatherLabService } from './lab/service.mjs';

const MAX_REQUEST_BYTES = 256 * 1024;

function corsHeaders() {
  return {
    'access-control-allow-origin': '*',
    'access-control-allow-methods': 'GET, POST, DELETE, OPTIONS',
    'access-control-allow-headers': 'content-type',
    'access-control-expose-headers': 'x-weather-chunk-id, x-weather-chunk-year, x-weather-chunk-format, x-weather-chunk-checksum-sha256',
  };
}

function sendJson(response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, { ...corsHeaders(), 'content-type': 'application/json; charset=utf-8', 'content-length': Buffer.byteLength(payload) });
  response.end(payload);
}

function sendError(response, error) {
  const serviceError = asWeatherServiceError(error);
  return sendJson(response, serviceError.status, { error: serviceError.toJSON() });
}

function sendChunk(response, descriptor, data) {
  response.writeHead(200, {
    ...corsHeaders(),
    // Deliberately omit Content-Encoding: consumers need the exact compressed
    // bytes to validate descriptor.checksumSha256 before decompression.
    'content-type': 'application/octet-stream', 'content-length': data.byteLength,
    'cache-control': 'public, immutable, max-age=31536000',
    'x-weather-chunk-id': descriptor.id, 'x-weather-chunk-year': String(descriptor.year),
    'x-weather-chunk-format': descriptor.format, 'x-weather-chunk-checksum-sha256': descriptor.checksumSha256,
  });
  response.end(data);
}

async function readBody(request) {
  const contentLength = Number(request.headers['content-length'] ?? 0);
  if (Number.isFinite(contentLength) && contentLength > MAX_REQUEST_BYTES) {
    throw new WeatherServiceError('REQUEST_TOO_LARGE', 'Weather package request is too large.', { status: 413 });
  }
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > MAX_REQUEST_BYTES) {
      throw new WeatherServiceError('REQUEST_TOO_LARGE', 'Weather package request is too large.', { status: 413 });
    }
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch (cause) {
    throw new WeatherServiceError('INVALID_REQUEST', 'Weather package request must be valid JSON.', { cause });
  }
}

function jobArtifact(job, artifactStore) {
  if (job.status === 'failed') throw new WeatherServiceError(job.error?.code ?? 'INTERNAL', job.error?.message ?? 'Weather package job failed.', {
    retryable: job.error?.retryable, details: job.error?.details,
  });
  if (job.status === 'cancelled') throw new WeatherServiceError('JOB_CANCELLED', 'Weather package job was cancelled.', { status: 409 });
  if (job.status !== 'succeeded' || !job.result?.contentHash) {
    throw new WeatherServiceError('JOB_NOT_READY', 'Weather package job is not complete yet.', { status: 409, retryable: true, details: { status: job.status } });
  }
  return artifactStore.readManifest(job.result.contentHash);
}

async function legacyPackage(artifactStore, contentHash) {
  const packageArtifact = await artifactStore.packageWithChunks(contentHash);
  if (!packageArtifact) throw new WeatherServiceError('PACKAGE_NOT_FOUND', 'Weather package artifact was not found.');
  const historicalYears = packageArtifact.chunks.map((chunk) => ({
    year: chunk.descriptor.year,
    hours: decodeWeatherChunk(Buffer.from(chunk.dataBase64, 'base64'), chunk.descriptor),
  }));
  return { ...packageArtifact, historicalYears };
}

function routeSegments(pathname) {
  return pathname.split('/').filter(Boolean).map(decodeURIComponent);
}

/**
 * Creates the HTTP service without binding a port. Tests inject a temporary
 * cache and fixture provider set; production launches the same handler.
 */
export function createWeatherService(options = {}) {
  const environment = options.environment ?? process.env;
  const cacheDirectory = options.cacheDirectory ?? environment.WEATHER_CACHE_DIR ?? path.resolve('weather-service/.weather-cache');
  const mode = options.mode ?? environment.WEATHER_SERVICE_MODE ?? 'fixture';
  const sourceCache = options.sourceCache ?? new SourceCache(cacheDirectory);
  const artifactStore = options.artifactStore ?? new PackageArtifactStore(cacheDirectory);
  const providerSet = options.providerSet ?? createProviderSet({ mode, sourceCache, fetchImpl: options.fetchImpl ?? globalThis.fetch, environment });
  const builder = options.builder ?? new WeatherPackageBuilder({ providerSet, artifactStore, now: options.now });
  const jobs = options.jobs ?? new WeatherJobManager({ builder, now: options.now, idFactory: options.idFactory });
  const weatherLab = options.weatherLab ?? new WeatherLabService({ cacheDirectory, mode,
    fetchImpl: options.fetchImpl ?? globalThis.fetch, environment, now: options.now });

  const handler = async (request, response) => {
    try {
      if (!request.url) throw new WeatherServiceError('INVALID_REQUEST', 'Request URL is required.');
      const url = new URL(request.url, 'http://127.0.0.1');
      const segments = routeSegments(url.pathname);
      if (request.method === 'OPTIONS') {
        response.writeHead(204, corsHeaders());
        response.end();
        return;
      }
      if (request.method === 'GET' && url.pathname === '/health') {
        return sendJson(response, 200, {
          ok: true, apiVersion: 2, mode: providerSet.mode, offlineRuntimeGuaranteed: true,
          providerPolicy: providerSet.mode === 'fixture' ? 'fixture-v1' : 'daymet-v4r1-merra2-v1',
          providers: { daymet: providerSet.daymet.version, merra2: providerSet.merra2.version },
        });
      }

      // Standalone Lab API. Its artifacts and jobs are intentionally isolated
      // from installed gameplay weather packages under weather-lab-v1.
      if (request.method === 'GET' && url.pathname === '/v1/weather-lab/location-context') {
        return sendJson(response, 200, { context: await weatherLab.locationContext({ latitude: url.searchParams.get('latitude'),
          longitude: url.searchParams.get('longitude'), elevationOverrideM: url.searchParams.get('elevationM') ?? undefined }) });
      }
      if (request.method === 'GET' && url.pathname === '/v1/weather-lab/stations') {
        return sendJson(response, 200, { stations: await weatherLab.stations(url.searchParams), mode });
      }
      if (request.method === 'POST' && url.pathname === '/v1/weather-lab/preparations') {
        return sendJson(response, 202, { preparation: await weatherLab.create(await readBody(request)) });
      }
      if (segments.length === 4 && segments[0] === 'v1' && segments[1] === 'weather-lab' && segments[2] === 'preparations') {
        if (request.method === 'GET') return sendJson(response, 200, { preparation: await weatherLab.get(segments[3]) });
        if (request.method === 'DELETE') return sendJson(response, 200, { preparation: await weatherLab.cancel(segments[3]) });
      }
      if (segments.length === 4 && segments[0] === 'v1' && segments[1] === 'weather-lab' && segments[2] === 'models' && request.method === 'GET') {
        return sendJson(response, 200, await weatherLab.store.read('models', segments[3]));
      }
      if (segments.length === 4 && segments[0] === 'v1' && segments[1] === 'weather-lab' && segments[2] === 'observed-series' && request.method === 'GET') {
        return sendJson(response, 200, await weatherLab.store.read('observations', segments[3]));
      }

      // Preferred asynchronous API.
      if (request.method === 'POST' && url.pathname === '/v1/weather-package-jobs') {
        const weatherRequest = validateWeatherPackageRequest(await readBody(request), options);
        const created = jobs.create(weatherRequest, packageRequestFingerprint(weatherRequest));
        return sendJson(response, 202, { job: created.job, reused: created.reused });
      }
      if (segments.length >= 3 && segments[0] === 'v1' && segments[1] === 'weather-package-jobs') {
        const jobId = segments[2];
        if (segments.length === 3 && request.method === 'GET') return sendJson(response, 200, { job: jobs.get(jobId) });
        if (segments.length === 3 && request.method === 'DELETE') return sendJson(response, 200, { job: jobs.cancel(jobId) });
        const job = jobs.get(jobId);
        if (segments.length === 4 && segments[3] === 'manifest' && request.method === 'GET') {
          const manifest = await jobArtifact(job, artifactStore);
          if (!manifest) throw new WeatherServiceError('PACKAGE_NOT_FOUND', 'Weather package artifact was not found.');
          return sendJson(response, 200, manifest);
        }
        if (segments.length === 5 && segments[3] === 'chunks' && request.method === 'GET') {
          await jobArtifact(job, artifactStore);
          const chunk = await artifactStore.readChunk(job.result.contentHash, segments[4]);
          if (!chunk) throw new WeatherServiceError('CHUNK_NOT_FOUND', `Weather chunk '${segments[4]}' was not found.`);
          return sendChunk(response, chunk.descriptor, chunk.data);
        }
      }

      // Content-addressed reads make it possible to resume a completed package
      // after a browser reload without relying on an in-memory job.
      if (segments.length === 5 && segments[0] === 'v1' && segments[1] === 'weather-packages' && segments[2] === 'content' && segments[4] === 'manifest' && request.method === 'GET') {
        const manifest = await artifactStore.readManifest(segments[3]);
        if (!manifest) throw new WeatherServiceError('PACKAGE_NOT_FOUND', 'Weather package artifact was not found.');
        return sendJson(response, 200, manifest);
      }
      if (segments.length === 6 && segments[0] === 'v1' && segments[1] === 'weather-packages' && segments[2] === 'content' && segments[4] === 'chunks' && request.method === 'GET') {
        const chunk = await artifactStore.readChunk(segments[3], segments[5]);
        if (!chunk) throw new WeatherServiceError('CHUNK_NOT_FOUND', `Weather chunk '${segments[5]}' was not found.`);
        return sendChunk(response, chunk.descriptor, chunk.data);
      }

      // Transitional compatibility endpoint. New callers use jobs, manifest,
      // and binary chunks. Existing Labs still receive a fully decoded package.
      if (request.method === 'POST' && url.pathname === '/v1/weather-packages') {
        const weatherRequest = validateWeatherPackageRequest(await readBody(request), options);
        const created = jobs.create(weatherRequest, packageRequestFingerprint(weatherRequest));
        if (url.searchParams.get('async') === '1' || request.headers.prefer?.includes('respond-async')) {
          return sendJson(response, 202, { job: created.job, reused: created.reused });
        }
        // Job execution is intentionally independent of the HTTP connection.
        // IncomingMessage.signal may become aborted after its request body has
        // been consumed, so it must not cancel a package that was explicitly
        // requested for offline installation.
        const completed = await jobs.wait(created.job.id);
        if (completed.status === 'failed') throw new WeatherServiceError(completed.error?.code ?? 'INTERNAL', completed.error?.message ?? 'Weather package job failed.', {
          retryable: completed.error?.retryable, details: completed.error?.details,
        });
        if (completed.status === 'cancelled') throw new WeatherServiceError('JOB_CANCELLED', 'Weather package preparation was cancelled.', { status: 409 });
        return sendJson(response, 200, await legacyPackage(artifactStore, completed.result.contentHash));
      }
      throw new WeatherServiceError('PACKAGE_NOT_FOUND', 'Weather service endpoint was not found.', { status: 404 });
    } catch (error) {
      return sendError(response, error);
    }
  };
  return { handler, jobs, builder, artifactStore, sourceCache, providerSet, weatherLab, cacheDirectory, mode };
}

export function listenWeatherService(options = {}) {
  const service = createWeatherService(options);
  const port = Number(options.port ?? options.environment?.WEATHER_SERVICE_PORT ?? process.env.WEATHER_SERVICE_PORT ?? 8787);
  const host = options.host ?? '127.0.0.1';
  const server = createServer(service.handler);
  server.listen(port, host, () => {
    process.stdout.write(`Weather service (${service.providerSet.mode}) listening at http://${host}:${port}\n`);
  });
  return { ...service, server, port, host };
}

const entrypoint = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (entrypoint) listenWeatherService();
