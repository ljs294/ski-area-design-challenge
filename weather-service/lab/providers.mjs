import tzLookup from '@photostructure/tz-lookup';
import { WeatherServiceError } from '../lib/errors.mjs';

const DAYMET_URL = 'https://daymet.ornl.gov/single-pixel/api/data';
const CORE_FIELDS = ['temperatureC', 'dewPointC', 'pressureHpa', 'windSpeedKph', 'cloudCoverPct', 'precipitationMm'];

export function parseCsv(text) {
  const lines = text.split(/\r?\n/).filter((line) => line.trim());
  if (!lines.length) return [];
  const cells = (line) => {
    const result = []; let value = ''; let quoted = false;
    for (let index = 0; index < line.length; index += 1) {
      const character = line[index];
      if (character === '"' && line[index + 1] === '"') { value += '"'; index += 1; }
      else if (character === '"') quoted = !quoted;
      else if (character === ',' && !quoted) { result.push(value.trim()); value = ''; }
      else value += character;
    }
    result.push(value.trim()); return result;
  };
  const headers = cells(lines[0]).map((header) => header.trim().toUpperCase());
  return lines.slice(1).map((line) => Object.fromEntries(cells(line).map((value, index) => [headers[index] ?? `COLUMN_${index}`, value])));
}

function finite(value) {
  if (value == null || (typeof value === 'string' && value.trim() === '')) return null;
  const parsed = Number(value); return Number.isFinite(parsed) ? parsed : null;
}
function rounded(value, digits = 6) { const multiplier = 10 ** digits; return Math.round(value * multiplier) / multiplier; }

async function response(fetchImpl, url, options, provider) {
  let result;
  try { result = await fetchImpl(url, options); } catch (cause) {
    throw new WeatherServiceError('PROVIDER_UNAVAILABLE', `${provider} could not be reached.`, { status: 503, retryable: true, cause });
  }
  if (!result.ok) throw new WeatherServiceError('PROVIDER_UNAVAILABLE', `${provider} returned HTTP ${result.status}.`, {
    status: result.status === 404 ? 422 : 503, retryable: result.status === 429 || result.status >= 500,
    details: { provider, upstreamStatus: result.status },
  });
  return result;
}

export function parseDaymet(text) {
  const elevation = text.match(/Elevation:\s*([\d.-]+)\s*meters/i);
  const lines = text.split(/\r?\n/); const header = lines.findIndex((line) => line.trim().toLowerCase().startsWith('year,yday,'));
  const rows = header < 0 ? [] : parseCsv(lines.slice(header).join('\n'));
  const days = rows.flatMap((row) => {
    const year = finite(row.YEAR); const day = finite(row.YDAY); if (year == null || day == null) return [];
    const value = (prefix) => finite(Object.entries(row).find(([key]) => key.startsWith(prefix))?.[1]);
    return [{ date: new Date(Date.UTC(year, 0, day)).toISOString().slice(0, 10), tminC: value('TMIN'), tmaxC: value('TMAX'),
      precipitationMm: value('PRCP'), vaporPressurePa: value('VP'), shortwaveWm2: value('SRAD'),
      snowWaterEquivalentMm: value('SWE'), daylightSeconds: value('DAYL') }];
  });
  return { elevationM: elevation ? Number(elevation[1]) : null, days };
}

export class DaymetLabAdapter {
  constructor({ sourceCache, fetchImpl = globalThis.fetch, environment = process.env }) {
    this.sourceCache = sourceCache; this.fetchImpl = fetchImpl; this.url = environment.DAYMET_SINGLE_PIXEL_URL ?? DAYMET_URL;
  }
  async request(latitude, longitude, years, signal) {
    const key = `${rounded(latitude, 4)}-${rounded(longitude, 4)}-${years.join('_')}`.replace(/-/g, 'm').replace(/\./g, '_');
    const cached = await this.sourceCache.getOrCreate('weather-lab-daymet-v4', key, async () => {
      const url = new URL(this.url); url.searchParams.set('lat', String(latitude)); url.searchParams.set('lon', String(longitude));
      url.searchParams.set('vars', 'tmax,tmin,prcp,vp,swe,dayl,srad'); url.searchParams.set('years', years.join(','));
      let upstream;
      try { upstream = await response(this.fetchImpl, url, { signal }, 'Daymet V4'); }
      catch (error) {
        if (error instanceof WeatherServiceError && [400, 404].includes(error.details?.upstreamStatus))
          throw new WeatherServiceError('UNSUPPORTED_LOCATION', 'Daymet has no North American land pixel at this coordinate.', { status: 422 });
        throw error;
      }
      const parsed = parseDaymet(await upstream.text());
      if (!parsed.days.length || parsed.elevationM == null)
        throw new WeatherServiceError('UNSUPPORTED_LOCATION', 'Daymet has no land pixel at this coordinate.', { status: 422 });
      return parsed;
    });
    return cached.value;
  }
}

export function resolveNorthAmericanTimezone(latitude, longitude) {
  return tzLookup(Number(latitude), Number(longitude));
}

export function coreCompleteness(hours) {
  if (!hours.length) return 0;
  return CORE_FIELDS.reduce((sum, field) => sum + hours.filter((hour) => hour[field] != null).length / hours.length, 0) / CORE_FIELDS.length;
}
