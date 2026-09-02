import {
  precipitationTypeFor, type PrecipitationType, type ResolvedWeatherHour,
} from './weatherModel';
import { localWeatherDateKey } from './localTime';
import { forecastForSession, wetBulbTemperatureC, type WeatherSession } from './weatherSession';

export const FORECAST_CONFIDENCE = [99, 98, 96, 85, 72, 58, 45] as const;

export interface GameForecastHour extends ResolvedWeatherHour {
  leadHour: number;
  confidencePct: number;
}

export interface GameForecastDay {
  date: string;
  confidencePct: number;
  condition: PrecipitationType;
  lowC: number;
  highC: number;
  precipitationMm: number;
  snowfallCm: number;
  windSpeedKph: number;
  windGustKph: number;
  hours: readonly GameForecastHour[];
}

export interface GameForecastIssue {
  schemaVersion: 1;
  issuedAt: string;
  endsAt: string;
  annualRunIdentity: string;
  hours: readonly GameForecastHour[];
  days: readonly GameForecastDay[];
}

function randomFor(seed: string): () => number {
  let state = 2166136261;
  for (let index = 0; index < seed.length; index += 1) {
    state ^= seed.charCodeAt(index);
    state = Math.imul(state, 16777619);
  }
  return () => {
    state += 0x6D2B79F5;
    let value = state;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4_294_967_296;
  };
}

const clamp = (value: number, low: number, high: number) => Math.max(low, Math.min(high, value));
const round = (value: number, digits = 1) => Math.round(value * 10 ** digits) / 10 ** digits;

function perturb(hour: ResolvedWeatherHour, leadHour: number, random: () => number): GameForecastHour {
  const day = Math.min(6, Math.floor(leadHour / 24));
  const confidencePct = FORECAST_CONFIDENCE[day];
  const uncertainty = (100 - confidencePct) / 10;
  const signed = () => random() * 2 - 1;
  const temperatureC = round(hour.temperatureC + signed() * uncertainty * 0.8);
  const humidityPct = round(clamp(hour.humidityPct + signed() * uncertainty * 3, 1, 100));
  const wetBulbC = round(wetBulbTemperatureC(temperatureC, humidityPct));
  const precipitationMm = round(Math.max(0, hour.precipitationMm * (1 + signed() * uncertainty * 0.18) +
    (random() < uncertainty * 0.015 ? random() * uncertainty * 0.25 : 0)), 2);
  const precipitationType = precipitationTypeFor(temperatureC, wetBulbC, precipitationMm);
  const sourceRatio = hour.precipitationMm > 0.01 ? hour.snowfallCm / hour.precipitationMm : 1;
  const snowfallCm = precipitationType === 'snow'
    ? round(precipitationMm * clamp(sourceRatio || 1, 0.5, 2.5), 2)
    : precipitationType === 'mixed' ? round(precipitationMm * clamp(sourceRatio || 0.5, 0.2, 1.2), 2) : 0;
  const windSpeedKph = round(Math.max(0, hour.windSpeedKph + signed() * uncertainty * 2));
  const windGustKph = round(Math.max(windSpeedKph, hour.windGustKph + signed() * uncertainty * 2.5));
  const windRadians = hour.windDirectionDeg * Math.PI / 180;
  return {
    ...hour,
    leadHour,
    confidencePct,
    temperatureC,
    humidityPct,
    wetBulbC,
    precipitationMm,
    precipitationType,
    snowfallCm,
    windSpeedKph,
    windGustKph,
    windUms: -windSpeedKph / 3.6 * Math.sin(windRadians),
    windVms: -windSpeedKph / 3.6 * Math.cos(windRadians),
    cloudCoverPct: round(clamp(hour.cloudCoverPct + signed() * uncertainty * 4, 0, 100)),
    visibilityKm: round(Math.max(0.1, hour.visibilityKm + signed() * uncertainty * 0.8)),
    snowWaterEquivalentMm: precipitationType === 'snow' || precipitationType === 'mixed'
      ? round(precipitationMm, 2) : 0,
  };
}

function dominantCondition(hours: readonly GameForecastHour[]): PrecipitationType {
  const totals = new Map<PrecipitationType, number>();
  for (const hour of hours) totals.set(hour.precipitationType,
    (totals.get(hour.precipitationType) ?? 0) + Math.max(0.01, hour.precipitationMm));
  return [...totals.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? 'none';
}

export function issueGameForecast(
  session: WeatherSession,
  issuedAt: string,
  annualRunIdentity: string,
): GameForecastIssue {
  const truth = forecastForSession(session, issuedAt, 168).hours;
  const random = randomFor(`${annualRunIdentity}:forecast:${issuedAt}`);
  const hours = truth.map((hour, index) => perturb(hour, index, random));
  const groups = new Map<string, GameForecastHour[]>();
  for (const hour of hours) {
    const key = localWeatherDateKey(hour.at, session.timezone);
    const group = groups.get(key) ?? [];
    group.push(hour);
    groups.set(key, group);
  }
  const days = [...groups.entries()].slice(0, 7).map(([date, dayHours], day) => ({
    date,
    confidencePct: FORECAST_CONFIDENCE[day],
    condition: dominantCondition(dayHours),
    lowC: Math.min(...dayHours.map((hour) => hour.temperatureC)),
    highC: Math.max(...dayHours.map((hour) => hour.temperatureC)),
    precipitationMm: round(dayHours.reduce((sum, hour) => sum + hour.precipitationMm, 0), 1),
    snowfallCm: round(dayHours.reduce((sum, hour) => sum + hour.snowfallCm, 0), 1),
    windSpeedKph: round(Math.max(...dayHours.map((hour) => hour.windSpeedKph))),
    windGustKph: round(Math.max(...dayHours.map((hour) => hour.windGustKph))),
    hours: dayHours,
  }));
  return {
    schemaVersion: 1,
    issuedAt,
    endsAt: hours.at(-1)?.at ?? issuedAt,
    annualRunIdentity,
    hours,
    days,
  };
}
