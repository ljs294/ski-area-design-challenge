import type {
  HistoricalWeatherSeriesV1, HourlyNormalV1, LocationClimateModelV1, MacroAirMassDefinitionV1,
  MacroAirMassId, MonthlyClimateModelV1, ObservedWeatherHourV1, ObservingStationMetadataV1,
  TrainingPeriodPolicyV1, WeatherCondition, WeatherLocationV1, WeatherMonth,
} from '../contracts.ts';
import { WEATHER_GENERATOR_VERSION } from '../contracts.ts';
import { hashWithout, sha256Hex } from '../engine/canonical.ts';
import { clamp, relativeHumidityPct } from '../engine/psychrometrics.ts';
import { fitWeatherEventThresholds } from '../validation/events.ts';

const CONDITIONS: readonly WeatherCondition[] = ['clear', 'partly-cloudy', 'overcast', 'flurries', 'snow', 'heavy-snow', 'mixed', 'freezing-rain', 'rain'];
const MACROS: readonly MacroAirMassId[] = ['arctic', 'continental-polar', 'maritime-polar', 'warm-wet', 'frontal'];

export interface ClimateTrainingInputV1 {
  version: 1;
  location: WeatherLocationV1;
  primaryStation: ObservingStationMetadataV1;
  trainingStations: readonly ObservingStationMetadataV1[];
  trainingPolicy: TrainingPeriodPolicyV1;
  validationYear: number;
  trainingSeries: readonly HistoricalWeatherSeriesV1[];
  sourceHashes: readonly string[];
  providers: readonly string[];
  compilerVersion?: string;
}

function mean(values: readonly number[], fallback: number): number {
  return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : fallback;
}

function standardDeviation(values: readonly number[], average: number, fallback: number): number {
  if (values.length < 2) return fallback;
  return Math.sqrt(values.reduce((sum, value) => sum + (value - average) ** 2, 0) / (values.length - 1));
}

function finite(values: readonly (number | null)[]): number[] {
  return values.filter((value): value is number => value != null && Number.isFinite(value));
}

function parsed(hour: ObservedWeatherHourV1): { month: number; localHour: number } {
  return { month: Number(hour.localDateTime.slice(5, 7)), localHour: Number(hour.localDateTime.slice(11, 13)) };
}

function conditionFor(hour: ObservedWeatherHourV1): WeatherCondition {
  if (hour.condition) return hour.condition;
  const water = hour.precipitationMm ?? 0;
  if (water > 0) {
    if (hour.precipitationPhase === 'freezing-rain') return 'freezing-rain';
    if (hour.precipitationPhase === 'rain') return 'rain';
    if (hour.precipitationPhase === 'mixed') return 'mixed';
    if (hour.precipitationPhase === 'snow') return water >= 2 ? 'heavy-snow' : water < 0.2 ? 'flurries' : 'snow';
  }
  const cloud = hour.cloudCoverPct ?? 50;
  return cloud <= 20 ? 'clear' : cloud <= 70 ? 'partly-cloudy' : 'overcast';
}

function macroFor(hour: ObservedWeatherHourV1, temperatureMean: number, temperatureStdDev: number): MacroAirMassId {
  const temperature = hour.temperatureC ?? temperatureMean;
  const dewPoint = hour.dewPointC ?? temperature - 5;
  const pressure = hour.pressureHpa ?? 1013;
  const anomaly = (temperature - temperatureMean) / Math.max(1, temperatureStdDev);
  if (Math.abs(pressure - 1013) >= 12 || Math.abs(temperature - dewPoint) < 1 && Math.abs(anomaly) < 0.4) return 'frontal';
  if (anomaly <= -1.1) return 'arctic';
  if (anomaly >= 0.75 && dewPoint >= temperatureMean - 1) return 'warm-wet';
  if ((hour.relativeHumidityPct ?? relativeHumidityPct(temperature, dewPoint)) >= 80) return 'maritime-polar';
  return 'continental-polar';
}

function normalizedRow(row: readonly number[]): number[] {
  const total = row.reduce((sum, value) => sum + Math.max(0, value), 0);
  return total > 0 ? row.map((value) => Math.max(0, value) / total) : row.map(() => 1 / row.length);
}

function macroDefinitions(hours: readonly ObservedWeatherHourV1[], meanTemperature: number, stdTemperature: number): MacroAirMassDefinitionV1[] {
  const groups = new Map<MacroAirMassId, ObservedWeatherHourV1[]>(MACROS.map((id) => [id, []]));
  for (const hour of hours) groups.get(macroFor(hour, meanTemperature, stdTemperature))!.push(hour);
  const defaults: Record<MacroAirMassId, Omit<MacroAirMassDefinitionV1, 'id'>> = {
    arctic: { meanDurationHours: 72, durationStdDevHours: 24, temperatureAnomalyC: -8, dewPointAnomalyC: -8, pressureAnomalyHpa: 8, windSpeedMultiplier: 1.1, precipitationMultiplier: 0.6 },
    'continental-polar': { meanDurationHours: 72, durationStdDevHours: 30, temperatureAnomalyC: -2, dewPointAnomalyC: -4, pressureAnomalyHpa: 4, windSpeedMultiplier: 0.9, precipitationMultiplier: 0.5 },
    'maritime-polar': { meanDurationHours: 60, durationStdDevHours: 24, temperatureAnomalyC: -1, dewPointAnomalyC: 1, pressureAnomalyHpa: -2, windSpeedMultiplier: 1, precipitationMultiplier: 1.2 },
    'warm-wet': { meanDurationHours: 48, durationStdDevHours: 20, temperatureAnomalyC: 6, dewPointAnomalyC: 6, pressureAnomalyHpa: -5, windSpeedMultiplier: 1.15, precipitationMultiplier: 1.5 },
    frontal: { meanDurationHours: 12, durationStdDevHours: 6, temperatureAnomalyC: 0, dewPointAnomalyC: 1, pressureAnomalyHpa: -8, windSpeedMultiplier: 1.5, precipitationMultiplier: 1.4 },
  };
  return MACROS.map((id) => {
    const selected = groups.get(id)!;
    const temperatures = finite(selected.map((hour) => hour.temperatureC));
    const dewPoints = finite(selected.map((hour) => hour.dewPointC));
    const pressures = finite(selected.map((hour) => hour.pressureHpa));
    return { id, ...defaults[id],
      temperatureAnomalyC: mean(temperatures, meanTemperature + defaults[id].temperatureAnomalyC) - meanTemperature,
      dewPointAnomalyC: mean(dewPoints, meanTemperature + defaults[id].dewPointAnomalyC) - meanTemperature,
      pressureAnomalyHpa: mean(pressures, 1013 + defaults[id].pressureAnomalyHpa) - 1013 };
  });
}

function buildMonthlyModel(month: WeatherMonth, allHours: readonly ObservedWeatherHourV1[],
  chronologies: readonly (readonly ObservedWeatherHourV1[])[]): MonthlyClimateModelV1 {
  const hours = allHours.filter((hour) => parsed(hour).month === month);
  const monthTemperatures = finite(hours.map((hour) => hour.temperatureC));
  const monthMean = mean(monthTemperatures, 5 - Math.abs(1 - month) * 0.8);
  const monthStd = standardDeviation(monthTemperatures, monthMean, 6);
  const hourlyNormals: HourlyNormalV1[] = Array.from({ length: 24 }, (_, localHour) => {
    const selected = hours.filter((hour) => parsed(hour).localHour === localHour);
    const temperatures = finite(selected.map((hour) => hour.temperatureC));
    const temperatureC = mean(temperatures, monthMean + 5 * Math.cos((localHour - 15) / 24 * Math.PI * 2));
    const dewPoints = finite(selected.map((hour) => hour.dewPointC));
    const dewPointC = Math.min(temperatureC, mean(dewPoints, temperatureC - 4));
    const windDirections = finite(selected.map((hour) => hour.windDirectionDeg));
    const directionRadians = windDirections.map((value) => value * Math.PI / 180);
    const windDirectionDeg = directionRadians.length ? (Math.atan2(mean(directionRadians.map(Math.sin), 0), mean(directionRadians.map(Math.cos), 1)) * 180 / Math.PI + 360) % 360 : 270;
    return {
      temperatureC, temperatureStdDevC: standardDeviation(temperatures, temperatureC, monthStd),
      dewPointC, dewPointStdDevC: standardDeviation(dewPoints, dewPointC, monthStd * 0.8),
      pressureHpa: mean(finite(selected.map((hour) => hour.pressureHpa)), 1013),
      relativeHumidityPct: mean(finite(selected.map((hour) => hour.relativeHumidityPct)), relativeHumidityPct(temperatureC, dewPointC)),
      windSpeedKph: mean(finite(selected.map((hour) => hour.windSpeedKph)), 12), windDirectionDeg,
      cloudCoverPct: mean(finite(selected.map((hour) => hour.cloudCoverPct)), 50),
      visibilityKm: mean(finite(selected.map((hour) => hour.visibilityKm)), 16),
      clearSkyRadiationWm2: mean(finite(selected.map((hour) => hour.shortwaveRadiationWm2)), Math.max(0, 600 * Math.sin((localHour - 6) / 12 * Math.PI))),
    };
  });
  const definitions = macroDefinitions(hours, monthMean, monthStd);
  const macroCounts = MACROS.map(() => MACROS.map(() => 1));
  const localCounts = Object.fromEntries(MACROS.map((macro) => [macro, CONDITIONS.map(() => CONDITIONS.map(() => 1))])) as Record<MacroAirMassId, number[][]>;
  // Pool transition counts after traversing each station-year independently.
  // Sorting equal timestamps from multiple stations into one sequence would
  // discard within-station transitions and create cross-station transitions.
  for (const chronology of chronologies) {
    const sorted = chronology.filter((hour) => parsed(hour).month === month)
      .sort((left, right) => left.at.localeCompare(right.at));
    for (let index = 1; index < sorted.length; index += 1) {
      if (new Date(sorted[index].at).getTime() - new Date(sorted[index - 1].at).getTime() !== 3_600_000) continue;
      const previousMacro = macroFor(sorted[index - 1], monthMean, monthStd);
      const currentMacro = macroFor(sorted[index], monthMean, monthStd);
      macroCounts[MACROS.indexOf(previousMacro)][MACROS.indexOf(currentMacro)] += 1;
      const previousCondition = conditionFor(sorted[index - 1]);
      const currentCondition = conditionFor(sorted[index]);
      localCounts[currentMacro][CONDITIONS.indexOf(previousCondition)][CONDITIONS.indexOf(currentCondition)] += 1;
    }
  }
  const wet = hours.filter((hour) => (hour.precipitationMm ?? 0) > 0).map((hour) => hour.precipitationMm!);
  const wetMean = mean(wet, 0.7);
  const wetVariance = standardDeviation(wet, wetMean, 0.8) ** 2;
  const shape = clamp(wetMean ** 2 / Math.max(0.01, wetVariance), 0.25, 8);
  return {
    month, hourlyNormals,
    macro: { states: definitions, transitionMatrix: macroCounts.map(normalizedRow) },
    local: { states: CONDITIONS, hourlyMatricesByMacro: Object.fromEntries(MACROS.map((macro) => [macro, localCounts[macro].map(normalizedRow)])) as unknown as Record<MacroAirMassId, readonly (readonly number[])[]>, backoffRows: normalizedRow(CONDITIONS.map((condition) => hours.filter((hour) => conditionFor(hour) === condition).length + 1)) },
    emissions: { temperatureAr1: 0.82, dewPointAr1: 0.86, temperatureDewPointCorrelation: 0.72,
      precipitationShape: shape, precipitationScaleMm: wetMean / shape, snowfallRatio: 10,
      // Gamma mean is shape * scale. Dividing by the fitted shape keeps the
      // stochastic draw centered on the observed training mean.
      windShape: 2, windScaleKph: Math.max(1, mean(finite(hours.map((hour) => hour.windSpeedKph)), 12) / 2), gustFactor: 1.45 },
    sampleCounts: { hours: hours.length, wetHours: wet.length },
  };
}

export function trainingYears(policy: TrainingPeriodPolicyV1, validationYear: number): readonly number[] {
  if (policy.kind === 'prior-30') return Array.from({ length: 30 }, (_, index) => validationYear - 30 + index);
  if (policy.kind === 'leave-one-out-1991-2020') return Array.from({ length: 30 }, (_, index) => 1991 + index).filter((year) => year !== validationYear);
  if (policy.startYear > policy.endYear) throw new Error('Fixed training period startYear must not exceed endYear');
  return Array.from({ length: policy.endYear - policy.startYear + 1 }, (_, index) => policy.startYear + index).filter((year) => year !== validationYear);
}

export function compileLocationClimateModel(input: ClimateTrainingInputV1): LocationClimateModelV1 {
  const years = trainingYears(input.trainingPolicy, input.validationYear);
  if (years.includes(input.validationYear)) throw new Error('Validation year leaked into the climate training period');
  const providedYears = new Set(input.trainingSeries.map((series) => series.validationYear));
  const missing = years.filter((year) => !providedYears.has(year));
  if (missing.length) throw new Error(`Training series missing required years: ${missing.slice(0, 6).join(', ')}`);
  if (input.trainingSeries.some((series) => series.validationYear === input.validationYear)) {
    throw new Error('Validation-year observations must not enter climate compilation');
  }
  const chronologies = input.trainingSeries.map((series) => series.hours.filter((hour) =>
    Object.values(hour.quality).every((quality) => quality !== 'suspect')));
  const hours = chronologies.flat();
  if (hours.length < years.length * 5_000) throw new Error('Climate compilation does not have enough quality-controlled hourly observations');
  const sourceHash = sha256Hex({ sourceHashes: [...input.sourceHashes].sort(), stationIds: input.trainingStations.map((station) => station.id).sort(), years });
  const base = {
    version: 1 as const, generatorVersion: WEATHER_GENERATOR_VERSION, location: input.location,
    primaryStation: input.primaryStation, trainingStations: input.trainingStations,
    trainingPeriod: { years, policy: input.trainingPolicy }, excludedValidationYear: input.validationYear,
    months: Array.from({ length: 12 }, (_, index) => buildMonthlyModel((index + 1) as WeatherMonth, hours, chronologies)),
    eventThresholds: fitWeatherEventThresholds(input.trainingSeries),
    sourceHash, climateModelHash: '', provenance: { compilerVersion: input.compilerVersion ?? 'weather-compiler-v1',
      providers: [...input.providers], sourceHashes: [...input.sourceHashes], warnings: [] as string[] },
  } satisfies LocationClimateModelV1;
  return { ...base, climateModelHash: hashWithout(base, ['climateModelHash']) };
}
