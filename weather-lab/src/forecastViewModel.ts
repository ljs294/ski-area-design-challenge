import type {
  ForecastHourV1,
  ForecastIssueV1,
  PrecipitationPhase,
} from '../../weather-engine/src/contracts.ts';

export type ForecastUnits = 'us' | 'metric';
export type ForecastMetric = 'temperature' | 'wetBulb' | 'precipitation' | 'snowfall' | 'wind' | 'cloud' | 'humidity';

export interface ForecastMetricHour {
  at: string;
  temperatureC: number | null;
  wetBulbC?: number | null;
  precipitationMm: number | null;
  precipitationPhase?: PrecipitationPhase | null;
  snowfallCm?: number | null;
  windSpeedKph: number | null;
  windGustKph?: number | null;
  cloudCoverPct?: number | null;
  relativeHumidityPct?: number | null;
}

const dateFormatters = new Map<string, Intl.DateTimeFormat>();
const hourFormatters = new Map<string, Intl.DateTimeFormat>();

function dateFormatter(timezone: string) {
  let formatter = dateFormatters.get(timezone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-CA', {
      timeZone: timezone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    });
    dateFormatters.set(timezone, formatter);
  }
  return formatter;
}

export function forecastLocalDate(at: string, timezone: string): string {
  const parts = dateFormatter(timezone).formatToParts(new Date(at));
  const value = (type: Intl.DateTimeFormatPartTypes) => parts.find((part) => part.type === type)?.value ?? '';
  return `${value('year')}-${value('month')}-${value('day')}`;
}

export function forecastLocalHour(at: string, timezone: string): string {
  let formatter = hourFormatters.get(timezone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat([], {
      timeZone: timezone,
      hour: 'numeric',
      minute: '2-digit',
      hour12: true,
    });
    hourFormatters.set(timezone, formatter);
  }
  return formatter.format(new Date(at));
}

export function fiveForecastDays(issue: ForecastIssueV1, timezone: string): readonly (readonly ForecastHourV1[])[] {
  const groups = new Map<string, ForecastHourV1[]>();
  for (const hour of issue.hourly) {
    const date = forecastLocalDate(hour.at, timezone);
    const group = groups.get(date) ?? [];
    group.push(hour);
    groups.set(date, group);
  }
  return [...groups.values()].slice(0, 5);
}

export function displayValue(value: number, metric: ForecastMetric, units: ForecastUnits): number {
  if (units === 'metric') return value;
  if (metric === 'temperature' || metric === 'wetBulb') return value * 9 / 5 + 32;
  if (metric === 'precipitation') return value / 25.4;
  if (metric === 'snowfall') return value / 2.54;
  if (metric === 'wind') return value * 0.621371;
  return value;
}

export function metricUnit(metric: ForecastMetric, units: ForecastUnits): string {
  if (metric === 'temperature' || metric === 'wetBulb') return units === 'us' ? '°F' : '°C';
  if (metric === 'precipitation') return units === 'us' ? 'in' : 'mm';
  if (metric === 'snowfall') return units === 'us' ? 'in' : 'cm';
  if (metric === 'wind') return units === 'us' ? 'mph' : 'km/h';
  return '%';
}

export function metricLabel(metric: ForecastMetric, units: ForecastUnits): string {
  const names: Record<ForecastMetric, string> = {
    temperature: 'Temperature',
    wetBulb: 'Wet bulb',
    precipitation: 'Precipitation',
    snowfall: 'Snowfall',
    wind: 'Wind / gust',
    cloud: 'Cloud cover',
    humidity: 'Humidity',
  };
  return `${names[metric]} (${metricUnit(metric, units)})`;
}

export function metricValue(hour: ForecastMetricHour | ForecastHourV1, metric: ForecastMetric): number | null {
  if (metric === 'temperature') return hour.temperatureC;
  if (metric === 'wetBulb') return hour.wetBulbC ?? null;
  if (metric === 'precipitation') return hour.precipitationMm;
  if (metric === 'snowfall') return hour.snowfallCm ?? null;
  if (metric === 'wind') return hour.windSpeedKph;
  if (metric === 'cloud') return hour.cloudCoverPct ?? null;
  return hour.relativeHumidityPct ?? null;
}

export function precipitationColor(phase: PrecipitationPhase | null | undefined): string {
  if (phase === 'snow') return '#8ed8ff';
  if (phase === 'mixed') return '#b995e8';
  if (phase === 'freezing-rain') return '#e08bc7';
  return '#2e9ee8';
}
