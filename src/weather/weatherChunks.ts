import {
  WEATHER_CHUNK_FORMAT,
  isPrecipitationType,
  type PrecipitationType,
  type WeatherChunk,
  type WeatherChunkDescriptor,
  type WeatherFieldProvenanceMap,
  type WeatherReferenceHour,
} from './weatherModel';

/** Fixed binary record layout. Floats are IEEE 754 little-endian. */
const MAGIC = 0x57483232; // "WH22"
const VERSION = 2;
const HEADER_BYTES = 16;
const RECORD_BYTES = 96;

export interface DecodedWeatherChunk {
  descriptor: WeatherChunkDescriptor;
  hours: readonly WeatherReferenceHour[];
}

export interface WeatherChunkEncodingInput {
  id: string;
  year: number;
  fieldProvenance: WeatherFieldProvenanceMap;
  encoding?: 'gzip' | 'identity';
}

function finiteOrNaN(value: number | undefined): number {
  return value == null || !Number.isFinite(value) ? Number.NaN : value;
}

function finiteOrZero(value: number | undefined): number {
  return value == null || !Number.isFinite(value) ? 0 : value;
}

function precipitationCode(value: PrecipitationType): number {
  switch (value) {
    case 'none': return 0;
    case 'rain': return 1;
    case 'mixed': return 2;
    case 'snow': return 3;
    case 'freezing-rain': return 4;
  }
}

function precipitationFromCode(value: number): PrecipitationType {
  switch (value) {
    case 1: return 'rain';
    case 2: return 'mixed';
    case 3: return 'snow';
    case 4: return 'freezing-rain';
    default: return 'none';
  }
}

function asNumber(value: number): number | undefined {
  return Number.isFinite(value) ? value : undefined;
}

/** Serialize normalized hourly records into a compact typed binary stream. */
export function encodeWeatherHourRecords(hours: readonly WeatherReferenceHour[]): Uint8Array {
  const buffer = new ArrayBuffer(HEADER_BYTES + hours.length * RECORD_BYTES);
  const view = new DataView(buffer);
  view.setUint32(0, MAGIC, true);
  view.setUint16(4, VERSION, true);
  view.setUint16(6, 0, true);
  view.setUint32(8, hours.length, true);
  view.setUint16(12, RECORD_BYTES, true);
  view.setUint16(14, 0, true);
  hours.forEach((hour, index) => {
    const offset = HEADER_BYTES + index * RECORD_BYTES;
    const at = Date.parse(hour.at);
    if (!Number.isFinite(at)) throw new Error(`Weather chunk hour ${index} has an invalid timestamp.`);
    view.setFloat64(offset, at, true);
    view.setFloat32(offset + 8, finiteOrNaN(hour.temperatureC), true);
    view.setFloat32(offset + 12, finiteOrNaN(hour.wetBulbC), true);
    view.setFloat32(offset + 16, finiteOrNaN(hour.humidityPct), true);
    view.setFloat32(offset + 20, finiteOrNaN(hour.precipitationMm), true);
    view.setFloat32(offset + 24, finiteOrNaN(hour.snowfallCm), true);
    view.setFloat32(offset + 28, finiteOrNaN(hour.windSpeedKph), true);
    view.setFloat32(offset + 32, finiteOrNaN(hour.windGustKph), true);
    view.setFloat32(offset + 36, finiteOrNaN(hour.windDirectionDeg), true);
    view.setFloat32(offset + 40, finiteOrNaN(hour.cloudCoverPct), true);
    view.setFloat32(offset + 44, finiteOrNaN(hour.visibilityKm), true);
    view.setFloat32(offset + 48, finiteOrNaN(hour.pressureHpa), true);
    view.setFloat32(offset + 52, finiteOrNaN(hour.radiationWm2), true);
    view.setFloat32(offset + 56, finiteOrNaN(hour.windUms), true);
    view.setFloat32(offset + 60, finiteOrNaN(hour.windVms), true);
    view.setFloat32(offset + 64, finiteOrNaN(hour.globalRadiationWm2), true);
    view.setFloat32(offset + 68, finiteOrNaN(hour.directRadiationWm2), true);
    view.setFloat32(offset + 72, finiteOrNaN(hour.diffuseRadiationWm2), true);
    view.setFloat32(offset + 76, finiteOrNaN(hour.cloudTransmissionPct), true);
    view.setFloat32(offset + 80, finiteOrNaN(hour.snowWaterEquivalentMm), true);
    view.setFloat32(offset + 84, finiteOrNaN(hour.solarElevationDeg), true);
    view.setFloat32(offset + 88, finiteOrNaN(hour.solarAzimuthDeg), true);
    view.setUint8(offset + 92, precipitationCode(hour.precipitationType));
    view.setUint8(offset + 93, 0);
    view.setUint16(offset + 94, finiteOrZero(hour.provenance?.fieldFlags) & 0xffff, true);
  });
  return new Uint8Array(buffer);
}

/** Decode the uncompressed v2 binary stream. Integrity is verified by decodeWeatherChunk. */
export function decodeWeatherHourRecords(bytes: Uint8Array, fieldProvenance: WeatherFieldProvenanceMap = {}): readonly WeatherReferenceHour[] {
  if (bytes.byteLength < HEADER_BYTES) throw new Error('Weather chunk is shorter than its header.');
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint32(0, true) !== MAGIC || view.getUint16(4, true) !== VERSION) {
    throw new Error('Weather chunk has an unsupported binary format.');
  }
  const count = view.getUint32(8, true);
  const recordBytes = view.getUint16(12, true);
  if (recordBytes !== RECORD_BYTES || bytes.byteLength !== HEADER_BYTES + count * RECORD_BYTES) {
    throw new Error('Weather chunk has an invalid record layout.');
  }
  const hours: WeatherReferenceHour[] = [];
  for (let index = 0; index < count; index += 1) {
    const offset = HEADER_BYTES + index * RECORD_BYTES;
    const rawType = precipitationFromCode(view.getUint8(offset + 92));
    const precipitationMm = finiteOrZero(asNumber(view.getFloat32(offset + 20, true)));
    const precipitationType = precipitationMm <= 0.001 ? 'none' : rawType;
    const hour: WeatherReferenceHour = {
      at: new Date(view.getFloat64(offset, true)).toISOString(),
      temperatureC: finiteOrZero(asNumber(view.getFloat32(offset + 8, true))),
      wetBulbC: finiteOrZero(asNumber(view.getFloat32(offset + 12, true))),
      humidityPct: finiteOrZero(asNumber(view.getFloat32(offset + 16, true))),
      precipitationMm,
      precipitationType,
      snowfallCm: finiteOrZero(asNumber(view.getFloat32(offset + 24, true))),
      windSpeedKph: finiteOrZero(asNumber(view.getFloat32(offset + 28, true))),
      windGustKph: finiteOrZero(asNumber(view.getFloat32(offset + 32, true))),
      windDirectionDeg: finiteOrZero(asNumber(view.getFloat32(offset + 36, true))),
      cloudCoverPct: finiteOrZero(asNumber(view.getFloat32(offset + 40, true))),
      visibilityKm: finiteOrZero(asNumber(view.getFloat32(offset + 44, true))),
      pressureHpa: finiteOrZero(asNumber(view.getFloat32(offset + 48, true))),
      radiationWm2: finiteOrZero(asNumber(view.getFloat32(offset + 52, true))),
      provenance: { fieldFlags: view.getUint16(offset + 94, true), fields: fieldProvenance },
    };
    const optional: Array<[keyof WeatherReferenceHour, number]> = [
      ['windUms', 56], ['windVms', 60], ['globalRadiationWm2', 64], ['directRadiationWm2', 68],
      ['diffuseRadiationWm2', 72], ['cloudTransmissionPct', 76], ['snowWaterEquivalentMm', 80],
      ['solarElevationDeg', 84], ['solarAzimuthDeg', 88],
    ];
    for (const [name, byteOffset] of optional) {
      const value = asNumber(view.getFloat32(offset + byteOffset, true));
      if (value != null) Object.assign(hour, { [name]: value });
    }
    if (!isPrecipitationType(hour.precipitationType)) throw new Error('Weather chunk has an invalid precipitation code.');
    hours.push(hour);
  }
  return hours;
}

function bytesToBase64(bytes: Uint8Array): string {
  if (typeof btoa === 'function') {
    let binary = '';
    const chunkSize = 0x8000;
    for (let start = 0; start < bytes.length; start += chunkSize) {
      binary += String.fromCharCode(...bytes.subarray(start, Math.min(bytes.length, start + chunkSize)));
    }
    return btoa(binary);
  }
  return Buffer.from(bytes).toString('base64');
}

function base64ToBytes(value: string): Uint8Array {
  if (typeof atob === 'function') {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    return bytes;
  }
  return new Uint8Array(Buffer.from(value, 'base64'));
}

function ownedBytes(bytes: Uint8Array): Uint8Array<ArrayBuffer> {
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  return copy;
}

async function streamTransform(bytes: Uint8Array, transform: CompressionStream | DecompressionStream): Promise<Uint8Array> {
  const stream = new Blob([ownedBytes(bytes)]).stream().pipeThrough(transform);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (!('CompressionStream' in globalThis)) throw new Error('This runtime cannot gzip weather chunks.');
  return streamTransform(bytes, new CompressionStream('gzip'));
}

async function gunzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (!('DecompressionStream' in globalThis)) throw new Error('This runtime cannot decompress weather chunks.');
  return streamTransform(bytes, new DecompressionStream('gzip'));
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  if (!globalThis.crypto?.subtle) throw new Error('This runtime cannot verify weather chunk checksums.');
  const digest = await globalThis.crypto.subtle.digest('SHA-256', ownedBytes(bytes));
  return [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, '0')).join('');
}

/** Build a checksummed immutable transport chunk from normalized hourly records. */
export async function encodeWeatherChunk(
  input: WeatherChunkEncodingInput,
  hours: readonly WeatherReferenceHour[],
): Promise<WeatherChunk> {
  if (hours.length === 0) throw new Error('Cannot encode an empty weather chunk.');
  const raw = encodeWeatherHourRecords(hours);
  const encoding = input.encoding ?? 'gzip';
  const encoded = encoding === 'gzip' ? await gzip(raw) : raw;
  return {
    descriptor: {
      id: input.id,
      year: input.year,
      startsAt: hours[0].at,
      endsAt: hours.at(-1)!.at,
      encoding,
      format: WEATHER_CHUNK_FORMAT,
      checksumSha256: await sha256Hex(encoded),
      byteLength: encoded.byteLength,
      uncompressedByteLength: raw.byteLength,
      recordCount: hours.length,
      fieldProvenance: input.fieldProvenance,
    },
    dataBase64: bytesToBase64(encoded),
  };
}

/** Verify checksum and layout before making any chunk data visible to simulation. */
export async function decodeWeatherChunk(chunk: WeatherChunk): Promise<DecodedWeatherChunk> {
  const { descriptor } = chunk;
  if (descriptor.format !== WEATHER_CHUNK_FORMAT) throw new Error('Weather chunk format is unsupported.');
  const encoded = base64ToBytes(chunk.dataBase64);
  if (encoded.byteLength !== descriptor.byteLength) throw new Error(`Weather chunk ${descriptor.id} has a byte-length mismatch.`);
  if (await sha256Hex(encoded) !== descriptor.checksumSha256) throw new Error(`Weather chunk ${descriptor.id} failed checksum validation.`);
  const raw = descriptor.encoding === 'gzip' ? await gunzip(encoded) : encoded;
  if (raw.byteLength !== descriptor.uncompressedByteLength) throw new Error(`Weather chunk ${descriptor.id} has an uncompressed length mismatch.`);
  const hours = decodeWeatherHourRecords(raw, descriptor.fieldProvenance);
  if (hours.length !== descriptor.recordCount) throw new Error(`Weather chunk ${descriptor.id} has a record-count mismatch.`);
  return { descriptor, hours };
}
