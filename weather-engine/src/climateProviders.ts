import { Xoshiro128 } from './random.ts';
import {
  deriveClimateBaseline,
  type RawClimateDay,
  type ResortClimateBaseline,
  type ResortWeatherLocation,
} from './climateBaseline.ts';

const DAYMET_URL = 'https://daymet.ornl.gov/single-pixel/api/data';
const POWER_URL = 'https://power.larc.nasa.gov/api/temporal/daily/point';
const YEARS = Array.from({ length: 10 }, (_, index) => 2010 + index);

export interface ParsedDaymet {
  elevationM: number;
  days: RawClimateDay[];
}

export function parseDaymetCsv(text: string): ParsedDaymet {
  const lines = text.trim().split(/\r?\n/);
  const elevationLine = lines.find((line) => line.startsWith('Elevation:'));
  const elevationMatch = elevationLine?.match(/Elevation:\s*([\d.-]+)\s*meters/i);
  const headerIndex = lines.findIndex((line) => line.startsWith('year,yday,'));
  if (headerIndex < 0) throw new Error('Daymet response is missing its data header');
  const headers = lines[headerIndex].split(',').map((value) => value.trim());
  const column = (prefix: string): number => {
    const index = headers.findIndex((header) => header.startsWith(prefix));
    if (index < 0) throw new Error(`Daymet response is missing ${prefix}`);
    return index;
  };
  const indexes = {
    year: column('year'),
    day: column('yday'),
    dayLength: column('dayl'),
    precip: column('prcp'),
    swe: column('swe'),
    max: column('tmax'),
    min: column('tmin'),
    vapor: column('vp'),
  };
  const days = lines.slice(headerIndex + 1).filter(Boolean).map((line): RawClimateDay => {
    const values = line.split(',').map(Number);
    return {
      year: values[indexes.year],
      dayOfYear: values[indexes.day],
      dayLengthSeconds: values[indexes.dayLength],
      precipitationMm: values[indexes.precip],
      snowWaterEquivalentKgM2: values[indexes.swe],
      maxTempC: values[indexes.max],
      minTempC: values[indexes.min],
      vaporPressurePa: values[indexes.vapor],
    };
  });
  return { elevationM: elevationMatch ? Number(elevationMatch[1]) : 0, days };
}

export async function fetchDaymetBaseline(
  location: ResortWeatherLocation,
  fetcher: typeof fetch = fetch,
): Promise<ResortClimateBaseline> {
  const query = new URLSearchParams({
    lat: String(location.latitude),
    lon: String(location.longitude),
    vars: 'tmax,tmin,prcp,vp,swe,dayl',
    years: YEARS.join(','),
  });
  const response = await fetcher(`${DAYMET_URL}?${query}`);
  if (!response.ok) throw new Error(`Daymet returned ${response.status}`);
  const parsed = parseDaymetCsv(await response.text());
  return deriveClimateBaseline(location, parsed.days, 'daymet', parsed.elevationM);
}

interface PowerResponse {
  properties?: {
    parameter?: Record<string, Record<string, number>>;
  };
  geometry?: { coordinates?: [number, number, number] };
}

export function parsePowerJson(
  raw: PowerResponse,
): { elevationM: number; days: RawClimateDay[] } {
  const parameters = raw.properties?.parameter;
  if (!parameters) throw new Error('NASA POWER response is missing parameters');
  const min = parameters.T2M_MIN;
  const max = parameters.T2M_MAX;
  const precip = parameters.PRECTOTCORR;
  const humidity = parameters.RH2M;
  if (!min || !max || !precip) throw new Error('NASA POWER response is missing required variables');
  const keys = Object.keys(min).filter((key) => /^\d{8}$/.test(key)).sort();
  const days = keys.map((key): RawClimateDay => {
    const year = Number(key.slice(0, 4));
    const date = new Date(Date.UTC(year, Number(key.slice(4, 6)) - 1, Number(key.slice(6, 8))));
    const dayOfYear = Math.floor((date.getTime() - Date.UTC(year, 0, 0)) / 86_400_000);
    const averageTemp = (min[key] + max[key]) / 2;
    const rh = humidity?.[key] ?? 70;
    const saturation = 610.94 * Math.exp((17.625 * averageTemp) / (averageTemp + 243.04));
    return {
      year,
      dayOfYear: Math.min(365, dayOfYear),
      minTempC: min[key],
      maxTempC: max[key],
      precipitationMm: Math.max(0, precip[key]),
      vaporPressurePa: saturation * rh / 100,
      snowWaterEquivalentKgM2: 0,
      dayLengthSeconds: 43_200,
    };
  });
  return { elevationM: raw.geometry?.coordinates?.[2] ?? 0, days };
}

export async function fetchPowerBaseline(
  location: ResortWeatherLocation,
  fetcher: typeof fetch = fetch,
): Promise<ResortClimateBaseline> {
  const query = new URLSearchParams({
    parameters: 'T2M_MIN,T2M_MAX,PRECTOTCORR,RH2M',
    community: 'AG',
    longitude: String(location.longitude),
    latitude: String(location.latitude),
    start: '20100101',
    end: '20191231',
    format: 'JSON',
  });
  const response = await fetcher(`${POWER_URL}?${query}`);
  if (!response.ok) throw new Error(`NASA POWER returned ${response.status}`);
  const parsed = parsePowerJson(await response.json() as PowerResponse);
  return deriveClimateBaseline(location, parsed.days, 'nasa-power', parsed.elevationM);
}

export function createProceduralBaseline(location: ResortWeatherLocation): ResortClimateBaseline {
  const random = new Xoshiro128(`procedural:${location.latitude}:${location.longitude}`);
  const days: RawClimateDay[] = [];
  for (const year of YEARS) {
    for (let day = 1; day <= 365; day += 1) {
      const seasonal = Math.cos(((day - 15) / 365) * Math.PI * 2);
      const latitudeCooling = Math.abs(location.latitude - 35) * 0.35;
      const elevationCooling = location.baseElevationM * 0.0065;
      const meanTemp = 12 - latitudeCooling - elevationCooling - seasonal * 14;
      const wet = random.next() < 0.34;
      const precipitation = wet ? random.gamma(1.2, 7) : 0;
      const low = meanTemp - 5 + random.normal(0, 3.5);
      const high = meanTemp + 5 + random.normal(0, 3.5);
      const rh = wet ? 88 : 65;
      const saturation = 610.94 * Math.exp((17.625 * meanTemp) / (meanTemp + 243.04));
      days.push({
        year,
        dayOfYear: day,
        minTempC: low,
        maxTempC: Math.max(low + 1, high),
        precipitationMm: precipitation,
        vaporPressurePa: saturation * rh / 100,
        snowWaterEquivalentKgM2: 0,
        dayLengthSeconds: 43_200,
      });
    }
  }
  return deriveClimateBaseline(location, days, 'procedural', location.baseElevationM);
}

function isDaymetCoverage(location: ResortWeatherLocation): boolean {
  return location.latitude >= 14 && location.latitude <= 83 &&
    location.longitude >= -179 && location.longitude <= -52;
}

export async function fetchClimateBaseline(
  location: ResortWeatherLocation,
  fetcher: typeof fetch = fetch,
): Promise<ResortClimateBaseline> {
  if (isDaymetCoverage(location)) {
    try {
      return await fetchDaymetBaseline(location, fetcher);
    } catch {
      // Continue to the global provider.
    }
  }
  try {
    return await fetchPowerBaseline(location, fetcher);
  } catch {
    return createProceduralBaseline(location);
  }
}
