import { describe, expect, it } from 'vitest';
import type { WeatherDataPackage, WeatherReferenceHour } from './weatherModel';
import { createWeatherSession } from './weatherSession';
import { FORECAST_CONFIDENCE, issueGameForecast } from './gameForecast';

function sourceHour(at: string, hour: number): WeatherReferenceHour {
  return {
    at, temperatureC: -5 + hour / 12, wetBulbC: -6 + hour / 12, humidityPct: 80,
    precipitationMm: hour % 5 === 0 ? 1 : 0, precipitationType: hour % 5 === 0 ? 'snow' : 'none',
    snowfallCm: hour % 5 === 0 ? 1.2 : 0, windSpeedKph: 12, windGustKph: 20,
    windDirectionDeg: 270, cloudCoverPct: 45, visibilityKm: 20, pressureHpa: 900,
    radiationWm2: Math.max(0, 400 - Math.abs(12 - hour) * 50),
  };
}

function session() {
  const hours = Array.from({ length: 24 }, (_, hour) => sourceHour(
    `2025-01-01T${hour.toString().padStart(2, '0')}:00:00.000Z`, hour));
  const weatherPackage = {
    manifest: {
      schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', complete: true,
      contentHash: 'hash', generatorVersion: 2, timezone: 'UTC', sourceVersion: 'fixture-v1',
      historicalStartYear: 2025, historicalEndYear: 2025, quality: 'verified', sourceSummary: 'test',
      createdAt: '2025-01-01T00:00:00.000Z',
    },
    historicalYears: [{ year: 2025, hours }],
  } as WeatherDataPackage;
  return createWeatherSession(weatherPackage, {
    seed: 'truth', startsAt: '2026-01-01T00:00:00.000Z', days: 9, timezone: 'UTC',
  });
}

describe('game forecast', () => {
  it('is deterministic, covers 168 hours, and exposes the confidence curve', () => {
    const weather = session();
    const first = issueGameForecast(weather, weather.plan.startsAt, 'annual-run');
    const second = issueGameForecast(weather, weather.plan.startsAt, 'annual-run');
    expect(first).toEqual(second);
    expect(first.hours).toHaveLength(168);
    expect(first.days).toHaveLength(7);
    expect(first.days.map((day) => day.confidencePct)).toEqual([...FORECAST_CONFIDENCE]);
  });

  it('recalculates phase and gust consistency after perturbation without changing truth', () => {
    const weather = session();
    const truth = JSON.stringify(weather.plan.hours);
    const issue = issueGameForecast(weather, weather.plan.startsAt, 'annual-run');
    expect(issue.hours.every((hour) => hour.windGustKph >= hour.windSpeedKph)).toBe(true);
    expect(issue.hours.every((hour) => hour.precipitationMm > 0 || hour.precipitationType === 'none')).toBe(true);
    expect(JSON.stringify(weather.plan.hours)).toBe(truth);
  });
});
