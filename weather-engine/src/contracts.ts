export const WEATHER_ENGINE_ID = 'mountain-planner-weather-v2' as const;
export const WEATHER_GENERATOR_VERSION = 2 as const;

export type WeatherMonth = 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12;
export type WeatherCondition =
  | 'clear'
  | 'partly-cloudy'
  | 'overcast'
  | 'flurries'
  | 'snow'
  | 'heavy-snow'
  | 'mixed'
  | 'freezing-rain'
  | 'rain';
export type WeatherHazard = 'fog' | 'high-wind' | 'icing' | 'frontal-passage';
export type PrecipitationPhase = 'none' | 'rain' | 'mixed' | 'snow' | 'freezing-rain';
export type MacroAirMassId = 'arctic' | 'continental-polar' | 'maritime-polar' | 'warm-wet' | 'frontal';
export type WeatherEventType = 'storm' | 'cold-snap' | 'warm-up' | 'dry-spell';
export type WeatherEventSeverity = 'minor' | 'notable' | 'major';
export type StormStyle =
  | 'pacific-system'
  | 'atmospheric-river'
  | 'nor-easter'
  | 'clipper'
  | 'lake-effect'
  | 'upslope'
  | 'frontal'
  | 'tropical-remnant'
  | 'convective';
export type StormStyleConfidence = 'low' | 'moderate' | 'high';
export type WeatherVariable =
  | 'temperatureC'
  | 'dewPointC'
  | 'pressureHpa'
  | 'relativeHumidityPct'
  | 'wetBulbC'
  | 'precipitationMm'
  | 'snowfallCm'
  | 'windSpeedKph'
  | 'windDirectionDeg'
  | 'windGustKph'
  | 'shortwaveRadiationWm2'
  | 'cloudCoverPct'
  | 'visibilityKm'
  | 'condition';

export interface WeatherLocationV1 {
  id: string;
  name: string;
  latitude: number;
  longitude: number;
  comparisonElevationM: number;
  bands?: Readonly<{
    baseElevationM: number;
    midElevationM: number;
    summitElevationM: number;
  }>;
}

export interface ObservingStationMetadataV1 {
  id: string;
  sourceIds: readonly string[];
  name: string;
  latitude: number;
  longitude: number;
  elevationM: number;
  timezone: string;
  distanceKm: number;
  score: number;
  scoreComponents?: Readonly<{
    coreFieldCompleteness: number;
    distance: number;
    elevationMatch: number;
    trainingOverlap: number;
  }>;
  availableYears?: readonly number[];
}

export interface WeatherLabLocationContextV1 {
  version: 1;
  latitude: number;
  longitude: number;
  coverage: 'supported' | 'unsupported';
  coverageReason?: string;
  resolvedElevationM: number | null;
  elevationSource: 'daymet' | 'fixture' | 'unavailable';
  timezone: string | null;
  timezoneResolution: 'coordinate' | 'fixture' | 'unavailable';
  stations: readonly ObservingStationMetadataV1[];
  selectedStation: ObservingStationMetadataV1 | null;
  eligibleValidationYears: readonly number[];
  warnings: readonly string[];
}

export type TrainingPeriodPolicyV1 =
  | Readonly<{ kind: 'prior-30' }>
  | Readonly<{ kind: 'leave-one-out-1991-2020' }>
  | Readonly<{ kind: 'fixed'; startYear: number; endYear: number }>;

export interface WeatherDifficultyProfileV1 {
  version: 1;
  id: string;
  stormArrivalMultiplier: number;
  stormPersistenceMultiplier: number;
  precipitationIntensityMultiplier: number;
  warmIntrusionMultiplier: number;
  coldOutbreakMultiplier: number;
  temperatureVolatilityMultiplier: number;
  windSeverityMultiplier: number;
  extremeEventMultiplier: number;
  forecastErrorMultiplier: number;
}

/**
 * A versioned, explicit overlay used by the standalone Weather Model Lab.
 * Omitting this overlay retains the original generator-v2 behavior.
 */
export interface WeatherSimulationTuningV1 {
  version: 1;
  id: string;
  stormArrivalMultiplier: number;
  macroDurationMultiplier: number;
  conditionPersistenceMultiplier: number;
  precipitationIntensityMultiplier: number;
  warmIntrusionMultiplier: number;
  coldOutbreakMultiplier: number;
  temperatureVolatilityMultiplier: number;
  temperatureAr1: number | null;
  dewPointAr1: number | null;
  hourlyNormalSmoothingRadius: number;
  temperatureResponse: number;
  windSeverityMultiplier: number;
  extremeEventMultiplier: number;
  forecastErrorMultiplier: number;
}

export interface WeatherLabRunRequestV1 {
  version: 1;
  location: WeatherLocationV1;
  stationId: string;
  stationTimeZone: string;
  validationYear: number;
  trainingPolicy: TrainingPeriodPolicyV1;
  worldSeed: string;
  difficultyProfile: WeatherDifficultyProfileV1;
  generatorVersion: 2;
  climateModelHash: string;
}

export interface WeatherLabRunRequestV2 extends Omit<WeatherLabRunRequestV1, 'version'> {
  version: 2;
  tuning: WeatherSimulationTuningV1;
  /** Keeps baseline/candidate random draws paired without hiding tuning from run identity. */
  comparisonStreamKey: string;
}

export type WeatherLabRunRequest = WeatherLabRunRequestV1 | WeatherLabRunRequestV2;

export interface WeatherLabPreparationRequestV1 {
  version: 1;
  latitude: number;
  longitude: number;
  elevationOverrideM?: number;
  validationYear: number;
  trainingPolicy: TrainingPeriodPolicyV1;
}

export interface WeatherLabPreparationV1 {
  version: 1;
  id: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  progress: Readonly<{
    stage: string;
    completed: number;
    total: number;
    message?: string;
    detailCompleted?: number;
    detailTotal?: number;
  }>;
  events?: readonly Readonly<{
    sequence: number;
    at: string;
    stage: string;
    message: string;
  }>[];
  context?: WeatherLabLocationContextV1;
  result?: Readonly<{
    modelHash: string;
    observationHash: string;
    modelUrl: string;
    observedSeriesUrl: string;
  }>;
  error?: Readonly<{ code: string; message: string; retryable?: boolean }>;
  createdAt: string;
  updatedAt?: string;
}

export type ObservationQuality = 'accepted' | 'suspect' | 'missing';

export interface ObservedWeatherHourV1 {
  at: string;
  localDateTime: string;
  utcOffsetMinutes: number;
  fold: 0 | 1;
  temperatureC: number | null;
  dewPointC: number | null;
  pressureHpa: number | null;
  relativeHumidityPct: number | null;
  wetBulbC: number | null;
  precipitationMm: number | null;
  precipitationPhase: PrecipitationPhase | null;
  snowfallCm: number | null;
  windSpeedKph: number | null;
  windDirectionDeg: number | null;
  windGustKph: number | null;
  shortwaveRadiationWm2: number | null;
  cloudCoverPct: number | null;
  visibilityKm: number | null;
  condition: WeatherCondition | null;
  hazards: readonly WeatherHazard[];
  quality: Readonly<Partial<Record<WeatherVariable, ObservationQuality>>>;
}

export interface HistoricalWeatherSeriesV1 {
  version: 1;
  station: ObservingStationMetadataV1;
  validationYear: number;
  startInclusive: string;
  endExclusive: string;
  hours: readonly ObservedWeatherHourV1[];
  days?: readonly ObservedWeatherDayV1[];
  completeness: Readonly<Record<WeatherVariable, number>>;
  observationHash: string;
  provenance: Readonly<{
    providers: readonly string[];
    sourceIds: readonly string[];
    retrievedAt?: string;
    warnings: readonly string[];
  }>;
}

export interface ObservedWeatherDayV1 {
  localDate: string;
  minimumTemperatureC: number | null;
  maximumTemperatureC: number | null;
  precipitationMm: number | null;
  snowfallCm: number | null;
  snowDepthCm: number | null;
  /** Optional for compatibility with daily artifacts prepared before explicit snow provenance. */
  snowfallKind?: 'observed' | 'derived' | 'unavailable';
  sources: Readonly<{
    temperature: string | null;
    precipitation: string | null;
    snowfall: string | null;
  }>;
}

export interface WeatherEventThresholdMonthV1 {
  month: WeatherMonth;
  stormMinimumTotalMm: number;
  stormMinimumDurationHours: number;
  rapidTemperatureChangeC: number;
  temperatureMaintenanceHours: number;
  drySpellMinimumHours: number;
  stormSeverity: Readonly<{
    totalP90Mm: number;
    totalP98Mm: number;
    peakP90Mm: number;
    peakP98Mm: number;
    durationP90Hours: number;
    durationP98Hours: number;
  }>;
  temperatureSeverity: Readonly<{
    changeP90C: number;
    changeP98C: number;
    durationP90Hours: number;
    durationP98Hours: number;
  }>;
  drySeverity: Readonly<{
    durationP90Hours: number;
    durationP98Hours: number;
  }>;
  /** Sorted qualifying training-event samples used to report empirical event percentiles. */
  empiricalDistributions?: Readonly<{
    stormTotalMm: readonly number[];
    stormPeakHourlyMm: readonly number[];
    stormDurationHours: readonly number[];
    temperatureChangeC: readonly number[];
    temperatureDurationHours: readonly number[];
    drySpellDurationHours: readonly number[];
  }>;
  sampleCounts: Readonly<{ wetSpells: number; temperatureChanges: number; drySpells: number }>;
}

export interface WeatherEventThresholdModelV1 {
  version: 1;
  measurablePrecipitationMm: 0.005;
  stormDryGapHours: 3;
  months: readonly WeatherEventThresholdMonthV1[];
  fittedFromYears: readonly number[];
}

export interface MacroAirMassDefinitionV1 {
  id: MacroAirMassId;
  meanDurationHours: number;
  durationStdDevHours: number;
  temperatureAnomalyC: number;
  dewPointAnomalyC: number;
  pressureAnomalyHpa: number;
  windSpeedMultiplier: number;
  precipitationMultiplier: number;
}

export interface HourlyNormalV1 {
  temperatureC: number;
  temperatureStdDevC: number;
  dewPointC: number;
  dewPointStdDevC: number;
  pressureHpa: number;
  relativeHumidityPct: number;
  windSpeedKph: number;
  windDirectionDeg: number;
  cloudCoverPct: number;
  visibilityKm: number;
  clearSkyRadiationWm2: number;
}

export interface MonthlyClimateModelV1 {
  month: WeatherMonth;
  hourlyNormals: readonly HourlyNormalV1[];
  macro: Readonly<{
    states: readonly MacroAirMassDefinitionV1[];
    transitionMatrix: readonly (readonly number[])[];
  }>;
  local: Readonly<{
    states: readonly WeatherCondition[];
    hourlyMatricesByMacro: Readonly<Record<MacroAirMassId, readonly (readonly number[])[]>>;
    backoffRows: readonly number[];
  }>;
  emissions: Readonly<{
    temperatureAr1: number;
    dewPointAr1: number;
    temperatureDewPointCorrelation: number;
    precipitationShape: number;
    precipitationScaleMm: number;
    snowfallRatio: number;
    windShape: number;
    windScaleKph: number;
    gustFactor: number;
  }>;
  sampleCounts: Readonly<Record<string, number>>;
}

export interface LocationClimateModelV1 {
  version: 1;
  generatorVersion: 2;
  location: WeatherLocationV1;
  primaryStation: ObservingStationMetadataV1;
  trainingStations: readonly ObservingStationMetadataV1[];
  trainingPeriod: Readonly<{ years: readonly number[]; policy: TrainingPeriodPolicyV1 }>;
  excludedValidationYear: number;
  months: readonly MonthlyClimateModelV1[];
  eventThresholds?: WeatherEventThresholdModelV1;
  sourceHash: string;
  climateModelHash: string;
  provenance: Readonly<{
    compilerVersion: string;
    providers: readonly string[];
    sourceHashes: readonly string[];
    warnings: readonly string[];
  }>;
}

export interface SimulatedElevationBandV1 {
  elevationM: number;
  temperatureC: number;
  wetBulbC: number;
  pressureHpa: number;
  precipitationMm: number;
  snowfallCm: number;
  windSpeedKph: number;
}

export interface SimulatedWeatherHourV1 {
  at: string;
  localDateTime: string;
  utcOffsetMinutes: number;
  fold: 0 | 1;
  macroAirMass: MacroAirMassId;
  condition: WeatherCondition;
  hazards: readonly WeatherHazard[];
  temperatureC: number;
  dewPointC: number;
  pressureHpa: number;
  relativeHumidityPct: number;
  wetBulbC: number;
  precipitationMm: number;
  precipitationPhase: PrecipitationPhase;
  snowfallCm: number;
  windSpeedKph: number;
  windDirectionDeg: number;
  windGustKph: number;
  shortwaveRadiationWm2: number;
  cloudCoverPct: number;
  visibilityKm: number;
  bands?: Readonly<{
    base: SimulatedElevationBandV1;
    mid: SimulatedElevationBandV1;
    summit: SimulatedElevationBandV1;
  }>;
}

export interface WeatherRandomStateV1 {
  state: readonly [number, number, number, number];
  draws: number;
  normalSpare: number | null;
}

export interface WeatherEngineSnapshotV2 {
  schemaVersion: 2;
  generatorVersion: 2;
  runIdentityHash: string;
  nextHourIndex: number;
  activeMonth: WeatherMonth;
  macroAirMass: MacroAirMassId;
  macroHoursRemaining: number;
  localCondition: WeatherCondition;
  temperatureResidualC: number;
  dewPointResidualC: number;
  pressureHpa: number;
  /** Optional so snapshots created before wind persistence was introduced still restore. */
  windSpeedKph?: number;
  windDirectionDeg: number;
  previousMoisture: number;
  previousTemperatureC: number;
  streams: Readonly<{
    macro: WeatherRandomStateV1;
    local: WeatherRandomStateV1;
    emissions: WeatherRandomStateV1;
    forecastError: WeatherRandomStateV1;
  }>;
}

export interface ForecastHourV1 {
  at: string;
  leadHours: number;
  confidencePct: number;
  temperatureC: number;
  dewPointC?: number;
  wetBulbC?: number;
  relativeHumidityPct?: number;
  precipitationMm: number;
  precipitationPhase: PrecipitationPhase;
  snowfallCm?: number;
  windSpeedKph: number;
  windGustKph?: number;
  cloudCoverPct?: number;
  condition: WeatherCondition;
}

export interface ForecastDayV1 {
  date: string;
  confidencePct: number;
  minTemperatureC: number;
  maxTemperatureC: number;
  precipitationMm: number;
  snowfallCm: number;
  highWindRiskPct: number;
}

export interface ForecastIssueV1 {
  version: 1;
  issuedAt: string;
  hourly: readonly ForecastHourV1[];
  daily: readonly ForecastDayV1[];
  signals: readonly string[];
}

export type ComparisonStatus = 'pass' | 'warn' | 'unavailable';

export interface WeatherMetricComparisonV1 {
  variable: WeatherVariable | 'freezeThawCycles' | 'snowmakingHours' | 'stormHours';
  metric: string;
  simulated: number | null;
  observed: number | null;
  difference: number | null;
  status: ComparisonStatus;
}

export interface MonthlyComparisonV1 {
  month: WeatherMonth;
  metrics: readonly WeatherMetricComparisonV1[];
}

export interface WeatherDiagnosticsV1 {
  conditionOccupancy: Readonly<Record<WeatherCondition, number>>;
  macroOccupancy: Readonly<Record<MacroAirMassId, number>>;
  transitionCounts: Readonly<Record<string, number>>;
  spellLengths: Readonly<Record<WeatherCondition, readonly number[]>>;
}

export interface WeatherDiagnosticsV2 extends WeatherDiagnosticsV1 {
  macroTransitionCounts: Readonly<Record<string, number>>;
  macroSpellLengths: Readonly<Record<MacroAirMassId, readonly number[]>>;
}

export interface WeatherConditionDiagnosticsV1 {
  conditionOccupancy: Readonly<Record<WeatherCondition, number>>;
  transitionCounts: Readonly<Record<string, number>>;
  spellLengths: Readonly<Record<WeatherCondition, readonly number[]>>;
}

export interface WeatherDailySummaryV1 {
  localDate: string;
  expectedHours: number;
  availableHours: number;
  completeness: Readonly<Partial<Record<WeatherVariable, number>>>;
  temperatureC: Readonly<{ minimum: number; mean: number; maximum: number }> | null;
  wetBulbC: Readonly<{ minimum: number; mean: number; maximum: number }> | null;
  snowmakingHours: number | null;
  precipitationMm: number | null;
  precipitationByPhaseMm: Readonly<Record<PrecipitationPhase, number>> | null;
  snowfallCm: number | null;
  snowfallSource: 'simulated' | 'observed' | 'derived' | 'unavailable';
  conditionHours: Readonly<Partial<Record<WeatherCondition, number>>>;
  dominantCondition: WeatherCondition | null;
  hazards: readonly WeatherHazard[];
  macroHours: Readonly<Partial<Record<MacroAirMassId, number>>> | null;
  dominantMacro: MacroAirMassId | null;
  eventIds: readonly string[];
}

export interface WeatherEventV1 {
  version: 1;
  id: string;
  type: WeatherEventType;
  startsAt: string;
  endsAt: string;
  localStartDate: string;
  localEndDate: string;
  durationHours: number;
  severity: WeatherEventSeverity;
  /** Percentile rank on a 0..100 scale. */
  intensityPercentile: number;
  totalPrecipitationMm: number;
  peakPrecipitationMm: number;
  precipitationByPhaseMm: Readonly<Record<PrecipitationPhase, number>>;
  snowfallCm: number;
  temperatureChangeC: number;
  meanWindSpeedKph: number | null;
  peakWindGustKph: number | null;
  pressureChangeHpa: number | null;
  stormStyle: StormStyle | null;
  styleConfidence: StormStyleConfidence | null;
  styleEvidence: readonly string[];
}

export interface WeatherComparisonScoresV1 {
  temperatureMeanBiasC: number | null;
  temperatureMeanMaeC: number | null;
  wetBulbMeanBiasC: number | null;
  wetBulbMeanMaeC: number | null;
  precipitationBiasMm: number | null;
  precipitationMaeMm: number | null;
  dominantConditionAgreement: number | null;
  eventCountDifference: number;
  eventDurationDifferenceHours: number;
  eventOverlapScore: number | null;
  stormSeverityAgreement: number | null;
  stormStyleAgreement: number | null;
}

export interface WeatherLabResultV1 {
  version: 1;
  runIdentityHash: string;
  truthHash: string;
  comparisonHash: string;
  run: WeatherLabRunRequestV1;
  simulated: readonly SimulatedWeatherHourV1[];
  observed: HistoricalWeatherSeriesV1;
  forecasts: readonly ForecastIssueV1[];
  monthly: readonly MonthlyComparisonV1[];
  annual: readonly WeatherMetricComparisonV1[];
  diagnostics: WeatherDiagnosticsV1;
  warnings: readonly string[];
  finalSnapshot: WeatherEngineSnapshotV2;
}

export interface WeatherLabResultV2 extends Omit<WeatherLabResultV1, 'version' | 'run' | 'diagnostics'> {
  version: 2;
  run: WeatherLabRunRequestV2;
  diagnostics: WeatherDiagnosticsV2;
  daily: Readonly<{
    simulated: readonly WeatherDailySummaryV1[];
    observed: readonly WeatherDailySummaryV1[];
  }>;
  events: Readonly<{
    simulated: readonly WeatherEventV1[];
    observed: readonly WeatherEventV1[];
  }>;
  observedDiagnostics: WeatherConditionDiagnosticsV1;
  eventThresholds: WeatherEventThresholdModelV1;
  scores: WeatherComparisonScoresV1;
}

export type WeatherLabResult = WeatherLabResultV1 | WeatherLabResultV2;

export const HISTORICAL_DIFFICULTY: WeatherDifficultyProfileV1 = Object.freeze({
  version: 1, id: 'historical', stormArrivalMultiplier: 1, stormPersistenceMultiplier: 1,
  precipitationIntensityMultiplier: 1, warmIntrusionMultiplier: 1, coldOutbreakMultiplier: 1,
  temperatureVolatilityMultiplier: 1, windSeverityMultiplier: 1, extremeEventMultiplier: 1,
  forecastErrorMultiplier: 1,
});

function profile(id: string, values: readonly number[]): WeatherDifficultyProfileV1 {
  const [stormArrivalMultiplier, stormPersistenceMultiplier, precipitationIntensityMultiplier,
    warmIntrusionMultiplier, coldOutbreakMultiplier, temperatureVolatilityMultiplier,
    windSeverityMultiplier, extremeEventMultiplier, forecastErrorMultiplier] = values;
  return Object.freeze({ version: 1, id, stormArrivalMultiplier, stormPersistenceMultiplier,
    precipitationIntensityMultiplier, warmIntrusionMultiplier, coldOutbreakMultiplier,
    temperatureVolatilityMultiplier, windSeverityMultiplier, extremeEventMultiplier,
    forecastErrorMultiplier });
}

export const WEATHER_DIFFICULTY_PRESETS = Object.freeze({
  mild: profile('mild', [0.75, 0.8, 0.8, 0.85, 0.75, 0.75, 0.75, 0.5, 0.75]),
  historical: HISTORICAL_DIFFICULTY,
  variable: profile('variable', [1.1, 1.1, 1, 1.25, 1.25, 1.35, 1.15, 1.25, 1.2]),
  severe: profile('severe', [1.5, 1.4, 1.5, 1.5, 1.5, 1.4, 1.6, 2, 1.5]),
});

export const WEATHER_SIMULATION_TUNING_LIMITS = Object.freeze({
  stormArrivalMultiplier: [0.5, 2],
  macroDurationMultiplier: [0.5, 2],
  conditionPersistenceMultiplier: [0.5, 3],
  precipitationIntensityMultiplier: [0.5, 2],
  warmIntrusionMultiplier: [0.5, 2],
  coldOutbreakMultiplier: [0.5, 2],
  temperatureVolatilityMultiplier: [0.5, 1.75],
  temperatureAr1: [0.5, 0.98],
  dewPointAr1: [0.5, 0.98],
  hourlyNormalSmoothingRadius: [0, 6],
  temperatureResponse: [0.25, 1],
  windSeverityMultiplier: [0.5, 2],
  extremeEventMultiplier: [0.25, 2.5],
  forecastErrorMultiplier: [0.5, 2],
} satisfies Readonly<Record<Exclude<keyof WeatherSimulationTuningV1, 'version' | 'id'>, readonly [number, number]>>);

export function simulationTuningForDifficulty(
  difficulty: WeatherDifficultyProfileV1,
  id = difficulty.id,
): WeatherSimulationTuningV1 {
  return {
    version: 1,
    id,
    stormArrivalMultiplier: difficulty.stormArrivalMultiplier,
    macroDurationMultiplier: difficulty.stormPersistenceMultiplier,
    conditionPersistenceMultiplier: difficulty.stormPersistenceMultiplier,
    precipitationIntensityMultiplier: difficulty.precipitationIntensityMultiplier,
    warmIntrusionMultiplier: difficulty.warmIntrusionMultiplier,
    coldOutbreakMultiplier: difficulty.coldOutbreakMultiplier,
    temperatureVolatilityMultiplier: difficulty.temperatureVolatilityMultiplier,
    temperatureAr1: null,
    dewPointAr1: null,
    hourlyNormalSmoothingRadius: 0,
    temperatureResponse: 1,
    windSeverityMultiplier: difficulty.windSeverityMultiplier,
    extremeEventMultiplier: difficulty.extremeEventMultiplier,
    forecastErrorMultiplier: difficulty.forecastErrorMultiplier,
  };
}

export const HISTORICAL_SIMULATION_TUNING = Object.freeze(
  simulationTuningForDifficulty(HISTORICAL_DIFFICULTY, 'historical'),
);

export const SMOOTHED_SIMULATION_TUNING = Object.freeze({
  ...HISTORICAL_SIMULATION_TUNING,
  id: 'smoothed',
  temperatureAr1: 0.94,
  dewPointAr1: 0.95,
  hourlyNormalSmoothingRadius: 1,
  temperatureResponse: 0.65,
  conditionPersistenceMultiplier: 1.5,
} satisfies WeatherSimulationTuningV1);
