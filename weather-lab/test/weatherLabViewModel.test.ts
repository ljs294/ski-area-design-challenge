import { describe, expect, it } from 'vitest';
import {
  HISTORICAL_SIMULATION_TUNING,
  SMOOTHED_SIMULATION_TUNING,
} from '../../weather-engine/src/index.ts';
import type {
  ObservedWeatherHourV1,
  SimulatedWeatherHourV1,
  WeatherDailySummaryV1,
  WeatherEventV1,
} from '../../weather-engine/src/index.ts';
import {
  WEATHER_LAB_TUNING_STORAGE_KEY,
  alignDailyComparison,
  dailyComparisonCsv,
  filterEventsByDate,
  formatDailyMetric,
  hourlyComparisonCsv,
  loadStoredTuning,
  monthDateRange,
  parseTuningJson,
  storeTuning,
  tuningJson,
} from '../src/weatherLabViewModel.ts';

function day(localDate: string, temperatureMean = -2): WeatherDailySummaryV1 {
  return {
    localDate,
    expectedHours: 24,
    availableHours: 24,
    completeness: { temperatureC: 1, wetBulbC: 1, precipitationMm: 1, snowfallCm: 1, condition: 1 },
    temperatureC: { minimum: temperatureMean - 3, mean: temperatureMean, maximum: temperatureMean + 3 },
    wetBulbC: { minimum: temperatureMean - 4, mean: temperatureMean - 1, maximum: temperatureMean + 2 },
    snowmakingHours: 18,
    precipitationMm: 5,
    precipitationByPhaseMm: { none: 0, rain: 0, mixed: 1, snow: 4, 'freezing-rain': 0 },
    snowfallCm: 5,
    snowfallSource: 'simulated',
    conditionHours: { clear: 8, snow: 16 },
    dominantCondition: 'snow',
    hazards: ['icing'],
    macroHours: { arctic: 24 },
    dominantMacro: 'arctic',
    eventIds: ['storm-1'],
  };
}

function event(overrides: Partial<WeatherEventV1> = {}): WeatherEventV1 {
  return {
    version: 1,
    id: 'storm-1',
    type: 'storm',
    startsAt: '2024-01-31T21:00:00Z',
    endsAt: '2024-02-01T06:00:00Z',
    localStartDate: '2024-01-31',
    localEndDate: '2024-02-01',
    durationHours: 9,
    severity: 'notable',
    intensityPercentile: 92,
    totalPrecipitationMm: 12,
    peakPrecipitationMm: 3,
    precipitationByPhaseMm: { none: 0, rain: 0, mixed: 2, snow: 10, 'freezing-rain': 0 },
    snowfallCm: 14,
    temperatureChangeC: -3,
    meanWindSpeedKph: 20,
    peakWindGustKph: 45,
    pressureChangeHpa: -8,
    stormStyle: null,
    styleConfidence: null,
    styleEvidence: [],
    ...overrides,
  };
}

function observedHour(at: string): ObservedWeatherHourV1 {
  return {
    at, localDateTime: at.slice(0, 13), utcOffsetMinutes: 0, fold: 0,
    temperatureC: -2, dewPointC: -3, pressureHpa: 1000, relativeHumidityPct: 90, wetBulbC: -2.5,
    precipitationMm: 1, precipitationPhase: 'snow', snowfallCm: 1.2, windSpeedKph: 12,
    windDirectionDeg: 180, windGustKph: 20, shortwaveRadiationWm2: 0, cloudCoverPct: 100,
    visibilityKm: 4, condition: 'snow', hazards: [], quality: { temperatureC: 'accepted' },
  };
}

function simulatedHour(at: string, temperatureC: number): SimulatedWeatherHourV1 {
  return {
    at, localDateTime: at.slice(0, 13), utcOffsetMinutes: 0, fold: 0, macroAirMass: 'arctic',
    condition: 'snow', hazards: [], temperatureC, dewPointC: -3, pressureHpa: 1000,
    relativeHumidityPct: 90, wetBulbC: -2.5, precipitationMm: 1, precipitationPhase: 'snow',
    snowfallCm: 1.2, windSpeedKph: 12, windDirectionDeg: 180, windGustKph: 20,
    shortwaveRadiationWm2: 0, cloudCoverPct: 100, visibilityKm: 4,
  };
}

describe('Weather Lab view model', () => {
  it('aligns sparse daily series by local date and filters by month', () => {
    const rows = alignDailyComparison({
      observed: [day('2024-01-31')],
      baseline: [day('2024-02-01')],
      candidate: [day('2024-01-31'), day('2024-02-01')],
    }, 2);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ localDate: '2024-02-01', observed: null });
    expect(rows[0].baseline?.localDate).toBe('2024-02-01');
  });

  it('formats precipitation partitions and condition occupancy', () => {
    expect(formatDailyMetric(day('2024-01-01'), 'precipitation')).toContain('Snow 4.0 mm');
    expect(formatDailyMetric(day('2024-01-01'), 'conditions')).toBe('Snow · Clear 8.0 h · Snow 16.0 h');
  });

  it('validates and round-trips versioned tuning JSON', () => {
    expect(parseTuningJson(tuningJson(SMOOTHED_SIMULATION_TUNING))).toEqual(SMOOTHED_SIMULATION_TUNING);
    expect(() => parseTuningJson('{"version":2}')).toThrow('version 1');
    expect(() => parseTuningJson(JSON.stringify({ ...SMOOTHED_SIMULATION_TUNING, temperatureResponse: 2 }))).toThrow('between');
    expect(() => parseTuningJson(JSON.stringify({ ...SMOOTHED_SIMULATION_TUNING, hourlyNormalSmoothingRadius: 1.5 }))).toThrow('whole number');
  });

  it('stores exactly one validated tuning draft', () => {
    const values = new Map<string, string>();
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => { values.set(key, value); },
    };
    storeTuning(storage, HISTORICAL_SIMULATION_TUNING);
    expect([...values.keys()]).toEqual([WEATHER_LAB_TUNING_STORAGE_KEY]);
    expect(loadStoredTuning(storage)).toEqual(HISTORICAL_SIMULATION_TUNING);
  });

  it('includes events that intersect rather than merely start in a date range', () => {
    expect(filterEventsByDate([event()], '2024-02-01', '2024-02-29')).toHaveLength(1);
    expect(filterEventsByDate([event()], '2024-03-01', '2024-03-31')).toHaveLength(0);
    expect(monthDateRange(2024, 2)).toEqual({ startDate: '2024-02-01', endDate: '2024-02-29' });
  });

  it('exports aligned, quoted daily and expanded hourly CSV', () => {
    const quoted = { ...day('2024-01-01'), eventIds: ['storm,"quoted"'] };
    const dailyCsv = dailyComparisonCsv({ observed: [quoted], baseline: [], candidate: [day('2024-01-01')] });
    expect(dailyCsv.split('\n')[0]).toContain('candidate.dominantCondition');
    expect(dailyCsv.split('\n')[0]).toContain('observed.completeness.temperatureC');
    expect(dailyCsv).toContain('"storm,""quoted"""');

    const at = '2024-01-01T00:00:00.000Z';
    const hourlyCsv = hourlyComparisonCsv({ observed: [observedHour(at)], baseline: [], candidate: [simulatedHour(at, -1)] });
    expect(hourlyCsv.split('\n')).toHaveLength(3);
    expect(hourlyCsv.split('\n')[0]).toContain('observed.utcOffsetMinutes');
    expect(hourlyCsv.split('\n')[0]).toContain('observed.fold');
    expect(hourlyCsv.split('\n')[0]).toContain('observed.quality');
    expect(hourlyCsv).toContain('candidate.macroAirMass');
    expect(hourlyCsv).toContain('arctic');
    expect(hourlyCsv).toContain('accepted');
  });
});
