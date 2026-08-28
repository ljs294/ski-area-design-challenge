import { describe, expect, it } from 'vitest';
import type {
  HistoricalWeatherSeriesV1, ObservedWeatherHourV1, SimulatedWeatherHourV1,
  WeatherCalendarHour, WeatherEventThresholdModelV1, WeatherEventV1, WeatherLabRunRequestV2,
} from '../src/index.ts';
import {
  HISTORICAL_SIMULATION_TUNING, JACKSON_STATION, SMOOTHED_SIMULATION_TUNING,
  V1_COMPATIBILITY_COMPARISON_STREAM_KEY, adjustedConditionTransitionRow, adjustedMacroTransitionRow,
  advanceWeatherTo, compareWeatherSeriesV2, compileLocationClimateModel,
  createJacksonClimateModel, createJacksonObserved2019,
  createJacksonRun, createWeatherSimulation, createWeatherSnapshot,
  detectObservedWeatherEvents, detectSimulatedWeatherEvents, fallbackWeatherEventThresholds,
  fitWeatherEventThresholds, generateForecastIssues, generateWeatherYear, sha256Hex, summarizeObservedWeatherDays,
  summarizeSimulatedWeatherDays, weatherCalendarYear,
  weatherComparisonScores,
} from '../src/index.ts';

function simulatedHour(calendar: WeatherCalendarHour, overrides: Partial<SimulatedWeatherHourV1> = {}): SimulatedWeatherHourV1 {
  return {
    at: calendar.at, localDateTime: calendar.localDateTime, utcOffsetMinutes: calendar.utcOffsetMinutes,
    fold: calendar.fold, macroAirMass: 'continental-polar', condition: 'clear', hazards: [],
    temperatureC: -5, dewPointC: -7, pressureHpa: 1015, relativeHumidityPct: 80,
    wetBulbC: -6, precipitationMm: 0, precipitationPhase: 'none', snowfallCm: 0,
    windSpeedKph: 10, windDirectionDeg: 270, windGustKph: 15, shortwaveRadiationWm2: 0,
    cloudCoverPct: 10, visibilityKm: 20, ...overrides,
  };
}

function observedHour(hour: SimulatedWeatherHourV1, quality: ObservedWeatherHourV1['quality'] = {}): ObservedWeatherHourV1 {
  const { macroAirMass: _macro, bands: _bands, ...shared } = hour;
  return { ...shared, quality };
}

function observedSeries(year: number, hours: readonly ObservedWeatherHourV1[]): HistoricalWeatherSeriesV1 {
  return {
    version: 1, station: JACKSON_STATION, validationYear: year,
    startInclusive: hours[0]?.at ?? `${year}-01-01T05:00:00.000Z`,
    endExclusive: hours.length ? new Date(new Date(hours.at(-1)!.at).getTime() + 3_600_000).toISOString()
      : `${year + 1}-01-01T05:00:00.000Z`,
    hours, completeness: {} as HistoricalWeatherSeriesV1['completeness'],
    observationHash: sha256Hex(hours), provenance: { providers: ['test'], sourceIds: ['test'], warnings: [] },
  };
}

describe('Weather Lab temporal tuning', () => {
  it('applies storm-arrival tuning only to dry-to-wet transitions', () => {
    const january = createJacksonClimateModel().months[0];
    const higherArrival = { ...HISTORICAL_SIMULATION_TUNING, stormArrivalMultiplier: 3 };
    const wetConditions = new Set(['flurries', 'snow', 'heavy-snow', 'mixed', 'freezing-rain', 'rain']);
    const wetProbability = (row: readonly number[]) => row.reduce((sum, probability, index) =>
      sum + (wetConditions.has(january.local.states[index]) ? probability : 0), 0);

    const baselineDry = adjustedConditionTransitionRow(
      january, 'continental-polar', 'clear', HISTORICAL_SIMULATION_TUNING,
    );
    const tunedDry = adjustedConditionTransitionRow(january, 'continental-polar', 'clear', higherArrival);
    expect(wetProbability(tunedDry)).toBeGreaterThan(wetProbability(baselineDry));

    const baselineWet = adjustedConditionTransitionRow(
      january, 'continental-polar', 'snow', HISTORICAL_SIMULATION_TUNING,
    );
    const tunedWet = adjustedConditionTransitionRow(january, 'continental-polar', 'snow', higherArrival);
    expect(tunedWet).toEqual(baselineWet);

    const colderMacros = { ...HISTORICAL_SIMULATION_TUNING, coldOutbreakMultiplier: 2 };
    const baselineMacros = adjustedMacroTransitionRow(
      january, 'continental-polar', HISTORICAL_SIMULATION_TUNING,
    );
    const tunedMacros = adjustedMacroTransitionRow(january, 'continental-polar', colderMacros);
    expect(tunedMacros[0]).toBeGreaterThan(baselineMacros[0]);
  });

  it('pairs V2 random streams while tuning changes identity and smooths temperature', () => {
    const model = createJacksonClimateModel();
    const baseline = {
      ...createJacksonRun('paired'), version: 2, tuning: HISTORICAL_SIMULATION_TUNING,
      comparisonStreamKey: V1_COMPATIBILITY_COMPARISON_STREAM_KEY,
    } satisfies WeatherLabRunRequestV2;
    const candidate = { ...baseline, tuning: SMOOTHED_SIMULATION_TUNING } satisfies WeatherLabRunRequestV2;
    const initialBaseline = createWeatherSimulation(baseline, model);
    const initialCandidate = createWeatherSimulation(candidate, model);
    expect(initialCandidate.snapshot.streams).toEqual(initialBaseline.snapshot.streams);
    const generatedBaseline = generateWeatherYear(baseline, model);
    const generatedCandidate = generateWeatherYear(candidate, model);
    expect(generatedCandidate.snapshot.runIdentityHash).not.toBe(generatedBaseline.snapshot.runIdentityHash);
    expect(advanceWeatherTo(createWeatherSimulation(candidate, model), 168).hours)
      .toEqual(generatedCandidate.hours.slice(0, 168));
    const meanJump = (hours: readonly SimulatedWeatherHourV1[]) => hours.slice(1)
      .reduce((sum, hour, index) => sum + Math.abs(hour.temperatureC - hours[index].temperatureC), 0)
      / (hours.length - 1);
    expect(meanJump(generatedCandidate.hours)).toBeLessThan(meanJump(generatedBaseline.hours) * 0.75);
    for (let month = 1; month <= 12; month += 1) {
      const selected = (hours: readonly SimulatedWeatherHourV1[]) => hours.filter((hour) =>
        Number(hour.localDateTime.slice(5, 7)) === month);
      const baselineHours = selected(generatedBaseline.hours); const candidateHours = selected(generatedCandidate.hours);
      const mean = (values: readonly number[]) => values.reduce((sum, value) => sum + value, 0) / values.length;
      const baselineTemperature = mean(baselineHours.map((hour) => hour.temperatureC));
      const candidateTemperature = mean(candidateHours.map((hour) => hour.temperatureC));
      expect(Math.abs(candidateTemperature - baselineTemperature)).toBeLessThanOrEqual(3);
      const baselinePrecipitation = baselineHours.reduce((sum, hour) => sum + hour.precipitationMm, 0);
      const candidatePrecipitation = candidateHours.reduce((sum, hour) => sum + hour.precipitationMm, 0);
      expect(Math.abs(candidatePrecipitation - baselinePrecipitation))
        .toBeLessThanOrEqual(Math.max(1, baselinePrecipitation * 0.35));
    }
  });

  it('keeps the characterized V1 stream byte-stable', () => {
    const model = createJacksonClimateModel();
    const generated = advanceWeatherTo(createWeatherSimulation(createJacksonRun('Historical'), model), 336);
    expect(sha256Hex(generated.hours)).toBe('b17c40ce4bcb653ef89561885a77b527e5f07cc7011e55f513c88e6131e8f7cc');
  });

  it('reproduces the complete V1 truth with historical V2 tuning and the compatibility stream key', () => {
    const model = createJacksonClimateModel();
    const legacyRun = createJacksonRun('full-year-v1-compatibility');
    const compatibilityRun = {
      ...legacyRun, version: 2, tuning: HISTORICAL_SIMULATION_TUNING,
      comparisonStreamKey: V1_COMPATIBILITY_COMPARISON_STREAM_KEY,
    } satisfies WeatherLabRunRequestV2;
    expect(createWeatherSimulation(compatibilityRun, model).snapshot.streams)
      .toEqual(createWeatherSimulation(legacyRun, model).snapshot.streams);
    const legacy = generateWeatherYear(legacyRun, model);
    const compatibility = generateWeatherYear(compatibilityRun, model);
    expect(compatibility.hours).toEqual(legacy.hours);
    expect(sha256Hex(compatibility.hours)).toBe(sha256Hex(legacy.hours));
  }, 20_000);

  it('builds the expanded V2 comparison result with daily, event, and diagnostic outputs', () => {
    const model = createJacksonClimateModel();
    const run = { ...createJacksonRun('comparison'), version: 2, tuning: HISTORICAL_SIMULATION_TUNING,
      comparisonStreamKey: 'comparison-result' } satisfies WeatherLabRunRequestV2;
    const generated = advanceWeatherTo(createWeatherSimulation(run, model), 168);
    const forecasts = generateForecastIssues(run, generated.hours).issues;
    const result = compareWeatherSeriesV2(run, generated.hours, createJacksonObserved2019(), forecasts,
      createWeatherSnapshot(generated.simulation), model);
    expect(result).toMatchObject({ version: 2, run: { version: 2 },
      daily: { simulated: expect.any(Array), observed: expect.any(Array) },
      events: { simulated: expect.any(Array), observed: expect.any(Array) } });
    expect(result.daily.simulated).toHaveLength(7);
    expect(result.observedDiagnostics.conditionOccupancy).toHaveProperty('snow');
    expect(result.comparisonHash).toHaveLength(64);
  });
});

describe('multi-station climate fitting', () => {
  it('pools independent station chronologies without cross-station transitions', () => {
    const fixture = createJacksonObserved2019();
    const hours = fixture.hours.map((hour, index) => ({
      ...hour, temperatureC: -5, dewPointC: -10, pressureHpa: 1017, relativeHumidityPct: 50,
      condition: index % 2 === 0 ? 'clear' as const : 'snow' as const, quality: {},
    }));
    const first = { ...fixture, validationYear: 2018, hours, observationHash: sha256Hex(hours) };
    const secondStation = { ...JACKSON_STATION, id: 'KMWN-SECOND', sourceIds: ['KMWN-SECOND'] };
    const second = { ...first, station: secondStation, observationHash: sha256Hex({ station: secondStation.id, hours }) };
    const input = {
      version: 1 as const, location: createJacksonClimateModel().location, primaryStation: JACKSON_STATION,
      trainingPolicy: { kind: 'fixed' as const, startYear: 2018, endYear: 2018 }, validationYear: 2019,
      sourceHashes: [sha256Hex('station-transition-test')], providers: ['test'],
    };
    const single = compileLocationClimateModel({ ...input, trainingStations: [JACKSON_STATION], trainingSeries: [first] });
    const pooled = compileLocationClimateModel({ ...input, trainingStations: [JACKSON_STATION, secondStation], trainingSeries: [first, second] });
    const probability = (model: typeof single) => {
      const january = model.months[0];
      return january.local.hourlyMatricesByMacro['continental-polar'][january.local.states.indexOf('clear')]
        [january.local.states.indexOf('snow')];
    };
    expect(probability(pooled)).toBeGreaterThan(probability(single));
  }, 20_000);
});

describe('local daily weather summaries', () => {
  it('preserves 23/25-hour DST dates instead of slicing fixed 24-hour windows', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York');
    const summaries = summarizeSimulatedWeatherDays(calendar.map((hour) => simulatedHour(hour)));
    expect(summaries.find((day) => day.localDate === '2019-03-10')?.expectedHours).toBe(23);
    expect(summaries.find((day) => day.localDate === '2019-11-03')?.expectedHours).toBe(25);
    expect(summaries).toHaveLength(365);
    expect(summarizeSimulatedWeatherDays(weatherCalendarYear(2020, 'America/New_York')
      .map((hour) => simulatedHour(hour)))).toHaveLength(366);
  });

  it('uses supplied daily water totals and proportionally partitions accepted hourly phases', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').filter((hour) =>
      hour.localDateTime.startsWith('2019-01-01'));
    const hours = calendar.map((entry, index) => observedHour(simulatedHour(entry, index === 0
      ? { precipitationMm: 1, precipitationPhase: 'snow', temperatureC: -5 }
      : index === 1 ? { precipitationMm: 1, precipitationPhase: 'rain', temperatureC: 3 }
        : {})));
    const series = { ...observedSeries(2019, hours), days: [{
      localDate: '2019-01-01', minimumTemperatureC: -5, maximumTemperatureC: 3,
      precipitationMm: 10, snowfallCm: null, snowDepthCm: null,
      sources: { temperature: 'daymet', precipitation: 'daymet', snowfall: null },
    }] };
    const day = summarizeObservedWeatherDays(series)[0];
    expect(day.precipitationMm).toBe(10);
    expect(day.precipitationByPhaseMm).toMatchObject({ snow: 5, rain: 5 });
    expect(day.snowfallSource).toBe('derived');
    expect(Object.values(day.precipitationByPhaseMm ?? {}).reduce((sum, value) => sum + value, 0)).toBe(10);
  });

  it('preserves a Daymet daily total without fabricating a split from partial phase coverage', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').filter((hour) =>
      hour.localDateTime.startsWith('2019-01-01'));
    const hours = calendar.map((entry, index) => {
      const hour = observedHour(simulatedHour(entry, index <= 1
        ? { precipitationMm: 1, precipitationPhase: 'snow' } : {}));
      return index === 1 ? { ...hour, precipitationPhase: null } : hour;
    });
    const series = { ...observedSeries(2019, hours), days: [{
      localDate: '2019-01-01', minimumTemperatureC: -5, maximumTemperatureC: -5,
      precipitationMm: 10, snowfallCm: null, snowDepthCm: null,
      sources: { temperature: 'daymet', precipitation: 'daymet', snowfall: null },
    }] };
    const day = summarizeObservedWeatherDays(series)[0];
    expect(day.precipitationMm).toBe(10);
    expect(day.precipitationByPhaseMm).toBeNull();
  });

  it('aggregates observed leap day by local date', () => {
    const leapDay = weatherCalendarYear(2020, 'America/New_York').filter((hour) =>
      hour.localDateTime.startsWith('2020-02-29'));
    const series = observedSeries(2020, leapDay.map((entry) => observedHour(simulatedHour(entry,
      { temperatureC: -4, wetBulbC: -5 }))));
    const summaries = summarizeObservedWeatherDays(series);
    const day = summaries.find((candidate) => candidate.localDate === '2020-02-29');
    expect(summaries).toHaveLength(366);
    expect(leapDay).toHaveLength(24);
    expect(day).toMatchObject({ localDate: '2020-02-29', expectedHours: 24, availableHours: 24,
      temperatureC: { minimum: -4, mean: -4, maximum: -4 } });
  });

  it('keeps missing observations unavailable and resolves condition ties deterministically', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').slice(0, 4);
    const missing = observedSeries(2019, calendar.map((entry) => observedHour(simulatedHour(entry), {
      precipitationMm: 'missing',
    })));
    expect(summarizeObservedWeatherDays(missing)[0].precipitationMm).toBeNull();
    const tied = calendar.map((entry, index) => simulatedHour(entry, {
      condition: index % 2 ? 'snow' : 'clear',
    }));
    expect(summarizeSimulatedWeatherDays(tied)[0].dominantCondition).toBe('clear');
  });
});

describe('deterministic weather event analysis', () => {
  it('merges storms across three dry hours and ends dry spells at measurable precipitation', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').slice(0, 100);
    const hours = calendar.map((entry, index) => simulatedHour(entry, index === 0 || index === 4
      ? { precipitationMm: 0.6, precipitationPhase: 'snow', snowfallCm: 0.6, condition: 'snow' }
      : {}));
    const thresholds = fallbackWeatherEventThresholds([2018]);
    const events = detectSimulatedWeatherEvents(hours, thresholds);
    const storm = events.find((event) => event.type === 'storm');
    expect(storm).toMatchObject({ durationHours: 5, totalPrecipitationMm: 1.2 });
    const dry = events.find((event) => event.type === 'dry-spell');
    expect(dry?.startsAt).toBe(hours[5].at);
    expect(dry?.endsAt).toBe(new Date(new Date(hours.at(-1)!.at).getTime() + 3_600_000).toISOString());
    expect(detectSimulatedWeatherEvents(hours, thresholds)).toEqual(events);
    expect(sha256Hex(hours)).toBe(sha256Hex(calendar.map((entry, index) => simulatedHour(entry, index === 0 || index === 4
      ? { precipitationMm: 0.6, precipitationPhase: 'snow', snowfallCm: 0.6, condition: 'snow' }
      : {}))));
  });

  it('splits storms at four dry hours and enforces storm and dry-spell boundaries', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').slice(0, 100);
    const thresholds = fallbackWeatherEventThresholds();
    const split = calendar.map((entry, index) => simulatedHour(entry, [0, 1, 2, 7, 8, 9].includes(index)
      ? { precipitationMm: 0.4, precipitationPhase: 'rain', condition: 'rain' } : {}));
    expect(detectSimulatedWeatherEvents(split, thresholds).filter((event) => event.type === 'storm')).toHaveLength(2);
    const belowMinimum = calendar.slice(0, 3).map((entry) => simulatedHour(entry,
      { precipitationMm: 0.3, precipitationPhase: 'rain', condition: 'rain' }));
    expect(detectSimulatedWeatherEvents(belowMinimum, thresholds).some((event) => event.type === 'storm')).toBe(false);
    const dryEnding = calendar.map((entry, index) => simulatedHour(entry, index === 72
      ? { precipitationMm: 1, precipitationPhase: 'rain', condition: 'rain' } : {}));
    expect(detectSimulatedWeatherEvents(dryEnding, thresholds).find((event) => event.type === 'dry-spell')?.endsAt)
      .toBe(dryEnding[72].at);
  });

  it('detects symmetric maintained freezing crossings and lets missing data break storms', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').slice(0, 50);
    const coldTemperatures = [4, 3.5, 3, 2, 1, 0.5, -2, ...Array(16).fill(-3), ...Array(27).fill(-4)];
    const cold = calendar.map((entry, index) => simulatedHour(entry, { temperatureC: coldTemperatures[index] }));
    expect(detectSimulatedWeatherEvents(cold, fallbackWeatherEventThresholds())
      .some((event) => event.type === 'cold-snap')).toBe(true);
    const warm = calendar.map((entry, index) => simulatedHour(entry,
      { temperatureC: -coldTemperatures[index] }));
    expect(detectSimulatedWeatherEvents(warm, fallbackWeatherEventThresholds())
      .some((event) => event.type === 'warm-up')).toBe(true);

    const wet = calendar.slice(0, 3).map((entry, index) => observedHour(simulatedHour(entry, {
      precipitationMm: index === 1 ? 0 : 0.6, precipitationPhase: index === 1 ? 'none' : 'snow',
    }), index === 1 ? { precipitationMm: 'missing' } : {}));
    expect(detectObservedWeatherEvents(observedSeries(2019, wet), fallbackWeatherEventThresholds()))
      .not.toContainEqual(expect.objectContaining({ type: 'storm' }));
  });

  it('requires at least nine of twelve post-crossing hours to maintain a cold snap', () => {
    const calendar = weatherCalendarYear(2019, 'America/New_York').slice(0, 18);
    const eventsForMaintainedHours = (maintainedHours: number) => {
      const following = [
        ...Array(maintainedHours).fill(-2), ...Array(12 - maintainedHours).fill(2),
      ];
      const temperatures = [...Array(6).fill(4), ...following];
      const hours = calendar.map((entry, index) => simulatedHour(entry,
        { temperatureC: temperatures[index] }));
      return detectSimulatedWeatherEvents(hours, fallbackWeatherEventThresholds())
        .filter((event) => event.type === 'cold-snap');
    };
    expect(eventsForMaintainedHours(8)).toHaveLength(0);
    expect(eventsForMaintainedHours(9)).toHaveLength(1);
  });

  it('fits thresholds only from the supplied training years and supports major severity', () => {
    const calendar = weatherCalendarYear(2018, 'America/New_York').slice(0, 120);
    const hours = calendar.map((entry, index) => observedHour(simulatedHour(entry, index < 6
      ? { precipitationMm: 2, precipitationPhase: 'snow' } : {})));
    const fitted = fitWeatherEventThresholds([observedSeries(2018, hours)]);
    expect(fitted.fittedFromYears).toEqual([2018]);
    expect(fitted.months[0].sampleCounts.wetSpells).toBeGreaterThan(0);
    const duplicatedStation = fitWeatherEventThresholds([observedSeries(2018, hours), observedSeries(2018, hours)]);
    expect(duplicatedStation.months[0].stormMinimumTotalMm).toBe(fitted.months[0].stormMinimumTotalMm);
    expect(duplicatedStation.months[0].sampleCounts.wetSpells).toBe(fitted.months[0].sampleCounts.wetSpells * 2);
    const severe: WeatherEventThresholdModelV1 = {
      ...fallbackWeatherEventThresholds([2018]),
      months: fallbackWeatherEventThresholds([2018]).months.map((month) => month.month === 1 ? {
        ...month, stormMinimumTotalMm: 1,
        stormSeverity: { totalP90Mm: 1, totalP98Mm: 2, peakP90Mm: 0.5, peakP98Mm: 1,
          durationP90Hours: 3, durationP98Hours: 4 },
      } : month),
    };
    expect(detectSimulatedWeatherEvents(hours.map((hour) => ({
      ...hour, macroAirMass: 'frontal', condition: hour.precipitationMm ? 'snow' : 'clear',
    } as SimulatedWeatherHourV1)), severe)[0]).toMatchObject({
      type: 'storm', severity: 'major', stormStyle: 'frontal', styleConfidence: 'high',
    });
  });

  it('fits severity percentiles from only spans that meet detected-event minima', () => {
    const calendar = weatherCalendarYear(2018, 'America/New_York').slice(0, 250);
    const isolatedSpikes = new Set(Array.from({ length: 9 }, (_, index) => index * 5));
    const hours = calendar.map((entry, index) => {
      const water = isolatedSpikes.has(index) ? 100 : index >= 45 && index <= 47 ? 200 / 3 : 0;
      return observedHour(simulatedHour(entry, water > 0
        ? { precipitationMm: water, precipitationPhase: 'rain', condition: 'rain' } : {}));
    });
    const january = fitWeatherEventThresholds([observedSeries(2018, hours)]).months[0];
    expect(january.stormMinimumTotalMm).toBe(100);
    expect(january.stormSeverity.totalP90Mm).toBeCloseTo(200, 3);
    expect(january.stormSeverity.durationP90Hours).toBe(3);
    expect(january.drySpellMinimumHours).toBe(72);
    expect(january.drySeverity.durationP90Hours).toBe(202);
  });

  it('reports an empirical percentile from qualifying training events while retaining cutoff severity', () => {
    const trainingCalendar = weatherCalendarYear(2018, 'America/New_York').slice(0, 100);
    const isolatedSpikes = new Set([0, 5, 10, 15, 20]);
    const storms = [
      { first: 30, duration: 3, rate: 1 },
      { first: 37, duration: 6, rate: 2 },
      { first: 47, duration: 9, rate: 3 },
      { first: 60, duration: 12, rate: 4 },
    ];
    const trainingHours = trainingCalendar.map((entry, index) => {
      const storm = storms.find((candidate) => index >= candidate.first
        && index < candidate.first + candidate.duration);
      const water = storm?.rate ?? (isolatedSpikes.has(index) ? 0.1 : 0);
      return observedHour(simulatedHour(entry, water > 0
        ? { precipitationMm: water, precipitationPhase: 'rain', condition: 'rain' } : {}));
    });
    const fitted = fitWeatherEventThresholds([observedSeries(2018, trainingHours)]);
    expect(fitted.months[0].empiricalDistributions).toMatchObject({
      stormTotalMm: [3, 12, 27, 48],
      stormPeakHourlyMm: [1, 2, 3, 4],
      stormDurationHours: [3, 6, 9, 12],
    });

    const validationHours = weatherCalendarYear(2019, 'America/New_York').slice(0, 7)
      .map((entry) => simulatedHour(entry,
        { precipitationMm: 2.5, precipitationPhase: 'rain', condition: 'rain' }));
    const storm = detectSimulatedWeatherEvents(validationHours, fitted)
      .find((event) => event.type === 'storm');
    expect(storm).toMatchObject({ intensityPercentile: 50, severity: 'minor' });
  });

  it('scores paired storm severity and excludes unknown or low-confidence observed styles', () => {
    const makeEvent = (id: string, severity: WeatherEventV1['severity'], style: WeatherEventV1['stormStyle'],
      confidence: WeatherEventV1['styleConfidence']): WeatherEventV1 => ({
      version: 1, id, type: 'storm', startsAt: '2019-01-01T00:00:00.000Z', endsAt: '2019-01-01T06:00:00.000Z',
      localStartDate: '2019-01-01', localEndDate: '2019-01-01', durationHours: 6, severity,
      intensityPercentile: 95, totalPrecipitationMm: 10, peakPrecipitationMm: 2,
      precipitationByPhaseMm: { none: 0, rain: 10, mixed: 0, snow: 0, 'freezing-rain': 0 },
      snowfallCm: 0, temperatureChangeC: 0, meanWindSpeedKph: 20, peakWindGustKph: 35,
      pressureChangeHpa: -5, stormStyle: style, styleConfidence: confidence, styleEvidence: [],
    });
    const simulated = makeEvent('simulated', 'major', 'frontal', 'high');
    const observed = makeEvent('observed', 'major', 'frontal', 'high');
    expect(weatherComparisonScores([], [], [simulated], [observed])).toMatchObject({
      stormSeverityAgreement: 1, stormStyleAgreement: 1,
    });
    expect(weatherComparisonScores([], [], [simulated], [{ ...observed, styleConfidence: 'low' }]).stormStyleAgreement)
      .toBeNull();
  });
});
