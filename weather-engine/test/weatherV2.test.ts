import { describe, expect, it } from 'vitest';
import { advanceWeatherHour, advanceWeatherTo, createJacksonClimateModel, createJacksonRun, createWeatherSimulation,
  generateForecastIssues, generateWeatherYear, hashWithout, restoreWeatherSnapshot, sha256Hex, weatherCalendarYear } from '../src/index.ts';

describe('standalone weather v2', () => {
  it('uses canonical SHA-256 and Unicode-normalized run seeds', () => {
    expect(sha256Hex('abc')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    const model = createJacksonClimateModel();
    expect(createWeatherSimulation(createJacksonRun('  Cafe\u0301 '), model).snapshot.runIdentityHash)
      .toBe(createWeatherSimulation(createJacksonRun('Café'), model).snapshot.runIdentityHash);
    expect(model.climateModelHash).toBe(hashWithout(model, ['climateModelHash']));
  });

  it('constructs exact standard/leap local calendar years with DST fold metadata', () => {
    const standard = weatherCalendarYear(2019, 'America/New_York');
    const leap = weatherCalendarYear(2020, 'America/New_York');
    expect(standard).toHaveLength(8760); expect(leap).toHaveLength(8784);
    expect(standard.some((hour) => hour.fold === 1)).toBe(true);
    expect(new Set(standard.map((hour) => hour.at)).size).toBe(standard.length);
  });

  it('is deterministic, seed-sensitive, physically coherent, and hourly/batch equivalent', () => {
    const model = createJacksonClimateModel(); const run = createJacksonRun('Historical');
    const one = advanceWeatherTo(createWeatherSimulation(run, model), 336);
    let incremental = createWeatherSimulation(run, model); const hours = [];
    for (let index = 0; index < 336; index += 1) { const next = advanceWeatherHour(incremental); incremental = next.simulation; hours.push(next.hour); }
    expect(hours).toEqual(one.hours); expect(incremental.snapshot).toEqual(one.simulation.snapshot);
    expect(one.hours.every((hour) => hour.dewPointC <= hour.temperatureC && hour.relativeHumidityPct >= 0 && hour.relativeHumidityPct <= 100)).toBe(true);
    expect(advanceWeatherTo(createWeatherSimulation(createJacksonRun('Different'), model), 24).hours).not.toEqual(one.hours.slice(0, 24));
    expect(restoreWeatherSnapshot(run, model, one.simulation.snapshot).snapshot).toEqual(one.simulation.snapshot);
    expect(() => restoreWeatherSnapshot(run, model, { ...one.simulation.snapshot, runIdentityHash: 'bad' })).toThrow(/identity/);
  });

  it('generates a complete year and advances a separate forecast-error stream', () => {
    const model = createJacksonClimateModel(); const run = createJacksonRun(); const year = generateWeatherYear(run, model);
    expect(year.hours).toHaveLength(8760);
    const forecasts = generateForecastIssues(run, year.hours);
    expect(forecasts.issues.length).toBeGreaterThan(700);
    expect(forecasts.issues[0].hourly).toHaveLength(168);
    expect(forecasts.issues[0].daily).toHaveLength(14);
    expect(forecasts.issues[0]).not.toHaveProperty('truth');
    expect(forecasts.finalState.draws).toBeGreaterThan(0);
  }, 20_000);
});
