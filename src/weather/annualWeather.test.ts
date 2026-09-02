import { describe, expect, it } from 'vitest';
import type { HistoricalWeatherYear, WeatherReferenceHour } from './weatherModel';
import { forecastIssueAt, historicalAverageAnnualSnowfallCm, weatherYearDayCount,
  weatherYearLabel, weatherYearStart } from './annualWeather';

function snowHour(at: string, snowfallCm: number): WeatherReferenceHour {
  return { at, snowfallCm, temperatureC: 0, wetBulbC: 0, humidityPct: 80,
    precipitationMm: 0, precipitationType: 'none', windSpeedKph: 0, windGustKph: 0,
    windDirectionDeg: 0, cloudCoverPct: 0, visibilityKm: 20, pressureHpa: 1000,
    radiationWm2: 0 };
}

describe('annual weather calendar', () => {
  it('uses a September 1 through August 31 weather year', () => {
    expect(weatherYearLabel('2026-08-31T23:00:00.000Z', 'UTC')).toBe(2025);
    expect(weatherYearLabel('2026-09-01T00:00:00.000Z', 'UTC')).toBe(2026);
    expect(weatherYearStart(2026, 'America/Los_Angeles')).toBe('2026-09-01T07:00:00.000Z');
    expect(weatherYearDayCount(2023)).toBe(366);
    expect(weatherYearDayCount(2024)).toBe(365);
  });

  it('selects the latest twice-daily local forecast issue', () => {
    expect(forecastIssueAt('2026-01-02T04:00:00.000Z', 'UTC')).toBe('2026-01-01T17:00:00.000Z');
    expect(forecastIssueAt('2026-01-02T05:00:00.000Z', 'UTC')).toBe('2026-01-02T05:00:00.000Z');
    expect(forecastIssueAt('2026-01-02T17:01:00.000Z', 'UTC')).toBe('2026-01-02T17:00:00.000Z');
  });

  it('averages complete September-August snowfall seasons from historical years', () => {
    const years: HistoricalWeatherYear[] = [
      { year: 2020, hours: [snowHour('2020-01-01T00:00:00.000Z', 100),
        snowHour('2020-09-01T00:00:00.000Z', 10)] },
      { year: 2021, hours: [snowHour('2021-08-31T23:00:00.000Z', 20),
        snowHour('2021-09-01T00:00:00.000Z', 30)] },
      { year: 2022, hours: [snowHour('2022-08-31T23:00:00.000Z', 50),
        snowHour('2022-09-01T00:00:00.000Z', 200)] },
    ];
    expect(historicalAverageAnnualSnowfallCm(years, 'UTC')).toBe(55);
  });

  it('requires two adjacent calendar years and ignores invalid negative snowfall', () => {
    expect(historicalAverageAnnualSnowfallCm([
      { year: 2020, hours: [snowHour('2020-09-01T00:00:00.000Z', 10)] },
    ], 'UTC')).toBeNull();
    expect(historicalAverageAnnualSnowfallCm([
      { year: 2020, hours: [snowHour('2020-09-01T00:00:00.000Z', -5)] },
      { year: 2021, hours: [snowHour('2021-08-31T23:00:00.000Z', Number.NaN)] },
    ], 'UTC')).toBe(0);
  });
});
