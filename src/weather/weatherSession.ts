import {
  detectWeatherEvents,
  isWeatherDataPackage,
  precipitationTypeFor,
  type HistoricalWeatherYear,
  type ResolvedWeatherHour,
  type SyntheticWeatherPlan,
  type WeatherCoordinates,
  type WeatherDataPackage,
  type WeatherEvent,
  type WeatherHourProvenance,
  type WeatherReferenceHour,
} from './weatherModel';
import {
  addWeatherLocalTime, isWeatherTimezone, localWeatherClockOffsetKey, localWeatherDateKey,
  localWeatherTimeOffsetKey, weatherLocalParts,
} from './localTime';
import { decodeWeatherChunk } from './weatherChunks';

const HOUR_MS = 3_600_000;

export interface WeatherSessionOptions {
  seed: string;
  startsAt: string;
  /** A local-calendar duration. DST years therefore contain 8,759 or 8,761 hours. */
  days?: number;
  timezone?: string;
  latitude?: number;
  longitude?: number;
}

export interface WeatherSolarPosition {
  elevationDeg: number;
  azimuthDeg: number;
}

export interface WeatherForecast {
  issuedAt: string;
  endsAt: string;
  hours: readonly ResolvedWeatherHour[];
  events: readonly WeatherEvent[];
}

export interface WeatherSessionSnapshot {
  cursor: string;
  current: ResolvedWeatherHour | null;
  forecast: WeatherForecast;
  events: readonly WeatherEvent[];
}

/**
 * A fully pure runtime object. It contains no clock, network, storage, or
 * mutable cursor; a game clock or Lab playback controller supplies timestamps.
 */
export interface WeatherSession {
  readonly weatherPackage: WeatherDataPackage;
  /** Checksum-validated decoded archive retained only for this runtime session. */
  readonly historicalYears: readonly HistoricalWeatherYear[];
  readonly plan: SyntheticWeatherPlan;
  readonly timezone: string;
  readonly midpoint?: WeatherCoordinates;
}

interface SourceDay {
  dateKey: string;
  month: number;
  day: number;
  /** Includes local UTC offset, retaining both repeated fall-back clock hours. */
  hoursByLocalTime: ReadonlyMap<string, WeatherReferenceHour>;
  /** Used only as an explicit last-resort for incomplete legacy archives. */
  hoursByLocalHour: ReadonlyMap<number, readonly WeatherReferenceHour[]>;
}

function seeded(seed: string): () => number {
  let state = 2166136261;
  for (let index = 0; index < seed.length; index += 1) {
    state ^= seed.charCodeAt(index);
    state = Math.imul(state, 16777619);
  }
  return () => {
    state += 0x6D2B79F5;
    let value = state;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4294967296;
  };
}

function round(value: number, digits = 1): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

function sourceDays(historicalYears: readonly HistoricalWeatherYear[], timezone: string): readonly SourceDay[] {
  const groups = new Map<string, {
    month: number;
    day: number;
    hoursByLocalTime: Map<string, WeatherReferenceHour>;
    hoursByLocalHour: Map<number, WeatherReferenceHour[]>;
  }>();
  for (const year of historicalYears) for (const hour of year.hours) {
    const local = weatherLocalParts(hour.at, timezone);
    const key = `${local.year}-${local.month}-${local.day}`;
    const group = groups.get(key) ?? {
      month: local.month, day: local.day, hoursByLocalTime: new Map<string, WeatherReferenceHour>(),
      hoursByLocalHour: new Map<number, WeatherReferenceHour[]>(),
    };
    group.hoursByLocalTime.set(localWeatherTimeOffsetKey(hour.at, timezone), hour);
    const sameHour = group.hoursByLocalHour.get(local.hour) ?? [];
    sameHour.push(hour);
    group.hoursByLocalHour.set(local.hour, sameHour);
    groups.set(key, group);
  }
  return [...groups.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([dateKey, group]) => ({
      dateKey,
      month: group.month,
      day: group.day,
      hoursByLocalTime: group.hoursByLocalTime,
      hoursByLocalHour: group.hoursByLocalHour,
    }));
}

function sourceCoversClockKeys(source: SourceDay, requiredClockKeys: ReadonlySet<string>): boolean {
  return [...requiredClockKeys].every((key) => source.hoursByLocalTime.has(key));
}

function sourceForTargetDate(
  sources: readonly SourceDay[],
  target: { month: number; day: number },
  requiredClockKeys: ReadonlySet<string>,
  random: () => number,
): SourceDay {
  const nearSeason = sources.filter((source) => calendarDistance(source, target) <= 16);
  const seasonalCandidates = nearSeason.length > 0 ? nearSeason : sources;
  const exactClockCandidates = seasonalCandidates.filter((source) => sourceCoversClockKeys(source, requiredClockKeys));
  const candidates = exactClockCandidates.length > 0 ? exactClockCandidates : seasonalCandidates;
  return candidates[Math.floor(random() * candidates.length)];
}

function calendarDistance(left: { month: number; day: number }, right: { month: number; day: number }): number {
  const dayOfLeapYear = ({ month, day }: { month: number; day: number }) =>
    Math.round((Date.UTC(2000, month - 1, day) - Date.UTC(1999, 11, 31)) / 86_400_000);
  const leftDay = dayOfLeapYear(left);
  const rightDay = dayOfLeapYear(right);
  return Math.min(Math.abs(leftDay - rightDay), 366 - Math.abs(leftDay - rightDay));
}

function hourlyTimeline(startsAt: string, timezone: string, days: number): readonly string[] {
  const start = new Date(startsAt);
  if (Number.isNaN(start.getTime())) throw new Error('Weather session start time is invalid.');
  const end = new Date(addWeatherLocalTime(start.toISOString(), timezone, { days }));
  const hours: string[] = [];
  for (let instant = start.getTime(); instant < end.getTime(); instant += HOUR_MS) hours.push(new Date(instant).toISOString());
  return hours;
}

function timelineClockKeysByDate(timeline: readonly string[], timezone: string): ReadonlyMap<string, ReadonlySet<string>> {
  const keys = new Map<string, Set<string>>();
  for (const at of timeline) {
    const date = localWeatherDateKey(at, timezone);
    const day = keys.get(date) ?? new Set<string>();
    day.add(localWeatherTimeOffsetKey(at, timezone));
    keys.set(date, day);
  }
  return keys;
}

function cloneAnalogHour(hour: WeatherReferenceHour, at: string, anomaly: number): WeatherReferenceHour {
  const temperatureC = round(hour.temperatureC + anomaly);
  const wetBulbC = round(hour.wetBulbC + anomaly);
  const precipitationType = precipitationTypeFor(temperatureC, wetBulbC, hour.precipitationMm);
  return {
    ...hour,
    at,
    temperatureC,
    wetBulbC,
    precipitationType,
    snowfallCm: precipitationType === 'snow' ? hour.snowfallCm : precipitationType === 'mixed' ? round(hour.snowfallCm * 0.5, 2) : 0,
  };
}

/** Generate local-calendar analog blocks so DST does not fabricate or drop an hour. */
function generateSessionPlan(
  weatherPackage: WeatherDataPackage,
  historicalYears: readonly HistoricalWeatherYear[],
  options: Required<Pick<WeatherSessionOptions, 'seed' | 'startsAt' | 'days'>> & { timezone: string },
): SyntheticWeatherPlan {
  const sources = sourceDays(historicalYears, options.timezone);
  if (sources.length === 0) throw new Error('Weather package has no decoded hourly history.');
  const random = seeded(options.seed);
  const timeline = hourlyTimeline(options.startsAt, options.timezone, options.days);
  const clockKeysByDate = timelineClockKeysByDate(timeline, options.timezone);
  const output: WeatherReferenceHour[] = [];
  let activeSourceIndex = 0;
  let remainingDays = 0;
  let anomaly = 0;
  let lastTargetDate = '';
  for (const at of timeline) {
    const target = weatherLocalParts(at, options.timezone);
    const targetDate = localWeatherDateKey(at, options.timezone);
    if (targetDate !== lastTargetDate) {
      const requiredClockKeys = clockKeysByDate.get(targetDate) ?? new Set<string>();
      let selectedNewBlock = false;
      if (remainingDays <= 0) {
        const selected = sourceForTargetDate(sources, target, requiredClockKeys, random);
        activeSourceIndex = sources.indexOf(selected);
        remainingDays = 2 + Math.floor(random() * 4);
        anomaly = round((random() - 0.5) * 2, 1);
        selectedNewBlock = true;
      }
      if (!selectedNewBlock && lastTargetDate) activeSourceIndex = (activeSourceIndex + 1) % sources.length;
      if (!sourceCoversClockKeys(sources[activeSourceIndex], requiredClockKeys)) {
        activeSourceIndex = sources.indexOf(sourceForTargetDate(sources, target, requiredClockKeys, random));
      }
      lastTargetDate = targetDate;
      remainingDays -= 1;
    }
    const source = sources[activeSourceIndex % sources.length];
    const sourceHour = source.hoursByLocalTime.get(localWeatherTimeOffsetKey(at, options.timezone)) ??
      source.hoursByLocalHour.get(target.hour)?.[0] ?? source.hoursByLocalTime.values().next().value as WeatherReferenceHour | undefined;
    if (!sourceHour) throw new Error(`Weather source day ${source.dateKey} has no hourly data.`);
    output.push(cloneAnalogHour(sourceHour, at, anomaly));
  }
  return {
    seed: options.seed,
    startsAt: timeline[0] ?? new Date(options.startsAt).toISOString(),
    endsAt: output.at(-1)?.at ?? new Date(options.startsAt).toISOString(),
    timezone: options.timezone,
    packageContentHash: weatherPackage.manifest.contentHash,
    generatorVersion: weatherPackage.manifest.generatorVersion,
    hours: output,
    events: detectWeatherEvents(output),
  };
}

function createWeatherSessionFromHistory(
  weatherPackage: WeatherDataPackage,
  historicalYears: readonly HistoricalWeatherYear[],
  options: WeatherSessionOptions,
): WeatherSession {
  if (!isWeatherDataPackage(weatherPackage)) throw new Error('Offline weather package failed structural validation.');
  if (!weatherPackage.manifest.complete || historicalYears.length === 0) {
    throw new Error('A complete offline weather package is required before a weather session can start.');
  }
  const timezone = options.timezone ?? weatherPackage.manifest.timezone;
  if (!isWeatherTimezone(timezone)) throw new Error(`Weather package has no valid IANA timezone: ${timezone}`);
  const midpoint = Number.isFinite(options.latitude) && Number.isFinite(options.longitude)
    ? { latitude: options.latitude!, longitude: options.longitude! }
    : weatherPackage.manifest.midpoint;
  const plan = generateSessionPlan(weatherPackage, historicalYears, {
    seed: options.seed,
    startsAt: options.startsAt,
    days: Math.max(1, Math.floor(options.days ?? 366)),
    timezone,
  });
  return Object.freeze({ weatherPackage, historicalYears, plan, timezone, ...(midpoint ? { midpoint } : {}) });
}

/** Create a session synchronously when a compatibility cache is already decoded. */
export function createWeatherSession(weatherPackage: WeatherDataPackage, options: WeatherSessionOptions): WeatherSession {
  if (weatherPackage.manifest.schemaVersion === 2) {
    throw new Error('Immutable v2 weather packages must be checksum-decoded with loadWeatherSession before simulation.');
  }
  if (!weatherPackage.historicalYears?.length) {
    throw new Error('Chunk-only weather packages must be loaded with loadWeatherSession before simulation.');
  }
  return createWeatherSessionFromHistory(weatherPackage, weatherPackage.historicalYears, options);
}

/**
 * Verify and decode an immutable v2 archive into ephemeral runtime memory. The
 * installed package remains chunk-only and no provider is ever contacted.
 */
export async function loadWeatherSession(weatherPackage: WeatherDataPackage, options: WeatherSessionOptions): Promise<WeatherSession> {
  if (!isWeatherDataPackage(weatherPackage)) throw new Error('Offline weather package failed structural validation.');
  if (weatherPackage.manifest.schemaVersion === 1) {
    if (!weatherPackage.historicalYears?.length) throw new Error('Legacy weather package has no decoded hourly history.');
    return createWeatherSessionFromHistory(weatherPackage, weatherPackage.historicalYears, options);
  }
  if (!weatherPackage.chunks || weatherPackage.chunks.length === 0) {
    throw new Error('Weather package has neither decoded history nor binary chunks.');
  }
  const decoded = await Promise.all(weatherPackage.chunks.map(decodeWeatherChunk));
  const historicalYears = decoded
    .sort((left, right) => left.descriptor.year - right.descriptor.year)
    .map((chunk) => ({ year: chunk.descriptor.year, hours: chunk.hours }));
  return createWeatherSessionFromHistory(weatherPackage, historicalYears, options);
}

export function wetBulbTemperatureC(temperatureC: number, humidityPct: number): number {
  const humidity = clamp(humidityPct, 1, 100);
  // Stull (2011), accurate enough for hourly phase classification in this model.
  return temperatureC * Math.atan(0.151977 * Math.sqrt(humidity + 8.313659)) +
    Math.atan(temperatureC + humidity) - Math.atan(humidity - 1.676331) +
    0.00391838 * humidity ** 1.5 * Math.atan(0.023101 * humidity) - 4.686035;
}

/** NOAA-style approximate solar geometry; provider radiation remains authoritative. */
export function solarPosition(at: string, coordinate: WeatherCoordinates): WeatherSolarPosition {
  const date = new Date(at);
  const yearStart = Date.UTC(date.getUTCFullYear(), 0, 1);
  const day = Math.floor((date.getTime() - yearStart) / 86_400_000) + 1;
  const minutes = date.getUTCHours() * 60 + date.getUTCMinutes() + date.getUTCSeconds() / 60;
  const gamma = 2 * Math.PI / 365 * (day - 1 + (minutes - 720) / 1440);
  const equationOfTime = 229.18 * (0.000075 + 0.001868 * Math.cos(gamma) - 0.032077 * Math.sin(gamma) -
    0.014615 * Math.cos(2 * gamma) - 0.040849 * Math.sin(2 * gamma));
  const declination = 0.006918 - 0.399912 * Math.cos(gamma) + 0.070257 * Math.sin(gamma) -
    0.006758 * Math.cos(2 * gamma) + 0.000907 * Math.sin(2 * gamma) -
    0.002697 * Math.cos(3 * gamma) + 0.00148 * Math.sin(3 * gamma);
  const trueSolarMinutes = ((minutes + equationOfTime + 4 * coordinate.longitude) % 1440 + 1440) % 1440;
  const hourAngle = (trueSolarMinutes / 4 - 180) * Math.PI / 180;
  const latitude = coordinate.latitude * Math.PI / 180;
  const cosineZenith = clamp(Math.sin(latitude) * Math.sin(declination) +
    Math.cos(latitude) * Math.cos(declination) * Math.cos(hourAngle), -1, 1);
  const elevationDeg = 90 - Math.acos(cosineZenith) * 180 / Math.PI;
  const azimuthDeg = (Math.atan2(
    Math.sin(hourAngle),
    Math.cos(hourAngle) * Math.sin(latitude) - Math.tan(declination) * Math.cos(latitude),
  ) * 180 / Math.PI + 180 + 360) % 360;
  return { elevationDeg, azimuthDeg };
}

function resolvedProvenance(hour: WeatherReferenceHour): WeatherHourProvenance {
  return hour.provenance ?? { fieldFlags: 0, fields: {} };
}

/**
 * Complete a provider-normalized hour for runtime use. This never invents an
 * alternate weather source: absent derived values are marked `derived` through
 * provenance supplied by the chunk descriptor, while provider values persist.
 */
export function resolveWeatherHour(hour: WeatherReferenceHour, midpoint?: WeatherCoordinates): ResolvedWeatherHour {
  const humidityPct = clamp(Number.isFinite(hour.humidityPct) ? hour.humidityPct : 0, 0, 100);
  const temperatureC = Number.isFinite(hour.temperatureC) ? hour.temperatureC : 0;
  const wetBulbC = Number.isFinite(hour.wetBulbC) ? hour.wetBulbC : round(wetBulbTemperatureC(temperatureC, humidityPct));
  const windSpeedKph = Math.max(0, Number.isFinite(hour.windSpeedKph) ? hour.windSpeedKph : 0);
  const windDirectionDeg = ((Number.isFinite(hour.windDirectionDeg) ? hour.windDirectionDeg : 0) % 360 + 360) % 360;
  const windRadians = windDirectionDeg * Math.PI / 180;
  const windUms = Number.isFinite(hour.windUms) ? hour.windUms! : -windSpeedKph / 3.6 * Math.sin(windRadians);
  const windVms = Number.isFinite(hour.windVms) ? hour.windVms! : -windSpeedKph / 3.6 * Math.cos(windRadians);
  const cloudCoverPct = clamp(Number.isFinite(hour.cloudCoverPct) ? hour.cloudCoverPct : 0, 0, 100);
  const position = midpoint ? solarPosition(hour.at, midpoint) : {
    elevationDeg: Number.isFinite(hour.solarElevationDeg) ? hour.solarElevationDeg! : 0,
    azimuthDeg: Number.isFinite(hour.solarAzimuthDeg) ? hour.solarAzimuthDeg! : 0,
  };
  const globalRadiationWm2 = Math.max(0, Number.isFinite(hour.globalRadiationWm2) ? hour.globalRadiationWm2! : hour.radiationWm2);
  const cloudTransmissionPct = clamp(
    Number.isFinite(hour.cloudTransmissionPct) ? hour.cloudTransmissionPct! : 100 * (1 - 0.75 * (cloudCoverPct / 100) ** 3.4),
    0,
    100,
  );
  const diffuseRadiationWm2 = Math.max(0, Number.isFinite(hour.diffuseRadiationWm2)
    ? hour.diffuseRadiationWm2! : globalRadiationWm2 * clamp(0.2 + 0.65 * cloudCoverPct / 100, 0.15, 0.95));
  const directHorizontal = Math.max(0, globalRadiationWm2 - diffuseRadiationWm2);
  const directRadiationWm2 = Math.max(0, Number.isFinite(hour.directRadiationWm2)
    ? hour.directRadiationWm2! : position.elevationDeg > 0.5 ? directHorizontal / Math.sin(position.elevationDeg * Math.PI / 180) : 0);
  const precipitationMm = Math.max(0, Number.isFinite(hour.precipitationMm) ? hour.precipitationMm : 0);
  const precipitationType = precipitationTypeFor(temperatureC, wetBulbC, precipitationMm);
  return {
    ...hour,
    temperatureC,
    wetBulbC,
    humidityPct,
    windSpeedKph,
    windDirectionDeg,
    windUms,
    windVms,
    precipitationMm,
    precipitationType,
    snowWaterEquivalentMm: Math.max(0, Number.isFinite(hour.snowWaterEquivalentMm) ? hour.snowWaterEquivalentMm! : 0),
    globalRadiationWm2,
    radiationWm2: globalRadiationWm2,
    directRadiationWm2,
    diffuseRadiationWm2,
    cloudTransmissionPct,
    solarElevationDeg: position.elevationDeg,
    solarAzimuthDeg: position.azimuthDeg,
    provenance: resolvedProvenance(hour),
  };
}

function indexAtOrBefore(hours: readonly WeatherReferenceHour[], at: string): number {
  const target = new Date(at).getTime();
  if (!Number.isFinite(target) || hours.length === 0) return -1;
  let low = 0;
  let high = hours.length - 1;
  let answer = -1;
  while (low <= high) {
    const middle = Math.floor((low + high) / 2);
    if (new Date(hours[middle].at).getTime() <= target) {
      answer = middle;
      low = middle + 1;
    } else high = middle - 1;
  }
  return answer;
}

export function clampWeatherSessionCursor(session: WeatherSession, cursor: string): string {
  const start = new Date(session.plan.startsAt).getTime();
  const end = new Date(session.plan.endsAt).getTime();
  const candidate = new Date(cursor).getTime();
  if (!Number.isFinite(candidate)) return session.plan.startsAt;
  return new Date(clamp(candidate, start, end)).toISOString();
}

export function weatherAtSession(session: WeatherSession, cursor: string): ResolvedWeatherHour | null {
  const index = indexAtOrBefore(session.plan.hours, clampWeatherSessionCursor(session, cursor));
  const hour = index >= 0 ? session.plan.hours[index] : null;
  return hour ? resolveWeatherHour(hour, session.midpoint) : null;
}

function historicalHourAt(year: HistoricalWeatherYear, cursor: string, timezone: string): WeatherReferenceHour | null {
  const target = localWeatherClockOffsetKey(cursor, timezone);
  const targetWallClock = target.slice(0, target.lastIndexOf('@'));
  const matches = year.hours.filter((hour) => {
    const candidate = localWeatherClockOffsetKey(hour.at, timezone);
    return candidate.slice(0, candidate.lastIndexOf('@')) === targetWallClock;
  });
  // Most local dates have one matching hour. On a fall-back date, retain the
  // matching offset so both 01:00 records remain independently comparable.
  // Historical U.S. DST rules changed in 2007, though, so a normal date can
  // legitimately have a different offset in the selected archive year.
  return matches.find((hour) => localWeatherClockOffsetKey(hour.at, timezone) === target) ?? matches[0] ?? null;
}

export function historicalAtSession(session: WeatherSession, year: number, cursor: string): ResolvedWeatherHour | null {
  const sourceYear = session.historicalYears.find((candidate) => candidate.year === year);
  const hour = sourceYear ? historicalHourAt(sourceYear, cursor, session.timezone) : null;
  return hour ? resolveWeatherHour(hour, session.midpoint) : null;
}

export function forecastForSession(session: WeatherSession, cursor: string, hours = 72): WeatherForecast {
  const clamped = clampWeatherSessionCursor(session, cursor);
  const startIndex = indexAtOrBefore(session.plan.hours, clamped);
  const entries = startIndex < 0 ? [] : session.plan.hours.slice(startIndex, startIndex + Math.max(0, Math.floor(hours)))
    .map((hour) => resolveWeatherHour(hour, session.midpoint));
  const endsAt = entries.at(-1)?.at ?? clamped;
  const events = session.plan.events.filter((event) => event.startsAt <= endsAt && event.endsAt >= clamped);
  return { issuedAt: clamped, endsAt, hours: entries, events };
}

/** A pure seek recomputes read models from the immutable plan, so backward scrub is safe. */
export function seekWeatherSession(session: WeatherSession, cursor: string, forecastHours = 72): WeatherSessionSnapshot {
  const clamped = clampWeatherSessionCursor(session, cursor);
  const forecast = forecastForSession(session, clamped, forecastHours);
  return {
    cursor: clamped,
    current: weatherAtSession(session, clamped),
    forecast,
    events: session.plan.events.filter((event) => event.startsAt <= clamped && event.endsAt >= clamped),
  };
}
