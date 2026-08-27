import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { WeatherServiceError } from '../lib/errors.mjs';

const JACKSON = { id: 'KMWN', sourceIds: ['726130-14755', 'KMWN'], name: 'Mount Washington Regional Composite',
  latitude: 44.266, longitude: -71.303, elevationM: 427, timezone: 'America/New_York' };
function canonical(value) { if (value === null || typeof value !== 'object') return JSON.stringify(value); if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`; }
function hash(value) { return createHash('sha256').update(typeof value === 'string' ? value : canonical(value)).digest('hex'); }
function distanceKm(a, b) { const radians = (value) => value * Math.PI / 180; const dLat = radians(b.latitude - a.latitude); const dLon = radians(b.longitude - a.longitude);
  const x = Math.sin(dLat / 2) ** 2 + Math.cos(radians(a.latitude)) * Math.cos(radians(b.latitude)) * Math.sin(dLon / 2) ** 2; return 6371 * 2 * Math.atan2(Math.sqrt(x), Math.sqrt(1 - x)); }
async function atomicWrite(target, value) { await mkdir(path.dirname(target), { recursive: true }); const temp = `${target}.${process.pid}-${Date.now()}.tmp`;
  try { await writeFile(temp, canonical(value)); await rename(temp, target); } catch (error) { await rm(temp, { force: true }).catch(() => undefined); throw error; } }
async function readJson(target) { try { return JSON.parse(await readFile(target, 'utf8')); } catch (error) { if (error?.code === 'ENOENT') return null; throw error; } }

export class WeatherLabArtifactStore {
  constructor(cacheDirectory) { this.root = path.resolve(cacheDirectory, 'weather-lab-v1'); }
  path(kind, digest) { if (!/^[a-f0-9]{64}$/.test(digest)) throw new WeatherServiceError('INVALID_REQUEST', 'Invalid content hash.'); return path.join(this.root, kind, digest, 'ready.json'); }
  async install(kind, artifact) { const digest = hash(artifact); const target = this.path(kind, digest); if (!await readJson(target)) await atomicWrite(target, { version: 1, immutable: true, complete: true, contentHash: digest, artifact }); return digest; }
  async read(kind, digest) { const envelope = await readJson(this.path(kind, digest)); if (!envelope || !envelope.complete || !envelope.immutable || envelope.contentHash !== digest || hash(envelope.artifact) !== digest)
    throw new WeatherServiceError('PACKAGE_NOT_FOUND', `Weather Lab ${kind} artifact was not found.`, { status: 404 }); return envelope.artifact; }
  preparationPath(id) { if (!/^[a-z0-9-]+$/i.test(id)) throw new WeatherServiceError('INVALID_REQUEST', 'Invalid preparation id.'); return path.join(this.root, 'preparations', `${id}.json`); }
  async writePreparation(job) { await atomicWrite(this.preparationPath(job.id), job); }
  async readPreparation(id) { return readJson(this.preparationPath(id)); }
}

function validatePolicy(policy, validationYear) {
  if (policy?.kind === 'prior-30') return Array.from({ length: 30 }, (_, index) => validationYear - 30 + index);
  if (policy?.kind === 'leave-one-out-1991-2020') return Array.from({ length: 30 }, (_, index) => 1991 + index).filter((year) => year !== validationYear);
  if (policy?.kind === 'fixed' && Number.isInteger(policy.startYear) && Number.isInteger(policy.endYear) && policy.startYear <= policy.endYear)
    return Array.from({ length: policy.endYear - policy.startYear + 1 }, (_, index) => policy.startYear + index).filter((year) => year !== validationYear);
  throw new WeatherServiceError('INVALID_REQUEST', 'Training policy is unavailable or invalid.', { status: 400 });
}

export class WeatherLabService {
  constructor({ cacheDirectory, mode = 'fixture' }) { this.mode = mode; this.store = new WeatherLabArtifactStore(cacheDirectory); this.jobs = new Map(); }
  stations(query) {
    const latitude = Number(query.get('latitude')); const longitude = Number(query.get('longitude')); const elevationM = Number(query.get('elevationM') ?? 427);
    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) throw new WeatherServiceError('INVALID_REQUEST', 'Station search requires latitude and longitude.', { status: 400 });
    if (this.mode !== 'fixture') throw new WeatherServiceError('LIVE_PROVIDER_UNAVAILABLE', 'Live Weather Lab station search requires configured NOAA ISD and Daymet adapters; no fixture fallback is permitted.', { status: 503, retryable: true });
    const distance = distanceKm({ latitude, longitude }, JACKSON); if (distance > 150) throw new WeatherServiceError('OUTSIDE_FIXTURE_COVERAGE', 'Fixture mode contains only the committed Jackson station artifact.', { status: 422 });
    const elevationScore = Math.max(0, 1 - Math.abs(elevationM - JACKSON.elevationM) / 1500); const station = { ...JACKSON, distanceKm: distance,
      components: { coreFieldCompleteness: 1, distance: 1 - distance / 150, elevationMatch: elevationScore, trainingOverlap: 1 } };
    return [{ ...station, score: .4 + .25 * station.components.distance + .2 * elevationScore + .15 }];
  }
  async create(body) {
    if (this.mode !== 'fixture') throw new WeatherServiceError('LIVE_PROVIDER_UNAVAILABLE', 'Live preparation failed because official provider adapters are not configured; procedural fallback is disabled.', { status: 503, retryable: true });
    if (body?.stationId !== JACKSON.id || body?.validationYear !== 2019) throw new WeatherServiceError('OUTSIDE_FIXTURE_COVERAGE', 'Fixture mode supports only Jackson station KMWN for 2019.', { status: 422 });
    const years = validatePolicy(body.trainingPolicy, body.validationYear); if (years.includes(body.validationYear)) throw new WeatherServiceError('TRAINING_LEAK', 'Validation year entered fitted inputs.', { status: 400 });
    const id = randomUUID(); const job = { id, status: 'queued', progress: { stage: 'queued', completed: 0, total: 4 }, createdAt: new Date().toISOString() };
    this.jobs.set(id, { ...job, cancelled: false }); await this.store.writePreparation(job); queueMicrotask(() => this.run(id, body, years)); return job;
  }
  async run(id, body, years) {
    const live = this.jobs.get(id); if (!live || live.cancelled) return;
    try {
      live.status = 'running'; live.progress = { stage: 'normalizing', completed: 1, total: 4 }; await this.store.writePreparation(live);
      const fixture = await import('../../weather-engine/src/fixtures/jackson2019.ts');
      const observed = fixture.createJacksonObserved2019();
      const observationHash = await this.store.install('observations', observed); if (live.cancelled) return;
      live.progress = { stage: 'compiling', completed: 2, total: 4 }; await this.store.writePreparation(live);
      const model = fixture.createJacksonClimateModel();
      if (model.trainingPeriod.years.some((year) => !years.includes(year)) || model.excludedValidationYear !== body.validationYear) {
        throw new WeatherServiceError('TRAINING_LEAK', 'Committed model does not match the requested isolated training period.', { status: 409 });
      }
      const modelHash = await this.store.install('models', model); if (live.cancelled) return;
      live.status = 'succeeded'; live.progress = { stage: 'ready', completed: 4, total: 4 }; live.result = { modelHash, observationHash,
        modelUrl: `/v1/weather-lab/models/${modelHash}`, observedSeriesUrl: `/v1/weather-lab/observed-series/${observationHash}` }; await this.store.writePreparation(live);
    } catch (error) { live.status = 'failed'; live.error = { code: 'PREPARATION_FAILED', message: error instanceof Error ? error.message : String(error) }; await this.store.writePreparation(live); }
  }
  async get(id) { const job = this.jobs.get(id) ?? await this.store.readPreparation(id); if (!job) throw new WeatherServiceError('UNKNOWN_JOB', `Weather Lab preparation '${id}' was not found.`, { status: 404 }); return job; }
  async cancel(id) { const job = this.jobs.get(id); if (!job) return this.get(id); if (!['succeeded','failed','cancelled'].includes(job.status)) { job.cancelled = true; job.status = 'cancelled'; job.progress = { stage: 'cancelled', completed: 0, total: 4 }; await this.store.writePreparation(job); } return job; }
}
