import {
  WEATHER_SIMULATION_TUNING_LIMITS,
} from '../../weather-engine/src/index.ts';
import type {
  HistoricalWeatherSeriesV1,
  MacroAirMassId,
  ObservedWeatherHourV1,
  PrecipitationPhase,
  SimulatedWeatherHourV1,
  WeatherComparisonScoresV1,
  WeatherCondition,
  WeatherDailySummaryV1,
  WeatherEventV1,
  WeatherLabResult,
  WeatherLabResultV2,
  WeatherSimulationTuningV1,
  WeatherVariable,
} from '../../weather-engine/src/index.ts';

export const WEATHER_LAB_TUNING_STORAGE_KEY = 'mountain-planner.weather-lab.tuning.v1';

export const WEATHER_CONDITIONS = Object.freeze([
  'clear',
  'partly-cloudy',
  'overcast',
  'flurries',
  'snow',
  'heavy-snow',
  'mixed',
  'freezing-rain',
  'rain',
] satisfies readonly WeatherCondition[]);

export const MACRO_AIR_MASSES = Object.freeze([
  'arctic',
  'continental-polar',
  'maritime-polar',
  'warm-wet',
  'frontal',
] satisfies readonly MacroAirMassId[]);

export const PRECIPITATION_PHASES = Object.freeze([
  'rain',
  'mixed',
  'snow',
  'freezing-rain',
] satisfies readonly PrecipitationPhase[]);

export type DailyMetric =
  | 'temperature'
  | 'wet-bulb'
  | 'precipitation'
  | 'snowfall'
  | 'conditions'
  | 'snowmaking'
  | 'macro';

export type DailyNumericMetric = Exclude<DailyMetric, 'conditions' | 'macro'>;

export interface DailyComparisonSeries {
  observed: readonly WeatherDailySummaryV1[];
  baseline?: readonly WeatherDailySummaryV1[];
  candidate: readonly WeatherDailySummaryV1[];
}

export interface DailyComparisonRow {
  localDate: string;
  observed: WeatherDailySummaryV1 | null;
  baseline: WeatherDailySummaryV1 | null;
  candidate: WeatherDailySummaryV1 | null;
}

export interface EventComparisonSeries {
  observed: readonly WeatherEventV1[];
  baseline?: readonly WeatherEventV1[];
  candidate: readonly WeatherEventV1[];
}

export interface HourlyComparisonSeries {
  observed: readonly ObservedWeatherHourV1[];
  baseline?: readonly SimulatedWeatherHourV1[];
  candidate: readonly SimulatedWeatherHourV1[];
}

export interface WeatherComparisonSeries {
  daily: DailyComparisonSeries;
  events: EventComparisonSeries;
  hourly: HourlyComparisonSeries;
}

type TuningNumberKey = Exclude<keyof WeatherSimulationTuningV1, 'version' | 'id'>;

export interface TuningControlDefinition {
  key: TuningNumberKey;
  label: string;
  help: string;
  step: number;
  group: 'Atmosphere' | 'Precipitation and events' | 'Forecast';
  nullable?: boolean;
  fallback: number;
}

export const TUNING_CONTROLS = Object.freeze([
  { key: 'temperatureVolatilityMultiplier', label: 'Temperature volatility', help: 'Scales hourly temperature residual variation.', step: 0.05, group: 'Atmosphere', fallback: 1 },
  { key: 'temperatureAr1', label: 'Temperature persistence', help: 'Overrides the fitted hour-to-hour temperature persistence.', step: 0.01, group: 'Atmosphere', nullable: true, fallback: 0.94 },
  { key: 'dewPointAr1', label: 'Dew-point persistence', help: 'Overrides the fitted hour-to-hour dew-point persistence.', step: 0.01, group: 'Atmosphere', nullable: true, fallback: 0.95 },
  { key: 'hourlyNormalSmoothingRadius', label: 'Normal smoothing radius', help: 'Blends neighboring hourly climate normals before generation.', step: 1, group: 'Atmosphere', fallback: 1 },
  { key: 'temperatureResponse', label: 'Temperature response', help: 'Controls how quickly temperature follows a changing target.', step: 0.05, group: 'Atmosphere', fallback: 0.65 },
  { key: 'macroDurationMultiplier', label: 'Air-mass duration', help: 'Scales the duration of generated macro air masses.', step: 0.05, group: 'Atmosphere', fallback: 1 },
  { key: 'conditionPersistenceMultiplier', label: 'Condition persistence', help: 'Scales the tendency for a local condition to continue.', step: 0.05, group: 'Atmosphere', fallback: 1 },
  { key: 'stormArrivalMultiplier', label: 'Storm arrival', help: 'Scales transitions into precipitation-producing weather.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'precipitationIntensityMultiplier', label: 'Precipitation intensity', help: 'Scales liquid-equivalent precipitation intensity.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'warmIntrusionMultiplier', label: 'Warm intrusions', help: 'Scales transitions into warm, wet air masses.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'coldOutbreakMultiplier', label: 'Cold outbreaks', help: 'Scales transitions into arctic air masses.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'windSeverityMultiplier', label: 'Wind severity', help: 'Scales generated wind speed and gust severity.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'extremeEventMultiplier', label: 'Extreme-event frequency', help: 'Scales the frequency of extreme atmospheric draws.', step: 0.05, group: 'Precipitation and events', fallback: 1 },
  { key: 'forecastErrorMultiplier', label: 'Forecast error', help: 'Scales forecast uncertainty without changing truth.', step: 0.05, group: 'Forecast', fallback: 1 },
] satisfies readonly TuningControlDefinition[]);

export function isWeatherLabResultV2(result: WeatherLabResult): result is WeatherLabResultV2 {
  return result.version === 2;
}

export function alignDailyComparison(series: DailyComparisonSeries, month?: number): readonly DailyComparisonRow[] {
  const indexes = {
    observed: new Map(series.observed.map((day) => [day.localDate, day])),
    baseline: new Map((series.baseline ?? []).map((day) => [day.localDate, day])),
    candidate: new Map(series.candidate.map((day) => [day.localDate, day])),
  };
  const dates = new Set<string>();
  for (const index of Object.values(indexes)) for (const date of index.keys()) dates.add(date);
  return [...dates]
    .filter((date) => month == null || Number(date.slice(5, 7)) === month)
    .sort()
    .map((localDate) => ({
      localDate,
      observed: indexes.observed.get(localDate) ?? null,
      baseline: indexes.baseline.get(localDate) ?? null,
      candidate: indexes.candidate.get(localDate) ?? null,
    }));
}

export interface DailyNumericValue {
  minimum: number;
  mean: number;
  maximum: number;
}

export function dailyNumericValue(day: WeatherDailySummaryV1 | null, metric: DailyNumericMetric): DailyNumericValue | null {
  if (!day) return null;
  if (metric === 'temperature') return day.temperatureC;
  if (metric === 'wet-bulb') return day.wetBulbC;
  const value = metric === 'precipitation' ? day.precipitationMm
    : metric === 'snowfall' ? day.snowfallCm : day.snowmakingHours;
  return value == null ? null : { minimum: 0, mean: value, maximum: value };
}

export function completenessForMetric(day: WeatherDailySummaryV1 | null, metric: DailyMetric): number | null {
  if (!day) return null;
  const key: WeatherVariable = metric === 'temperature' ? 'temperatureC'
    : metric === 'wet-bulb' || metric === 'snowmaking' ? 'wetBulbC'
      : metric === 'precipitation' ? 'precipitationMm'
        : metric === 'snowfall' ? 'snowfallCm' : 'condition';
  const value = day.completeness[key];
  if (value != null) return value;
  return day.expectedHours > 0 ? day.availableHours / day.expectedHours : null;
}

function formatNumber(value: number | null, digits = 1): string {
  return value == null || !Number.isFinite(value) ? 'Unavailable' : value.toFixed(digits);
}

function words(value: string): string {
  return value.split('-').map((part) => `${part.slice(0, 1).toUpperCase()}${part.slice(1)}`).join(' ');
}

function distributionText(values: Readonly<Partial<Record<string, number>>>, order: readonly string[], unit: string): string {
  const present = order.flatMap((key) => {
    const value = values[key];
    return value != null && value > 0 ? [`${words(key)} ${formatNumber(value)}${unit}`] : [];
  });
  return present.length > 0 ? present.join(' · ') : 'None';
}

export function formatDailyMetric(day: WeatherDailySummaryV1 | null, metric: DailyMetric): string {
  if (!day) return 'Unavailable';
  if (metric === 'temperature' || metric === 'wet-bulb') {
    const range = metric === 'temperature' ? day.temperatureC : day.wetBulbC;
    return range == null ? 'Unavailable' : `${formatNumber(range.minimum)} / ${formatNumber(range.mean)} / ${formatNumber(range.maximum)} °C`;
  }
  if (metric === 'precipitation') {
    if (day.precipitationMm == null) return 'Unavailable';
    const phases = day.precipitationByPhaseMm == null ? '' : ` · ${distributionText(day.precipitationByPhaseMm, PRECIPITATION_PHASES, ' mm')}`;
    return `${formatNumber(day.precipitationMm)} mm${phases}`;
  }
  if (metric === 'snowfall') {
    return day.snowfallCm == null ? 'Unavailable' : `${formatNumber(day.snowfallCm)} cm · ${words(day.snowfallSource)}`;
  }
  if (metric === 'snowmaking') return day.snowmakingHours == null ? 'Unavailable' : `${formatNumber(day.snowmakingHours)} hours`;
  if (metric === 'conditions') {
    const dominant = day.dominantCondition == null ? 'No dominant condition' : words(day.dominantCondition);
    return `${dominant} · ${distributionText(day.conditionHours, WEATHER_CONDITIONS, ' h')}`;
  }
  if (day.macroHours == null) return 'Unavailable';
  const dominant = day.dominantMacro == null ? 'No dominant air mass' : words(day.dominantMacro);
  return `${dominant} · ${distributionText(day.macroHours, MACRO_AIR_MASSES, ' h')}`;
}

export function formatCompleteness(value: number | null): string {
  if (value == null || !Number.isFinite(value)) return 'Unavailable';
  return `${Math.round(Math.max(0, Math.min(1, value)) * 100)}% complete`;
}

export function filterEventsByDate(
  events: readonly WeatherEventV1[],
  startDate?: string,
  endDate?: string,
): readonly WeatherEventV1[] {
  return events.filter((event) =>
    (startDate == null || event.localEndDate >= startDate)
    && (endDate == null || event.localStartDate <= endDate));
}

export function monthDateRange(year: number, month: number): Readonly<{ startDate: string; endDate: string }> {
  const paddedMonth = String(month).padStart(2, '0');
  const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return { startDate: `${year}-${paddedMonth}-01`, endDate: `${year}-${paddedMonth}-${String(lastDay).padStart(2, '0')}` };
}

export interface ComparisonScoreRow {
  key: keyof WeatherComparisonScoresV1;
  label: string;
  value: number | null;
  unit: string;
}

export function comparisonScoreRows(scores: WeatherComparisonScoresV1): readonly ComparisonScoreRow[] {
  return [
    { key: 'temperatureMeanBiasC', label: 'Temperature mean bias', value: scores.temperatureMeanBiasC, unit: '°C' },
    { key: 'temperatureMeanMaeC', label: 'Temperature daily MAE', value: scores.temperatureMeanMaeC, unit: '°C' },
    { key: 'wetBulbMeanBiasC', label: 'Wet-bulb mean bias', value: scores.wetBulbMeanBiasC, unit: '°C' },
    { key: 'wetBulbMeanMaeC', label: 'Wet-bulb daily MAE', value: scores.wetBulbMeanMaeC, unit: '°C' },
    { key: 'precipitationBiasMm', label: 'Precipitation bias', value: scores.precipitationBiasMm, unit: 'mm' },
    { key: 'precipitationMaeMm', label: 'Precipitation daily MAE', value: scores.precipitationMaeMm, unit: 'mm' },
    { key: 'dominantConditionAgreement', label: 'Dominant-condition agreement', value: scores.dominantConditionAgreement, unit: '%' },
    { key: 'eventCountDifference', label: 'Event-count difference', value: scores.eventCountDifference, unit: '' },
    { key: 'eventDurationDifferenceHours', label: 'Event-duration difference', value: scores.eventDurationDifferenceHours, unit: 'h' },
    { key: 'eventOverlapScore', label: 'Event overlap', value: scores.eventOverlapScore, unit: '%' },
    { key: 'stormSeverityAgreement', label: 'Storm-severity agreement', value: scores.stormSeverityAgreement, unit: '%' },
    { key: 'stormStyleAgreement', label: 'Classified storm-style agreement', value: scores.stormStyleAgreement, unit: '%' },
  ];
}

function tuningRange(key: TuningNumberKey): readonly [number, number] {
  return WEATHER_SIMULATION_TUNING_LIMITS[key];
}

export function validateTuningDraft(value: unknown): WeatherSimulationTuningV1 {
  if (value == null || typeof value !== 'object') throw new Error('Tuning JSON must contain an object.');
  const source = value as Readonly<Record<string, unknown>>;
  if (source.version !== 1) throw new Error('Tuning JSON must use version 1.');
  if (typeof source.id !== 'string' || source.id.trim() === '') throw new Error('Tuning JSON must include a non-empty id.');
  const result: Record<string, number | string | null> = { version: 1, id: source.id.trim() };
  for (const control of TUNING_CONTROLS) {
    const raw = source[control.key];
    if (raw === null && control.nullable) {
      result[control.key] = null;
      continue;
    }
    if (typeof raw !== 'number' || !Number.isFinite(raw)) throw new Error(`${control.label} must be a finite number${control.nullable ? ' or null' : ''}.`);
    const [minimum, maximum] = tuningRange(control.key);
    if (raw < minimum || raw > maximum) throw new Error(`${control.label} must be between ${minimum} and ${maximum}.`);
    if (control.key === 'hourlyNormalSmoothingRadius' && !Number.isInteger(raw)) throw new Error('Normal smoothing radius must be a whole number.');
    result[control.key] = raw;
  }
  return result as unknown as WeatherSimulationTuningV1;
}

export function tuningDraftMatches(left: WeatherSimulationTuningV1 | null, right: WeatherSimulationTuningV1): boolean {
  return left != null && JSON.stringify(validateTuningDraft(left)) === JSON.stringify(validateTuningDraft(right));
}

export function baselineIsCompatible(baseline: WeatherLabResultV2 | null, simulation: WeatherLabResultV2 | null): boolean {
  if (!baseline || !simulation) return false;
  const left = baseline.run; const right = simulation.run;
  return baseline.observed.observationHash === simulation.observed.observationHash
    && left.climateModelHash === right.climateModelHash
    && left.stationId === right.stationId
    && left.stationTimeZone === right.stationTimeZone
    && left.validationYear === right.validationYear
    && left.worldSeed === right.worldSeed
    && left.generatorVersion === right.generatorVersion
    && left.comparisonStreamKey === right.comparisonStreamKey
    && JSON.stringify(left.location) === JSON.stringify(right.location)
    && JSON.stringify(left.difficultyProfile) === JSON.stringify(right.difficultyProfile);
}

export function parseTuningJson(text: string): WeatherSimulationTuningV1 {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text) as unknown;
  } catch {
    throw new Error('Tuning import is not valid JSON.');
  }
  return validateTuningDraft(parsed);
}

export function tuningJson(tuning: WeatherSimulationTuningV1): string {
  return `${JSON.stringify(validateTuningDraft(tuning), null, 2)}\n`;
}

export function loadStoredTuning(storage: Pick<Storage, 'getItem'>): WeatherSimulationTuningV1 | null {
  const text = storage.getItem(WEATHER_LAB_TUNING_STORAGE_KEY);
  return text == null ? null : parseTuningJson(text);
}

export function storeTuning(storage: Pick<Storage, 'setItem'>, tuning: WeatherSimulationTuningV1): void {
  storage.setItem(WEATHER_LAB_TUNING_STORAGE_KEY, tuningJson(tuning));
}

function csvCell(value: unknown): string {
  if (value == null) return '';
  const text = typeof value === 'number' && Number.isFinite(value) ? String(value) : String(value);
  return /[",\r\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
}

function csvLine(values: readonly unknown[]): string {
  return values.map(csvCell).join(',');
}

const CSV_WEATHER_VARIABLES = Object.freeze([
  'temperatureC', 'dewPointC', 'pressureHpa', 'relativeHumidityPct', 'wetBulbC', 'precipitationMm',
  'snowfallCm', 'windSpeedKph', 'windDirectionDeg', 'windGustKph', 'shortwaveRadiationWm2',
  'cloudCoverPct', 'visibilityKm', 'condition',
] satisfies readonly WeatherVariable[]);

const DAILY_SOURCE_COLUMNS = Object.freeze([
  'availableHours', 'expectedHours', ...CSV_WEATHER_VARIABLES.map((variable) => `completeness.${variable}`),
  'temperatureMinimumC', 'temperatureMeanC', 'temperatureMaximumC',
  'wetBulbMinimumC', 'wetBulbMeanC', 'wetBulbMaximumC', 'snowmakingHours', 'precipitationMm',
  'rainMm', 'mixedMm', 'snowMm', 'freezingRainMm', 'snowfallCm', 'snowfallSource',
  'dominantCondition', 'conditionHours', 'hazards', 'dominantMacro', 'macroHours', 'eventIds',
]);

function dailyCells(day: WeatherDailySummaryV1 | null): readonly unknown[] {
  return [
    day?.availableHours, day?.expectedHours,
    ...CSV_WEATHER_VARIABLES.map((variable) => day?.completeness[variable]),
    day?.temperatureC?.minimum, day?.temperatureC?.mean, day?.temperatureC?.maximum,
    day?.wetBulbC?.minimum, day?.wetBulbC?.mean, day?.wetBulbC?.maximum,
    day?.snowmakingHours, day?.precipitationMm,
    day?.precipitationByPhaseMm?.rain, day?.precipitationByPhaseMm?.mixed,
    day?.precipitationByPhaseMm?.snow, day?.precipitationByPhaseMm?.['freezing-rain'],
    day?.snowfallCm, day?.snowfallSource, day?.dominantCondition,
    day == null ? null : JSON.stringify(day.conditionHours), day == null ? null : day.hazards.join('|'),
    day?.dominantMacro, day?.macroHours == null ? null : JSON.stringify(day.macroHours),
    day == null ? null : day.eventIds.join('|'),
  ];
}

export function dailyComparisonCsv(series: DailyComparisonSeries): string {
  const header = ['localDate', ...(['observed', 'baseline', 'candidate'] as const)
    .flatMap((source) => DAILY_SOURCE_COLUMNS.map((column) => `${source}.${column}`))];
  const lines = [csvLine(header)];
  for (const row of alignDailyComparison(series)) {
    lines.push(csvLine([row.localDate, ...dailyCells(row.observed), ...dailyCells(row.baseline), ...dailyCells(row.candidate)]));
  }
  return `${lines.join('\n')}\n`;
}

const HOURLY_SOURCE_COLUMNS = Object.freeze([
  'localDateTime', 'utcOffsetMinutes', 'fold', 'temperatureC', 'dewPointC', 'wetBulbC', 'pressureHpa', 'relativeHumidityPct',
  'precipitationMm', 'precipitationPhase', 'snowfallCm', 'windSpeedKph', 'windDirectionDeg',
  'windGustKph', 'shortwaveRadiationWm2', 'cloudCoverPct', 'visibilityKm', 'condition', 'hazards', 'quality', 'macroAirMass',
]);

type ComparisonHour = ObservedWeatherHourV1 | SimulatedWeatherHourV1;

function hourlyCells(hour: ComparisonHour | null): readonly unknown[] {
  return [
    hour?.localDateTime, hour?.utcOffsetMinutes, hour?.fold,
    hour?.temperatureC, hour?.dewPointC, hour?.wetBulbC, hour?.pressureHpa,
    hour?.relativeHumidityPct, hour?.precipitationMm, hour?.precipitationPhase, hour?.snowfallCm,
    hour?.windSpeedKph, hour?.windDirectionDeg, hour?.windGustKph, hour?.shortwaveRadiationWm2,
    hour?.cloudCoverPct, hour?.visibilityKm, hour?.condition, hour == null ? null : hour.hazards.join('|'),
    hour != null && 'quality' in hour ? JSON.stringify(hour.quality) : null,
    hour != null && 'macroAirMass' in hour ? hour.macroAirMass : null,
  ];
}

export function hourlyComparisonCsv(series: HourlyComparisonSeries): string {
  const indexes = {
    observed: new Map(series.observed.map((hour) => [hour.at, hour])),
    baseline: new Map((series.baseline ?? []).map((hour) => [hour.at, hour])),
    candidate: new Map(series.candidate.map((hour) => [hour.at, hour])),
  };
  const instants = new Set<string>();
  for (const index of Object.values(indexes)) for (const instant of index.keys()) instants.add(instant);
  const header = ['at', ...(['observed', 'baseline', 'candidate'] as const)
    .flatMap((source) => HOURLY_SOURCE_COLUMNS.map((column) => `${source}.${column}`))];
  const lines = [csvLine(header)];
  for (const at of [...instants].sort()) {
    lines.push(csvLine([
      at,
      ...hourlyCells(indexes.observed.get(at) ?? null),
      ...hourlyCells(indexes.baseline.get(at) ?? null),
      ...hourlyCells(indexes.candidate.get(at) ?? null),
    ]));
  }
  return `${lines.join('\n')}\n`;
}

export interface WeatherComparisonExportV1 {
  version: 1;
  baseline: WeatherLabResultV2 | null;
  candidate: WeatherLabResultV2;
  observationHash: string;
  comparisonStreamKey: string;
}

export function comparisonExportJson(baseline: WeatherLabResultV2 | null, candidate: WeatherLabResultV2): string {
  const payload: WeatherComparisonExportV1 = {
    version: 1,
    baseline,
    candidate,
    observationHash: candidate.observed.observationHash,
    comparisonStreamKey: candidate.run.comparisonStreamKey,
  };
  return `${JSON.stringify(payload, null, 2)}\n`;
}

export function comparisonSeriesFromResults(
  baseline: WeatherLabResultV2,
  candidate: WeatherLabResultV2,
): WeatherComparisonSeries {
  return {
    daily: {
      observed: candidate.daily.observed,
      baseline: baseline.daily.simulated,
      candidate: candidate.daily.simulated,
    },
    events: {
      observed: candidate.events.observed,
      baseline: baseline.events.simulated,
      candidate: candidate.events.simulated,
    },
    hourly: {
      observed: candidate.observed.hours,
      baseline: baseline.simulated,
      candidate: candidate.simulated,
    },
  };
}

export function hourlySeriesFromResults(
  observed: HistoricalWeatherSeriesV1,
  baseline: WeatherLabResultV2,
  candidate: WeatherLabResultV2,
): HourlyComparisonSeries {
  return { observed: observed.hours, baseline: baseline.simulated, candidate: candidate.simulated };
}
