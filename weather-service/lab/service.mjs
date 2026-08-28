import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { WeatherServiceError } from '../lib/errors.mjs';
import { SourceCache } from '../lib/cache.mjs';
import { Merra2Adapter } from '../lib/providers.mjs';
import { DaymetLabAdapter, resolveNorthAmericanTimezone, coreCompleteness } from './providers.mjs';

const JACKSON = { id: 'KMWN', sourceIds: ['72613014755', 'KMWN'], name: 'Mount Washington Regional Composite', latitude: 44.266,
  longitude: -71.303, elevationM: 427, timezone: 'America/New_York', distanceKm: 15.8, score: .91,
  scoreComponents: { coreFieldCompleteness: 1, distance: .89, elevationMatch: 1, trainingOverlap: 1 }, availableYears: [2019] };
const VARIABLES = ['temperatureC','dewPointC','pressureHpa','relativeHumidityPct','wetBulbC','precipitationMm','snowfallCm',
  'windSpeedKph','windDirectionDeg','windGustKph','shortwaveRadiationWm2','cloudCoverPct','visibilityKm','condition'];

function canonical(value) {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  return `{${Object.keys(value).filter((key) => value[key] !== undefined).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
}
function hash(value) { return createHash('sha256').update(typeof value === 'string' ? value : canonical(value)).digest('hex'); }
async function atomicWrite(target, value) {
  await mkdir(path.dirname(target), { recursive: true }); const temporary = `${target}.${process.pid}-${randomUUID()}.tmp`;
  try { await writeFile(temporary, canonical(value)); await rename(temporary, target); }
  catch (error) { await rm(temporary, { force: true }).catch(() => undefined); throw error; }
}
async function readJson(target) { try { return JSON.parse(await readFile(target, 'utf8')); } catch (error) { if (error?.code === 'ENOENT') return null; throw error; } }
function rounded(value, digits = 6) { const multiplier = 10 ** digits; return Math.round(value * multiplier) / multiplier; }
function validCoordinate(latitude, longitude) { return Number.isFinite(latitude) && latitude >= -90 && latitude <= 90 && Number.isFinite(longitude) && longitude >= -180 && longitude <= 180; }
function requireActive(job) {
  if (job.controller?.signal.aborted || job.status === 'cancelled') {
    throw new WeatherServiceError('JOB_CANCELLED', 'Weather Lab preparation was cancelled.', { status: 409 });
  }
}

export class WeatherLabArtifactStore {
  constructor(cacheDirectory) { this.root = path.resolve(cacheDirectory, 'weather-lab-v1'); }
  path(kind, digest) { if (!/^[a-f0-9]{64}$/.test(digest)) throw new WeatherServiceError('INVALID_REQUEST', 'Invalid content hash.'); return path.join(this.root, kind, digest, 'ready.json'); }
  async install(kind, artifact) { const digest = hash(artifact); const target = this.path(kind, digest); if (!await readJson(target)) await atomicWrite(target, { version: 1, immutable: true, complete: true, contentHash: digest, artifact }); return digest; }
  async read(kind, digest) { const envelope = await readJson(this.path(kind, digest)); if (!envelope || !envelope.complete || !envelope.immutable || envelope.contentHash !== digest || hash(envelope.artifact) !== digest)
    throw new WeatherServiceError('PACKAGE_NOT_FOUND', `Weather Lab ${kind} artifact was not found.`, { status: 404 }); return envelope.artifact; }
  preparationPath(id) { if (!/^[a-z0-9-]+$/i.test(id)) throw new WeatherServiceError('INVALID_REQUEST', 'Invalid preparation id.'); return path.join(this.root, 'preparations', `${id}.json`); }
  async writePreparation(job) { await atomicWrite(this.preparationPath(job.id), job); }
  async readPreparation(id) { return readJson(this.preparationPath(id)); }
  requestPath(digest) { if (!/^[a-f0-9]{64}$/.test(digest)) throw new WeatherServiceError('INVALID_REQUEST', 'Invalid request hash.'); return path.join(this.root, 'requests', `${digest}.json`); }
  async writePreparedRequest(digest, result) { await atomicWrite(this.requestPath(digest), { version: 1, complete: true, result }); }
  async readPreparedRequest(digest) { const entry = await readJson(this.requestPath(digest)); return entry?.complete ? entry.result : null; }
}

export function trainingYears(policy, validationYear) {
  if (policy?.kind === 'prior-30') return Array.from({ length: 30 }, (_, index) => validationYear - 30 + index);
  if (policy?.kind === 'leave-one-out-1991-2020') return Array.from({ length: 30 }, (_, index) => 1991 + index).filter((year) => year !== validationYear);
  if (policy?.kind === 'fixed' && Number.isInteger(policy.startYear) && Number.isInteger(policy.endYear) && policy.startYear <= policy.endYear)
    return Array.from({ length: policy.endYear - policy.startYear + 1 }, (_, index) => policy.startYear + index).filter((year) => year !== validationYear);
  throw new WeatherServiceError('INVALID_REQUEST', 'Training policy is unavailable or invalid.', { status: 400 });
}

function eligibleYears(source, latestYear) {
  const available = new Set(source.availableYears ?? []);
  return [...available].filter((year) => year <= latestYear && Array.from({ length: 30 }, (_, index) => year - 30 + index)
    .every((candidate) => available.has(candidate))).sort((a, b) => b - a);
}
function merraGrid(latitude, longitude, elevationM, timezone, latestYear) {
  const gridLatitude = Math.round(latitude / .5) * .5;
  const gridLongitude = Math.round(longitude / .625) * .625;
  const id = `MERRA2-${gridLatitude.toFixed(3)}-${gridLongitude.toFixed(3)}`;
  return { id, sourceIds: [id], name: 'MERRA-2 reanalysis grid cell', latitude: gridLatitude, longitude: gridLongitude,
    elevationM, timezone, distanceKm: 0, score: 1,
    scoreComponents: { coreFieldCompleteness: 1, distance: 1, elevationMatch: 1, trainingOverlap: 1 },
    availableYears: Array.from({ length: latestYear - 1980 + 1 }, (_, index) => 1980 + index) };
}
function dewPointFromHumidity(temperatureC, relativeHumidityPct) {
  if (temperatureC == null || relativeHumidityPct == null) return null;
  const humidity = Math.max(1, Math.min(100, relativeHumidityPct));
  const gamma = Math.log(humidity / 100) + 17.67 * temperatureC / (temperatureC + 243.5);
  return 243.5 * gamma / (17.67 - gamma);
}
function normalizedMerraRows(hours) {
  return hours.map((hour) => {
    const speedMps = Math.hypot(hour.uWindMps ?? 0, hour.vWindMps ?? 0);
    const direction = speedMps > 0 ? (Math.atan2(-(hour.uWindMps ?? 0), -(hour.vWindMps ?? 0)) * 180 / Math.PI + 360) % 360 : null;
    return { ...hour, dewPointC: dewPointFromHumidity(hour.temperatureC, hour.relativeHumidityPct),
      windSpeedKph: speedMps * 3.6, windDirectionDeg: direction, windGustKph: null,
      visibilityKm: null, precipitationQuality: 'accepted', cloudCoverQuality: 'accepted', weatherCodeQuality: 'missing' };
  });
}
function conditionFor(hour, phase) {
  const code = hour.weatherCode;
  const coded = code >= 50 && code <= 65 ? 'rain'
    : code >= 66 && code <= 67 ? 'freezing-rain'
      : code >= 68 && code <= 69 ? 'mixed'
        : code >= 70 && code <= 79 ? ((hour.precipitationMm ?? 0) >= 2 ? 'heavy-snow' : 'snow')
          : code >= 80 && code <= 82 ? 'rain'
            : code >= 83 && code <= 84 ? 'mixed'
              : code >= 85 && code <= 86 ? 'snow' : null;
  if (coded) return coded;
  if ((hour.precipitationMm ?? 0) > .005) {
    if (phase == null) return null;
    if (phase === 'freezing-rain') return 'freezing-rain'; if (phase === 'rain') return 'rain'; if (phase === 'mixed') return 'mixed';
    return hour.precipitationMm >= 2 ? 'heavy-snow' : hour.precipitationMm < .2 ? 'flurries' : 'snow';
  }
  if (hour.cloudCoverPct == null) return null;
  return hour.cloudCoverPct <= 20 ? 'clear' : hour.cloudCoverPct <= 70 ? 'partly-cloudy' : 'overcast';
}
export function bestRowsByUtcHour(rows) {
  const result = new Map();
  for (const row of rows) {
    const key = new Date(Math.floor(new Date(row.at).getTime() / 3_600_000) * 3_600_000).toISOString();
    const score = VARIABLES.filter((field) => row[field] != null).length;
    if (!result.has(key) || score > result.get(key).score) result.set(key, { score, row });
  }
  const precipitationByHour = new Map();
  for (const row of rows) {
    if (row.precipitationMm == null) continue;
    const end = Math.floor(new Date(row.at).getTime() / 3_600_000) * 3_600_000;
    const duration = Number.isInteger(row.precipitationDurationHours) && row.precipitationDurationHours > 0
      ? row.precipitationDurationHours : 1;
    for (let offset = 0; offset < duration; offset += 1) {
      const key = new Date(end - offset * 3_600_000).toISOString();
      const previous = precipitationByHour.get(key);
      if (!previous || duration < previous.duration || duration === previous.duration && offset < previous.offset) {
        precipitationByHour.set(key, { duration, offset, value: row.precipitationMm,
          quality: row.precipitationQuality ?? 'accepted' });
      }
    }
  }
  for (const [key, precipitation] of precipitationByHour) {
    const selected = result.get(key); const row = { ...(selected?.row ?? { at: key }), precipitationMm: precipitation.value,
      precipitationQuality: precipitation.quality };
    result.set(key, { score: selected?.score ?? 1, row });
  }
  return new Map([...result].map(([key, value]) => [key, value.row]));
}

function snowfallCentimetres(engine, precipitationMm, temperatureC, phase) {
  if (precipitationMm == null || phase == null) return null;
  return engine.snowfallCentimetresFromLiquid(precipitationMm, temperatureC, phase);
}

function derivedDailySnowfall(engine, precipitationMm, hours) {
  if (precipitationMm == null) return null;
  if (precipitationMm <= .005) return 0;
  const wetSamples = hours.filter((hour) => hour.precipitationMm != null && hour.precipitationMm > .005 &&
    hour.temperatureC != null && hour.precipitationPhase != null && hour.quality.precipitationMm === 'accepted');
  const phaseSamples = wetSamples.length ? wetSamples : hours.filter((hour) => hour.temperatureC != null && hour.wetBulbC != null &&
    hour.quality.temperatureC === 'accepted' && hour.quality.wetBulbC === 'accepted').map((hour) => ({ ...hour,
      precipitationMm: 1, precipitationPhase: engine.precipitationPhase(hour.temperatureC, hour.wetBulbC, 1) }));
  const totalWeight = phaseSamples.reduce((sum, hour) => sum + hour.precipitationMm, 0);
  if (totalWeight <= 0) return null;
  const centimetresPerMm = phaseSamples.reduce((sum, hour) => sum +
    (snowfallCentimetres(engine, hour.precipitationMm, hour.temperatureC, hour.precipitationPhase) ?? 0), 0) / totalWeight;
  return precipitationMm * centimetresPerMm;
}

function daymetConstrainedPrecipitation(calendar, byHour, daymetByDate) {
  const entriesByDate = new Map(); const result = new Map();
  for (const entry of calendar) {
    const date = entry.localDateTime.slice(0, 10); const entries = entriesByDate.get(date) ?? [];
    entries.push(entry); entriesByDate.set(date, entries);
  }
  for (const [date, entries] of entriesByDate) {
    const target = daymetByDate.get(date)?.precipitationMm;
    if (target == null) continue;
    if (target <= .005) { for (const entry of entries) result.set(entry.at, 0); continue; }
    const raw = entries.map((entry) => Math.max(0, byHour.get(entry.at)?.precipitationMm ?? 0));
    const rawTotal = raw.reduce((sum, value) => sum + value, 0);
    if (rawTotal > .005) {
      entries.forEach((entry, index) => result.set(entry.at, target * raw[index] / rawTotal));
      continue;
    }
    const candidates = entries.map((entry, index) => {
      const hour = byHour.get(entry.at) ?? {}; const cloud = Math.max(0, hour.cloudCoverPct ?? 0) / 100;
      const humidity = Math.max(0, hour.relativeHumidityPct ?? 0) / 100;
      return { entry, index, weight: Math.max(.01, cloud * cloud * Math.max(.2, humidity)) };
    }).sort((left, right) => right.weight - left.weight || left.index - right.index).slice(0, Math.min(6, entries.length));
    const weightTotal = candidates.reduce((sum, candidate) => sum + candidate.weight, 0);
    for (const entry of entries) result.set(entry.at, 0);
    for (const candidate of candidates) result.set(candidate.entry.at, target * candidate.weight / weightTotal);
  }
  return result;
}

function observedDays(calendar, daymetByDate, hours, engine) {
  const dates = [...new Set(calendar.map((entry) => entry.localDateTime.slice(0, 10)))]; const hoursByDate = new Map();
  for (const hour of hours) { const date = hour.localDateTime.slice(0, 10); const values = hoursByDate.get(date) ?? []; values.push(hour); hoursByDate.set(date, values); }
  return dates.map((localDate) => {
    const anchor = daymetByDate.get(localDate); const precipitationMm = anchor?.precipitationMm ?? null;
    const snowfallCm = derivedDailySnowfall(engine, precipitationMm, hoursByDate.get(localDate) ?? []);
    return { localDate, minimumTemperatureC: anchor?.tminC ?? null, maximumTemperatureC: anchor?.tmaxC ?? null, precipitationMm,
      snowfallCm, snowDepthCm: null, snowfallKind: snowfallCm != null ? 'derived' : 'unavailable', sources: {
        temperature: anchor?.tminC != null || anchor?.tmaxC != null ? 'Daymet V4' : null,
        precipitation: precipitationMm != null ? 'Daymet V4' : null,
        snowfall: snowfallCm != null ? 'Derived from Daymet V4 and MERRA-2 using weather-engine psychrometrics' : null,
      } };
  });
}

export async function historicalSeries(station, year, timezone, rows, daymetDays) {
  const engine = await import('../../weather-engine/src/index.ts'); const calendar = engine.weatherCalendarYear(year, timezone);
  const byHour = bestRowsByUtcHour(rows); const byDay = new Map(daymetDays.map((day) => [day.date, day]));
  const constrainedPrecipitation = daymetConstrainedPrecipitation(calendar, byHour, byDay);
  const hours = calendar.map((entry) => {
    const source = byHour.get(entry.at) ?? {}; const daily = byDay.get(entry.localDateTime.slice(0, 10));
    const temperatureC = source.temperatureC ?? null;
    const dewPointC = source.dewPointC ?? dewPointFromHumidity(temperatureC, source.relativeHumidityPct) ?? null;
    const constrained = constrainedPrecipitation.get(entry.at);
    const sourcePrecipitationQuality = constrained !== undefined ? 'accepted'
      : source.precipitationQuality ?? (source.precipitationMm == null ? 'missing' : 'accepted');
    const inferredDry = sourcePrecipitationQuality !== 'suspect' && source.precipitationMm == null &&
      daily?.precipitationMm != null && daily.precipitationMm <= .005;
    const precipitationMm = sourcePrecipitationQuality === 'suspect' ? null : constrained ?? source.precipitationMm ?? (inferredDry ? 0 : null);
    const precipitationQuality = sourcePrecipitationQuality === 'suspect' ? 'suspect'
      : source.precipitationMm != null || inferredDry ? 'accepted' : 'missing';
    const cloudCoverQuality = source.cloudCoverQuality ?? (source.cloudCoverPct == null ? 'missing' : 'accepted');
    const weatherCodeQuality = source.weatherCodeQuality ?? (source.weatherCode == null ? 'missing' : 'accepted');
    const cloudCoverPct = cloudCoverQuality === 'suspect' ? null : source.cloudCoverPct ?? null;
    const weatherCode = weatherCodeQuality === 'suspect' ? null : source.weatherCode ?? null;
    const humidity = temperatureC != null && dewPointC != null ? engine.relativeHumidityPct(temperatureC, dewPointC) : null;
    const wetBulb = temperatureC != null && humidity != null ? engine.wetBulbTemperatureC(temperatureC, humidity, source.pressureHpa ?? 1013.25) : null;
    const phase = temperatureC != null && wetBulb != null && precipitationMm != null
      ? engine.precipitationPhase(temperatureC, wetBulb, precipitationMm) : precipitationMm === 0 ? 'none' : null;
    const solarWeight = Math.max(0, Math.sin((entry.hour - 6) / 12 * Math.PI));
    const shortwaveRadiationWm2 = source.shortwaveWm2 ?? (daily?.shortwaveWm2 == null ? null : daily.shortwaveWm2 * solarWeight * Math.PI / 2);
    const snowfallCm = precipitationMm != null && phase != null
      ? engine.snowfallCentimetresFromLiquid(precipitationMm, temperatureC ?? 0, phase) : null;
    const condition = conditionFor({ ...source, precipitationMm, cloudCoverPct, weatherCode }, phase); const values = { ...source, precipitationMm,
      cloudCoverPct, weatherCode, relativeHumidityPct: humidity, wetBulbC: wetBulb, precipitationPhase: phase, snowfallCm, shortwaveRadiationWm2, condition };
    const quality = Object.fromEntries(VARIABLES.map((field) => [field, values[field] == null ? 'missing' : 'accepted']));
    quality.precipitationMm = precipitationQuality; quality.cloudCoverPct = cloudCoverQuality;
    if (precipitationQuality === 'suspect') quality.snowfallCm = 'suspect';
    if (condition == null && [precipitationQuality, cloudCoverQuality, weatherCodeQuality].includes('suspect')) quality.condition = 'suspect';
    return { at: entry.at, localDateTime: entry.localDateTime, utcOffsetMinutes: entry.utcOffsetMinutes, fold: entry.fold,
      temperatureC, dewPointC, pressureHpa: source.pressureHpa ?? null, relativeHumidityPct: humidity, wetBulbC: wetBulb,
      precipitationMm, precipitationPhase: phase, snowfallCm,
      windSpeedKph: source.windSpeedKph ?? null, windDirectionDeg: source.windDirectionDeg ?? null, windGustKph: source.windGustKph ?? null,
      shortwaveRadiationWm2, cloudCoverPct, visibilityKm: source.visibilityKm ?? null,
      condition, hazards: [], quality };
  });
  const days = observedDays(calendar, byDay, hours, engine);
  const completeness = Object.fromEntries(VARIABLES.map((field) => [field, hours.filter((hour) => hour[field] != null).length / hours.length]));
  const endExclusive = new Date(new Date(hours.at(-1).at).getTime() + 3_600_000).toISOString();
  const base = { version: 1, station, validationYear: year, startInclusive: hours[0].at, endExclusive, hours, days, completeness,
    observationHash: '', provenance: { providers: ['MERRA-2', 'Daymet V4'], sourceIds: station.sourceIds, warnings: [
      'Snowfall is derived from liquid-equivalent precipitation and thermodynamic phase; no station snowfall product is used.',
    ] } };
  return { ...base, observationHash: engine.hashWithout(base, ['observationHash']) };
}
function publicJob(job) {
  const { controller: _controller, cancelled: _cancelled, terminalPromise: _terminalPromise, writeChain: _writeChain, ...value } = job;
  return structuredClone(value);
}

export class WeatherLabService {
  constructor({ cacheDirectory, mode = 'fixture', sourceCache, fetchImpl = globalThis.fetch, environment = process.env, now = () => new Date(), daymet, merra2 }) {
    this.mode = mode; this.store = new WeatherLabArtifactStore(cacheDirectory); this.jobs = new Map(); this.requestJobs = new Map(); this.now = now;
    const labSourceCache = sourceCache ?? new SourceCache(path.resolve(cacheDirectory, 'weather-lab-v1'));
    this.daymet = daymet ?? new DaymetLabAdapter({ sourceCache: labSourceCache, fetchImpl, environment });
    this.merra2 = merra2 ?? new Merra2Adapter({ sourceCache: labSourceCache, fetchImpl, environment });
  }
  async locationContext({ latitude, longitude, elevationOverrideM, signal } = {}) {
    latitude = Number(latitude); longitude = Number(longitude);
    if (!validCoordinate(latitude, longitude)) throw new WeatherServiceError('INVALID_REQUEST', 'Latitude must be -90..90 and longitude must be -180..180.', { status: 400 });
    const normalizedLatitude = rounded(latitude); const normalizedLongitude = rounded(longitude);
    if (elevationOverrideM !== undefined && (!Number.isFinite(Number(elevationOverrideM)) || Number(elevationOverrideM) < -500 || Number(elevationOverrideM) > 9000))
      throw new WeatherServiceError('INVALID_REQUEST', 'Elevation override must be between -500 and 9000 metres.', { status: 400 });
    if (this.mode === 'fixture') {
      const supported = Math.hypot(normalizedLatitude - 44.1672897, normalizedLongitude + 71.164239) < 1;
      return { version: 1, latitude: normalizedLatitude, longitude: normalizedLongitude, coverage: supported ? 'supported' : 'unsupported',
        ...(supported ? {} : { coverageReason: 'Fixture mode contains only the Jackson development artifact.' }),
        resolvedElevationM: supported ? 427 : null, elevationSource: supported ? 'fixture' : 'unavailable', timezone: supported ? JACKSON.timezone : null,
        timezoneResolution: supported ? 'fixture' : 'unavailable', stations: supported ? [JACKSON] : [], selectedStation: supported ? JACKSON : null,
        eligibleValidationYears: supported ? [2019] : [], warnings: ['Fixture mode is not a live historical comparison.'] };
    }
    const latestYear = this.now().getUTCFullYear() - 1; let daymet;
    try { daymet = await this.daymet.request(normalizedLatitude, normalizedLongitude, [latestYear], signal); }
    catch (error) {
      if (error instanceof WeatherServiceError && error.status === 422) return { version: 1, latitude: normalizedLatitude, longitude: normalizedLongitude,
        coverage: 'unsupported', coverageReason: error.message, resolvedElevationM: null, elevationSource: 'unavailable', timezone: null,
        timezoneResolution: 'unavailable', stations: [], selectedStation: null, eligibleValidationYears: [], warnings: [] };
      throw error;
    }
    const elevation = elevationOverrideM === undefined ? daymet.elevationM : Number(elevationOverrideM);
    const timezone = resolveNorthAmericanTimezone(normalizedLatitude, normalizedLongitude);
    if (!timezone) return { version: 1, latitude: normalizedLatitude, longitude: normalizedLongitude, coverage: 'supported',
      resolvedElevationM: elevation, elevationSource: 'daymet', timezone: null, timezoneResolution: 'unavailable', stations: [], selectedStation: null,
      eligibleValidationYears: [], warnings: ['No IANA timezone could be resolved for this Daymet coordinate.'] };
    const selectedStation = merraGrid(normalizedLatitude, normalizedLongitude, elevation, timezone, latestYear);
    const stations = [selectedStation]; const years = eligibleYears(selectedStation, latestYear);
    return { version: 1, latitude: normalizedLatitude, longitude: normalizedLongitude, coverage: 'supported',
      resolvedElevationM: elevation, elevationSource: 'daymet', timezone, timezoneResolution: 'coordinate',
      stations, selectedStation, eligibleValidationYears: years,
      warnings: years.length ? [] : ['The selected station does not have a complete prior-30 period for an available validation year.'] };
  }
  async stations(query) { return (await this.locationContext({ latitude: query.get('latitude'), longitude: query.get('longitude'), elevationOverrideM: query.get('elevationM') ?? undefined })).stations; }
  async create(body) {
    const legacyFixture = body?.stationId === JACKSON.id;
    const request = legacyFixture ? { version: 1, latitude: 44.1672897, longitude: -71.164239, validationYear: 2019,
      trainingPolicy: body.trainingPolicy ?? { kind: 'prior-30' } } : body;
    if (!request || request.version !== 1 || !Number.isInteger(request.validationYear)) throw new WeatherServiceError('INVALID_REQUEST', 'Weather Lab preparation request is invalid.', { status: 400 });
    const controller = new AbortController(); const context = await this.locationContext({ ...request, signal: controller.signal });
    if (context.coverage !== 'supported') throw new WeatherServiceError('UNSUPPORTED_LOCATION', context.coverageReason ?? 'Coordinate is outside Daymet land coverage.', { status: 422 });
    if (!context.selectedStation) throw new WeatherServiceError('NO_SOURCE_GRID', 'No qualifying MERRA-2 grid cell was resolved.', { status: 422 });
    if (!context.eligibleValidationYears.includes(request.validationYear)) throw new WeatherServiceError('INSUFFICIENT_HISTORY', `Validation year ${request.validationYear} lacks the requested training period.`, { status: 422 });
    const years = trainingYears(request.trainingPolicy, request.validationYear); if (years.includes(request.validationYear)) throw new WeatherServiceError('TRAINING_LEAK', 'Validation year entered fitted inputs.', { status: 400 });
    const requestKey = hash({ namespace: 'weather-lab-preparation-v2', mode: this.mode,
      request: { ...request, latitude: context.latitude, longitude: context.longitude }, stationId: context.selectedStation.id });
    const activeJob = this.requestJobs.get(requestKey);
    if (activeJob && !['failed', 'cancelled'].includes(activeJob.status)) return publicJob(activeJob);
    const cached = await this.store.readPreparedRequest(requestKey);
    if (cached) {
      try {
        await Promise.all([this.store.read('models', cached.modelHash), this.store.read('observations', cached.observationHash)]);
        const id = randomUUID(); const timestamp = this.now().toISOString(); const job = { version: 1, id, status: 'succeeded',
          progress: { stage: 'ready', completed: 5, total: 5, message: 'Cached historical preparation is ready.' }, context, request,
          requestKey, createdAt: timestamp, updatedAt: timestamp, result: cached,
          events: [{ sequence: 1, at: timestamp, stage: 'ready', message: 'Reused cached climate and observation artifacts.' }] };
        await this.store.writePreparation(job); this.jobs.set(id, job); this.requestJobs.set(requestKey, job); return publicJob(job);
      } catch (error) {
        if (error?.code !== 'PACKAGE_NOT_FOUND') throw error;
      }
    }
    const id = randomUUID(); const timestamp = this.now().toISOString(); const job = { version: 1, id, status: 'queued',
      progress: { stage: 'queued', completed: 0, total: 5, message: 'Preparation queued.' }, context, request, createdAt: timestamp, updatedAt: timestamp,
      requestKey, events: [{ sequence: 1, at: timestamp, stage: 'queued', message: 'Preparation queued.' }], controller, cancelled: false };
    this.jobs.set(id, job); this.requestJobs.set(requestKey, job); await this.store.writePreparation(publicJob(job)); queueMicrotask(() => this.run(job, years)); return publicJob(job);
  }
  async update(job, stage, completed, message, detail) {
    if (job.terminalPromise) { await job.terminalPromise; return; }
    job.updatedAt = this.now().toISOString();
    job.progress = { stage, completed, total: 5, message,
      ...(detail ? { detailCompleted: detail.completed, detailTotal: detail.total } : {}) };
    job.events ??= [];
    job.events.push({ sequence: (job.events.at(-1)?.sequence ?? 0) + 1, at: job.updatedAt, stage, message });
    if (job.events.length > 300) job.events.splice(0, job.events.length - 300);
    await this.persist(job, publicJob(job));
  }
  async persist(job, snapshot) {
    const previous = job.writeChain ?? Promise.resolve();
    const pending = previous.catch(() => undefined).then(() => this.store.writePreparation(snapshot));
    job.writeChain = pending;
    try { await pending; } finally { if (job.writeChain === pending) job.writeChain = undefined; }
  }
  async publishTerminal(job, status, stage, completed, message, error) {
    if (job.terminalPromise) return job.terminalPromise;
    const updatedAt = this.now().toISOString(); const events = [...(job.events ?? []), {
      sequence: (job.events?.at(-1)?.sequence ?? 0) + 1, at: updatedAt, stage, message,
    }];
    if (events.length > 300) events.splice(0, events.length - 300);
    const snapshot = { ...publicJob(job), status, updatedAt, progress: { stage, completed, total: 5, message }, events,
      ...(error ? { error } : {}) };
    const pending = (async () => { await this.persist(job, snapshot); Object.assign(job, snapshot); })();
    job.terminalPromise = pending;
    try { await pending; } catch (cause) { if (job.terminalPromise === pending) job.terminalPromise = undefined; throw cause; }
  }
  async run(job, years) {
    try {
      requireActive(job);
      job.status = 'running'; await this.update(job, 'daymet', 1, 'Fetching Daymet daily history and elevation.');
      if (this.mode === 'fixture') {
        const fixture = await import('../../weather-engine/src/fixtures/jackson2019.ts'); const observed = fixture.createJacksonObserved2019(); const model = fixture.createJacksonClimateModel();
        const observationHash = await this.store.install('observations', observed); const modelHash = await this.store.install('models', model);
        requireActive(job); job.result = { modelHash, observationHash, modelUrl: `/v1/weather-lab/models/${modelHash}`,
          observedSeriesUrl: `/v1/weather-lab/observed-series/${observationHash}` };
        await this.store.writePreparedRequest(job.requestKey, job.result); requireActive(job);
        await this.publishTerminal(job, 'succeeded', 'ready', 5, 'Development fixture ready.'); return;
      }
      const allYears = [...new Set([...years, job.request.validationYear])].sort((a, b) => a - b);
      await this.update(job, 'daymet', 1,
        `Fetching Daymet daily history for ${allYears[0]}–${allYears.at(-1)} (${allYears.length} years).`);
      const daymet = await this.daymet.request(job.context.latitude, job.context.longitude, allYears, job.controller.signal);
      requireActive(job);
      const source = job.context.selectedStation; const series = []; const merraByYear = new Map();
      const sourceYears = [...new Set([...allYears, ...allYears.map((year) => year + 1)])].sort((a, b) => a - b);
      const sourceYearTotal = sourceYears.length; let sourceYearCompleted = 0;
      await this.update(job, 'merra2', 2, `Preparing ${sourceYearTotal} MERRA-2 years.`,
        { completed: sourceYearCompleted, total: sourceYearTotal });
      for (const year of sourceYears) {
        requireActive(job);
        await this.update(job, 'merra2', 2, `MERRA-2: retrieving ${year} (${sourceYearCompleted + 1}/${sourceYearTotal}).`,
          { completed: sourceYearCompleted, total: sourceYearTotal });
        const context = { signal: job.controller.signal, throwIfAborted: () => requireActive(job) };
        const hourly = await this.merra2.getHourly({ latitude: job.context.latitude, longitude: job.context.longitude }, year, context);
        requireActive(job);
        merraByYear.set(year, normalizedMerraRows(hourly.hours)); sourceYearCompleted += 1;
        await this.update(job, 'merra2', 2, `MERRA-2 hourly preparation ${sourceYearCompleted}/${sourceYearTotal}.`,
          { completed: sourceYearCompleted, total: sourceYearTotal });
      }
      for (const year of allYears) {
        const normalized = await historicalSeries(source, year, job.context.timezone,
          [...(merraByYear.get(year) ?? []), ...(merraByYear.get(year + 1) ?? [])], daymet.days);
        if (coreCompleteness(normalized.hours) < .45 || normalized.completeness.temperatureC < .7)
          throw new WeatherServiceError('INSUFFICIENT_HISTORY', `MERRA-2 lacks complete core fields for ${year}.`, { status: 422 });
        series.push(normalized);
      }
      const observed = series.find((candidate) => candidate.validationYear === job.request.validationYear);
      if (!observed) throw new WeatherServiceError('INSUFFICIENT_HISTORY', 'Primary validation observations were not prepared.', { status: 422 });
      const missingTrainingYears = years.filter((year) => !series.some((candidate) => candidate.validationYear === year));
      if (missingTrainingYears.length) throw new WeatherServiceError('INSUFFICIENT_HISTORY',
        `No quality-controlled station series is available for training year${missingTrainingYears.length === 1 ? '' : 's'} ${missingTrainingYears.join(', ')}.`, { status: 422 });
      await this.update(job, 'compiling', 3, 'Compiling monthly climate transitions and emissions.');
      const engine = await import('../../weather-engine/src/index.ts'); const elevation = job.request.elevationOverrideM ?? job.context.resolvedElevationM;
      const location = { id: `coord-${job.context.latitude.toFixed(5)}-${job.context.longitude.toFixed(5)}`,
        name: `${job.context.latitude.toFixed(4)}, ${job.context.longitude.toFixed(4)}`, latitude: job.context.latitude,
        longitude: job.context.longitude, comparisonElevationM: elevation };
      const trainingSeries = series.filter((candidate) => years.includes(candidate.validationYear));
      const model = engine.compileLocationClimateModel({ version: 1, location, primaryStation: job.context.selectedStation,
        trainingStations: [job.context.selectedStation], trainingPolicy: job.request.trainingPolicy, validationYear: job.request.validationYear,
        trainingSeries, sourceHashes: [engine.sha256Hex(daymet), ...trainingSeries.map((candidate) => candidate.observationHash)],
        providers: ['Daymet V4', 'MERRA-2'], compilerVersion: 'weather-compiler-v1-live' });
      requireActive(job);
      await this.update(job, 'persisting', 4, 'Writing immutable model and observation artifacts.');
      const observationHash = await this.store.install('observations', observed); requireActive(job);
      const modelHash = await this.store.install('models', model); requireActive(job);
      job.result = { modelHash, observationHash, modelUrl: `/v1/weather-lab/models/${modelHash}`,
        observedSeriesUrl: `/v1/weather-lab/observed-series/${observationHash}` };
      await this.store.writePreparedRequest(job.requestKey, job.result); requireActive(job);
      await this.publishTerminal(job, 'succeeded', 'ready', 5, 'Historical preparation is ready.');
    } catch (error) {
      if (job.controller.signal.aborted || error?.code === 'JOB_CANCELLED') {
        const cancelled = { code: 'JOB_CANCELLED', message: 'Weather Lab preparation was cancelled.' };
        await this.publishTerminal(job, 'cancelled', 'cancelled', 0, cancelled.message, cancelled);
        return;
      }
      const failure = { code: error?.code ?? 'PREPARATION_FAILED', message: error instanceof Error ? error.message : String(error), retryable: error?.retryable === true };
      await this.publishTerminal(job, 'failed', 'failed', 0, failure.message, failure);
    }
  }
  async get(id) { const job = this.jobs.get(id); if (job) return publicJob(job); const stored = await this.store.readPreparation(id); if (!stored) throw new WeatherServiceError('UNKNOWN_JOB', `Weather Lab preparation '${id}' was not found.`, { status: 404 }); return stored; }
  async cancel(id) {
    const job = this.jobs.get(id); if (!job) return this.get(id);
    if (job.terminalPromise) { await job.terminalPromise; return publicJob(job); }
    if (!['succeeded','failed','cancelled'].includes(job.status)) {
      job.cancelled = true; job.controller.abort(); const cancelled = { code: 'JOB_CANCELLED', message: 'Weather Lab preparation was cancelled.' };
      await this.publishTerminal(job, 'cancelled', 'cancelled', 0, cancelled.message, cancelled);
    }
    return publicJob(job);
  }
}
