import { WeatherServiceError, invariant } from './errors.mjs';
import { sourceCacheKey } from './contract.mjs';

const DAYMET_VARIABLES = ['dayl', 'prcp', 'srad', 'swe', 'tmax', 'tmin', 'vp'];
const NASA_POWER_HOURLY_URL = 'https://power.larc.nasa.gov/api/temporal/hourly/point';
const NASA_POWER_PARAMETERS = ['T2M', 'RH2M', 'PS', 'U10M', 'V10M', 'PRECTOT', 'CLOUD_AMT', 'ALLSKY_SFC_SW_DWN'];

function number(value, fallback = 0) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function clamp(value, minimum, maximum) {
  return Math.max(minimum, Math.min(maximum, value));
}

function hash32(text) {
  let hash = 2166136261;
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0;
}

function noise(text) {
  return hash32(text) / 0xffffffff;
}

function yearDays(year) {
  const start = Date.UTC(year, 0, 1);
  const end = Date.UTC(year + 1, 0, 1);
  return Math.round((end - start) / 86_400_000);
}

function dayOfYear(date) {
  return Math.floor((Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()) - Date.UTC(date.getUTCFullYear(), 0, 1)) / 86_400_000) + 1;
}

function isoDate(year, index) {
  return new Date(Date.UTC(year, 0, 1 + index)).toISOString().slice(0, 10);
}

function saturationVaporPressureHpa(temperatureC) {
  return 6.112 * Math.exp((17.67 * temperatureC) / (temperatureC + 243.5));
}

function relativeHumidityForVaporPressure(temperatureC, vaporPressurePa) {
  return clamp((vaporPressurePa / 100) / saturationVaporPressureHpa(temperatureC) * 100, 1, 100);
}

function solarGeometry(latitude, longitude, date) {
  const day = dayOfYear(date);
  const declination = 23.44 * Math.sin((2 * Math.PI * (284 + day)) / 365) * Math.PI / 180;
  const latitudeRadians = latitude * Math.PI / 180;
  const localSolarHour = date.getUTCHours() + date.getUTCMinutes() / 60 + longitude / 15;
  const hourAngle = (localSolarHour - 12) * 15 * Math.PI / 180;
  const sineElevation = Math.sin(latitudeRadians) * Math.sin(declination) + Math.cos(latitudeRadians) * Math.cos(declination) * Math.cos(hourAngle);
  const elevation = Math.asin(clamp(sineElevation, -1, 1));
  const clearSkyWm2 = Math.max(0, 980 * Math.sin(Math.max(0, elevation)) ** 0.9);
  const azimuth = (Math.atan2(
    Math.sin(hourAngle),
    Math.cos(hourAngle) * Math.sin(latitudeRadians) - Math.tan(declination) * Math.cos(latitudeRadians),
  ) * 180 / Math.PI + 180 + 360) % 360;
  return { elevationDeg: elevation * 180 / Math.PI, azimuthDeg: azimuth, clearSkyWm2 };
}

/**
 * Pressure-aware psychrometric solve.  A bounded bisection is slower than the
 * Stull shortcut but makes the package's wet-bulb field suitable for snow
 * phase and future snow-cover calculations at high elevation.
 */
function wetBulbC(temperatureC, relativeHumidityPct, pressureHpa = 1013.25) {
  const humidity = clamp(relativeHumidityPct, 1, 100);
  const pressure = clamp(pressureHpa, 300, 1100);
  const vaporPressure = saturationVaporPressureHpa(temperatureC) * humidity / 100;
  const residual = (candidate) => {
    const psychrometricHpaPerC = 0.00066 * (1 + 0.00115 * candidate) * pressure;
    return saturationVaporPressureHpa(candidate) - psychrometricHpaPerC * (temperatureC - candidate) - vaporPressure;
  };
  let low = Math.min(-80, temperatureC - 60);
  let high = temperatureC;
  for (let iteration = 0; iteration < 24; iteration += 1) {
    const middle = (low + high) / 2;
    if (residual(middle) > 0) high = middle;
    else low = middle;
  }
  return (low + high) / 2;
}

function precipitationType(wetBulb) {
  if (wetBulb <= -1) return 'snow';
  if (wetBulb < 0.75) return 'mixed';
  if (wetBulb < 1.5) return 'freezing-rain';
  return 'rain';
}

/**
 * The development source intentionally produces deterministic but clearly
 * marked fixture data. It is not a procedural production fallback: live mode
 * refuses to build if a provider is not configured.
 */
export class FixtureDaymetAdapter {
  constructor(sourceCache) {
    this.id = 'fixture-daymet';
    this.version = 'fixture-v1';
    this.sourceCache = sourceCache;
  }

  async getDaily(request, year, context) {
    const cacheKey = sourceCacheKey(this.id, this.version, request, { year });
    const result = await this.sourceCache.getOrCreate(this.id, cacheKey, async () => {
      context.throwIfAborted();
      const days = [];
      const latitudeEffect = Math.abs(request.latitude - 37) * 0.55;
      const locality = noise(`${request.latitude}:${request.longitude}:climate`);
      const annualMean = 11 - latitudeEffect + (locality - 0.5) * 8;
      const annualAmplitude = 10 + Math.abs(request.latitude - 28) * 0.25 + locality * 4;
      for (let index = 0; index < yearDays(year); index += 1) {
        const date = new Date(Date.UTC(year, 0, 1 + index));
        const seasonal = Math.sin((2 * Math.PI * (dayOfYear(date) - 173)) / 365);
        const dailyNoise = (noise(`${request.terrainBinding}:daymet:${year}:${index}`) - 0.5) * 5;
        const center = annualMean + annualAmplitude * seasonal + dailyNoise;
        const range = 5 + noise(`${request.terrainBinding}:range:${year}:${index}`) * 8;
        const geometry = solarGeometry(request.latitude, request.longitude, new Date(Date.UTC(year, 0, 1 + index, 12)));
        const daylightSeconds = Math.max(1_800, Math.min(86_400, 43_200 + 23_000 * Math.sin((2 * Math.PI * (dayOfYear(date) - 80)) / 365) * Math.cos(request.latitude * Math.PI / 180)));
        const stormDay = noise(`${request.terrainBinding}:storm:${year}:${Math.floor(index / 2)}`) > 0.82;
        const precipitationMm = stormDay ? Math.round((1 + noise(`${request.terrainBinding}:precip:${year}:${index}`) * 14) * 10) / 10 : 0;
        const shortwaveWm2 = Math.round((geometry.clearSkyWm2 * (0.32 + noise(`${request.terrainBinding}:sun:${year}:${index}`) * 0.48)) * 10) / 10;
        const vaporPressurePa = Math.round(saturationVaporPressureHpa(center - 1) * (0.42 + noise(`${request.terrainBinding}:humidity:${year}:${index}`) * 0.44) * 100);
        days.push({
          date: isoDate(year, index), tminC: Math.round((center - range / 2) * 10) / 10,
          tmaxC: Math.round((center + range / 2) * 10) / 10, precipitationMm,
          vaporPressurePa, snowWaterEquivalentMm: center < 0 ? precipitationMm * 0.7 : 0,
          shortwaveWm2, daylightSeconds,
        });
      }
      return { days, sourceGrid: { id: `fixture-daymet-${request.latitude.toFixed(3)}-${request.longitude.toFixed(3)}`, resolutionMeters: 1000 } };
    });
    return { ...result.value, cacheHit: result.cacheHit, provider: this.id, version: this.version, quality: 'limited' };
  }
}

export class FixtureMerra2Adapter {
  constructor(sourceCache) {
    this.id = 'fixture-merra2';
    this.version = 'fixture-v1';
    this.sourceCache = sourceCache;
  }

  async getHourly(request, year, context) {
    const cacheKey = sourceCacheKey(this.id, this.version, request, { year });
    const result = await this.sourceCache.getOrCreate(this.id, cacheKey, async () => {
      const hours = [];
      const total = yearDays(year) * 24;
      const latitudeEffect = Math.abs(request.latitude - 37) * 0.55;
      const locality = noise(`${request.latitude}:${request.longitude}:climate`);
      const annualMean = 11 - latitudeEffect + (locality - 0.5) * 8;
      const annualAmplitude = 10 + Math.abs(request.latitude - 28) * 0.25 + locality * 4;
      for (let index = 0; index < total; index += 1) {
        if (index % 512 === 0) context.throwIfAborted();
        const date = new Date(Date.UTC(year, 0, 1, index));
        const day = Math.floor(index / 24);
        const hour = date.getUTCHours();
        const seasonal = Math.sin((2 * Math.PI * (dayOfYear(date) - 173)) / 365);
        const diurnal = Math.sin((2 * Math.PI * ((hour + request.longitude / 15) - 15)) / 24) * (3 + locality * 3);
        const weatherNoise = (noise(`${request.terrainBinding}:merra-temp:${year}:${Math.floor(index / 6)}`) - 0.5) * 4;
        const temperatureC = annualMean + annualAmplitude * seasonal + diurnal + weatherNoise;
        const cloudPulse = noise(`${request.terrainBinding}:cloud:${year}:${Math.floor(index / 4)}`);
        const cloudCoverPct = clamp(Math.round((cloudPulse > 0.73 ? 70 + cloudPulse * 30 : 10 + cloudPulse * 70)), 0, 100);
        const geometry = solarGeometry(request.latitude, request.longitude, date);
        const shortwaveWm2 = geometry.clearSkyWm2 * (1 - cloudCoverPct / 125);
        const storm = noise(`${request.terrainBinding}:storm:${year}:${Math.floor(day / 2)}`) > 0.82;
        const stormHour = storm && noise(`${request.terrainBinding}:storm-hour:${year}:${Math.floor(index / 3)}`) > 0.38;
        const precipitationMm = stormHour ? (0.15 + noise(`${request.terrainBinding}:merra-precip:${year}:${index}`) * 2.4) : 0;
        const relativeHumidityPct = clamp(48 + cloudCoverPct * 0.42 + precipitationMm * 10 + (noise(`${request.terrainBinding}:rh:${year}:${index}`) - 0.5) * 14, 15, 100);
        const windSpeedMps = 1.5 + noise(`${request.terrainBinding}:wind:${year}:${Math.floor(index / 3)}`) * 11;
        const windDirection = noise(`${request.terrainBinding}:wind-direction:${year}:${Math.floor(index / 8)}`) * Math.PI * 2;
        hours.push({
          at: date.toISOString(), temperatureC, relativeHumidityPct,
          pressureHpa: 1_008 + (noise(`${request.terrainBinding}:pressure:${year}:${Math.floor(index / 12)}`) - 0.5) * 28,
          uWindMps: Math.cos(windDirection) * windSpeedMps, vWindMps: Math.sin(windDirection) * windSpeedMps,
          precipitationMm, shortwaveWm2, cloudCoverPct,
        });
      }
      return { hours, sourceGrid: { id: `fixture-merra2-${request.latitude.toFixed(2)}-${request.longitude.toFixed(2)}`, resolutionDegrees: 0.5 } };
    });
    return { ...result.value, cacheHit: result.cacheHit, provider: this.id, version: this.version, quality: 'limited' };
  }
}

function isAlaskaOrHawaii(latitude, longitude) {
  return (latitude >= 51 && latitude <= 72.8 && longitude <= -129) || (latitude >= 18.5 && latitude <= 22.6 && longitude >= -161.2 && longitude <= -154.4);
}

function daymetCell(latitude, longitude) {
  // The provider still validates its exact 1 km pixel; this rounded cache key
  // avoids re-requesting the same local source subset for nearby map bounds.
  return { latitude: Math.round(latitude * 100) / 100, longitude: Math.round(longitude * 100) / 100 };
}

function merra2Cell(latitude, longitude) {
  return { latitude: Math.round(latitude / 0.5) * 0.5, longitude: Math.round(longitude / 0.625) * 0.625 };
}

async function fetchResponse(fetchImpl, url, options, provider) {
  let response;
  try {
    response = await fetchImpl(url, options);
  } catch (cause) {
    throw new WeatherServiceError('PROVIDER_UNAVAILABLE', `${provider} could not be reached.`, { retryable: true, cause });
  }
  if (!response.ok) {
    throw new WeatherServiceError('PROVIDER_UNAVAILABLE', `${provider} returned HTTP ${response.status}.`, {
      retryable: response.status >= 500 || response.status === 429,
      details: { provider, upstreamStatus: response.status },
    });
  }
  return response;
}

function csvRows(text) {
  const lines = text.split(/\r?\n/).filter((line) => line.trim() && !line.trimStart().startsWith('#'));
  if (lines.length < 2) throw new WeatherServiceError('PROVIDER_RESPONSE_INVALID', 'Provider CSV has no data rows.');
  const parse = (line) => {
    const cells = [];
    let current = '';
    let quoted = false;
    for (let index = 0; index < line.length; index += 1) {
      const char = line[index];
      if (char === '"' && line[index + 1] === '"') { current += char; index += 1; }
      else if (char === '"') quoted = !quoted;
      else if (char === ',' && !quoted) { cells.push(current.trim()); current = ''; }
      else current += char;
    }
    cells.push(current.trim());
    return cells;
  };
  const headings = parse(lines[0]).map((heading) => heading.toLowerCase());
  return lines.slice(1).map((line) => Object.fromEntries(parse(line).map((cell, index) => [headings[index] ?? `column${index}`, cell])));
}

function daymetDate(row) {
  if (row.date) return String(row.date).slice(0, 10);
  const year = Number(row.year);
  const yday = Number(row.yday ?? row.day_of_year);
  invariant(Number.isInteger(year) && Number.isInteger(yday), 'PROVIDER_RESPONSE_INVALID', 'Daymet row has no date or year/yday.');
  return new Date(Date.UTC(year, 0, yday)).toISOString().slice(0, 10);
}

export class DaymetAdapter {
  constructor({ sourceCache, fetchImpl = globalThis.fetch, environment = process.env } = {}) {
    this.id = 'daymet';
    this.version = environment.DAYMET_VERSION ?? 'V4R1';
    this.sourceCache = sourceCache;
    this.fetchImpl = fetchImpl;
    this.environment = environment;
  }

  async getDaily(request, year, context) {
    const specialRoute = isAlaskaOrHawaii(request.latitude, request.longitude);
    const route = specialRoute ? 'ncss' : 'single-pixel';
    const cacheKey = sourceCacheKey(this.id, this.version, request, { year, route, location: daymetCell(request.latitude, request.longitude) });
    const result = await this.sourceCache.getOrCreate(this.id, cacheKey, async () => {
      context.throwIfAborted();
      const configured = specialRoute ? this.environment.DAYMET_NCSS_URL : (this.environment.DAYMET_SINGLE_PIXEL_URL ?? 'https://daymet.ornl.gov/single-pixel/api/data');
      if (!configured) {
        throw new WeatherServiceError('PROVIDER_CONFIGURATION', 'DAYMET_NCSS_URL is required for Alaska and Hawaii Daymet subsets.', {
          details: { provider: this.id, route },
        });
      }
      const url = new URL(configured);
      url.searchParams.set('lat', String(request.latitude));
      url.searchParams.set('lon', String(request.longitude));
      url.searchParams.set('vars', DAYMET_VARIABLES.join(','));
      url.searchParams.set('start', `${year}-01-01`);
      url.searchParams.set('end', `${year}-12-31`);
      url.searchParams.set('format', 'csv');
      const response = await fetchResponse(this.fetchImpl, url, { signal: context.signal }, 'Daymet');
      const rows = csvRows(await response.text());
      const days = rows.map((row) => ({
        date: daymetDate(row), tminC: number(row.tmin), tmaxC: number(row.tmax), precipitationMm: number(row.prcp),
        vaporPressurePa: number(row.vp), snowWaterEquivalentMm: number(row.swe), shortwaveWm2: number(row.srad), daylightSeconds: number(row.dayl),
      })).filter((day) => day.date.startsWith(`${year}-`));
      invariant(days.length >= 360, 'PROVIDER_RESPONSE_INVALID', `Daymet returned only ${days.length} days for ${year}.`, { details: { provider: this.id, year } });
      return { days, sourceGrid: { id: `${route}:${request.latitude.toFixed(5)},${request.longitude.toFixed(5)}`, resolutionMeters: 1000, route } };
    });
    return { ...result.value, cacheHit: result.cacheHit, provider: this.id, version: this.version, quality: 'estimated' };
  }
}

function powerValue(parameters, name, key, fillValue) {
  const value = Number(parameters?.[name]?.[key]);
  return Number.isFinite(value) && value !== fillValue && value > -900 ? value : null;
}

/** Normalizes the public NASA POWER hourly point response into the MERRA-2 contract. */
export function normalizePowerHourly(payload, year) {
  const parameters = payload?.properties?.parameter; const fillValue = Number(payload?.header?.fill_value ?? -999);
  invariant(parameters && typeof parameters === 'object', 'PROVIDER_RESPONSE_INVALID', 'NASA POWER did not return hourly parameters.');
  const keys = Object.keys(parameters.T2M ?? {}).filter((key) => key.startsWith(String(year)) && /^\d{10}$/.test(key)).sort();
  const hours = keys.flatMap((key) => {
    const temperatureC = powerValue(parameters, 'T2M', key, fillValue);
    const relativeHumidityPct = powerValue(parameters, 'RH2M', key, fillValue);
    const pressureKpa = powerValue(parameters, 'PS', key, fillValue);
    const uWindMps = powerValue(parameters, 'U10M', key, fillValue);
    const vWindMps = powerValue(parameters, 'V10M', key, fillValue);
    const precipitationMm = powerValue(parameters, 'PRECTOT', key, fillValue);
    const cloudCoverPct = powerValue(parameters, 'CLOUD_AMT', key, fillValue);
    const solarKwhM2 = powerValue(parameters, 'ALLSKY_SFC_SW_DWN', key, fillValue);
    if ([temperatureC, relativeHumidityPct, pressureKpa, uWindMps, vWindMps,
      precipitationMm, cloudCoverPct, solarKwhM2].some((value) => value == null)) return [];
    const [, month, day, hour] = key.match(/^\d{4}(\d{2})(\d{2})(\d{2})$/);
    return [{ at: new Date(Date.UTC(year, Number(month) - 1, Number(day), Number(hour))).toISOString(),
      temperatureC, relativeHumidityPct, pressureHpa: pressureKpa * 10, uWindMps, vWindMps,
      precipitationMm: Math.max(0, precipitationMm), shortwaveWm2: Math.max(0, solarKwhM2 * 1_000),
      cloudCoverPct: clamp(cloudCoverPct, 0, 100) }];
  });
  const coordinates = payload?.geometry?.coordinates;
  return { hours, sourceGrid: { id: Array.isArray(coordinates) ? `nasa-power:${coordinates[1]},${coordinates[0]}` : `nasa-power:${year}`,
    resolutionDegrees: .5 } };
}

export class Merra2Adapter {
  constructor({ sourceCache, fetchImpl = globalThis.fetch, environment = process.env } = {}) {
    this.id = 'merra2';
    this.version = environment.NASA_POWER_VERSION ?? 'POWER-hourly-v1';
    this.sourceCache = sourceCache;
    this.fetchImpl = fetchImpl;
  }

  async getHourly(request, year, context) {
    const grid = merra2Cell(request.latitude, request.longitude);
    const cacheKey = sourceCacheKey(this.id, this.version, request, { year, route: 'nasa-power', grid: `${grid.latitude},${grid.longitude}`, location: grid });
    const result = await this.sourceCache.getOrCreate(this.id, cacheKey, async () => {
      context.throwIfAborted();
      const url = new URL(NASA_POWER_HOURLY_URL); url.searchParams.set('parameters', NASA_POWER_PARAMETERS.join(','));
      url.searchParams.set('community', 'RE'); url.searchParams.set('longitude', String(request.longitude));
      const boundaryOnly = year >= new Date().getUTCFullYear();
      url.searchParams.set('latitude', String(request.latitude)); url.searchParams.set('start', `${year}0101`);
      url.searchParams.set('end', boundaryOnly ? `${year}0102` : `${year}1231`);
      url.searchParams.set('format', 'JSON'); url.searchParams.set('time-standard', 'UTC');
      const response = await fetchResponse(this.fetchImpl, url, { signal: context.signal }, 'NASA POWER MERRA-2 hourly API');
      let payload;
      try { payload = await response.json(); } catch (cause) {
        throw new WeatherServiceError('PROVIDER_RESPONSE_INVALID', 'NASA POWER did not return JSON.', { cause });
      }
      const normalized = normalizePowerHourly(payload, year); const hours = normalized.hours;
      const minimumHours = year >= new Date().getUTCFullYear() ? 24 : 8_700;
      invariant(hours.length >= minimumHours, 'PROVIDER_RESPONSE_INVALID', `MERRA-2 returned only ${hours.length} hours for ${year}.`, { details: { provider: this.id, year } });
      return normalized;
    });
    return { ...result.value, cacheHit: result.cacheHit, provider: this.id, version: this.version, quality: 'estimated' };
  }
}

export function createProviderSet({ mode = 'fixture', sourceCache, fetchImpl = globalThis.fetch, environment = process.env } = {}) {
  invariant(sourceCache, 'INTERNAL', 'A source cache is required to create weather providers.');
  if (mode === 'fixture' || mode === 'mock') {
    return {
      mode: 'fixture',
      daymet: new FixtureDaymetAdapter(sourceCache), merra2: new FixtureMerra2Adapter(sourceCache),
      sourceSummary: 'Deterministic fixture weather for local development only; no provider observations were downloaded.',
      sourceVersion: 'fixture-v1', quality: 'limited',
    };
  }
  if (mode !== 'live') {
    throw new WeatherServiceError('PROVIDER_CONFIGURATION', `WEATHER_SERVICE_MODE '${mode}' is not supported. Use fixture or live.`);
  }
  return {
    mode: 'live', daymet: new DaymetAdapter({ sourceCache, fetchImpl, environment }),
    merra2: new Merra2Adapter({ sourceCache, fetchImpl, environment }),
    sourceSummary: 'Daymet 1 km daily constraints + public NASA POWER MERRA-2-based hourly atmosphere.',
    sourceVersion: `Daymet ${environment.DAYMET_VERSION ?? 'V4R1'}; NASA POWER ${environment.NASA_POWER_VERSION ?? 'POWER-hourly-v1'} (MERRA-2 meteorology)`,
    quality: 'estimated',
  };
}

export const weatherMath = { saturationVaporPressureHpa, relativeHumidityForVaporPressure, wetBulbC, precipitationType, solarGeometry };
