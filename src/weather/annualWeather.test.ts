import { describe, expect, it } from 'vitest';
import { forecastIssueAt, weatherYearDayCount, weatherYearLabel, weatherYearStart } from './annualWeather';

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
});
