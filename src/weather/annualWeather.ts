import type { WeatherDataPackage } from './weatherModel';
import { addWeatherLocalTime, weatherInstantForLocal, weatherLocalParts } from './localTime';
import { loadWeatherSession, type WeatherSession } from './weatherSession';

export const WEATHER_YEAR_GENERATOR_VERSION = 2 as const;
export const WEATHER_YEAR_CONFIGURATION_VERSION = 2 as const;

export function weatherYearLabel(at: string, timezone: string): number {
  const local = weatherLocalParts(at, timezone);
  return local.month >= 9 ? local.year : local.year - 1;
}

export function weatherYearStart(year: number, timezone: string): string {
  return weatherInstantForLocal({ year, month: 9, day: 1, hour: 0, minute: 0, second: 0 }, timezone);
}

export function weatherYearDayCount(year: number): number {
  return Math.round((Date.UTC(year + 1, 8, 1) - Date.UTC(year, 8, 1)) / 86_400_000);
}

export function annualWeatherSeed(baseSeed: string, year: number): string {
  return `${baseSeed}:weather-year:${year}`;
}

export async function loadAnnualWeatherSession(
  weatherPackage: WeatherDataPackage,
  baseSeed: string,
  year: number,
): Promise<WeatherSession> {
  return loadWeatherSession(weatherPackage, {
    seed: annualWeatherSeed(baseSeed, year),
    startsAt: weatherYearStart(year, weatherPackage.manifest.timezone),
    days: weatherYearDayCount(year),
    timezone: weatherPackage.manifest.timezone,
    latitude: weatherPackage.manifest.midpoint?.latitude,
    longitude: weatherPackage.manifest.midpoint?.longitude,
  });
}

/** Most recent 05:00 or 17:00 issue in resort-local time. */
export function forecastIssueAt(at: string, timezone: string): string {
  const local = weatherLocalParts(at, timezone);
  if (local.hour >= 17) {
    return weatherInstantForLocal({ ...local, hour: 17, minute: 0, second: 0 }, timezone);
  }
  if (local.hour >= 5) {
    return weatherInstantForLocal({ ...local, hour: 5, minute: 0, second: 0 }, timezone);
  }
  const previous = addWeatherLocalTime(
    weatherInstantForLocal({ ...local, hour: 17, minute: 0, second: 0 }, timezone),
    timezone,
    { days: -1 },
  );
  return previous;
}
