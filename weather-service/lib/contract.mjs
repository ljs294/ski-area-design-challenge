import { createHash } from 'node:crypto';
import { WeatherServiceError, invariant } from './errors.mjs';

export const WEATHER_REQUEST_SCHEMA_VERSION = 1;
export const WEATHER_PACKAGE_SCHEMA_VERSION = 2;
export const DEFAULT_SOURCE_POLICY_VERSION = 'daymet-v4r1-merra2-ghcnh-v1';
export const HISTORICAL_START_YEAR = 1991;
export const HISTORICAL_END_YEAR = 2020;

const TERRAIN_KEY = /^[a-z0-9][a-z0-9_.-]*$/i;
const TIMEZONE = /^[A-Za-z_+\-/]+(?:\/[A-Za-z_+\-/]+)+$/;

/**
 * The source policy is intentionally limited to the fifty states.  These
 * rectangles are only an early rejection guard; each provider remains the
 * authority for land-mask/coverage validation.
 */
export function isSupportedUSPoint(latitude, longitude) {
  const lower48 = latitude >= 24.2 && latitude <= 49.6 && longitude >= -125.1 && longitude <= -66.5;
  const alaska = latitude >= 51.0 && latitude <= 72.8 && longitude >= -180 && longitude <= -129.0;
  const hawaii = latitude >= 18.5 && latitude <= 22.6 && longitude >= -161.2 && longitude <= -154.4;
  return lower48 || alaska || hawaii;
}

function finite(value) {
  return typeof value === 'number' && Number.isFinite(value);
}

function normalizedBounds(value) {
  if (value === undefined) return undefined;
  invariant(value && typeof value === 'object', 'INVALID_REQUEST', 'bounds must be an object when provided.');
  const bounds = value;
  const keys = ['west', 'south', 'east', 'north'];
  for (const key of keys) invariant(finite(bounds[key]), 'INVALID_REQUEST', `bounds.${key} must be finite.`);
  invariant(bounds.west <= bounds.east && bounds.south <= bounds.north, 'INVALID_REQUEST', 'bounds must be ordered west/south/east/north.');
  return { west: bounds.west, south: bounds.south, east: bounds.east, north: bounds.north };
}

/**
 * Validate and canonicalize the public request.  Keeping this small makes it
 * safe to persist and hash, while leaving future provider options out of the
 * player-facing contract.
 */
export function validateWeatherPackageRequest(value, { resolveTimezone = resolveUSIanaTimezone } = {}) {
  invariant(value && typeof value === 'object' && !Array.isArray(value), 'INVALID_REQUEST', 'Weather package request must be a JSON object.');
  const request = value;
  const schemaVersion = request.schemaVersion ?? WEATHER_REQUEST_SCHEMA_VERSION;
  invariant(schemaVersion === WEATHER_REQUEST_SCHEMA_VERSION, 'INVALID_REQUEST', `Unsupported weather request schema version ${String(schemaVersion)}.`);
  invariant(typeof request.terrainKey === 'string' && TERRAIN_KEY.test(request.terrainKey), 'INVALID_REQUEST', 'terrainKey contains unsupported characters.');
  invariant(typeof request.terrainBinding === 'string' && request.terrainBinding.length >= 8 && request.terrainBinding.length <= 256, 'INVALID_REQUEST', 'terrainBinding must be a stable non-empty map binding.');
  invariant(finite(request.latitude) && request.latitude >= -90 && request.latitude <= 90, 'INVALID_REQUEST', 'latitude must be between -90 and 90.');
  invariant(finite(request.longitude) && request.longitude >= -180 && request.longitude <= 180, 'INVALID_REQUEST', 'longitude must be between -180 and 180.');
  invariant(isSupportedUSPoint(request.latitude, request.longitude), 'UNSUPPORTED_LOCATION', 'Weather preparation currently supports locations in the 50 United States only.', {
    details: { latitude: request.latitude, longitude: request.longitude },
  });
  const requestedTimezone = request.timezone ?? 'auto';
  invariant(typeof requestedTimezone === 'string' && (requestedTimezone === 'auto' || requestedTimezone === 'UTC' || TIMEZONE.test(requestedTimezone)), 'INVALID_REQUEST', 'timezone must be auto or an IANA timezone such as America/Denver.');
  const timezone = requestedTimezone === 'auto' ? resolveTimezone(request.latitude, request.longitude) : requestedTimezone;
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: timezone });
  } catch {
    throw new WeatherServiceError('INVALID_REQUEST', `timezone '${timezone}' is not available in this runtime.`);
  }
  const historicalStartYear = request.historicalStartYear ?? HISTORICAL_START_YEAR;
  const historicalEndYear = request.historicalEndYear ?? HISTORICAL_END_YEAR;
  invariant(historicalStartYear === HISTORICAL_START_YEAR && historicalEndYear === HISTORICAL_END_YEAR,
    'INVALID_REQUEST', `The installed source policy only supports ${HISTORICAL_START_YEAR}–${HISTORICAL_END_YEAR}.`);
  const areaSizeMeters = request.areaSizeMeters;
  invariant(areaSizeMeters === undefined || (finite(areaSizeMeters) && areaSizeMeters > 0 && areaSizeMeters <= 200_000),
    'INVALID_REQUEST', 'areaSizeMeters must be between 0 and 200000 when provided.');
  const sourcePolicyVersion = request.sourcePolicyVersion ?? DEFAULT_SOURCE_POLICY_VERSION;
  invariant(typeof sourcePolicyVersion === 'string' && sourcePolicyVersion.length <= 64, 'INVALID_REQUEST', 'sourcePolicyVersion must be a short string.');
  return {
    schemaVersion: WEATHER_REQUEST_SCHEMA_VERSION,
    terrainKey: request.terrainKey,
    terrainBinding: request.terrainBinding,
    latitude: round(request.latitude, 6),
    longitude: round(request.longitude, 6),
    ...(normalizedBounds(request.bounds) === undefined ? {} : { bounds: normalizedBounds(request.bounds) }),
    ...(areaSizeMeters === undefined ? {} : { areaSizeMeters: round(areaSizeMeters, 3) }),
    timezone,
    timezoneResolution: requestedTimezone === 'auto' ? 'coordinate-resolved' : 'caller-specified',
    historicalStartYear,
    historicalEndYear,
    sourcePolicyVersion,
  };
}

/**
 * A no-network US resolver used by local fixture mode.  Production hosting may
 * inject an exact boundary resolver through validateWeatherPackageRequest.
 * The explicit resolution marker in the request/manifest prevents this small
 * geographic table from being mistaken for a station or provider observation.
 */
export function resolveUSIanaTimezone(latitude, longitude) {
  if (latitude >= 18.5 && latitude <= 22.6 && longitude >= -161.2 && longitude <= -154.4) return 'Pacific/Honolulu';
  if (latitude >= 51 && longitude <= -169) return 'America/Adak';
  if (latitude >= 51) return 'America/Anchorage';
  // Arizona is the only large lower-48 DST exception.  Navajo Nation is an
  // unavoidable boundary-level exception and must use caller-specified IANA.
  if (latitude >= 31 && latitude <= 37.2 && longitude >= -115 && longitude <= -108.7) return 'America/Phoenix';
  if (longitude < -114.15) return 'America/Los_Angeles';
  if (longitude < -101.45) return 'America/Denver';
  if (longitude < -86.25) return 'America/Chicago';
  return 'America/New_York';
}

export function packageRequestFingerprint(request) {
  return sha256(stableJson(request));
}

export function sourceCacheKey(provider, version, request, extra = {}) {
  const { location, ...rest } = extra;
  const latitude = location?.latitude ?? request.latitude;
  const longitude = location?.longitude ?? request.longitude;
  return sha256(stableJson({ provider, version, latitude, longitude,
    historicalStartYear: request.historicalStartYear, historicalEndYear: request.historicalEndYear, ...rest }));
}

export function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

export function stableJson(value) {
  if (value === undefined) return undefined;
  if (Array.isArray(value)) return `[${value.map((item) => stableJson(item) ?? 'null').join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value).sort().filter((key) => value[key] !== undefined)
      .map((key) => `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

function round(value, decimals) {
  const multiplier = 10 ** decimals;
  return Math.round(value * multiplier) / multiplier;
}
