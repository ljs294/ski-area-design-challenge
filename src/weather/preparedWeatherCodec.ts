import {
  isPreparedAnnualWeather,
  isPreparedWeatherIdentity,
  preparedWeatherIdentityKey,
  preparedWeatherPayloadJson,
  type PreparedAnnualWeather,
  type PreparedWeatherIdentity,
} from './preparedWeatherModel';
import type { WeatherHourProvenance } from './weatherModel';
import { isWeatherFieldProvenanceMap } from './weatherModel';

const CACHE_SCHEMA_VERSION = 1 as const;

interface CompactHour extends Record<string, unknown> {
  provenanceRef?: number;
}

interface CompactEnvelope {
  cacheSchemaVersion: 1;
  identity: PreparedWeatherIdentity;
  provenanceDictionary: WeatherHourProvenance[];
  payload: {
    identity: PreparedWeatherIdentity;
    session: {
      [key: string]: unknown;
      plan: { [key: string]: unknown; hours: CompactHour[] };
    };
    resolvedHours: CompactHour[];
    averageAnnualSnowfallCm: number | null;
  };
  payloadSha256: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function canonicalJson(value: unknown): string {
  if (value === null || typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number') {
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (!isRecord(value)) throw new Error('Invalid JSON value.');
  return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
}

async function checksum(value: string): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle || typeof TextEncoder === 'undefined') throw new Error('Web Crypto is unavailable.');
  const digest = await subtle.digest('SHA-256', new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
}

async function gzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (typeof CompressionStream === 'undefined' || typeof Response === 'undefined') {
    throw new Error('CompressionStream is unavailable.');
  }
  const stream = new Blob([bytes.slice().buffer as ArrayBuffer]).stream().pipeThrough(new CompressionStream('gzip'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

async function gunzip(bytes: Uint8Array): Promise<Uint8Array> {
  if (typeof DecompressionStream === 'undefined' || typeof Response === 'undefined') {
    throw new Error('DecompressionStream is unavailable.');
  }
  const stream = new Blob([bytes.slice().buffer as ArrayBuffer]).stream().pipeThrough(new DecompressionStream('gzip'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

function provenanceKey(value: WeatherHourProvenance): string {
  return canonicalJson(value);
}

function isProvenance(value: unknown): value is WeatherHourProvenance {
  if (!isRecord(value) || typeof value.fieldFlags !== 'number' ||
    !Number.isSafeInteger(value.fieldFlags) || value.fieldFlags < 0) return false;
  return value.fields === undefined || isWeatherFieldProvenanceMap(value.fields);
}

function compactHours(
  hours: readonly Record<string, unknown>[],
  dictionary: WeatherHourProvenance[],
  indexes: Map<string, number>,
): CompactHour[] {
  return hours.map((hour) => {
    const compact = { ...hour } as CompactHour;
    if (!Object.prototype.hasOwnProperty.call(hour, 'provenance')) return compact;
    const provenance = hour.provenance;
    if (!isRecord(provenance)) throw new Error('Invalid hourly provenance.');
    const key = provenanceKey(provenance as unknown as WeatherHourProvenance);
    let index = indexes.get(key);
    if (index === undefined) {
      index = dictionary.length;
      indexes.set(key, index);
      dictionary.push(provenance as unknown as WeatherHourProvenance);
    }
    delete compact.provenance;
    compact.provenanceRef = index;
    return compact;
  });
}

function expandHours(hours: unknown, dictionary: WeatherHourProvenance[]): Record<string, unknown>[] | null {
  if (!Array.isArray(hours)) return null;
  return hours.map((value) => {
    if (!isRecord(value)) throw new Error('Invalid compact hourly value.');
    const compact = { ...value };
    if (!Object.prototype.hasOwnProperty.call(compact, 'provenanceRef')) return compact;
    const indexValue = compact.provenanceRef;
    if (typeof indexValue !== 'number' || !Number.isInteger(indexValue) || indexValue < 0 || indexValue >= dictionary.length) {
      throw new Error('Invalid provenance reference.');
    }
    const index = indexValue;
    delete compact.provenanceRef;
    return { ...compact, provenance: dictionary[index] };
  });
}

function compactPrepared(prepared: PreparedAnnualWeather): CompactEnvelope['payload'] & {
  provenanceDictionary: WeatherHourProvenance[];
} {
  const dictionary: WeatherHourProvenance[] = [];
  const indexes = new Map<string, number>();
  const session = prepared.session as unknown as Record<string, unknown>;
  const plan = session.plan as unknown as Record<string, unknown>;
  return {
    identity: prepared.identity,
    session: {
      ...session,
      plan: { ...plan, hours: compactHours(plan.hours as readonly Record<string, unknown>[], dictionary, indexes) },
    },
    resolvedHours: compactHours(prepared.resolvedHours as unknown as readonly Record<string, unknown>[], dictionary, indexes),
    averageAnnualSnowfallCm: prepared.averageAnnualSnowfallCm,
    provenanceDictionary: dictionary,
  };
}

export async function encodePreparedWeather(prepared: PreparedAnnualWeather): Promise<Uint8Array> {
  if (!isPreparedAnnualWeather(prepared, prepared?.identity)) throw new Error('Invalid prepared weather payload.');
  const compact = compactPrepared(prepared);
  const payload = {
    identity: compact.identity,
    session: compact.session,
    resolvedHours: compact.resolvedHours,
    averageAnnualSnowfallCm: compact.averageAnnualSnowfallCm,
  };
  const payloadSha256 = await checksum(preparedWeatherPayloadJson(prepared));
  const envelope: CompactEnvelope = {
    cacheSchemaVersion: CACHE_SCHEMA_VERSION,
    identity: prepared.identity,
    provenanceDictionary: compact.provenanceDictionary,
    payload,
    payloadSha256,
  };
  return gzip(new TextEncoder().encode(canonicalJson(envelope)));
}

export async function decodePreparedWeather(
  bytes: Uint8Array | ArrayBuffer,
  expectedIdentity: PreparedWeatherIdentity,
): Promise<PreparedAnnualWeather | null> {
  try {
    if (!isPreparedWeatherIdentity(expectedIdentity)) return null;
    const compressed = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    if (!compressed.length) return null;
    const parsed: unknown = JSON.parse(new TextDecoder().decode(await gunzip(compressed)));
    if (!isRecord(parsed) || parsed.cacheSchemaVersion !== CACHE_SCHEMA_VERSION ||
      !isPreparedWeatherIdentity(parsed.identity) ||
      preparedWeatherIdentityKey(parsed.identity) !== preparedWeatherIdentityKey(expectedIdentity) ||
      !Array.isArray(parsed.provenanceDictionary) || !parsed.provenanceDictionary.every(isProvenance) ||
      !isRecord(parsed.payload) ||
      typeof parsed.payloadSha256 !== 'string' || !/^[a-f0-9]{64}$/i.test(parsed.payloadSha256)) return null;
    const dictionary = parsed.provenanceDictionary as WeatherHourProvenance[];
    const payload = parsed.payload;
    if (!isRecord(payload.session) || !isRecord(payload.session.plan)) return null;
    const planHours = expandHours(payload.session.plan.hours, dictionary);
    const resolvedHours = expandHours(payload.resolvedHours, dictionary);
    if (!planHours || !resolvedHours) return null;
    const expanded = {
      ...payload,
      session: { ...payload.session, plan: { ...payload.session.plan, hours: planHours } },
      resolvedHours,
    };
    if (!isPreparedAnnualWeather(expanded, expectedIdentity)) return null;
    const actual = await checksum(preparedWeatherPayloadJson(expanded));
    return actual.toLowerCase() === parsed.payloadSha256.toLowerCase() ? expanded : null;
  } catch {
    return null;
  }
}
