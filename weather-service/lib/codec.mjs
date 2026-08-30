import { gzipSync, gunzipSync } from 'node:zlib';
import { WeatherServiceError, invariant } from './errors.mjs';
import { sha256 } from './contract.mjs';

/** Must remain byte-for-byte compatible with src/weather/weatherChunks.ts. */
export const WEATHER_CHUNK_FORMAT = 'weather-hour-v2';
const MAGIC = 0x57483232; // "WH22", little endian
const VERSION = 2;
const HEADER_BYTES = 16;
const RECORD_BYTES = 96;

const PHASE_TO_CODE = Object.freeze({ none: 0, rain: 1, mixed: 2, snow: 3, 'freezing-rain': 4 });
const CODE_TO_PHASE = Object.freeze(['none', 'rain', 'mixed', 'snow', 'freezing-rain']);

function finiteOrNaN(value) {
  return Number.isFinite(value) ? value : Number.NaN;
}

function finiteOrZero(value) {
  return Number.isFinite(value) ? value : 0;
}

/**
 * Encode the renderer's portable v2 record format. The core decoder owns the
 * browser implementation; this Node equivalent makes the builder transport
 * interoperable without importing UI/runtime code into the service.
 */
export function encodeWeatherHours(hours, year, fieldProvenance = {}) {
  invariant(Array.isArray(hours) && hours.length > 0, 'PACKAGE_INTEGRITY', `Weather chunk ${year} has no hours.`);
  const raw = Buffer.allocUnsafe(HEADER_BYTES + hours.length * RECORD_BYTES);
  raw.writeUInt32LE(MAGIC, 0);
  raw.writeUInt16LE(VERSION, 4);
  raw.writeUInt16LE(0, 6);
  raw.writeUInt32LE(hours.length, 8);
  raw.writeUInt16LE(RECORD_BYTES, 12);
  raw.writeUInt16LE(0, 14);
  for (let index = 0; index < hours.length; index += 1) {
    const hour = hours[index];
    const offset = HEADER_BYTES + index * RECORD_BYTES;
    const at = Date.parse(hour.at);
    invariant(Number.isFinite(at), 'PACKAGE_INTEGRITY', `Weather chunk ${year} hour ${index} has an invalid timestamp.`);
    raw.writeDoubleLE(at, offset);
    raw.writeFloatLE(finiteOrNaN(hour.temperatureC), offset + 8);
    raw.writeFloatLE(finiteOrNaN(hour.wetBulbC), offset + 12);
    raw.writeFloatLE(finiteOrNaN(hour.humidityPct), offset + 16);
    raw.writeFloatLE(finiteOrNaN(hour.precipitationMm), offset + 20);
    raw.writeFloatLE(finiteOrNaN(hour.snowfallCm), offset + 24);
    raw.writeFloatLE(finiteOrNaN(hour.windSpeedKph), offset + 28);
    raw.writeFloatLE(finiteOrNaN(hour.windGustKph), offset + 32);
    raw.writeFloatLE(finiteOrNaN(hour.windDirectionDeg), offset + 36);
    raw.writeFloatLE(finiteOrNaN(hour.cloudCoverPct), offset + 40);
    raw.writeFloatLE(finiteOrNaN(hour.visibilityKm), offset + 44);
    raw.writeFloatLE(finiteOrNaN(hour.pressureHpa), offset + 48);
    raw.writeFloatLE(finiteOrNaN(hour.radiationWm2), offset + 52);
    raw.writeFloatLE(finiteOrNaN(hour.windUms), offset + 56);
    raw.writeFloatLE(finiteOrNaN(hour.windVms), offset + 60);
    raw.writeFloatLE(finiteOrNaN(hour.globalRadiationWm2), offset + 64);
    raw.writeFloatLE(finiteOrNaN(hour.directRadiationWm2), offset + 68);
    raw.writeFloatLE(finiteOrNaN(hour.diffuseRadiationWm2), offset + 72);
    raw.writeFloatLE(finiteOrNaN(hour.cloudTransmissionPct), offset + 76);
    raw.writeFloatLE(finiteOrNaN(hour.snowWaterEquivalentMm), offset + 80);
    raw.writeFloatLE(finiteOrNaN(hour.solarElevationDeg), offset + 84);
    raw.writeFloatLE(finiteOrNaN(hour.solarAzimuthDeg), offset + 88);
    raw.writeUInt8(PHASE_TO_CODE[hour.precipitationType] ?? 0, offset + 92);
    raw.writeUInt8(0, offset + 93);
    raw.writeUInt16LE(finiteOrZero(hour.provenance?.fieldFlags) & 0xffff, offset + 94);
  }
  const compressed = gzipSync(raw, { level: 9, mtime: 0 });
  return {
    descriptor: {
      id: String(year), year, startsAt: hours[0].at, endsAt: hours.at(-1).at,
      encoding: 'gzip', format: WEATHER_CHUNK_FORMAT, checksumSha256: sha256(compressed),
      byteLength: compressed.byteLength, uncompressedByteLength: raw.byteLength, recordCount: hours.length, fieldProvenance,
    },
    data: compressed,
  };
}

function asOptional(value) {
  return Number.isFinite(value) ? value : undefined;
}

export function decodeWeatherChunk(data, descriptor) {
  const compressed = Buffer.isBuffer(data) ? data : Buffer.from(data);
  if (descriptor?.checksumSha256 && sha256(compressed) !== descriptor.checksumSha256) {
    throw new WeatherServiceError('PACKAGE_INTEGRITY', `Weather chunk ${descriptor.id ?? 'unknown'} failed checksum validation.`);
  }
  let raw;
  try { raw = descriptor?.encoding === 'identity' ? compressed : gunzipSync(compressed); } catch (cause) {
    throw new WeatherServiceError('PACKAGE_INTEGRITY', 'Weather chunk cannot be decompressed.', { cause });
  }
  invariant(raw.byteLength >= HEADER_BYTES && raw.readUInt32LE(0) === MAGIC && raw.readUInt16LE(4) === VERSION,
    'PACKAGE_INTEGRITY', 'Weather chunk has an invalid v2 header.');
  const recordCount = raw.readUInt32LE(8);
  invariant(raw.readUInt16LE(12) === RECORD_BYTES && raw.byteLength === HEADER_BYTES + recordCount * RECORD_BYTES,
    'PACKAGE_INTEGRITY', 'Weather chunk record layout does not match its header.');
  if (descriptor) {
    invariant(descriptor.format === WEATHER_CHUNK_FORMAT, 'PACKAGE_INTEGRITY', 'Weather chunk format does not match its descriptor.');
    invariant(descriptor.recordCount === recordCount, 'PACKAGE_INTEGRITY', 'Weather chunk record count does not match its descriptor.');
    invariant(descriptor.uncompressedByteLength === raw.byteLength, 'PACKAGE_INTEGRITY', 'Weather chunk byte length does not match its descriptor.');
  }
  const hours = [];
  for (let index = 0; index < recordCount; index += 1) {
    const offset = HEADER_BYTES + index * RECORD_BYTES;
    const precipitationMm = finiteOrZero(raw.readFloatLE(offset + 20));
    const phase = precipitationMm <= 0.001 ? 'none' : (CODE_TO_PHASE[raw.readUInt8(offset + 92)] ?? 'none');
    const optional = (byteOffset) => asOptional(raw.readFloatLE(offset + byteOffset));
    const hour = {
      at: new Date(raw.readDoubleLE(offset)).toISOString(),
      temperatureC: finiteOrZero(raw.readFloatLE(offset + 8)), wetBulbC: finiteOrZero(raw.readFloatLE(offset + 12)),
      humidityPct: finiteOrZero(raw.readFloatLE(offset + 16)), precipitationMm, precipitationType: phase,
      snowfallCm: finiteOrZero(raw.readFloatLE(offset + 24)), windSpeedKph: finiteOrZero(raw.readFloatLE(offset + 28)),
      windGustKph: finiteOrZero(raw.readFloatLE(offset + 32)), windDirectionDeg: finiteOrZero(raw.readFloatLE(offset + 36)),
      cloudCoverPct: finiteOrZero(raw.readFloatLE(offset + 40)), visibilityKm: finiteOrZero(raw.readFloatLE(offset + 44)),
      pressureHpa: finiteOrZero(raw.readFloatLE(offset + 48)), radiationWm2: finiteOrZero(raw.readFloatLE(offset + 52)),
      provenance: { fieldFlags: raw.readUInt16LE(offset + 94), fields: descriptor?.fieldProvenance ?? {} },
    };
    const values = {
      windUms: optional(56), windVms: optional(60), globalRadiationWm2: optional(64), directRadiationWm2: optional(68),
      diffuseRadiationWm2: optional(72), cloudTransmissionPct: optional(76), snowWaterEquivalentMm: optional(80),
      solarElevationDeg: optional(84), solarAzimuthDeg: optional(88),
    };
    for (const [key, value] of Object.entries(values)) if (value !== undefined) hour[key] = value;
    hours.push(hour);
  }
  return hours;
}
