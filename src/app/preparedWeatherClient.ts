import { desktop } from '../desktopBridge';
import { isPreparedWeatherIdentity, preparedWeatherIdentityKey, type PreparedAnnualWeather, type PreparedWeatherIdentity } from '../weather/preparedWeatherModel';
import type { WeatherDataPackage } from '../weather/weatherModel';
import { WEATHER_YEAR_CONFIGURATION_VERSION } from '../weather/annualWeather';
import type { PreparedWeatherWorkerRequest, PreparedWeatherWorkerResponse } from './preparedWeather.worker';

interface WorkerLike {
  onmessage: ((event: MessageEvent<PreparedWeatherWorkerResponse>) => void) | null;
  onerror: ((event: ErrorEvent) => void) | null;
  postMessage(message: PreparedWeatherWorkerRequest, transfer?: Transferable[]): void;
  terminate(): void;
}

export interface PreparedWeatherDesktopStorage {
  read(identity: PreparedWeatherIdentity): Promise<Uint8Array | null>;
  write(identity: PreparedWeatherIdentity, bytes: Uint8Array): Promise<boolean>;
}

interface Consumer {
  resolve: (prepared: PreparedAnnualWeather) => void;
  reject: (error: Error) => void;
  signal?: AbortSignal;
  abortListener?: () => void;
}

interface SharedRequest {
  key: string;
  id: number;
  generation: number;
  worker: WorkerLike;
  consumers: Set<Consumer>;
  identity: PreparedWeatherIdentity;
  plan: unknown[] | null;
  resolved: unknown[] | null;
  header?: Extract<PreparedWeatherWorkerResponse, { type: 'header' }>;
  events: unknown[] | null;
  nextPlanIndex: number;
  nextResolvedIndex: number;
}

let nextRequestId = 1;
let generation = 0;
const shared = new Map<string, SharedRequest>();
let workerFactory: () => WorkerLike = () => new Worker(new URL('./preparedWeather.worker.ts', import.meta.url), { type: 'module' });
let preparedWeatherDesktop: PreparedWeatherDesktopStorage | null = desktop?.preparedWeather ?? null;

export function setPreparedWeatherWorkerFactoryForTests(factory: (() => WorkerLike) | null): void {
  workerFactory = factory ?? (() => new Worker(new URL('./preparedWeather.worker.ts', import.meta.url), { type: 'module' }));
}

export function setPreparedWeatherDesktopForTests(storage: PreparedWeatherDesktopStorage | null): void {
  preparedWeatherDesktop = storage ?? desktop?.preparedWeather ?? null;
}

function identityKey(identity: PreparedWeatherIdentity): string {
  return preparedWeatherIdentityKey(identity);
}

function authoritativeIdentityMatches(weatherPackage: WeatherDataPackage, identity: PreparedWeatherIdentity): boolean {
  const manifest = weatherPackage.manifest;
  return isPreparedWeatherIdentity(identity) && identity.packageContentHash === manifest.contentHash &&
    identity.terrainBinding === manifest.terrainBinding && identity.timezone === manifest.timezone &&
    identity.generatorVersion === manifest.generatorVersion &&
    identity.configurationVersion === WEATHER_YEAR_CONFIGURATION_VERSION;
}

function rejectConsumer(request: SharedRequest, consumer: Consumer, error: Error): void {
  request.consumers.delete(consumer);
  if (consumer.signal && consumer.abortListener) consumer.signal.removeEventListener('abort', consumer.abortListener);
  consumer.reject(error);
  if (!request.consumers.size) {
    request.plan = null; request.resolved = null; request.events = null;
    request.worker.terminate();
    shared.delete(request.key);
    generation++;
  }
}
function abortError(): Error { const error = new Error('Prepared weather resolution cancelled.'); error.name = 'AbortError'; return error; }

function fail(request: SharedRequest, error: Error): void {
  if (shared.get(request.key) !== request) return;
  shared.delete(request.key);
  request.plan = null;
  request.resolved = null;
  request.events = null;
  request.worker.terminate();
  for (const consumer of request.consumers) {
    if (consumer.signal && consumer.abortListener) consumer.signal.removeEventListener('abort', consumer.abortListener);
    consumer.reject(error);
  }
  request.consumers.clear();
}

function complete(request: SharedRequest, prepared: PreparedAnnualWeather): void {
  if (shared.get(request.key) !== request) return;
  shared.delete(request.key);
  request.plan = null;
  request.resolved = null;
  request.events = null;
  request.worker.terminate();
  for (const consumer of request.consumers) {
    if (consumer.signal && consumer.abortListener) consumer.signal.removeEventListener('abort', consumer.abortListener);
    consumer.resolve(prepared);
  }
  request.consumers.clear();
}

function handleResponse(request: SharedRequest, response: PreparedWeatherWorkerResponse): void {
  if (shared.get(request.key) !== request) return;
  if (response.id !== request.id || response.generation !== request.generation) return fail(request, new Error('Stale prepared weather worker response.'));
  if (response.type === 'error') return fail(request, new Error(response.error));
  if (response.type === 'header') {
    const sessionPlan = response.session && typeof response.session === 'object' &&
      response.session.plan && typeof response.session.plan === 'object'
      ? response.session.plan as Record<string, unknown> : null;
    if (response.planTotal <= 0 || response.resolvedTotal <= 0 ||
      !Number.isSafeInteger(response.planTotal) || !Number.isSafeInteger(response.resolvedTotal) ||
      response.planTotal !== response.resolvedTotal ||
      request.header ||
      !isPreparedWeatherIdentity(response.identity) ||
      preparedWeatherIdentityKey(response.identity) !== preparedWeatherIdentityKey(request.identity) ||
      !sessionPlan || Array.isArray(sessionPlan) || Object.prototype.hasOwnProperty.call(sessionPlan, 'hours') ||
      Object.prototype.hasOwnProperty.call(sessionPlan, 'events')) {
      return fail(request, new Error('Invalid prepared weather header.'));
    }
    request.header = response;
    request.plan = Array(response.planTotal);
    request.resolved = Array(response.resolvedTotal);
    return;
  }
  if (response.type === 'events') {
    if (!request.header || request.events || !Array.isArray(response.events)) return fail(request, new Error('Invalid prepared weather events.'));
    request.events = response.events;
    return;
  }
  if (response.type === 'plan-hours' || response.type === 'resolved-hours') {
    const target = response.type === 'plan-hours' ? request.plan : request.resolved;
    const expected = response.type === 'plan-hours' ? request.nextPlanIndex : request.nextResolvedIndex;
    if (!request.header || !request.events ||
      (response.type === 'resolved-hours' && request.nextPlanIndex !== request.plan!.length) ||
      !target || !Array.isArray(response.hours) || !Number.isSafeInteger(response.total) ||
      !Number.isSafeInteger(response.index) || response.total !== target.length || response.index !== expected ||
      response.index < 0 ||
      response.hours.length === 0 || response.hours.length > 256 || response.index + response.hours.length > response.total ||
      (response.index + response.hours.length < response.total && response.hours.length !== 256)) {
      return fail(request, new Error('Invalid prepared weather chunk.'));
    }
    response.hours.forEach((hour, index) => { target[response.index + index] = hour; });
    if (response.type === 'plan-hours') request.nextPlanIndex += response.hours.length;
    else request.nextResolvedIndex += response.hours.length;
    return;
  }
  if (!request.header || !request.plan || !request.resolved) {
    return fail(request, new Error('Prepared weather completed before all chunks arrived.'));
  }
  if (response.type !== 'complete') return fail(request, new Error('Invalid prepared weather completion.'));
  if (!request.events || request.nextPlanIndex !== request.plan.length || request.nextResolvedIndex !== request.resolved.length) {
    return fail(request, new Error('Prepared weather completed before all chunks arrived.'));
  }
  const plan = { ...request.header.session.plan, hours: request.plan, events: request.events };
  const prepared = {
    identity: request.header.identity,
    session: { ...request.header.session, plan },
    resolvedHours: request.resolved,
    averageAnnualSnowfallCm: request.header.averageAnnualSnowfallCm,
  } as PreparedAnnualWeather;
  const completeResponse = response;
  let persist = Promise.resolve(true);
  if (completeResponse.cacheBytes && preparedWeatherDesktop) {
    try {
      persist = Promise.resolve(preparedWeatherDesktop.write(prepared.identity, completeResponse.cacheBytes));
    } catch {
      persist = Promise.resolve(false);
    }
  }
  void persist.catch(() => undefined).then(() => complete(request, prepared));
}

export function resolvePreparedWeather(
  weatherPackage: WeatherDataPackage,
  identity: PreparedWeatherIdentity,
  signal?: AbortSignal,
): Promise<PreparedAnnualWeather> {
  if (!weatherPackage?.manifest || !authoritativeIdentityMatches(weatherPackage, identity)) {
    return Promise.reject(new Error('The weather package and identity do not match.'));
  }
  const key = `${identityKey(identity)}|${weatherPackage.manifest.contentHash}`;
  let request = shared.get(key);
  return new Promise<PreparedAnnualWeather>((resolve, reject) => {
    const consumer: Consumer = { resolve, reject, signal };
    if (signal?.aborted) return reject(abortError());
    if (!request) {
      const worker = workerFactory();
      request = { key, id: nextRequestId++, generation: ++generation, worker, consumers: new Set(),
        identity, plan: null, resolved: null, events: null, nextPlanIndex: 0, nextResolvedIndex: 0 };
      shared.set(key, request);
      worker.onmessage = (event) => handleResponse(request!, event.data);
      worker.onerror = () => fail(request!, new Error('Prepared weather worker crashed.'));
      const start = async () => {
        const cacheBytes = preparedWeatherDesktop ? await preparedWeatherDesktop.read(identity).catch(() => null) : undefined;
        if (shared.get(key) !== request) return;
        const message: PreparedWeatherWorkerRequest = { id: request!.id, generation: request!.generation, weatherPackage, identity,
          cacheBytes: cacheBytes ?? undefined, storageMode: preparedWeatherDesktop ? 'desktop-relay' : 'browser-indexeddb' };
        worker.postMessage(message, cacheBytes ? [cacheBytes.buffer] : []);
      };
      void start().catch((error) => fail(request!, error instanceof Error ? error : new Error('Prepared weather load failed.')));
    }
    request!.consumers.add(consumer);
    if (signal) {
      consumer.abortListener = () => rejectConsumer(request!, consumer, abortError());
      signal.addEventListener('abort', consumer.abortListener, { once: true });
    }
  });
}
