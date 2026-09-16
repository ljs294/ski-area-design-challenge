/// <reference lib="webworker" />
import { buildPreparedWeather } from '../weather/preparedWeather';
import { decodePreparedWeather, encodePreparedWeather } from '../weather/preparedWeatherCodec';
import { readPreparedWeather, writePreparedWeatherBytes } from '../preparedWeatherStorageClient';
import { isPreparedAnnualWeather, isPreparedWeatherIdentity,
  type PreparedAnnualWeather, type PreparedWeatherIdentity } from '../weather/preparedWeatherModel';
import { isWeatherDataPackage, type WeatherDataPackage } from '../weather/weatherModel';
import { WEATHER_YEAR_CONFIGURATION_VERSION } from '../weather/annualWeather';

export interface PreparedWeatherWorkerRequest {
  id: number;
  generation: number;
  weatherPackage: WeatherDataPackage;
  identity: PreparedWeatherIdentity;
  cacheBytes?: Uint8Array;
  storageMode: 'desktop-relay' | 'browser-indexeddb';
}

export interface PreparedWeatherHeader {
  type: 'header';
  id: number;
  generation: number;
  identity: PreparedWeatherIdentity;
  session: Omit<PreparedAnnualWeather['session'], 'plan'> & {
    plan: Omit<PreparedAnnualWeather['session']['plan'], 'hours' | 'events'>;
  };
  averageAnnualSnowfallCm: number | null;
  planTotal: number;
  resolvedTotal: number;
}
export interface PreparedWeatherEvents { type: 'events'; id: number; generation: number; events: unknown[]; }

export interface PreparedWeatherHoursChunk {
  type: 'plan-hours' | 'resolved-hours';
  id: number;
  generation: number;
  index: number;
  total: number;
  hours: unknown[];
}

export interface PreparedWeatherComplete {
  type: 'complete';
  id: number;
  generation: number;
  cacheBytes?: Uint8Array;
}

export interface PreparedWeatherWorkerError {
  type: 'error';
  id: number;
  generation: number;
  error: string;
}

export type PreparedWeatherWorkerResponse = PreparedWeatherHeader | PreparedWeatherEvents | PreparedWeatherHoursChunk |
  PreparedWeatherComplete | PreparedWeatherWorkerError;

interface PreparedWeatherWorkerStorage {
  read(identity: PreparedWeatherIdentity): Promise<PreparedAnnualWeather | null>;
  write(identity: PreparedWeatherIdentity, bytes: Uint8Array): Promise<boolean>;
}

let workerStorage: PreparedWeatherWorkerStorage = {
  read: readPreparedWeather,
  write: writePreparedWeatherBytes,
};

export function setPreparedWeatherWorkerStorageForTests(storage: PreparedWeatherWorkerStorage | null): void {
  workerStorage = storage ?? { read: readPreparedWeather, write: writePreparedWeatherBytes };
}

function authoritativeIdentityMatches(weatherPackage: WeatherDataPackage, identity: PreparedWeatherIdentity): boolean {
  const manifest = weatherPackage.manifest;
  return isPreparedWeatherIdentity(identity) && identity.packageContentHash === manifest.contentHash &&
    identity.terrainBinding === manifest.terrainBinding && identity.timezone === manifest.timezone &&
    identity.generatorVersion === manifest.generatorVersion &&
    identity.configurationVersion === WEATHER_YEAR_CONFIGURATION_VERSION;
}

function emitPrepared(scope: DedicatedWorkerGlobalScope, request: PreparedWeatherWorkerRequest,
  prepared: PreparedAnnualWeather, cacheBytes?: Uint8Array): void {
  const plan = prepared.session.plan;
  const header: PreparedWeatherHeader = {
    type: 'header', id: request.id, generation: request.generation, identity: prepared.identity,
    session: { ...prepared.session, plan: (({ hours: _hours, events: _events, ...rest }) => rest)(plan) } as PreparedWeatherHeader['session'],
    averageAnnualSnowfallCm: prepared.averageAnnualSnowfallCm,
    planTotal: plan.hours.length, resolvedTotal: prepared.resolvedHours.length,
  };
  scope.postMessage(header);
  scope.postMessage({ type: 'events', id: request.id, generation: request.generation, events: Array.from(plan.events) } satisfies PreparedWeatherEvents);
  for (const [type, hours] of [['plan-hours', plan.hours], ['resolved-hours', prepared.resolvedHours] ] as const) {
    for (let index = 0; index < hours.length; index += 256) {
      const chunk: PreparedWeatherHoursChunk = {
        type, id: request.id, generation: request.generation, index, total: hours.length,
        hours: Array.from(hours.slice(index, index + 256)),
      };
      scope.postMessage(chunk);
    }
  }
  const complete: PreparedWeatherComplete = { type: 'complete', id: request.id, generation: request.generation, cacheBytes };
  if (cacheBytes) scope.postMessage(complete, [cacheBytes.buffer]);
  else scope.postMessage(complete);
}

export const emitPreparedWeatherForTests = emitPrepared;

export async function handlePreparedWeatherRequest(
  scope: DedicatedWorkerGlobalScope,
  request: PreparedWeatherWorkerRequest,
): Promise<void> {
  try {
    if (!isWeatherDataPackage(request.weatherPackage) ||
      !authoritativeIdentityMatches(request.weatherPackage, request.identity)) {
      throw new Error('The weather package and identity do not match.');
    }
    let prepared: PreparedAnnualWeather | null = request.cacheBytes
      ? await decodePreparedWeather(request.cacheBytes, request.identity)
      : request.storageMode === 'browser-indexeddb' ? await workerStorage.read(request.identity) : null;
    let cacheBytes: Uint8Array | undefined;
    if (!prepared) {
      prepared = await buildPreparedWeather(request.weatherPackage, request.identity);
      cacheBytes = await encodePreparedWeather(prepared);
      if (request.storageMode === 'browser-indexeddb') await workerStorage.write(request.identity, cacheBytes);
    }
    if (!isPreparedAnnualWeather(prepared, request.identity)) throw new Error('Generated prepared weather failed validation.');
    emitPrepared(scope, request, prepared, cacheBytes);
  } catch (error) {
    const response: PreparedWeatherWorkerError = {
      type: 'error', id: request.id, generation: request.generation,
      error: error instanceof Error ? error.message : 'Prepared weather generation failed.',
    };
    scope.postMessage(response);
  }
}

const scope: DedicatedWorkerGlobalScope | null = typeof self === 'undefined'
  ? null : self as unknown as DedicatedWorkerGlobalScope;
if (scope) scope.onmessage = (event: MessageEvent<PreparedWeatherWorkerRequest>) => {
  void handlePreparedWeatherRequest(scope, event.data);
};
