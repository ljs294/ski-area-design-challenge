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
  completeness: Readonly<Record<WeatherVariable, number>>;
  observationHash: string;
  provenance: Readonly<{
    providers: readonly string[];
    sourceIds: readonly string[];
    retrievedAt?: string;
    warnings: readonly string[];
  }>;
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
  precipitationMm: number;
  precipitationPhase: PrecipitationPhase;
  windSpeedKph: number;
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
