import { randomUUID } from 'node:crypto';
import { asWeatherServiceError, WeatherServiceError } from './errors.mjs';

const TERMINAL = new Set(['succeeded', 'failed', 'cancelled']);

function nowIso(now) {
  return now().toISOString();
}

function clone(value) {
  return structuredClone(value);
}

/** In-memory job coordinator; finished package artifacts are durable in PackageArtifactStore. */
export class WeatherJobManager {
  constructor({ builder, now = () => new Date(), idFactory = () => randomUUID() }) {
    this.builder = builder;
    this.now = now;
    this.idFactory = idFactory;
    this.jobs = new Map();
    this.activeByFingerprint = new Map();
  }

  serialize(job) {
    return {
      id: job.id, status: job.status, request: clone(job.request), progress: clone(job.progress),
      createdAt: job.createdAt, updatedAt: job.updatedAt,
      ...(job.result === undefined ? {} : { result: clone(job.result) }),
      ...(job.error === undefined ? {} : { error: clone(job.error) }),
    };
  }

  get(jobId) {
    const job = this.jobs.get(jobId);
    if (!job) throw new WeatherServiceError('UNKNOWN_JOB', `Weather package job '${jobId}' was not found.`);
    return this.serialize(job);
  }

  create(request, fingerprint) {
    const runningId = this.activeByFingerprint.get(fingerprint);
    if (runningId) return { job: this.get(runningId), reused: true };
    const timestamp = nowIso(this.now);
    const controller = new AbortController();
    const job = {
      id: this.idFactory(), fingerprint, request, controller, status: 'queued', createdAt: timestamp, updatedAt: timestamp,
      progress: { stage: 'queued', completed: 0, total: 1, message: 'Weather package preparation is queued.', updatedAt: timestamp },
      result: undefined, error: undefined,
    };
    this.jobs.set(job.id, job);
    this.activeByFingerprint.set(fingerprint, job.id);
    // Start after the response can be returned. A job never needs a browser
    // connection to stay alive.
    queueMicrotask(() => this.run(job));
    return { job: this.serialize(job), reused: false };
  }

  async run(job) {
    if (job.controller.signal.aborted) return this.markCancelled(job);
    job.status = 'running';
    this.touch(job, { stage: 'starting', completed: 0, total: 1, message: 'Preparing immutable offline weather package.' });
    try {
      const packageArtifact = await this.builder.build(job.request, {
        signal: job.controller.signal,
        onProgress: (progress) => this.touch(job, progress),
      });
      if (job.controller.signal.aborted) return this.markCancelled(job);
      job.status = 'succeeded';
      job.result = {
        contentHash: packageArtifact.contentHash,
        manifestUrl: `/v1/weather-package-jobs/${encodeURIComponent(job.id)}/manifest`,
        chunkUrls: packageArtifact.manifest.chunks.map((chunk) =>
          `/v1/weather-package-jobs/${encodeURIComponent(job.id)}/chunks/${encodeURIComponent(chunk.id)}`),
        cacheHit: packageArtifact.cacheHit,
      };
      this.touch(job, { stage: 'complete', completed: 1, total: 1, message: 'Offline weather package is ready.' });
    } catch (error) {
      const serviceError = asWeatherServiceError(error);
      if (serviceError.code === 'BUILD_CANCELLED' || job.controller.signal.aborted) return this.markCancelled(job);
      job.status = 'failed';
      job.error = serviceError.toJSON();
      this.touch(job, { stage: 'failed', completed: 0, total: 1, message: serviceError.message });
    } finally {
      this.activeByFingerprint.delete(job.fingerprint);
    }
  }

  touch(job, progress) {
    const updatedAt = nowIso(this.now);
    job.updatedAt = updatedAt;
    job.progress = {
      stage: progress.stage, completed: progress.completed, total: progress.total,
      message: progress.message, ...(progress.year === undefined ? {} : { year: progress.year }), updatedAt,
    };
  }

  markCancelled(job) {
    if (TERMINAL.has(job.status) && job.status !== 'cancelled') return;
    job.status = 'cancelled';
    job.error = new WeatherServiceError('JOB_CANCELLED', 'Weather package preparation was cancelled.', { status: 409 }).toJSON();
    this.touch(job, { stage: 'cancelled', completed: 0, total: 1, message: 'Weather package preparation was cancelled.' });
  }

  cancel(jobId) {
    const job = this.jobs.get(jobId);
    if (!job) throw new WeatherServiceError('UNKNOWN_JOB', `Weather package job '${jobId}' was not found.`);
    if (!TERMINAL.has(job.status)) {
      job.controller.abort();
      this.markCancelled(job);
      this.activeByFingerprint.delete(job.fingerprint);
    }
    return this.serialize(job);
  }

  async wait(jobId, { signal, timeoutMs = 0 } = {}) {
    const started = Date.now();
    while (true) {
      if (signal?.aborted) throw new WeatherServiceError('BUILD_CANCELLED', 'The request waiting for a weather package was cancelled.', { status: 409 });
      const job = this.get(jobId);
      if (TERMINAL.has(job.status)) return job;
      if (timeoutMs > 0 && Date.now() - started >= timeoutMs) return job;
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
  }
}
