import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  JACKSON_NH_TEST_LOCATION,
  type ResortClimateBaseline,
} from '../src/climateBaseline.ts';
import { parseDaymetCsv } from '../src/climateProviders.ts';
import {
  NORMAL_WEATHER_TUNING,
  advanceWeather,
  createForecast,
  createWeatherSnapshot,
  createWeatherState,
  generateWeatherSeason,
  restoreWeatherSnapshot,
} from '../src/weatherEngine.ts';

const baseline = JSON.parse(readFileSync(
  new URL('../fixtures/jackson-nh-2010-2019.json', import.meta.url),
  'utf8',
)) as ResortClimateBaseline;
const START = '2026-11-02T05:00:00.000Z';

describe('Jackson climate fixture', () => {
  it('is pinned to the exact default location and 2010-2019 Daymet period', () => {
    expect(baseline.location).toEqual(JACKSON_NH_TEST_LOCATION);
    expect(baseline.source).toBe('daymet');
    expect(baseline.sourcePeriod).toEqual({ startYear: 2010, endYear: 2019 });
    expect(baseline.bins).toHaveLength(52);
    expect(baseline.sourceElevationM).toBe(427);
  });

  it('parses live Daymet-shaped CSV into the raw schema', () => {
    const parsed = parseDaymetCsv([
      'Daymet Software Version: 4.0',
      'Elevation: 427 meters',
      'year,yday,dayl (s),prcp (mm/day),swe (kg/m^2),tmax (deg c),tmin (deg c),vp (Pa)',
      '2010,1,32000,2.5,3.2,-3.1,-12.4,500',
    ].join('\n'));
    expect(parsed.elevationM).toBe(427);
    expect(parsed.days[0]).toEqual({
      year: 2010,
      dayOfYear: 1,
      dayLengthSeconds: 32_000,
      precipitationMm: 2.5,
      snowWaterEquivalentKgM2: 3.2,
      maxTempC: -3.1,
      minTempC: -12.4,
      vaporPressurePa: 500,
    });
  });
});

describe('weather season generation', () => {
  it('is deterministic for an identical seed', () => {
    const a = generateWeatherSeason(baseline, START, 24, 'repeatable');
    const b = generateWeatherSeason(baseline, START, 24, 'repeatable');
    expect(a).toEqual(b);
  });

  it('generates exactly 24 coherent mountain hours on every winter day', () => {
    const { plan } = generateWeatherSeason(baseline, START, 24, 'hourly-shape');
    expect(plan.days).toHaveLength(168);
    expect(plan.days.every((day) => day.hours.length === 24)).toBe(true);
    for (const hour of plan.days.flatMap((day) => day.hours)) {
      expect(hour.base.temperatureC).toBeGreaterThanOrEqual(hour.mid.temperatureC);
      expect(hour.mid.temperatureC).toBeGreaterThanOrEqual(hour.summit.temperatureC);
      expect(hour.base.precipitationMm > 0).toBe(hour.summit.precipitationMm > 0);
    }
  });

  it('uses unamplified natural event tuning by default', () => {
    expect(NORMAL_WEATHER_TUNING).toEqual({
      eventRateMultiplier: 1,
      majorEventMultiplier: 1,
      quietWeatherBoost: 0,
      minimumStormCooldownDays: 0,
    });
  });

  it('reconciles hourly precipitation and preserves elevation orographic ordering', () => {
    const { plan } = generateWeatherSeason(baseline, START, 2, 'precipitation');
    for (const day of plan.days) {
      const base = day.hours.reduce((sum, hour) => sum + hour.base.precipitationMm, 0);
      const mid = day.hours.reduce((sum, hour) => sum + hour.mid.precipitationMm, 0);
      const summit = day.hours.reduce((sum, hour) => sum + hour.summit.precipitationMm, 0);
      expect(base).toBeLessThanOrEqual(mid + 0.01);
      expect(mid).toBeLessThanOrEqual(summit + 0.01);
    }
  });

  it('only emits freeze/thaw events that satisfy heat, moisture, and refreeze prerequisites', () => {
    const { plan } = generateWeatherSeason(baseline, START, 24, 'freeze-thaw-audit');
    const hours = plan.days.flatMap((day) => day.hours);
    for (const event of plan.events.filter((candidate) => candidate.type === 'freeze-thaw')) {
      const band = event.bands[0];
      const eventStartIndex = hours.findIndex((hour) => hour.at === event.startsAt);
      const selected = hours.filter((hour) =>
        new Date(hour.at) >= new Date(event.startsAt) && new Date(hour.at) < new Date(event.endsAt));
      const moistureWindow = hours.slice(Math.max(0, eventStartIndex - 48), eventStartIndex + 6);
      expect(moistureWindow.some((hour) =>
        hour[band].precipitationMm > 0 || hour[band].snowfallCm > 0)).toBe(true);
      expect(selected.some((_, index) =>
        selected.slice(index, index + 6).length === 6
        && selected.slice(index, index + 6).every((hour) => hour[band].temperatureC > 1))).toBe(true);
      expect(selected.some((_, index) =>
        selected.slice(index, index + 6).length === 6
        && selected.slice(index, index + 6).every((hour) => hour[band].temperatureC < -2))).toBe(true);
    }
  });
});

describe('forecast, advancement, and persistence', () => {
  it('makes near-term forecasts more confident than long-range forecasts', () => {
    const { plan, randomStreams } = generateWeatherSeason(baseline, START, 24, 'forecast');
    const forecast = createForecast(plan, START, randomStreams.forecastError);
    expect(forecast.days).toHaveLength(21);
    expect(forecast.days[0].hours).toHaveLength(24);
    expect(forecast.days[6].hours).toHaveLength(24);
    expect(forecast.days[7].hours).toBeUndefined();
    expect(forecast.days[0].confidencePct).toBeGreaterThan(forecast.days[14].confidencePct);
  });

  it('issues forecasts at 6 AM and 6 PM across a skip', () => {
    const state = createWeatherState(JACKSON_NH_TEST_LOCATION, baseline, START, 24, 'issues');
    const result = advanceWeather(state, '2026-11-03T19:00:00.000Z');
    expect(result.forecastIssues.map((issue) => issue.issuedAt)).toEqual([
      '2026-11-02T06:00:00.000Z',
      '2026-11-02T18:00:00.000Z',
      '2026-11-03T06:00:00.000Z',
      '2026-11-03T18:00:00.000Z',
    ]);
  });

  it('produces the same state through a batched or hourly advancement', () => {
    const initial = createWeatherState(JACKSON_NH_TEST_LOCATION, baseline, START, 24, 'skip-equivalence');
    const target = new Date('2026-11-09T05:00:00.000Z');
    const batch = advanceWeather(initial, target.toISOString()).state;
    let stepped = initial;
    for (
      let at = new Date(new Date(START).getTime() + 3_600_000);
      at <= target;
      at = new Date(at.getTime() + 3_600_000)
    ) {
      stepped = advanceWeather(stepped, at.toISOString()).state;
    }
    expect(stepped).toEqual(batch);
  });

  it('round-trips hidden truth, RNG streams, forecast, and selected band', () => {
    const state = createWeatherState(JACKSON_NH_TEST_LOCATION, baseline, START, 24, 'snapshot');
    const snapshot = createWeatherSnapshot({ ...state, selectedBand: 'summit' });
    expect(restoreWeatherSnapshot(snapshot)).toEqual(snapshot);
    expect(() => restoreWeatherSnapshot({ ...snapshot, schemaVersion: 99 })).toThrow();
  });
});
