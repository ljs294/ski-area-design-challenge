import type { PrecipitationPhase } from '../../weather-engine/src/contracts.ts';

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
  windDirectionDeg?: number | null;
  windGustKph?: number | null;
  cloudCoverPct?: number | null;
  relativeHumidityPct?: number | null;
}

const COMPASS_POINTS = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'] as const;

export function normalizeWindDirection(directionDeg: number): number {
  return ((directionDeg % 360) + 360) % 360;
}

export function formatWindDirection(directionDeg: number | null | undefined): string {
  if (directionDeg == null || !Number.isFinite(directionDeg)) return 'direction unavailable';
  const normalized = normalizeWindDirection(directionDeg);
  const compass = COMPASS_POINTS[Math.round(normalized / 22.5) % COMPASS_POINTS.length];
  return `${compass} (${Math.round(normalized)}°)`;
}

export function circularMeanWindDirection(directions: readonly (number | null | undefined)[]): number | null {
  const available = directions.filter((value): value is number => value != null && Number.isFinite(value));
  if (!available.length) return null;
  const vectors = available.reduce((sum, direction) => {
    const radians = normalizeWindDirection(direction) * Math.PI / 180;
    return { x: sum.x + Math.cos(radians), y: sum.y + Math.sin(radians) };
  }, { x: 0, y: 0 });
  if (Math.abs(vectors.x) < 1e-10 && Math.abs(vectors.y) < 1e-10) return null;
  return normalizeWindDirection(Math.atan2(vectors.y, vectors.x) * 180 / Math.PI);
}

const hourFormatters = new Map<string, Intl.DateTimeFormat>();

export function localHourLabel(at: string, timezone: string): string {
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

export function metricValue(hour: ForecastMetricHour, metric: ForecastMetric): number | null {
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
