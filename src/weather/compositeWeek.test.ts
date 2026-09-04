import { describe, expect, it } from 'vitest';
import type { ResolvedWeatherHour } from './weatherModel';
import {
  COMPOSITE_WEEK_HOURS,
  COMPOSITE_WEEK_SIMULATION_SECONDS,
  calculateCompositeWeekOutlook,
  compositeWeekPhysicsDueSecond,
  createCompositeWeekWeather,
  scheduleCompositeWeekPhysics,
  selectCompositeWeekWitnessDay,
} from './compositeWeek';

function hour(index: number, overrides: Partial<ResolvedWeatherHour> = {}): ResolvedWeatherHour {
  const at = new Date(Date.UTC(2026, 0, 4, index)).toISOString();
  return {
    at,
    temperatureC: -4,
    wetBulbC: -5,
    humidityPct: 82,
    precipitationMm: 0,
    precipitationType: 'none',
    snowfallCm: 0,
    windSpeedKph: 18,
    windGustKph: 30,
    windDirectionDeg: 230,
    cloudCoverPct: 30,
    visibilityKm: 20,
    pressureHpa: 1010,
    radiationWm2: 250,
    windUms: 0,
    windVms: 0,
    snowWaterEquivalentMm: 0,
    globalRadiationWm2: 250,
    directRadiationWm2: 100,
    diffuseRadiationWm2: 150,
    cloudTransmissionPct: 75,
    solarElevationDeg: 0,
    solarAzimuthDeg: 0,
    provenance: { fieldFlags: 0 },
    ...overrides,
  };
}

function week(overrides: (index: number) => Partial<ResolvedWeatherHour> = () => ({})): ResolvedWeatherHour[] {
  return Array.from({ length: COMPOSITE_WEEK_HOURS }, (_, index) => hour(index, overrides(index)));
}

describe('composite winter week weather', () => {
  it('rejects packets that do not contain exactly 168 hourly records', () => {
    expect(() => createCompositeWeekWeather(week().slice(0, -1))).toThrow(/exactly 168/);
  });

  it('selects an unmodified operating day and never blends records', () => {
    const source = week();
    const result = createCompositeWeekWeather(source);
    // All fields are constant, so day 0 is the earliest exact medoid tie.
    expect(result.witness.dayIndex).toBe(0);
    expect(result.witness.day).toHaveLength(24);
    expect(result.witness.operatingHours).toHaveLength(12);
    expect(result.witness.operatingHours[0]).toBe(source[8]);
    expect(result.witness.operatingHours[11]).toBe(source[19]);
    expect(result.witness.day.every((record, index) => record === source[index])).toBe(true);
  });

  it('uses the lowest robust-medoid score and earliest index on exact ties', () => {
    const source = week((index) => {
      const day = Math.floor(index / 24);
      // Day 3 is the unique representative of the monotonic weekly profile.
      return { temperatureC: day, wetBulbC: day - 1 };
    });
    expect(selectCompositeWeekWitnessDay(source).dayIndex).toBe(3);

    const tied = week();
    expect(selectCompositeWeekWitnessDay(tied).dayIndex).toBe(0);
  });

  it('scores hourly profiles against medians at the same clock hour', () => {
    const source = week((index) => {
      const day = Math.floor(index / 24);
      const hourOfDay = index % 24;
      const temperatureC = day === 0 ? 0
        : day === 1 ? 10
          : day <= 4 ? (hourOfDay < 12 ? 0 : 10)
            : (hourOfDay < 12 ? 10 : 0);
      return { temperatureC, wetBulbC: temperatureC - 1 };
    });
    // The first twelve hours and second twelve hours have different typical
    // profiles. A global (all-168) median would tie and choose day 0;
    // per-clock-hour medians correctly identify day 2 as the first match.
    expect(selectCompositeWeekWitnessDay(source).dayIndex).toBe(2);
  });

  it('schedules each source hour once with the exact ceil due-second formula', () => {
    const source = week();
    const schedule = scheduleCompositeWeekPhysics(source);
    expect(schedule).toHaveLength(168);
    expect(schedule[0]?.dueSecond).toBe(Math.ceil(43_200 / 168));
    expect(schedule[167]?.dueSecond).toBe(COMPOSITE_WEEK_SIMULATION_SECONDS);
    expect(new Set(schedule.map((step) => step.sourceHourIndex)).size).toBe(168);
    expect(new Set(schedule.map((step) => step.dueSecond)).size).toBe(168);
    expect(schedule.every((step) => step.hour === source[step.sourceHourIndex])).toBe(true);
    expect(compositeWeekPhysicsDueSecond(42)).toBe(Math.ceil(43 * 43_200 / 168));
  });

  it('reports weekly outlook totals without averaging source conditions', () => {
    const source = week((index) => {
      const day = Math.floor(index / 24);
      const hourOfDay = index % 24;
      const freezing = day === 0 ? -4 : day === 1 ? 2 : -1;
      return {
        temperatureC: day === 0 && hourOfDay === 12 ? 4 : freezing,
        wetBulbC: day === 0 ? -3 : 0,
        precipitationMm: day === 2 ? 2 : 0,
        precipitationType: day === 2 ? 'rain' : 'none',
        snowfallCm: day === 3 ? 1.5 : 0,
        windSpeedKph: day === 4 ? 42 : 10,
        windGustKph: day === 4 ? 55 : 20,
      };
    });
    expect(calculateCompositeWeekOutlook(source)).toMatchObject({
      temperatureRangeC: { minimum: -4, maximum: 4 },
      snowfallCm: 36,
      rainMm: 48,
      maxWindKph: 42,
      maxWindGustKph: 55,
      snowmakingEligibleHours: 24,
      freezeThawTransitions: 4,
      totalPrecipitationMm: 48,
    });
  });

  it('rejects non-monotonic timestamps rather than silently reordering weather', () => {
    const source = week();
    source[24] = { ...source[24], at: source[23].at };
    expect(() => scheduleCompositeWeekPhysics(source)).toThrow(/strictly increasing/);
  });

  it('keeps all 168 source hours across a DST cardinality change', () => {
    const source = week();
    expect(selectCompositeWeekWitnessDay(source, { timezone: 'UTC' }).operatingHours).toHaveLength(12);
    const dst = Array.from({ length: 168 }, (_, index) => ({
      ...source[index],
      at: new Date(Date.UTC(2026, 2, 7, 5 + index)).toISOString(),
    }));
    const result = createCompositeWeekWeather(dst, { timezone: 'America/New_York' });
    expect(result.physics).toHaveLength(168);
    expect(new Set(result.physics.map((step) => step.sourceHourIndex)).size).toBe(168);
    expect(result.witness.operatingHours).toHaveLength(12);
  });
});
