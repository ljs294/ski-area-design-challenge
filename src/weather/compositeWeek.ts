import { localWeatherDateKey, weatherLocalParts } from './localTime';
import type { PrecipitationType, ResolvedWeatherHour } from './weatherModel';

/** Number of source records in one condensed winter week. */
export const COMPOSITE_WEEK_HOURS = 168 as const;
/** A displayed winter week is one twelve-hour operating day. */
export const COMPOSITE_WEEK_SIMULATION_SECONDS = 43_200 as const;
export const COMPOSITE_WEEK_OPERATING_START_HOUR = 8 as const;
export const COMPOSITE_WEEK_OPERATING_END_HOUR = 20 as const;
export const SNOWMAKING_WET_BULB_THRESHOLD_C = -2 as const;

export const COMPOSITE_WEEK_FEATURE_WEIGHTS = Object.freeze({
  temperature: 0.40,
  wetBulb: 0.20,
  wind: 0.15,
  cloudCover: 0.10,
  precipitationClass: 0.10,
  dailySnowfall: 0.05,
} as const);

/** Floors keep a constant feature from making the medoid score infinite. */
export const COMPOSITE_WEEK_FEATURE_FLOORS = Object.freeze({
  temperature: 1,
  wetBulb: 1,
  wind: 5,
  cloudCover: 10,
  precipitationClass: 1,
  dailySnowfall: 1,
} as const);

export interface CompositeWeekOptions {
  /**
   * Optional IANA timezone used to identify dates and wall-clock hours. When
   * omitted, the normalized ISO/UTC clock in each record is used.
   */
  timezone?: string;
  featureFloors?: Partial<typeof COMPOSITE_WEEK_FEATURE_FLOORS>;
}

export interface CompositeWeekDay {
  index: number;
  dateKey: string;
  /** The original 24 records; none are averaged or otherwise changed. */
  hours: readonly ResolvedWeatherHour[];
  /** Robust medoid score, before any display rounding. */
  score: number;
  dailySnowfallCm: number;
}

export interface CompositeWeekWitness {
  dayIndex: number;
  dateKey: string;
  score: number;
  /** All 24 source records for the selected, unmodified day. */
  day: readonly ResolvedWeatherHour[];
  /** The 08:00 through 19:00 records shown during the operating day. */
  operatingHours: readonly ResolvedWeatherHour[];
}

export interface CompositeWeekOutlook {
  temperatureRangeC: Readonly<{ minimum: number; maximum: number }>;
  snowfallCm: number;
  /** Liquid precipitation whose generated phase is rain. */
  rainMm: number;
  maxWindKph: number;
  maxWindGustKph: number;
  freezeThawTransitions: number;
  snowmakingEligibleHours: number;
  /** Useful explicit phase totals for callers displaying a richer outlook. */
  precipitationByType: Readonly<Record<PrecipitationType, number>>;
  totalPrecipitationMm: number;
}

export interface CompositeWeekPhysicsStep {
  sourceHourIndex: number;
  dueSecond: number;
  /** Same source object supplied to create/schedule; never a blended record. */
  hour: ResolvedWeatherHour;
}

export interface CompositeWeekWeather {
  hours: readonly ResolvedWeatherHour[];
  days: readonly CompositeWeekDay[];
  witness: CompositeWeekWitness;
  outlook: CompositeWeekOutlook;
  physics: readonly CompositeWeekPhysicsStep[];
}

const PRECIPITATION_TYPES: readonly PrecipitationType[] = [
  'none', 'rain', 'mixed', 'snow', 'freezing-rain',
];

function assertFinite(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new Error(`Composite week weather field ${label} must be finite.`);
}

function validateHours(hours: readonly ResolvedWeatherHour[]): void {
  if (!Array.isArray(hours) || hours.length !== COMPOSITE_WEEK_HOURS) {
    throw new Error(`A composite week requires exactly ${COMPOSITE_WEEK_HOURS} resolved hourly records.`);
  }
  let previousTimestamp = Number.NEGATIVE_INFINITY;
  for (const [index, hour] of hours.entries()) {
    if (!hour || typeof hour !== 'object') throw new Error(`Composite week weather record ${index} is invalid.`);
    const timestamp = new Date(hour.at).getTime();
    if (!Number.isFinite(timestamp) || timestamp <= previousTimestamp) {
      throw new Error('Composite week weather records must have strictly increasing timestamps.');
    }
    previousTimestamp = timestamp;
    assertFinite(hour.temperatureC, `temperatureC at index ${index}`);
    assertFinite(hour.wetBulbC, `wetBulbC at index ${index}`);
    assertFinite(hour.windSpeedKph, `windSpeedKph at index ${index}`);
    assertFinite(hour.windGustKph, `windGustKph at index ${index}`);
    assertFinite(hour.cloudCoverPct, `cloudCoverPct at index ${index}`);
    assertFinite(hour.precipitationMm, `precipitationMm at index ${index}`);
    assertFinite(hour.snowfallCm, `snowfallCm at index ${index}`);
    if (!PRECIPITATION_TYPES.includes(hour.precipitationType)) {
      throw new Error(`Composite week weather record ${index} has an invalid precipitation type.`);
    }
  }
}

function dateKeyFor(hour: ResolvedWeatherHour, timezone?: string): string {
  return timezone ? localWeatherDateKey(hour.at, timezone) : hour.at.slice(0, 10);
}

function clockHourFor(hour: ResolvedWeatherHour, timezone?: string): number {
  return timezone ? weatherLocalParts(hour.at, timezone).hour : new Date(hour.at).getUTCHours();
}

interface DayGroup {
  dateKey: string;
  hours: ResolvedWeatherHour[];
}

/**
 * Split the authoritative 168-record packet into seven chronological source
 * days. Source cardinality, rather than local wall-clock cardinality, is the
 * invariant: a DST transition must never delete an hourly physics record.
 */
function groupDays(hours: readonly ResolvedWeatherHour[], timezone?: string): readonly DayGroup[] {
  return Array.from({ length: 7 }, (_, dayIndex) => {
    const sourceDay = hours.slice(dayIndex * 24, (dayIndex + 1) * 24);
    return { dateKey: dateKeyFor(sourceDay[0], timezone), hours: [...sourceDay] };
  });
}

function median(values: readonly number[]): number {
  const sorted = [...values].sort((left, right) => left - right);
  if (sorted.length === 0) return 0;
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
}

/** Inclusive linear quantile, deterministic for the seven-day sample. */
function quantile(values: readonly number[], probability: number): number {
  const sorted = [...values].sort((left, right) => left - right);
  if (sorted.length === 0) return 0;
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  if (lower === upper) return sorted[lower];
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function precipitationClass(type: PrecipitationType): number {
  switch (type) {
    case 'none': return 0;
    case 'rain': return 1;
    case 'mixed': return 2;
    case 'snow': return 3;
    case 'freezing-rain': return 4;
  }
}

type HourlyFeature = keyof typeof COMPOSITE_WEEK_FEATURE_WEIGHTS;

function hourlyFeature(feature: HourlyFeature, hour: ResolvedWeatherHour, dailySnowfallCm: number): number {
  switch (feature) {
    case 'temperature': return hour.temperatureC;
    case 'wetBulb': return hour.wetBulbC;
    case 'wind': return hour.windSpeedKph;
    case 'cloudCover': return hour.cloudCoverPct;
    case 'precipitationClass': return precipitationClass(hour.precipitationType);
    case 'dailySnowfall': return dailySnowfallCm;
  }
}

function dailySnowfall(day: readonly ResolvedWeatherHour[]): number {
  return day.reduce((sum, hour) => sum + Math.max(0, hour.snowfallCm), 0);
}

function medoidDays(
  groups: readonly DayGroup[],
  featureFloors: typeof COMPOSITE_WEEK_FEATURE_FLOORS,
): readonly CompositeWeekDay[] {
  const dailySnowfalls = groups.map(({ hours }) => dailySnowfall(hours));
  const hourlyFeatures = (Object.keys(COMPOSITE_WEEK_FEATURE_WEIGHTS) as HourlyFeature[])
    .filter((feature): feature is Exclude<HourlyFeature, 'dailySnowfall'> => feature !== 'dailySnowfall');
  // The representative value is compared with the other six days at the
  // same clock hour. This preserves a morning/evening profile instead of
  // allowing a warm afternoon in one day to cancel a cold morning in another.
  const mediansByHour: Record<Exclude<HourlyFeature, 'dailySnowfall'>, number[]> = {
    temperature: [], wetBulb: [], wind: [], cloudCover: [], precipitationClass: [],
  };
  const iqrsByHour: Record<Exclude<HourlyFeature, 'dailySnowfall'>, number[]> = {
    temperature: [], wetBulb: [], wind: [], cloudCover: [], precipitationClass: [],
  };
  for (const feature of hourlyFeatures) {
    for (let clockHour = 0; clockHour < 24; clockHour += 1) {
      const values = groups.map(({ hours }) => hourlyFeature(feature, hours[clockHour], 0));
      mediansByHour[feature][clockHour] = median(values);
      iqrsByHour[feature][clockHour] = quantile(values, 0.75) - quantile(values, 0.25);
    }
  }
  const dailySnowfallMedian = median(dailySnowfalls);
  const dailySnowfallIqr = quantile(dailySnowfalls, 0.75) - quantile(dailySnowfalls, 0.25);
  return groups.map((group, dayIndex) => {
    const score = group.hours.reduce((dayScore, hour, clockHour) => dayScore + hourlyFeatures
      .reduce((hourScore, feature) => {
        const dayValue = hourlyFeature(feature, hour, 0);
        const denominator = Math.max(iqrsByHour[feature][clockHour], featureFloors[feature]);
        return hourScore + COMPOSITE_WEEK_FEATURE_WEIGHTS[feature] * Math.abs(dayValue - mediansByHour[feature][clockHour]) / denominator;
      }, 0), 0) + COMPOSITE_WEEK_FEATURE_WEIGHTS.dailySnowfall *
      Math.abs(dailySnowfalls[dayIndex] - dailySnowfallMedian) / Math.max(dailySnowfallIqr, featureFloors.dailySnowfall);
    return { index: dayIndex, dateKey: group.dateKey, hours: group.hours, score, dailySnowfallCm: dailySnowfalls[dayIndex] };
  });
}

function selectLowestScore(days: readonly CompositeWeekDay[]): CompositeWeekDay {
  let selected = days[0];
  for (const day of days.slice(1)) {
    // Strict comparison intentionally keeps the earliest day on an exact tie.
    if (day.score < selected.score) selected = day;
  }
  return selected;
}

function resolvedFeatureFloors(options: CompositeWeekOptions): typeof COMPOSITE_WEEK_FEATURE_FLOORS {
  const floors = { ...COMPOSITE_WEEK_FEATURE_FLOORS, ...options.featureFloors };
  for (const [feature, floor] of Object.entries(floors)) {
    if (!Number.isFinite(floor) || floor <= 0) throw new Error(`Composite week feature floor ${feature} must be positive.`);
  }
  return floors;
}

function calculateFreezeThawTransitions(hours: readonly ResolvedWeatherHour[]): number {
  let state: 'frozen' | 'thawed' | null = null;
  let transitions = 0;
  for (const hour of hours) {
    const nextState: 'frozen' | 'thawed' | null = hour.temperatureC <= 0 ? 'frozen' : hour.temperatureC >= 1 ? 'thawed' : state;
    if (state !== null && nextState !== null && nextState !== state) transitions += 1;
    state = nextState;
  }
  return transitions;
}

export function calculateCompositeWeekOutlook(hours: readonly ResolvedWeatherHour[]): CompositeWeekOutlook {
  validateHours(hours);
  const precipitationByType = Object.fromEntries(PRECIPITATION_TYPES.map((type) => [type, 0])) as Record<PrecipitationType, number>;
  let snowfallCm = 0;
  let totalPrecipitationMm = 0;
  let maxWindKph = 0;
  let maxWindGustKph = 0;
  let snowmakingEligibleHours = 0;
  for (const hour of hours) {
    const precipitationMm = Math.max(0, hour.precipitationMm);
    precipitationByType[hour.precipitationType] += precipitationMm;
    totalPrecipitationMm += precipitationMm;
    snowfallCm += Math.max(0, hour.snowfallCm);
    maxWindKph = Math.max(maxWindKph, hour.windSpeedKph);
    maxWindGustKph = Math.max(maxWindGustKph, hour.windGustKph);
    if (hour.wetBulbC <= SNOWMAKING_WET_BULB_THRESHOLD_C) snowmakingEligibleHours += 1;
  }
  return {
    temperatureRangeC: {
      minimum: Math.min(...hours.map((hour) => hour.temperatureC)),
      maximum: Math.max(...hours.map((hour) => hour.temperatureC)),
    },
    snowfallCm,
    rainMm: precipitationByType.rain,
    maxWindKph,
    maxWindGustKph,
    freezeThawTransitions: calculateFreezeThawTransitions(hours),
    snowmakingEligibleHours,
    precipitationByType: Object.freeze({ ...precipitationByType }),
    totalPrecipitationMm,
  };
}

/** Compute the due second for zero-based source-hour index `i`. */
export function compositeWeekPhysicsDueSecond(index: number, weekStartSecond = 0): number {
  if (!Number.isInteger(index) || index < 0 || index >= COMPOSITE_WEEK_HOURS) {
    throw new Error(`Composite week physics hour index must be an integer from 0 to ${COMPOSITE_WEEK_HOURS - 1}.`);
  }
  if (!Number.isFinite(weekStartSecond)) throw new Error('Composite week physics start second must be finite.');
  return weekStartSecond + Math.ceil((index + 1) * COMPOSITE_WEEK_SIMULATION_SECONDS / COMPOSITE_WEEK_HOURS);
}

export function scheduleCompositeWeekPhysics(
  hours: readonly ResolvedWeatherHour[],
  weekStartSecond = 0,
): readonly CompositeWeekPhysicsStep[] {
  validateHours(hours);
  return Object.freeze(hours.map((hour, sourceHourIndex) => Object.freeze({
    sourceHourIndex,
    dueSecond: compositeWeekPhysicsDueSecond(sourceHourIndex, weekStartSecond),
    hour,
  })));
}

/** Select the lowest robust-median score, preserving source records verbatim. */
export function selectCompositeWeekWitnessDay(
  hours: readonly ResolvedWeatherHour[],
  options: CompositeWeekOptions = {},
): CompositeWeekWitness {
  validateHours(hours);
  const groups = groupDays(hours, options.timezone);
  const floors = resolvedFeatureFloors(options);
  const days = medoidDays(groups, floors);
  const selected = selectLowestScore(days);
  const operatingHours = selected.hours.filter((hour) => {
    const clockHour = clockHourFor(hour, options.timezone);
    return clockHour >= COMPOSITE_WEEK_OPERATING_START_HOUR && clockHour < COMPOSITE_WEEK_OPERATING_END_HOUR;
  });
  if (operatingHours.length !== COMPOSITE_WEEK_OPERATING_END_HOUR - COMPOSITE_WEEK_OPERATING_START_HOUR) {
    throw new Error('The witnessed day must provide unmodified 08:00-20:00 hourly conditions.');
  }
  return {
    dayIndex: selected.index,
    dateKey: selected.dateKey,
    score: selected.score,
    day: selected.hours,
    operatingHours,
  };
}

export function createCompositeWeekWeather(
  hours: readonly ResolvedWeatherHour[],
  options: CompositeWeekOptions = {},
): CompositeWeekWeather {
  validateHours(hours);
  const groups = groupDays(hours, options.timezone);
  const floors = resolvedFeatureFloors(options);
  const days = medoidDays(groups, floors);
  const selected = selectLowestScore(days);
  const operatingHours = selected.hours.filter((hour) => {
    const clockHour = clockHourFor(hour, options.timezone);
    return clockHour >= COMPOSITE_WEEK_OPERATING_START_HOUR && clockHour < COMPOSITE_WEEK_OPERATING_END_HOUR;
  });
  if (operatingHours.length !== 12) throw new Error('The witnessed day must provide unmodified 08:00-20:00 hourly conditions.');
  return {
    hours,
    days,
    witness: { dayIndex: selected.index, dateKey: selected.dateKey, score: selected.score, day: selected.hours, operatingHours },
    outlook: calculateCompositeWeekOutlook(hours),
    physics: scheduleCompositeWeekPhysics(hours),
  };
}

/** Short aliases for callers that describe the selected day as a medoid. */
export const selectMedoidWitnessDay = selectCompositeWeekWitnessDay;
export const scheduleWeeklyWeatherPhysics = scheduleCompositeWeekPhysics;
