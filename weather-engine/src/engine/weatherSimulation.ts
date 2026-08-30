import type {
  ForecastDayV1, ForecastHourV1, ForecastIssueV1, HourlyNormalV1, LocationClimateModelV1,
  MacroAirMassDefinitionV1, MacroAirMassId, MonthlyClimateModelV1, SimulatedElevationBandV1,
  SimulatedWeatherHourV1, WeatherCondition, WeatherDifficultyProfileV1, WeatherEngineSnapshotV2,
  WeatherHazard, WeatherLabRunRequest, WeatherMonth, WeatherRandomStateV1, WeatherSimulationTuningV1,
} from '../contracts.ts';
import { simulationTuningForDifficulty, WEATHER_ENGINE_ID, WEATHER_GENERATOR_VERSION,
  WEATHER_SIMULATION_TUNING_LIMITS } from '../contracts.ts';
import { canonicalJson, hashWithout, hmacSha256, normalizeWorldSeed, sha256Hex } from './canonical.ts';
import { monthBlend, weatherCalendarYear, type WeatherCalendarHour } from './calendar.ts';
import { WeatherRandom } from './randomV2.ts';
import { angularDifference, clamp, precipitationPhase, pressureAtElevation, quantize, relativeHumidityPct,
  snowfallCentimetresFromLiquid, wetBulbTemperatureC } from './psychrometrics.ts';

const MACROS: readonly MacroAirMassId[] = ['arctic', 'continental-polar', 'maritime-polar', 'warm-wet', 'frontal'];
const PRECIPITATING = new Set<WeatherCondition>(['flurries', 'snow', 'heavy-snow', 'mixed', 'freezing-rain', 'rain']);
export const V1_COMPATIBILITY_COMPARISON_STREAM_KEY = 'weather-v1-historical';

export interface WeatherSimulationV2 {
  readonly run: WeatherLabRunRequest;
  readonly model: LocationClimateModelV1;
  readonly calendar: readonly WeatherCalendarHour[];
  readonly snapshot: WeatherEngineSnapshotV2;
}

export interface WeatherAdvanceV2 {
  simulation: WeatherSimulationV2;
  hour: SimulatedWeatherHourV1;
}

export interface GeneratedWeatherYearV2 {
  runIdentityHash: string;
  hours: readonly SimulatedWeatherHourV1[];
  snapshot: WeatherEngineSnapshotV2;
}

function assertDifficulty(profile: WeatherDifficultyProfileV1): void {
  const ranges: Array<[keyof WeatherDifficultyProfileV1, number, number]> = [
    ['stormArrivalMultiplier', 0.5, 2], ['stormPersistenceMultiplier', 0.5, 2],
    ['precipitationIntensityMultiplier', 0.5, 2], ['warmIntrusionMultiplier', 0.5, 2],
    ['coldOutbreakMultiplier', 0.5, 2], ['temperatureVolatilityMultiplier', 0.5, 1.75],
    ['windSeverityMultiplier', 0.5, 2], ['extremeEventMultiplier', 0.25, 2.5],
    ['forecastErrorMultiplier', 0.5, 2],
  ];
  for (const [key, minimum, maximum] of ranges) {
    const value = profile[key];
    if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum || value > maximum) {
      throw new Error(`${String(key)} must be between ${minimum} and ${maximum}`);
    }
  }
}

function assertTuning(tuning: WeatherSimulationTuningV1): void {
  if (tuning.version !== 1 || !tuning.id.trim()) throw new Error('Weather simulation tuning identity is invalid');
  for (const [key, [minimum, maximum]] of Object.entries(WEATHER_SIMULATION_TUNING_LIMITS) as
    Array<[keyof typeof WEATHER_SIMULATION_TUNING_LIMITS, readonly [number, number]]>) {
    const value = tuning[key];
    if ((key === 'temperatureAr1' || key === 'dewPointAr1') && value == null) continue;
    if (typeof value !== 'number' || !Number.isFinite(value) || value < minimum || value > maximum) {
      throw new Error(`${key} must be between ${minimum} and ${maximum}`);
    }
  }
  if (!Number.isInteger(tuning.hourlyNormalSmoothingRadius)) {
    throw new Error('hourlyNormalSmoothingRadius must be an integer');
  }
}

function tuningFor(run: WeatherLabRunRequest): WeatherSimulationTuningV1 {
  return run.version === 2 ? run.tuning : simulationTuningForDifficulty(run.difficultyProfile);
}

function runIdentity(run: WeatherLabRunRequest): string {
  return sha256Hex({ engineId: WEATHER_ENGINE_ID, ...run, worldSeed: normalizeWorldSeed(run.worldSeed),
    difficultyProfileHash: sha256Hex(run.difficultyProfile) });
}

function monthStreams(run: WeatherLabRunRequest, month: WeatherMonth): WeatherEngineSnapshotV2['streams'] {
  const seed = normalizeWorldSeed(run.worldSeed);
  const shared = { engineId: WEATHER_ENGINE_ID, generatorVersion: run.generatorVersion,
    climateModelHash: run.climateModelHash, locationKey: run.location.id, validationYear: run.validationYear,
    yyyyMm: `${run.validationYear}-${String(month).padStart(2, '0')}` };
  // The compatibility key deliberately uses the original V1 payload shape so a
  // historical V2 baseline starts every month from the exact V1 stream sequence.
  const useV1CompatibilityStreams = run.version === 1
    || run.comparisonStreamKey === V1_COMPATIBILITY_COMPARISON_STREAM_KEY;
  const base = useV1CompatibilityStreams
    ? { ...shared, difficultyProfileHash: sha256Hex(run.difficultyProfile) }
    : { ...shared, comparisonStreamKey: run.comparisonStreamKey };
  const stream = (name: string): WeatherRandomStateV1 => WeatherRandom.fromDigest(hmacSha256(seed, { ...base, stream: name })).snapshot();
  return { macro: stream('macro'), local: stream('local'), emissions: stream('emissions'), forecastError: stream('forecast-error') };
}

function modelMonth(model: LocationClimateModelV1, month: number): MonthlyClimateModelV1 {
  const found = model.months.find((candidate) => candidate.month === month);
  if (!found) throw new Error(`Climate model is missing month ${month}`);
  return found;
}

function blendNumber(current: number, previous: number, next: number, previousWeight: number, nextWeight: number): number {
  return current * (1 - previousWeight - nextWeight) + previous * previousWeight + next * nextWeight;
}

function blendedNormalAtHour(model: LocationClimateModelV1, calendar: WeatherCalendarHour, hour: number): HourlyNormalV1 {
  const blend = monthBlend(calendar);
  const current = modelMonth(model, blend.current).hourlyNormals[hour];
  const previous = modelMonth(model, blend.previous).hourlyNormals[hour];
  const next = modelMonth(model, blend.next).hourlyNormals[hour];
  const value = (key: keyof HourlyNormalV1) => blendNumber(current[key], previous[key], next[key], blend.previousWeight, blend.nextWeight);
  const directionDeltaPrevious = angularDifference(current.windDirectionDeg, previous.windDirectionDeg);
  const directionDeltaNext = angularDifference(current.windDirectionDeg, next.windDirectionDeg);
  return { temperatureC: value('temperatureC'), temperatureStdDevC: value('temperatureStdDevC'),
    dewPointC: value('dewPointC'), dewPointStdDevC: value('dewPointStdDevC'), pressureHpa: value('pressureHpa'),
    relativeHumidityPct: value('relativeHumidityPct'), windSpeedKph: value('windSpeedKph'),
    windDirectionDeg: (current.windDirectionDeg + directionDeltaPrevious * blend.previousWeight + directionDeltaNext * blend.nextWeight + 360) % 360,
    cloudCoverPct: value('cloudCoverPct'), visibilityKm: value('visibilityKm'), clearSkyRadiationWm2: value('clearSkyRadiationWm2') };
}

function blendedNormal(model: LocationClimateModelV1, calendar: WeatherCalendarHour, radius = 0): HourlyNormalV1 {
  const center = blendedNormalAtHour(model, calendar, calendar.hour);
  if (radius === 0) return center;
  const normals = Array.from({ length: radius * 2 + 1 }, (_, index) =>
    blendedNormalAtHour(model, calendar, (calendar.hour + index - radius + 24) % 24));
  const mean = (key: Exclude<keyof HourlyNormalV1, 'windDirectionDeg'>) =>
    normals.reduce((sum, normal) => sum + normal[key], 0) / normals.length;
  return {
    temperatureC: mean('temperatureC'), temperatureStdDevC: mean('temperatureStdDevC'),
    dewPointC: mean('dewPointC'), dewPointStdDevC: mean('dewPointStdDevC'),
    pressureHpa: mean('pressureHpa'), relativeHumidityPct: mean('relativeHumidityPct'),
    windSpeedKph: mean('windSpeedKph'),
    windDirectionDeg: (center.windDirectionDeg + normals.reduce((sum, normal) =>
      sum + angularDifference(center.windDirectionDeg, normal.windDirectionDeg), 0) / normals.length + 360) % 360,
    cloudCoverPct: mean('cloudCoverPct'), visibilityKm: mean('visibilityKm'),
    clearSkyRadiationWm2: mean('clearSkyRadiationWm2'),
  };
}

function definition(month: MonthlyClimateModelV1, id: MacroAirMassId): MacroAirMassDefinitionV1 {
  const found = month.macro.states.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`Climate month ${month.month} is missing macro state ${id}`);
  return found;
}

function tilted(weights: readonly number[], multipliers: readonly number[]): number[] {
  const adjusted = weights.map((probability, index) => {
    const bounded = clamp(probability, 1e-6, 1 - 1e-6);
    const odds = bounded / (1 - bounded);
    const nextOdds = odds * (multipliers[index] ?? 1);
    return nextOdds / (1 + nextOdds);
  });
  const total = adjusted.reduce((sum, value) => sum + value, 0);
  return adjusted.map((value) => value / total);
}

export function adjustedMacroTransitionRow(month: MonthlyClimateModelV1, priorMacro: MacroAirMassId,
  tuning: WeatherSimulationTuningV1): readonly number[] {
  const row = month.macro.transitionMatrix[MACROS.indexOf(priorMacro)];
  const multipliers = MACROS.map((id) => id === 'arctic' ? tuning.coldOutbreakMultiplier
    : id === 'warm-wet' ? tuning.warmIntrusionMultiplier
      : id === 'frontal' ? tuning.extremeEventMultiplier : 1);
  return tilted(row, multipliers);
}

function chooseMacro(snapshot: WeatherEngineSnapshotV2, month: MonthlyClimateModelV1, random: WeatherRandom,
  tuning: WeatherSimulationTuningV1): { id: MacroAirMassId; duration: number; changed: boolean } {
  if (snapshot.macroHoursRemaining > 0) {
    return { id: snapshot.macroAirMass, duration: snapshot.macroHoursRemaining, changed: false };
  }
  const row = adjustedMacroTransitionRow(month, snapshot.macroAirMass, tuning);
  const id = MACROS[random.weighted(row)];
  const selected = definition(month, id);
  const duration = Math.round(clamp(random.normal(selected.meanDurationHours, selected.durationStdDevHours) *
    tuning.macroDurationMultiplier, id === 'frontal' ? 3 : 12, 24 * 8));
  return { id, duration, changed: id !== snapshot.macroAirMass };
}

export function adjustedConditionTransitionRow(month: MonthlyClimateModelV1, macro: MacroAirMassId,
  priorCondition: WeatherCondition, tuning: WeatherSimulationTuningV1): readonly number[] {
  const priorIndex = Math.max(0, month.local.states.indexOf(priorCondition));
  const row = month.local.hourlyMatricesByMacro[macro][priorIndex] ?? month.local.backoffRows;
  const priorIsPrecipitating = PRECIPITATING.has(priorCondition);
  const multipliers = month.local.states.map((condition) => {
    let multiplier = !priorIsPrecipitating && PRECIPITATING.has(condition)
      ? tuning.stormArrivalMultiplier : 1;
    if (condition === priorCondition) multiplier *= tuning.conditionPersistenceMultiplier;
    if (condition === 'heavy-snow' || condition === 'freezing-rain') multiplier *= tuning.extremeEventMultiplier;
    return multiplier;
  });
  return tilted(row, multipliers);
}

function chooseCondition(snapshot: WeatherEngineSnapshotV2, month: MonthlyClimateModelV1, macro: MacroAirMassId,
  random: WeatherRandom, tuning: WeatherSimulationTuningV1): WeatherCondition {
  const priorIndex = Math.max(0, month.local.states.indexOf(snapshot.localCondition));
  const row = adjustedConditionTransitionRow(month, macro, snapshot.localCondition, tuning);
  return month.local.states[random.weighted(row)] ?? month.local.states[priorIndex];
}

function cloudFor(condition: WeatherCondition, normal: number, random: WeatherRandom): number {
  const ranges: Record<WeatherCondition, [number, number]> = {
    clear: [0, 20], 'partly-cloudy': [20, 70], overcast: [75, 100], flurries: [70, 100],
    snow: [85, 100], 'heavy-snow': [95, 100], mixed: [90, 100], 'freezing-rain': [95, 100], rain: [90, 100],
  };
  const [minimum, maximum] = ranges[condition];
  return clamp(normal * 0.25 + random.normal((minimum + maximum) / 2, (maximum - minimum) / 5) * 0.75, minimum, maximum);
}

function physicalCondition(initial: WeatherCondition, phase: ReturnType<typeof precipitationPhase>, water: number): WeatherCondition {
  if (water < 0.005) return initial === 'clear' || initial === 'partly-cloudy' ? initial : 'overcast';
  if (phase === 'freezing-rain') return 'freezing-rain';
  if (phase === 'rain') return 'rain';
  if (phase === 'mixed') return 'mixed';
  return water >= 2 ? 'heavy-snow' : water < 0.2 ? 'flurries' : 'snow';
}

function elevationBand(hour: Omit<SimulatedWeatherHourV1, 'bands'>, referenceElevationM: number, elevationM: number): SimulatedElevationBandV1 {
  const lapse = clamp(0.0065 * (elevationM - referenceElevationM), -8, 12);
  const temperatureC = hour.temperatureC - lapse;
  const pressureHpa = pressureAtElevation(hour.pressureHpa, referenceElevationM, elevationM);
  const wetBulbC = wetBulbTemperatureC(temperatureC, hour.relativeHumidityPct, pressureHpa);
  const precipitationMm = hour.precipitationMm * (1 + clamp((elevationM - referenceElevationM) * 0.0002, -0.2, 0.5));
  const phase = precipitationPhase(temperatureC, wetBulbC, precipitationMm);
  const snowfallCm = phase === 'snow' ? precipitationMm * clamp(10 - temperatureC * 0.35, 7, 18) / 10 : phase === 'mixed' ? precipitationMm * 0.4 : 0;
  return { elevationM, temperatureC: quantize(temperatureC, 2), wetBulbC: quantize(wetBulbC, 2),
    pressureHpa: quantize(pressureHpa, 1), precipitationMm: quantize(precipitationMm, 3), snowfallCm: quantize(snowfallCm, 2),
    windSpeedKph: quantize(hour.windSpeedKph * (1 + clamp((elevationM - referenceElevationM) * 0.00012, -0.1, 0.35)), 1) };
}

export function createWeatherSimulation(run: WeatherLabRunRequest, model: LocationClimateModelV1): WeatherSimulationV2 {
  if (run.generatorVersion !== WEATHER_GENERATOR_VERSION || model.generatorVersion !== WEATHER_GENERATOR_VERSION) throw new Error('Weather generator version mismatch');
  if (run.climateModelHash !== model.climateModelHash || model.climateModelHash !== hashWithout(model, ['climateModelHash'])) throw new Error('Climate model hash mismatch');
  if (model.excludedValidationYear !== run.validationYear || model.trainingPeriod.years.includes(run.validationYear)) throw new Error('Validation year leaked into the climate model');
  if (model.primaryStation.id !== run.stationId || model.primaryStation.timezone !== run.stationTimeZone) throw new Error('Run station does not match climate model');
  assertDifficulty(run.difficultyProfile);
  if (run.version === 2) {
    assertTuning(run.tuning);
    if (!run.comparisonStreamKey.trim()) throw new Error('comparisonStreamKey must not be empty');
  }
  const normalizedRun = { ...run, worldSeed: normalizeWorldSeed(run.worldSeed) };
  const calendar = weatherCalendarYear(run.validationYear, run.stationTimeZone);
  const streams = monthStreams(normalizedRun, 1);
  const identity = runIdentity(normalizedRun);
  const snapshot: WeatherEngineSnapshotV2 = { schemaVersion: 2, generatorVersion: 2, runIdentityHash: identity,
    nextHourIndex: 0, activeMonth: 1, macroAirMass: 'continental-polar', macroHoursRemaining: 0,
    localCondition: 'partly-cloudy', temperatureResidualC: 0, dewPointResidualC: 0,
    pressureHpa: model.months[0].hourlyNormals[0].pressureHpa,
    windSpeedKph: model.months[0].hourlyNormals[0].windSpeedKph * 0.82,
    windDirectionDeg: model.months[0].hourlyNormals[0].windDirectionDeg,
    previousMoisture: 0, previousTemperatureC: model.months[0].hourlyNormals[0].temperatureC, streams };
  return Object.freeze({ run: normalizedRun, model, calendar, snapshot });
}

export function restoreWeatherSnapshot(run: WeatherLabRunRequest, model: LocationClimateModelV1, snapshot: WeatherEngineSnapshotV2): WeatherSimulationV2 {
  if (snapshot.schemaVersion !== 2 || snapshot.generatorVersion !== 2) throw new Error('Unsupported weather snapshot version');
  const initial = createWeatherSimulation(run, model);
  if (snapshot.runIdentityHash !== initial.snapshot.runIdentityHash) throw new Error('Weather snapshot run identity mismatch');
  if (!Number.isInteger(snapshot.nextHourIndex) || snapshot.nextHourIndex < 0 || snapshot.nextHourIndex > initial.calendar.length) throw new Error('Weather snapshot cursor is invalid');
  for (const state of Object.values(snapshot.streams)) {
    if (state.state.length !== 4 || state.state.some((value) => !Number.isInteger(value))) throw new Error('Weather snapshot RNG state is corrupt');
  }
  return Object.freeze({ ...initial, snapshot: structuredClone(snapshot) });
}

export function createWeatherSnapshot(simulation: WeatherSimulationV2): WeatherEngineSnapshotV2 {
  return structuredClone(simulation.snapshot);
}

export function advanceWeatherHour(simulation: WeatherSimulationV2): WeatherAdvanceV2 {
  const calendar = simulation.calendar[simulation.snapshot.nextHourIndex];
  if (!calendar) throw new Error('Weather simulation is already at end of year');
  let snapshot = simulation.snapshot;
  const month = calendar.month as WeatherMonth;
  if (month !== snapshot.activeMonth) snapshot = { ...snapshot, activeMonth: month, streams: monthStreams(simulation.run, month) };
  const monthModel = modelMonth(simulation.model, month);
  const macroRandom = new WeatherRandom(snapshot.streams.macro);
  const localRandom = new WeatherRandom(snapshot.streams.local);
  const emissionRandom = new WeatherRandom(snapshot.streams.emissions);
  const tuning = tuningFor(simulation.run);
  const macro = chooseMacro(snapshot, monthModel, macroRandom, tuning);
  const initialCondition = chooseCondition(snapshot, monthModel, macro.id, localRandom, tuning);
  const normal = blendedNormal(simulation.model, calendar, tuning.hourlyNormalSmoothingRadius);
  const macroModel = definition(monthModel, macro.id);
  const temperatureAr1 = tuning.temperatureAr1 ?? monthModel.emissions.temperatureAr1;
  const dewPointAr1 = tuning.dewPointAr1 ?? monthModel.emissions.dewPointAr1;
  const residualScale = normal.temperatureStdDevC * Math.sqrt(1 - temperatureAr1 ** 2) * tuning.temperatureVolatilityMultiplier;
  const temperatureResidualC = macro.changed ? emissionRandom.normal(0, residualScale) :
    temperatureAr1 * snapshot.temperatureResidualC + emissionRandom.normal(0, residualScale);
  const independentDew = emissionRandom.normal(0, normal.dewPointStdDevC * Math.sqrt(1 - dewPointAr1 ** 2));
  const correlatedDew = monthModel.emissions.temperatureDewPointCorrelation * temperatureResidualC +
    Math.sqrt(1 - monthModel.emissions.temperatureDewPointCorrelation ** 2) * independentDew;
  const dewPointResidualC = macro.changed ? correlatedDew : dewPointAr1 * snapshot.dewPointResidualC + correlatedDew;
  const rawTemperatureC = normal.temperatureC + macroModel.temperatureAnomalyC + temperatureResidualC;
  const temperatureC = tuning.temperatureResponse === 1 ? rawTemperatureC
    : snapshot.previousTemperatureC + (rawTemperatureC - snapshot.previousTemperatureC) * tuning.temperatureResponse;
  const dewPointC = Math.min(temperatureC, normal.dewPointC + macroModel.dewPointAnomalyC + dewPointResidualC);
  const pressureHpa = clamp(normal.pressureHpa + macroModel.pressureAnomalyHpa + emissionRandom.normal(0, 1.4), 850, 1050);
  const humidity = relativeHumidityPct(temperatureC, dewPointC);
  const wetBulbC = wetBulbTemperatureC(temperatureC, humidity, pressureHpa);
  const precipitationDraw = PRECIPITATING.has(initialCondition)
    ? emissionRandom.gamma(monthModel.emissions.precipitationShape, monthModel.emissions.precipitationScaleMm) : 0;
  const precipitationMm = PRECIPITATING.has(initialCondition)
    ? precipitationDraw * macroModel.precipitationMultiplier * tuning.precipitationIntensityMultiplier : 0;
  const phase = precipitationPhase(temperatureC, wetBulbC, precipitationMm);
  const condition = physicalCondition(initialCondition, phase, precipitationMm);
  const snowFraction = phase === 'snow' ? 1 : phase === 'mixed' ? 0.45 : 0;
  const snowfallCm = precipitationMm * clamp(monthModel.emissions.snowfallRatio - temperatureC * 0.35, 6, 20) * snowFraction / 10;
  const sampledWindSpeedKph = emissionRandom.gamma(monthModel.emissions.windShape, monthModel.emissions.windScaleKph);
  const windTargetKph = clamp(
    (normal.windSpeedKph * 0.8 + sampledWindSpeedKph * 0.2) * macroModel.windSpeedMultiplier * tuning.windSeverityMultiplier * 0.82,
    0,
    normal.windSpeedKph * 2.4 + 5,
  );
  const priorWindSpeedKph = snapshot.windSpeedKph ?? normal.windSpeedKph * tuning.windSeverityMultiplier * 0.82;
  const windResponse = macro.changed ? 0.2 : 0.12;
  const windSpeedKph = priorWindSpeedKph + (windTargetKph - priorWindSpeedKph) * windResponse;
  const directionTargetDeg = (normal.windDirectionDeg + emissionRandom.normal(0, macro.id === 'frontal' ? 18 : 7) + 360) % 360;
  const windDirectionDeg = (snapshot.windDirectionDeg
    + angularDifference(snapshot.windDirectionDeg, directionTargetDeg) * (macro.id === 'frontal' ? 0.22 : 0.12) + 360) % 360;
  const windGustKph = windSpeedKph * clamp(monthModel.emissions.gustFactor + emissionRandom.normal(0, 0.08), 1, 2.2);
  const cloudCoverPct = cloudFor(condition, normal.cloudCoverPct, emissionRandom);
  const shortwaveRadiationWm2 = normal.clearSkyRadiationWm2 * clamp(1 - 0.75 * (cloudCoverPct / 100) ** 3.4, 0, 1);
  const visibilityKm = clamp(normal.visibilityKm * (1 - cloudCoverPct / 180) /
    (1 + precipitationMm * (condition === 'heavy-snow' ? 1.8 : 0.45)), 0.1, 50);
  const hazards: WeatherHazard[] = [];
  if (visibilityKm <= 1 && humidity >= 95) hazards.push('fog');
  if (windSpeedKph >= 40 || windGustKph >= 60) hazards.push('high-wind');
  if (phase === 'freezing-rain' || (phase === 'mixed' && wetBulbC <= 0)) hazards.push('icing');
  if (macro.id === 'frontal') hazards.push('frontal-passage');
  const baseHour = {
    at: calendar.at, localDateTime: calendar.localDateTime, utcOffsetMinutes: calendar.utcOffsetMinutes, fold: calendar.fold,
    macroAirMass: macro.id, condition, hazards,
    temperatureC: quantize(temperatureC, 2), dewPointC: quantize(dewPointC, 2), pressureHpa: quantize(pressureHpa, 1),
    relativeHumidityPct: quantize(humidity, 1), wetBulbC: quantize(wetBulbC, 2), precipitationMm: quantize(precipitationMm, 3),
    precipitationPhase: phase, snowfallCm: quantize(snowfallCm, 2), windSpeedKph: quantize(windSpeedKph, 1),
    windDirectionDeg: quantize(windDirectionDeg, 1), windGustKph: quantize(windGustKph, 1),
    shortwaveRadiationWm2: quantize(shortwaveRadiationWm2, 1), cloudCoverPct: quantize(cloudCoverPct, 1),
    visibilityKm: quantize(visibilityKm, 2),
  } satisfies Omit<SimulatedWeatherHourV1, 'bands'>;
  const bands = simulation.run.location.bands;
  const hour: SimulatedWeatherHourV1 = bands ? { ...baseHour, bands: {
    base: elevationBand(baseHour, simulation.run.location.comparisonElevationM, bands.baseElevationM),
    mid: elevationBand(baseHour, simulation.run.location.comparisonElevationM, bands.midElevationM),
    summit: elevationBand(baseHour, simulation.run.location.comparisonElevationM, bands.summitElevationM),
  } } : baseHour;
  const nextSnapshot: WeatherEngineSnapshotV2 = { ...snapshot, nextHourIndex: snapshot.nextHourIndex + 1,
    macroAirMass: macro.id, macroHoursRemaining: macro.duration - 1, localCondition: condition,
    temperatureResidualC: quantize(temperatureResidualC, 6), dewPointResidualC: quantize(dewPointResidualC, 6),
    pressureHpa: hour.pressureHpa, windSpeedKph: hour.windSpeedKph, windDirectionDeg: hour.windDirectionDeg,
    previousMoisture: quantize(snapshot.previousMoisture * 0.85 + precipitationMm, 6), previousTemperatureC: hour.temperatureC,
    streams: { macro: macroRandom.snapshot(), local: localRandom.snapshot(), emissions: emissionRandom.snapshot(), forecastError: snapshot.streams.forecastError } };
  return { simulation: Object.freeze({ ...simulation, snapshot: nextSnapshot }), hour };
}

export function advanceWeatherTo(simulation: WeatherSimulationV2, endExclusiveIndex: number): { simulation: WeatherSimulationV2; hours: readonly SimulatedWeatherHourV1[] } {
  if (!Number.isInteger(endExclusiveIndex) || endExclusiveIndex < simulation.snapshot.nextHourIndex || endExclusiveIndex > simulation.calendar.length) throw new Error('Weather advance target is invalid');
  const hours: SimulatedWeatherHourV1[] = [];
  let current = simulation;
  while (current.snapshot.nextHourIndex < endExclusiveIndex) {
    const advanced = advanceWeatherHour(current);
    current = advanced.simulation; hours.push(advanced.hour);
  }
  return { simulation: current, hours };
}

export function generateWeatherYear(run: WeatherLabRunRequest, model: LocationClimateModelV1,
  options: { onProgress?: (completedHours: number, totalHours: number) => void; shouldCancel?: () => boolean } = {}): GeneratedWeatherYearV2 {
  let simulation = createWeatherSimulation(run, model);
  const hours: SimulatedWeatherHourV1[] = [];
  while (simulation.snapshot.nextHourIndex < simulation.calendar.length) {
    if (options.shouldCancel?.()) throw new Error('Weather generation cancelled');
    const target = Math.min(simulation.calendar.length, simulation.snapshot.nextHourIndex + 168);
    const advanced = advanceWeatherTo(simulation, target);
    simulation = advanced.simulation; hours.push(...advanced.hours);
    options.onProgress?.(hours.length, simulation.calendar.length);
  }
  return { runIdentityHash: simulation.snapshot.runIdentityHash, hours, snapshot: createWeatherSnapshot(simulation) };
}

function forecastRandom(run: WeatherLabRunRequest, month: WeatherMonth, state?: WeatherRandomStateV1): WeatherRandom {
  return state ? new WeatherRandom(state) : new WeatherRandom(monthStreams(run, month).forecastError);
}

export function issueForecast(run: WeatherLabRunRequest, truth: readonly SimulatedWeatherHourV1[], issuedIndex: number,
  state?: WeatherRandomStateV1): { issue: ForecastIssueV1; state: WeatherRandomStateV1 } {
  const issued = truth[issuedIndex];
  if (!issued) throw new Error('Forecast issue index is outside generated truth');
  const month = Number(issued.localDateTime.slice(5, 7)) as WeatherMonth;
  const random = forecastRandom(run, month, state);
  const multiplier = tuningFor(run).forecastErrorMultiplier;
  const hourly: ForecastHourV1[] = truth.slice(issuedIndex, issuedIndex + 168).map((hour, leadHours) => {
    const uncertainty = (0.35 + leadHours * 0.025) * multiplier;
    const temperatureC = hour.temperatureC + random.normal(0, uncertainty);
    const dewPointC = Math.min(temperatureC, hour.dewPointC + random.normal(0, uncertainty * 0.55));
    const relativeHumidity = relativeHumidityPct(temperatureC, dewPointC);
    const wetBulbC = Math.min(temperatureC, wetBulbTemperatureC(temperatureC, relativeHumidity, hour.pressureHpa));
    const precipitationMm = Math.max(0, hour.precipitationMm * Math.exp(random.normal(0, uncertainty * 0.12)) + random.normal(0, uncertainty * 0.025));
    const phase = precipitationPhase(temperatureC, wetBulbC, precipitationMm);
    const snowfallCm = snowfallCentimetresFromLiquid(precipitationMm, temperatureC, phase);
    const windSpeedKph = Math.max(0, hour.windSpeedKph + random.normal(0, uncertainty * 1.2));
    const windGustKph = Math.max(windSpeedKph, hour.windGustKph + random.normal(0, uncertainty * 1.5));
    const roundedWindSpeedKph = quantize(windSpeedKph, 1);
    const roundedWindGustKph = Math.max(roundedWindSpeedKph, quantize(windGustKph, 1));
    const cloudCoverPct = clamp(hour.cloudCoverPct + random.normal(0, uncertainty * 3), 0, 100);
    return { at: hour.at, leadHours, confidencePct: quantize(clamp(96 - leadHours * 0.32 * multiplier, 35, 96), 1),
      temperatureC: quantize(temperatureC, 1), dewPointC: quantize(dewPointC, 1), wetBulbC: quantize(wetBulbC, 1),
      relativeHumidityPct: quantize(relativeHumidity, 1), precipitationMm: quantize(precipitationMm, 2), precipitationPhase: phase,
      snowfallCm: quantize(snowfallCm, 2), windSpeedKph: roundedWindSpeedKph, windGustKph: roundedWindGustKph,
      cloudCoverPct: quantize(cloudCoverPct, 1),
      condition: physicalCondition(hour.condition, phase, precipitationMm) };
  });
  const daily: ForecastDayV1[] = [];
  for (let offset = 168; offset < 504; offset += 24) {
    const selected = truth.slice(issuedIndex + offset, issuedIndex + offset + 24);
    if (!selected.length) break;
    const uncertainty = (2 + offset * 0.012) * multiplier;
    daily.push({ date: selected[0].localDateTime.slice(0, 10), confidencePct: quantize(clamp(75 - (offset - 168) * 0.09 * multiplier, 20, 75), 1),
      minTemperatureC: quantize(Math.min(...selected.map((hour) => hour.temperatureC)) + random.normal(0, uncertainty), 1),
      maxTemperatureC: quantize(Math.max(...selected.map((hour) => hour.temperatureC)) + random.normal(0, uncertainty), 1),
      precipitationMm: quantize(Math.max(0, selected.reduce((sum, hour) => sum + hour.precipitationMm, 0) * Math.exp(random.normal(0, 0.25 * multiplier))), 1),
      snowfallCm: quantize(Math.max(0, selected.reduce((sum, hour) => sum + hour.snowfallCm, 0) * Math.exp(random.normal(0, 0.3 * multiplier))), 1),
      highWindRiskPct: quantize(clamp(selected.filter((hour) => hour.hazards.includes('high-wind')).length / selected.length * 100 + random.normal(0, 8 * multiplier), 0, 100), 1) });
  }
  const signals = daily.filter((day) => day.precipitationMm >= 20).slice(0, 2).map((day) => `Elevated precipitation potential during the week of ${day.date.slice(0, 7)}`);
  return { issue: { version: 1, issuedAt: issued.at, hourly, daily, signals }, state: random.snapshot() };
}

export function generateForecastIssues(run: WeatherLabRunRequest, truth: readonly SimulatedWeatherHourV1[]): { issues: readonly ForecastIssueV1[]; finalState: WeatherRandomStateV1 } {
  const issues: ForecastIssueV1[] = [];
  let activeMonth = 0;
  let state: WeatherRandomStateV1 | undefined;
  for (let index = 0; index < truth.length; index += 1) {
    const hour = truth[index];
    const localHour = Number(hour.localDateTime.slice(11, 13));
    if (localHour !== 6 && localHour !== 18) continue;
    const month = Number(hour.localDateTime.slice(5, 7));
    if (month !== activeMonth) { activeMonth = month; state = undefined; }
    const forecast = issueForecast(run, truth, index, state);
    issues.push(forecast.issue); state = forecast.state;
  }
  return { issues, finalState: state ?? monthStreams(run, 12).forecastError };
}

export function serializedWeatherYear(year: GeneratedWeatherYearV2): string {
  return canonicalJson({ runIdentityHash: year.runIdentityHash, hours: year.hours, snapshot: year.snapshot });
}
