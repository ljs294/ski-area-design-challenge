import { describe, expect, it } from 'vitest';
import type { ForecastHourV1, ForecastIssueV1 } from '../../weather-engine/src/contracts.ts';
import {
  displayValue,
  fiveForecastDays,
  forecastLocalDate,
  forecastLocalHour,
  metricUnit,
  precipitationColor,
} from '../src/forecastViewModel.ts';

function forecastHour(at: string): ForecastHourV1 {
  return {
    at,
    leadHours: 0,
    confidencePct: 90,
    temperatureC: 0,
    wetBulbC: -1,
    precipitationMm: 1,
    precipitationPhase: 'snow',
    snowfallCm: 1,
    windSpeedKph: 16.09344,
    windGustKph: 24,
    relativeHumidityPct: 85,
    cloudCoverPct: 75,
    condition: 'snow',
  };
}

describe('forecast view model', () => {
  it('buckets forecast hours by local date across a DST transition', () => {
    const start = Date.parse('2024-03-09T11:00:00.000Z');
    const hourly = Array.from({ length: 120 }, (_, index) => forecastHour(new Date(start + index * 3_600_000).toISOString()));
    const issue: ForecastIssueV1 = { version: 1, issuedAt: hourly[0].at, hourly, daily: [], signals: [] };
    const days = fiveForecastDays(issue, 'America/New_York');
    expect(days).toHaveLength(5);
    expect(days[0]).toHaveLength(18);
    expect(days[1]).toHaveLength(23);
    expect(forecastLocalDate(days[1][0].at, 'America/New_York')).toBe('2024-03-10');
    expect(forecastLocalHour(issue.issuedAt, 'America/New_York')).toMatch(/6:00\s*AM/i);
  });

  it('converts only display values and retains distinct precipitation phase colors', () => {
    expect(displayValue(0, 'temperature', 'us')).toBe(32);
    expect(displayValue(25.4, 'precipitation', 'us')).toBeCloseTo(1);
    expect(displayValue(2.54, 'snowfall', 'us')).toBeCloseTo(1);
    expect(displayValue(16.09344, 'wind', 'us')).toBeCloseTo(10, 4);
    expect(displayValue(25.4, 'precipitation', 'metric')).toBe(25.4);
    expect(metricUnit('temperature', 'us')).toBe('°F');
    expect(new Set(['rain', 'snow', 'mixed', 'freezing-rain'].map((phase) => precipitationColor(phase as never))).size).toBe(4);
  });
});
