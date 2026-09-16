import type { ResolvedWeatherHour } from './weatherModel';
import type { GameplayWeatherSession } from './weatherSession';
import { annualWeatherSeed, weatherYearDayCount, weatherYearStart } from './annualWeather';

export interface PreparedWeatherIdentity {
  readonly packageContentHash: string;
  readonly terrainBinding: string;
  readonly seed: string;
  readonly year: number;
  readonly timezone: string;
  readonly generatorVersion: number;
  readonly configurationVersion: number;
  readonly cacheFormatVersion: 1;
}

export interface PreparedAnnualWeather {
  readonly identity: PreparedWeatherIdentity;
  readonly session: GameplayWeatherSession;
  readonly resolvedHours: readonly ResolvedWeatherHour[];
  readonly averageAnnualSnowfallCm: number | null;
}

export interface PreparedWeatherCacheEnvelope {
  readonly cacheSchemaVersion: 1;
  readonly payload: PreparedAnnualWeather;
  readonly payloadSha256: string;
}

const IDENTITY_KEYS = [
  'packageContentHash', 'terrainBinding', 'seed', 'year', 'timezone',
  'generatorVersion', 'configurationVersion', 'cacheFormatVersion',
] as const;
const PREPARED_KEYS = ['identity', 'session', 'resolvedHours', 'averageAnnualSnowfallCm'] as const;

type UnknownRecord = Record<string, unknown>;

function isRecord(value: unknown): value is UnknownRecord {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function hasExactKeys(value: UnknownRecord, keys: readonly string[]): boolean {
  const actual = Object.keys(value).sort();
  return actual.length === keys.length && keys.every((key) => actual.includes(key));
}

function hasRequiredKeys(value: UnknownRecord, keys: readonly string[]): boolean {
  return keys.every((key) => Object.prototype.hasOwnProperty.call(value, key));
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** Returns true only for values that can be represented without loss by JSON. */
function jsonPayload(value: unknown, stack = new WeakSet<object>()): boolean {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return true;
  if (typeof value === 'number') return Number.isFinite(value);
  if (typeof value !== 'object' || stack.has(value)) return false;
  stack.add(value);
  try {
    if (Array.isArray(value)) return value.every((entry) => jsonPayload(entry, stack));
    if (!isRecord(value)) return false;
    return Object.keys(value).every((key) => jsonPayload(value[key], stack));
  } catch {
    return false;
  } finally {
    stack.delete(value);
  }
}

function canonicalJson(value: unknown): string {
  if (value === null || typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number') {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) {
    const entries = Array.from({ length: value.length }, (_, index) => index in value ? canonicalJson(value[index]) : 'null');
    return `[${entries.join(',')}]`;
  }
  if (!isRecord(value)) throw new Error('Invalid JSON payload.');
  return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
}

export function isPreparedWeatherIdentity(value: unknown): value is PreparedWeatherIdentity {
  if (!isRecord(value) || !hasExactKeys(value, IDENTITY_KEYS)) return false;
  return typeof value.packageContentHash === 'string' && value.packageContentHash.length > 0 &&
    typeof value.terrainBinding === 'string' && value.terrainBinding.length > 0 &&
    typeof value.seed === 'string' && value.seed.length > 0 &&
    Number.isInteger(value.year) && (value.year as number) >= 1 &&
    typeof value.timezone === 'string' && value.timezone.length > 0 &&
    Number.isInteger(value.generatorVersion) && (value.generatorVersion as number) >= 0 &&
    Number.isInteger(value.configurationVersion) && (value.configurationVersion as number) >= 0 &&
    value.cacheFormatVersion === 1;
}

/** Fixed field order makes the cache key independent of object insertion order. */
export function preparedWeatherIdentityKey(identity: PreparedWeatherIdentity): string {
  if (!isPreparedWeatherIdentity(identity)) throw new Error('Invalid prepared weather identity.');
  return JSON.stringify(IDENTITY_KEYS.map((key) => [key, identity[key]]));
}

function validDate(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(Date.parse(value));
}

function planIdentityMatches(plan: UnknownRecord, identity: PreparedWeatherIdentity): boolean {
  const checks: Array<[string, unknown]> = [
    ['packageContentHash', identity.packageContentHash],
    ['terrainBinding', identity.terrainBinding],
    ['seed', annualWeatherSeed(identity.seed, identity.year)],
    ['year', identity.year],
    ['timezone', identity.timezone],
    ['generatorVersion', identity.generatorVersion],
    ['configurationVersion', identity.configurationVersion],
    ['cacheFormatVersion', identity.cacheFormatVersion],
  ];
  for (const [key, expected] of checks) {
    if (key in plan && plan[key] !== expected) return false;
  }
  if ('identity' in plan) {
    if (!isPreparedWeatherIdentity(plan.identity)) return false;
    if (preparedWeatherIdentityKey(plan.identity) !== preparedWeatherIdentityKey(identity)) return false;
  }
  return true;
}

function validHourlyEntries(entries: unknown[], start: number, end: number, expectedLength: number): boolean {
  if (entries.length !== expectedLength || expectedLength <= 0) return false;
  let previous = -Infinity;
  for (let index = 0; index < entries.length; index += 1) {
    const hour = entries[index];
    if (!isRecord(hour) || !jsonPayload(hour) || typeof hour.at !== 'string' || !validDate(hour.at)) return false;
    const timestamp = Date.parse(hour.at);
    const expected = start + index * 3_600_000;
    if (timestamp !== expected || timestamp <= previous || timestamp > end) return false;
    previous = timestamp;
  }
  return previous === end;
}

function validPreparedWeather(value: unknown, expectedIdentity?: PreparedWeatherIdentity): value is PreparedAnnualWeather {
  if (!jsonPayload(value) || !isRecord(value) || !hasExactKeys(value, PREPARED_KEYS) ||
    !isPreparedWeatherIdentity(value.identity) ||
    (expectedIdentity && preparedWeatherIdentityKey(value.identity) !== preparedWeatherIdentityKey(expectedIdentity)) ||
    !isRecord(value.session) || !isRecord(value.session.plan) || !Array.isArray(value.resolvedHours)) {
    return false;
  }
  const identity = value.identity;
  const plan = value.session.plan;
  if (!hasRequiredKeys(plan, ['timezone', 'startsAt', 'endsAt', 'hours', 'seed', 'packageContentHash', 'generatorVersion']) ||
    plan.timezone !== identity.timezone || plan.seed !== annualWeatherSeed(identity.seed, identity.year) ||
    plan.packageContentHash !== identity.packageContentHash || plan.generatorVersion !== identity.generatorVersion ||
    !validDate(plan.startsAt) || !validDate(plan.endsAt) || !Array.isArray(plan.hours) ||
    !planIdentityMatches(plan, identity)) return false;

  let expectedStart: number;
  let expectedEnd: number;
  try {
    expectedStart = Date.parse(weatherYearStart(identity.year, identity.timezone));
    expectedEnd = Date.parse(weatherYearStart(identity.year + 1, identity.timezone));
  } catch {
    return false;
  }
  const start = Date.parse(plan.startsAt as string);
  const end = Date.parse(plan.endsAt as string);
  const expectedLength = (expectedEnd - expectedStart) / 3_600_000;
  const calendarDays = weatherYearDayCount(identity.year);
  const calendarSpanHours = (Date.UTC(identity.year + 1, 8, 1) - Date.UTC(identity.year, 8, 1)) / 3_600_000;
  if (start !== expectedStart || end !== expectedEnd - 3_600_000 ||
    !Number.isSafeInteger(calendarDays) || calendarDays < 365 || calendarDays > 366 ||
    calendarSpanHours !== calendarDays * 24 || !Number.isSafeInteger(expectedLength) || expectedLength <= 0 ||
    plan.hours.length !== expectedLength || value.resolvedHours.length !== expectedLength ||
    !validHourlyEntries(plan.hours, start, end, expectedLength) ||
    !validHourlyEntries(value.resolvedHours, start, end, expectedLength)) return false;

  for (let index = 0; index < plan.hours.length; index += 1) {
    const planHour = plan.hours[index];
    const resolvedHour = value.resolvedHours[index];
    if (!isRecord(planHour) || !isRecord(resolvedHour) || planHour.at !== resolvedHour.at) return false;
  }
  return value.averageAnnualSnowfallCm === null ||
    (isFiniteNumber(value.averageAnnualSnowfallCm) && value.averageAnnualSnowfallCm >= 0);
}

export function isPreparedAnnualWeather(value: unknown, expectedIdentity?: PreparedWeatherIdentity): value is PreparedAnnualWeather {
  return validPreparedWeather(value, expectedIdentity);
}

export function preparedWeatherPayloadJson(prepared: PreparedAnnualWeather): string {
  if (!isPreparedAnnualWeather(prepared, prepared.identity)) throw new Error('Invalid prepared weather payload.');
  return canonicalJson(prepared);
}

export function isPreparedWeatherCacheEnvelope(
  value: unknown,
  expectedIdentity?: PreparedWeatherIdentity,
): value is PreparedWeatherCacheEnvelope {
  if (!isRecord(value) || !hasExactKeys(value, ['cacheSchemaVersion', 'payload', 'payloadSha256']) ||
    value.cacheSchemaVersion !== 1 || typeof value.payloadSha256 !== 'string' ||
    !/^[a-f0-9]{64}$/i.test(value.payloadSha256) ||
    !isPreparedAnnualWeather(value.payload, expectedIdentity)) return false;
  return true;
}
