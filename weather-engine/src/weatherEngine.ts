import type { ResortClimateBaseline, ResortWeatherLocation } from './climateBaseline.ts';
import { binForDate } from './climateBaseline.ts';
import {
  createWeatherRandomStreams,
  type RandomState,
  type WeatherRandomStreams,
  Xoshiro128,
} from './random.ts';

export type ElevationBand = 'base' | 'mid' | 'summit';
export type WeatherRegime = 'normal' | 'cold-snap' | 'warm-spell' | 'storm' | 'dry-spell';
export type PrecipitationType = 'none' | 'rain' | 'mixed' | 'snow';
export type WeatherEventType =
  | 'notable-storm'
  | 'major-storm'
  | 'cold-snap'
  | 'warm-spell'
  | 'dry-spell'
  | 'freeze-thaw'
  | 'flash-freeze';

export interface WeatherTuningProfile {
  eventRateMultiplier: number;
  majorEventMultiplier: number;
  quietWeatherBoost: number;
  minimumStormCooldownDays: number;
}

export const NORMAL_WEATHER_TUNING: WeatherTuningProfile = {
  eventRateMultiplier: 1,
  majorEventMultiplier: 1,
  quietWeatherBoost: 0,
  minimumStormCooldownDays: 0,
};

export interface BandWeather {
  temperatureC: number;
  wetBulbC: number;
  relativeHumidityPct: number;
  precipitationMm: number;
  precipitationType: PrecipitationType;
  snowfallCm: number;
}

export interface MountainWeatherHour {
  at: string;
  regime: WeatherRegime;
  base: BandWeather;
  mid: BandWeather;
  summit: BandWeather;
}

export interface WeatherDay {
  date: string;
  regime: WeatherRegime;
  hours: MountainWeatherHour[];
}

export interface WeatherEvent {
  id: string;
  type: WeatherEventType;
  name: string;
  startsAt: string;
  endsAt: string;
  severity: 'minor' | 'notable' | 'major';
  bands: ElevationBand[];
}

export interface WeatherSeasonPlan {
  schemaVersion: 1;
  winterIdentifier: string;
  seed: string;
  startsAt: string;
  endsAt: string;
  days: WeatherDay[];
  events: WeatherEvent[];
}

export interface ForecastBandHour {
  temperatureC: number;
  precipitationMm: number;
  precipitationType: PrecipitationType;
  snowfallCm: number;
}

export interface ForecastHour {
  at: string;
  base: ForecastBandHour;
  mid: ForecastBandHour;
  summit: ForecastBandHour;
}

export interface ForecastDay {
  date: string;
  confidencePct: number;
  minTempC: Record<ElevationBand, number>;
  maxTempC: Record<ElevationBand, number>;
  snowfallCm: Record<ElevationBand, number>;
  precipitationProbabilityPct: number;
  eventSignal: string | null;
  hours?: ForecastHour[];
}

export interface ForecastIssue {
  issuedAt: string;
  days: ForecastDay[];
  generalSignals: string[];
}

export interface WeatherState {
  schemaVersion: 1;
  generatorVersion: number;
  location: ResortWeatherLocation;
  climateBaseline: ResortClimateBaseline;
  tuningProfile: WeatherTuningProfile;
  randomStreams: WeatherRandomStreams;
  winterIdentifier: string;
  seed: string;
  truth: WeatherSeasonPlan;
  latestForecast: ForecastIssue;
  lastProcessedHour: string;
  activeEventIds: string[];
  selectedBand: ElevationBand;
}

export interface WeatherSnapshot extends WeatherState {}

export interface WeatherAdvanceResult {
  state: WeatherState;
  processedHours: MountainWeatherHour[];
  startedEvents: WeatherEvent[];
  endedEvents: WeatherEvent[];
  forecastIssues: ForecastIssue[];
}

const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;
const BANDS: readonly ElevationBand[] = ['base', 'mid', 'summit'];
const GENERATOR_VERSION = 1;

function clamp(min: number, max: number, value: number): number {
  return Math.max(min, Math.min(max, value));
}

function round(value: number, digits = 2): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

function dateOnly(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function elevation(location: ResortWeatherLocation, band: ElevationBand): number {
  return location[`${band}ElevationM`];
}

// Stull (2011) approximation; sufficiently accurate for precipitation typing.
function wetBulbC(tempC: number, humidityPct: number): number {
  const rh = clamp(5, 100, humidityPct);
  return tempC * Math.atan(0.151977 * Math.sqrt(rh + 8.313659))
    + Math.atan(tempC + rh)
    - Math.atan(rh - 1.676331)
    + 0.00391838 * rh ** 1.5 * Math.atan(0.023101 * rh)
    - 4.686035;
}

function precipitationType(wetBulb: number, precipitationMm: number): PrecipitationType {
  if (precipitationMm < 0.005) return 'none';
  if (wetBulb <= -1) return 'snow';
  if (wetBulb < 1) return 'mixed';
  return 'rain';
}

function snowfallRatio(tempC: number): number {
  return clamp(5, 20, 10 + Math.max(-8, Math.min(8, -tempC)) * 0.7);
}

function makeBand(
  sourceTempC: number,
  humidityPct: number,
  sourcePrecipitationMm: number,
  band: ElevationBand,
  baseline: ResortClimateBaseline,
): BandWeather {
  const height = elevation(baseline.location, band);
  const temp = sourceTempC - 0.0065 * (height - baseline.sourceElevationM);
  const orographicFactor = 1 + Math.max(0, height - baseline.location.baseElevationM) * 0.00025;
  const water = sourcePrecipitationMm * orographicFactor;
  const wb = wetBulbC(temp, humidityPct);
  const type = precipitationType(wb, water);
  const frozenFraction = type === 'snow' ? 1 : type === 'mixed' ? 0.5 : 0;
  return {
    temperatureC: round(temp),
    wetBulbC: round(wb),
    relativeHumidityPct: round(humidityPct, 1),
    precipitationMm: round(water, 3),
    precipitationType: type,
    snowfallCm: round((water * snowfallRatio(temp) * frozenFraction) / 10),
  };
}

function pulseWeights(start: number, duration: number, rng: Xoshiro128): number[] {
  const weights = Array<number>(24).fill(0);
  for (let offset = 0; offset < duration; offset += 1) {
    const phase = (offset + 1) / (duration + 1);
    const triangular = 1 - Math.abs(phase * 2 - 1);
    weights[start + offset] += Math.max(0.08, triangular + rng.normal(0, 0.08));
  }
  const total = weights.reduce((sum, value) => sum + value, 0);
  return weights.map((value) => value / total);
}

function dailyRegime(
  wet: boolean,
  anomalyC: number,
  spreadC: number,
  dryStreak: number,
): WeatherRegime {
  if (wet) return 'storm';
  if (anomalyC <= -Math.max(3, spreadC * 1.15)) return 'cold-snap';
  if (anomalyC >= Math.max(3, spreadC * 1.15)) return 'warm-spell';
  if (dryStreak >= 6) return 'dry-spell';
  return 'normal';
}

export function generateWeatherSeason(
  climateBaseline: ResortClimateBaseline,
  startsAt: string,
  winterWeeks: number,
  seed: string,
  tuningProfile: WeatherTuningProfile = NORMAL_WEATHER_TUNING,
): { plan: WeatherSeasonPlan; randomStreams: WeatherRandomStreams } {
  if (!Number.isInteger(winterWeeks) || winterWeeks < 1) throw new Error('winterWeeks must be a positive integer');
  const start = new Date(startsAt);
  if (Number.isNaN(start.getTime())) throw new Error('startsAt must be a valid ISO date');
  const initialStreams = createWeatherRandomStreams(seed);
  const truthRng = new Xoshiro128(initialStreams.truth);
  const shapeRng = new Xoshiro128(initialStreams.hourlyShape);
  const days: WeatherDay[] = [];
  let previousWet = false;
  let dryStreak = 0;
  let anomalyC = 0;
  let lastStormDay = -10_000;

  for (let dayIndex = 0; dayIndex < winterWeeks * 7; dayIndex += 1) {
    const date = new Date(start.getTime() + dayIndex * DAY_MS);
    date.setUTCHours(0, 0, 0, 0);
    const bin = binForDate(climateBaseline, date);
    const persistence = clamp(0.15, 0.88, bin.temperaturePersistence);
    const tempSpread = (bin.minTempStdDevC + bin.maxTempStdDevC) / 2;
    anomalyC = persistence * anomalyC
      + truthRng.normal(0, Math.max(1, tempSpread) * Math.sqrt(1 - persistence ** 2));
    const naturalWetChance = previousWet ? bin.wetToWetProbability : bin.dryToWetProbability;
    const cooldown = dayIndex - lastStormDay <= tuningProfile.minimumStormCooldownDays;
    const wetChance = cooldown
      ? 0
      : clamp(0, 0.98, naturalWetChance * tuningProfile.eventRateMultiplier);
    const wet = truthRng.next() < wetChance;
    if (wet) {
      previousWet = true;
      dryStreak = 0;
      lastStormDay = dayIndex;
    } else {
      previousWet = false;
      dryStreak += 1;
    }
    const regime = dailyRegime(wet, anomalyC, tempSpread, dryStreak);
    const dailyWater = wet
      ? clamp(
        0.1,
        Math.max(1, bin.precipitationP98Mm * 3 * tuningProfile.majorEventMultiplier),
        truthRng.gamma(bin.precipitationShape, bin.precipitationScale),
      )
      : 0;
    const onset = wet ? shapeRng.int(0, 18) : 0;
    const duration = wet
      ? shapeRng.int(Math.min(5, 24 - onset), Math.min(24 - onset, 10 + Math.round(dailyWater)))
      : 1;
    const weights = wet ? pulseWeights(onset, duration, shapeRng) : Array<number>(24).fill(0);
    const low = bin.meanMinTempC + anomalyC + truthRng.normal(0, bin.minTempStdDevC * 0.2);
    const high = Math.max(low + 1, bin.meanMaxTempC + anomalyC + truthRng.normal(0, bin.maxTempStdDevC * 0.2));
    const highHour = 14.5;
    const hours: MountainWeatherHour[] = [];

    for (let hour = 0; hour < 24; hour += 1) {
      // Cosine places the daily maximum in mid-afternoon and minimum before dawn.
      const midpoint = (high + low) / 2;
      const amplitude = (high - low) / 2;
      const sourceTemp = midpoint + amplitude * Math.cos(((hour - highHour) / 24) * Math.PI * 2)
        + shapeRng.normal(0, 0.25);
      const sourceWater = dailyWater * weights[hour];
      const humidity = clamp(
        25,
        100,
        bin.meanRelativeHumidityPct + (sourceWater > 0 ? 15 : 0) + shapeRng.normal(0, 5),
      );
      const at = new Date(date.getTime() + hour * HOUR_MS).toISOString();
      hours.push({
        at,
        regime,
        base: makeBand(sourceTemp, humidity, sourceWater, 'base', climateBaseline),
        mid: makeBand(sourceTemp, humidity, sourceWater, 'mid', climateBaseline),
        summit: makeBand(sourceTemp, humidity, sourceWater, 'summit', climateBaseline),
      });
    }
    days.push({ date: dateOnly(date), regime, hours });
  }

  const plan: WeatherSeasonPlan = {
    schemaVersion: 1,
    winterIdentifier: `${climateBaseline.location.id}-${dateOnly(start)}`,
    seed,
    startsAt: start.toISOString(),
    endsAt: new Date(start.getTime() + winterWeeks * 7 * DAY_MS).toISOString(),
    days,
    events: [],
  };
  plan.events = detectWeatherEvents(plan, climateBaseline);
  return {
    plan,
    randomStreams: {
      truth: truthRng.snapshot(),
      hourlyShape: shapeRng.snapshot(),
      forecastError: initialStreams.forecastError,
    },
  };
}

function addSequenceEvents(
  events: WeatherEvent[],
  plan: WeatherSeasonPlan,
  regime: WeatherRegime,
  type: WeatherEventType,
  name: string,
  minimumDays: number,
): void {
  let start = -1;
  for (let i = 0; i <= plan.days.length; i += 1) {
    if (i < plan.days.length && plan.days[i].regime === regime) {
      if (start < 0) start = i;
    } else if (start >= 0) {
      const length = i - start;
      if (length >= minimumDays) {
        events.push({
          id: `${type}-${start}`,
          type,
          name,
          startsAt: plan.days[start].hours[0].at,
          endsAt: new Date(new Date(plan.days[i - 1].hours[23].at).getTime() + HOUR_MS).toISOString(),
          severity: length >= minimumDays + 2 ? 'major' : 'notable',
          bands: [...BANDS],
        });
      }
      start = -1;
    }
  }
}

export function detectWeatherEvents(
  plan: WeatherSeasonPlan,
  baseline: ResortClimateBaseline,
): WeatherEvent[] {
  const events: WeatherEvent[] = [];
  let stormStart = -1;
  for (let i = 0; i <= plan.days.length; i += 1) {
    if (i < plan.days.length && plan.days[i].regime === 'storm') {
      if (stormStart < 0) stormStart = i;
    } else if (stormStart >= 0) {
      const stormDays = plan.days.slice(stormStart, i);
      const total = stormDays.flatMap((day) => day.hours)
        .reduce((sum, hour) => sum + hour.mid.precipitationMm, 0);
      const snow = stormDays.flatMap((day) => day.hours)
        .reduce((sum, hour) => sum + hour.mid.snowfallCm, 0);
      const frozenShare = total > 0
        ? stormDays.flatMap((day) => day.hours)
          .filter((hour) => hour.mid.precipitationType === 'snow' || hour.mid.precipitationType === 'mixed')
          .reduce((sum, hour) => sum + hour.mid.precipitationMm, 0) / total
        : 0;
      const reference = binForDate(baseline, new Date(stormDays[0].date));
      if (total >= reference.precipitationP90Mm) {
        const major = total >= reference.precipitationP98Mm;
        const name = frozenShare >= 0.65 && snow > 2
          ? (major ? 'Major Winter Storm' : 'Winter Storm')
          : frozenShare >= 0.2
            ? 'Mixed Precipitation Event'
            : 'Heavy Rain Event';
        events.push({
          id: `storm-${stormStart}`,
          type: major ? 'major-storm' : 'notable-storm',
          name,
          startsAt: stormDays[0].hours.find((hour) => hour.mid.precipitationMm > 0)?.at
            ?? stormDays[0].hours[0].at,
          endsAt: new Date(
            new Date(stormDays.at(-1)?.hours.at(-1)?.at ?? stormDays[0].hours[0].at).getTime() + HOUR_MS,
          ).toISOString(),
          severity: major ? 'major' : 'notable',
          bands: [...BANDS],
        });
      }
      stormStart = -1;
    }
  }
  addSequenceEvents(events, plan, 'cold-snap', 'cold-snap', 'Cold Snap', 2);
  addSequenceEvents(events, plan, 'warm-spell', 'warm-spell', 'Warm Spell', 2);
  addSequenceEvents(events, plan, 'dry-spell', 'dry-spell', 'Dry Spell', 3);

  const hours = plan.days.flatMap((day) => day.hours);
  for (const band of BANDS) {
    let lastFreezeThaw = -100;
    let lastFlashFreeze = -100;
    for (let i = 6; i < hours.length - 6; i += 1) {
      const recentWet = hours.slice(Math.max(0, i - 48), i + 1)
        .some((hour) => hour[band].precipitationMm > 0 || hour[band].snowfallCm > 0);
      const thaw = hours.slice(i - 5, i + 1).every((hour) => hour[band].temperatureC > 1);
      if (recentWet && thaw && i - lastFreezeThaw > 48) {
        let freezeAt = -1;
        for (let j = i + 1; j <= Math.min(hours.length - 6, i + 36); j += 1) {
          if (hours.slice(j, j + 6).every((hour) => hour[band].temperatureC < -2)) {
            freezeAt = j;
            break;
          }
        }
        if (freezeAt >= 0) {
          events.push({
            id: `freeze-thaw-${band}-${i}`,
            type: 'freeze-thaw',
            name: `Freeze/Thaw Cycle (${band})`,
            startsAt: hours[i - 5].at,
            endsAt: new Date(new Date(hours[freezeAt + 5].at).getTime() + HOUR_MS).toISOString(),
            severity: 'notable',
            bands: [band],
          });
          lastFreezeThaw = i;
        }
      }
      const sixHourDrop = hours[i - 6][band].temperatureC - hours[i][band].temperatureC;
      const crossedFreezing = hours[i - 6][band].temperatureC > 0 && hours[i][band].temperatureC <= 0;
      const wetBefore = hours.slice(i - 6, i + 1).some((hour) => hour[band].precipitationMm > 0);
      if (sixHourDrop >= 5 && crossedFreezing && wetBefore && i - lastFlashFreeze > 24) {
        events.push({
          id: `flash-freeze-${band}-${i}`,
          type: 'flash-freeze',
          name: `Flash Freeze (${band})`,
          startsAt: hours[i - 6].at,
          endsAt: new Date(new Date(hours[i].at).getTime() + HOUR_MS).toISOString(),
          severity: 'major',
          bands: [band],
        });
        lastFlashFreeze = i;
      }
    }
  }
  const sorted = events.sort((a, b) => a.startsAt.localeCompare(b.startsAt) || a.id.localeCompare(b.id));
  const merged: WeatherEvent[] = [];
  for (const event of sorted) {
    if (event.type !== 'freeze-thaw' && event.type !== 'flash-freeze') {
      merged.push(event);
      continue;
    }
    const prior = [...merged].reverse().find((candidate) =>
      candidate.type === event.type
      && Math.abs(new Date(candidate.startsAt).getTime() - new Date(event.startsAt).getTime()) <= 6 * HOUR_MS);
    if (!prior) {
      merged.push({
        ...event,
        id: event.id.replace(/-(base|mid|summit)-/, '-mountain-'),
        name: event.type === 'freeze-thaw' ? 'Freeze/Thaw Cycle' : 'Flash Freeze',
      });
      continue;
    }
    prior.startsAt = new Date(Math.min(
      new Date(prior.startsAt).getTime(),
      new Date(event.startsAt).getTime(),
    )).toISOString();
    prior.endsAt = new Date(Math.max(
      new Date(prior.endsAt).getTime(),
      new Date(event.endsAt).getTime(),
    )).toISOString();
    prior.bands = BANDS.filter((band) => prior.bands.includes(band) || event.bands.includes(band));
  }
  return merged.sort((a, b) => a.startsAt.localeCompare(b.startsAt) || a.id.localeCompare(b.id));
}

function findHour(plan: WeatherSeasonPlan, at: Date): MountainWeatherHour | undefined {
  const day = plan.days.find((candidate) => candidate.date === dateOnly(at));
  return day?.hours[at.getUTCHours()];
}

function forecastNoiseStd(leadHours: number): number {
  if (leadHours <= 72) return 0.5 + leadHours / 72;
  if (leadHours <= 168) return 1.5 + (leadHours - 72) / 48;
  return 3.5 + (leadHours - 168) / 96;
}

export function createForecast(
  plan: WeatherSeasonPlan,
  issuedAt: string,
  randomState: RandomState,
): ForecastIssue {
  const issue = new Date(issuedAt);
  const rng = new Xoshiro128(randomState);
  const forecasts: ForecastDay[] = [];
  const startOfIssueDay = new Date(issue);
  startOfIssueDay.setUTCHours(0, 0, 0, 0);

  for (let dayOffset = 0; dayOffset < 21; dayOffset += 1) {
    const date = new Date(startOfIssueDay.getTime() + dayOffset * DAY_MS);
    const predictedHours: ForecastHour[] = [];
    for (let hour = 0; hour < 24; hour += 1) {
      const at = new Date(date.getTime() + hour * HOUR_MS);
      const truth = findHour(plan, at);
      if (!truth) continue;
      const lead = Math.max(0, (at.getTime() - issue.getTime()) / HOUR_MS);
      const noise = forecastNoiseStd(lead);
      const sharedTempError = rng.normal(0, noise);
      const precipitationFactor = Math.max(0, 1 + rng.normal(0, 0.08 + lead / 900));
      const forecastBand = (band: ElevationBand): ForecastBandHour => {
        const actual = truth[band];
        const temperatureC = actual.temperatureC + sharedTempError + rng.normal(0, noise * 0.15);
        const precipitationMm = actual.precipitationMm * precipitationFactor;
        const type = precipitationType(temperatureC - 0.8, precipitationMm);
        const frozenFraction = type === 'snow' ? 1 : type === 'mixed' ? 0.5 : 0;
        return {
          temperatureC: round(temperatureC),
          precipitationMm: round(precipitationMm, 3),
          precipitationType: type,
          snowfallCm: round((precipitationMm * snowfallRatio(temperatureC) * frozenFraction) / 10),
        };
      };
      predictedHours.push({
        at: at.toISOString(),
        base: forecastBand('base'),
        mid: forecastBand('mid'),
        summit: forecastBand('summit'),
      });
    }
    if (!predictedHours.length) break;
    const leadHours = Math.max(0, (date.getTime() - issue.getTime()) / HOUR_MS);
    const precipitationHours = predictedHours.filter((hour) => hour.mid.precipitationMm > 0.01).length;
    const record = (selector: (hour: ForecastHour, band: ElevationBand) => number): Record<ElevationBand, number> =>
      Object.fromEntries(BANDS.map((band) => [
        band,
        round(predictedHours.reduce((sum, hour) => sum + selector(hour, band), 0)),
      ])) as Record<ElevationBand, number>;
    const minTempC = Object.fromEntries(BANDS.map((band) => [
      band,
      Math.min(...predictedHours.map((hour) => hour[band].temperatureC)),
    ])) as Record<ElevationBand, number>;
    const maxTempC = Object.fromEntries(BANDS.map((band) => [
      band,
      Math.max(...predictedHours.map((hour) => hour[band].temperatureC)),
    ])) as Record<ElevationBand, number>;
    const relevantEvent = plan.events.find((event) =>
      new Date(event.startsAt).getTime() < date.getTime() + DAY_MS
      && new Date(event.endsAt).getTime() > date.getTime());
    forecasts.push({
      date: dateOnly(date),
      confidencePct: Math.round(clamp(28, 96, 94 - Math.max(0, leadHours) * 0.18)),
      minTempC,
      maxTempC,
      snowfallCm: record((hour, band) => hour[band].snowfallCm),
      precipitationProbabilityPct: Math.round(clamp(5, 98, precipitationHours * 8 + rng.normal(0, 8))),
      eventSignal: relevantEvent?.name ?? null,
      hours: dayOffset <= 6 ? predictedHours : undefined,
    });
  }
  return {
    issuedAt: issue.toISOString(),
    days: forecasts,
    generalSignals: forecasts.slice(14).flatMap((day) =>
      day.eventSignal ? [`${day.date}: ${day.eventSignal} possible`] : []),
  };
}

export function createWeatherState(
  location: ResortWeatherLocation,
  climateBaseline: ResortClimateBaseline,
  startsAt: string,
  winterWeeks: number,
  seed: string,
  tuningProfile: WeatherTuningProfile = NORMAL_WEATHER_TUNING,
): WeatherState {
  const { plan, randomStreams } = generateWeatherSeason(
    climateBaseline,
    startsAt,
    winterWeeks,
    seed,
    tuningProfile,
  );
  const firstHour = plan.days[0].hours[0].at;
  return {
    schemaVersion: 1,
    generatorVersion: GENERATOR_VERSION,
    location,
    climateBaseline,
    tuningProfile: { ...tuningProfile },
    randomStreams,
    winterIdentifier: plan.winterIdentifier,
    seed,
    truth: plan,
    latestForecast: createForecast(plan, firstHour, randomStreams.forecastError),
    lastProcessedHour: new Date(new Date(firstHour).getTime() - HOUR_MS).toISOString(),
    activeEventIds: [],
    selectedBand: 'mid',
  };
}

function forecastIssueTimes(fromExclusive: Date, toInclusive: Date): Date[] {
  const results: Date[] = [];
  const cursor = new Date(fromExclusive);
  cursor.setUTCMinutes(0, 0, 0);
  cursor.setUTCHours(cursor.getUTCHours() < 6 ? 6 : cursor.getUTCHours() < 18 ? 18 : 30);
  while (cursor <= toInclusive) {
    if (cursor > fromExclusive) results.push(new Date(cursor));
    cursor.setUTCHours(cursor.getUTCHours() + 12);
  }
  return results;
}

export function advanceWeather(state: WeatherState, through: string): WeatherAdvanceResult {
  const from = new Date(state.lastProcessedHour);
  const requestedTo = new Date(through);
  if (Number.isNaN(requestedTo.getTime())) throw new Error('through must be a valid ISO date');
  const seasonEnd = new Date(state.truth.endsAt);
  const to = new Date(Math.min(requestedTo.getTime(), seasonEnd.getTime()));
  if (to <= from) {
    return { state, processedHours: [], startedEvents: [], endedEvents: [], forecastIssues: [] };
  }
  const processedHours = state.truth.days.flatMap((day) => day.hours).filter((hour) => {
    const at = new Date(hour.at);
    return at > from && at <= to;
  });
  const startedEvents = state.truth.events.filter((event) => {
    const at = new Date(event.startsAt);
    return at > from && at <= to;
  });
  const endedEvents = state.truth.events.filter((event) => {
    const at = new Date(event.endsAt);
    return at > from && at <= to;
  });
  const issueTimes = forecastIssueTimes(from, to);
  const forecastIssues = issueTimes.map((at) =>
    createForecast(state.truth, at.toISOString(), state.randomStreams.forecastError));
  const activeEventIds = state.truth.events.filter((event) =>
    new Date(event.startsAt) <= to && new Date(event.endsAt) > to).map((event) => event.id);
  const nextState: WeatherState = {
    ...state,
    latestForecast: forecastIssues.at(-1) ?? state.latestForecast,
    lastProcessedHour: new Date(Math.floor(to.getTime() / HOUR_MS) * HOUR_MS).toISOString(),
    activeEventIds,
  };
  return { state: nextState, processedHours, startedEvents, endedEvents, forecastIssues };
}

export function selectWeatherBand(state: WeatherState, band: ElevationBand): WeatherState {
  return { ...state, selectedBand: band };
}

export function createWeatherSnapshot(state: WeatherState): WeatherSnapshot {
  return structuredClone(state);
}

function validLocation(value: ResortWeatherLocation): boolean {
  return typeof value?.id === 'string'
    && typeof value?.name === 'string'
    && Number.isFinite(value?.latitude)
    && Number.isFinite(value?.longitude)
    && Number.isFinite(value?.baseElevationM)
    && Number.isFinite(value?.midElevationM)
    && Number.isFinite(value?.summitElevationM)
    && value.baseElevationM <= value.midElevationM
    && value.midElevationM <= value.summitElevationM;
}

export function restoreWeatherSnapshot(snapshot: unknown): WeatherState {
  if (!snapshot || typeof snapshot !== 'object') throw new Error('Weather snapshot must be an object');
  const candidate = snapshot as WeatherSnapshot;
  if (candidate.schemaVersion !== 1 || candidate.generatorVersion !== GENERATOR_VERSION) {
    throw new Error('Unsupported weather snapshot version');
  }
  if (!validLocation(candidate.location)) throw new Error('Weather snapshot has an invalid location');
  if (
    candidate.climateBaseline?.schemaVersion !== 1
    || candidate.climateBaseline.sourcePeriod?.startYear !== 2010
    || candidate.climateBaseline.sourcePeriod?.endYear !== 2019
    || candidate.climateBaseline.bins?.length !== 52
    || candidate.truth?.schemaVersion !== 1
  ) {
    throw new Error('Weather snapshot is missing climate or truth data');
  }
  if (!Array.isArray(candidate.truth.days) || candidate.truth.days.some((day) => day.hours.length !== 24)) {
    throw new Error('Weather snapshot contains invalid hourly truth');
  }
  if (Number.isNaN(new Date(candidate.lastProcessedHour).getTime())) {
    throw new Error('Weather snapshot has an invalid processed hour');
  }
  const validRandomState = (state: RandomState): boolean =>
    state != null && ['s0', 's1', 's2', 's3'].every((key) =>
      Number.isInteger(state[key as keyof RandomState])
      && state[key as keyof RandomState] >= 0
      && state[key as keyof RandomState] <= 0xffff_ffff);
  if (
    !candidate.randomStreams
    || !validRandomState(candidate.randomStreams.truth)
    || !validRandomState(candidate.randomStreams.hourlyShape)
    || !validRandomState(candidate.randomStreams.forecastError)
  ) {
    throw new Error('Weather snapshot contains invalid random streams');
  }
  if (!BANDS.includes(candidate.selectedBand) || !Array.isArray(candidate.activeEventIds)) {
    throw new Error('Weather snapshot contains invalid event or band state');
  }
  if (
    typeof candidate.seed !== 'string'
    || typeof candidate.winterIdentifier !== 'string'
    || !Array.isArray(candidate.latestForecast?.days)
    || Number.isNaN(new Date(candidate.latestForecast?.issuedAt).getTime())
  ) {
    throw new Error('Weather snapshot contains invalid forecast metadata');
  }
  return structuredClone(candidate);
}

export function weatherHourAt(plan: WeatherSeasonPlan, at: string): MountainWeatherHour | undefined {
  return findHour(plan, new Date(at));
}

export function nextWeatherEvent(state: WeatherState, after: string): WeatherEvent | undefined {
  const timestamp = new Date(after).getTime();
  return state.truth.events.find((event) => new Date(event.startsAt).getTime() > timestamp);
}
