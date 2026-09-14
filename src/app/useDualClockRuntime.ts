import { useCallback, useEffect, useRef, useState } from 'react';
import type { DualCheckpoint, DualInitialization, DualPublication, DualSpeed, ResortSimulationInput, AdvanceDestination } from '../dualClock/model';
import { advanceDestination } from '../dualClock/clock';
import type { DualCommand, DualWorkerRequest, DualWorkerResponse } from './dualClockProtocol';
import type { SnowLayerState } from './useSnowLayer';
import type { PreparedRoute } from '../dualClock/geometry';
import { applyDualSnowPatch } from './dualSnowPublication';
import { decodeDualMovement } from './dualMovementPublication';
import type { SnowAddResult } from '../types/dualClock';
import { resortSimulationInputKey } from './resortSimulationInput';

export interface DualSimulationControls {
  publication: DualPublication | null;
  geometry: Record<string, PreparedRoute>;
  error: string | null;
  ready: boolean;
  weatherReady?: boolean;
  initialPortal: DualCheckpoint['portal'];
  initialTicketPriceCents: number;
  updateResort(input: ResortSimulationInput): void;
  setSpeed(speed: DualSpeed): void;
  togglePlayback(): void;
  pause(): void;
  advance(destination: AdvanceDestination): Promise<void>;
  cancel(): void;
  resume(): void;
  select(id: string | null, autoTrack?: boolean): void;
  acknowledge(id: string): void;
  follow(): Promise<void>;
  checkpoint(): Promise<DualCheckpoint>;
  addSnow(meters: number): Promise<SnowAddResult>;
}

export interface WeatherReadinessAck {
  requestId: number;
  generation: number;
  acceptedFrom: string;
  acceptedTo: string;
}

export function weatherCoverageContains(ack: WeatherReadinessAck, at: string): boolean {
  const current = Date.parse(at), from = Date.parse(ack.acceptedFrom), to = Date.parse(ack.acceptedTo);
  return Number.isFinite(current) && Number.isFinite(from) && Number.isFinite(to) && from <= current && current <= to;
}

export function matchesWeatherReadinessAck(
  ack: WeatherReadinessAck,
  pendingRequestId: number,
  activeGeneration: number,
  currentAt: string,
): boolean {
  return ack.requestId === pendingRequestId && ack.generation === activeGeneration && weatherCoverageContains(ack, currentAt);
}

export function boundedWeatherChunks(hours: DualInitialization['weather'], maxHours = 72): DualInitialization['weather'][] {
  const chunks: DualInitialization['weather'][] = [];
  for (let index = 0; index < hours.length; index += maxHours) chunks.push(hours.slice(index, index + maxHours));
  return chunks;
}

export function useDualClockRuntime(options: {
  enabled: boolean; initialization: DualInitialization | null; snow: SnowLayerState;
  prepareWeather(from: string, to: string, signal?: AbortSignal): Promise<DualInitialization['weather'] | null>;
}): DualSimulationControls {
  const [publication, setPublication] = useState<DualPublication | null>(null);
  const [geometry, setGeometry] = useState<Record<string, PreparedRoute>>({});
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const worker = useRef<Worker | null>(null), generation = useRef(0), id = useRef(0);
  const minimumPublicationId = useRef(0);
  const published = useRef<DualPublication | null>(null), busy = useRef(false), paused = useRef(true);
  const optionsRef = useRef(options); optionsRef.current = options;
  const preparationGeneration = useRef(0);
  const weatherReady = useRef(false);
  const weatherAcceptedThrough = useRef<number | null>(null);
  const [weatherReadyState, setWeatherReadyState] = useState(false);
  const pendingWeather = useRef<{ requestId: number; generation: number; requiredAt: string; resolve(ack: WeatherReadinessAck): void; reject(error: Error): void } | null>(null);
  const weatherAbort = useRef<AbortController | null>(null);
  const initial = useRef(options.initialization); initial.current = options.initialization;
  const lastSentResortKey = useRef<string | null>(null);
  const initialWeatherRequestId = useRef(0);
  const lastWeatherReference = useRef<DualInitialization['weather']>([]);
  const preparedTerrain = useRef<DualInitialization['terrain']>(null);
  const pending = useRef(new Map<number, { resolve(value: DualCheckpoint): void; reject(reason: Error): void }>());
  const pendingSnowAdd = useRef(new Map<number, { resolve(value: SnowAddResult): void; reject(reason: Error): void }>());
  const post = useCallback((command: DualCommand, beforePost?: (requestId: number) => void): number => {
    const requestId = ++id.current;
    if (command.type !== 'advance') minimumPublicationId.current = requestId;
    beforePost?.(requestId);
    worker.current?.postMessage({ ...command, requestId, generation: generation.current,
      committedRevision: published.current?.clock.revision ?? 0 } satisfies DualWorkerRequest);
    return requestId;
  }, []);
  const binding = options.enabled && options.initialization?.terrain ? options.initialization.terrain.key : null;
  const abortWeatherPreparation = useCallback(() => {
    weatherAbort.current?.abort(); weatherAbort.current = null;
    const pending = pendingWeather.current;
    if (pending) { pendingWeather.current = null; pending.reject(Object.assign(new Error('Weather preparation cancelled.'), { name: 'AbortError' })); }
  }, []);
  const waitForWeather = useCallback(async (hours: DualInitialization['weather'], requiredAt: string,
    signal?: AbortSignal): Promise<void> => {
    if (!hours.length) return Promise.reject(new Error('Prepare the required weather before playing.'));
    if (signal?.aborted) return Promise.reject(Object.assign(new Error('Weather preparation cancelled.'), { name: 'AbortError' }));
    weatherReady.current = false; weatherAcceptedThrough.current = null; setWeatherReadyState(false);
    let acceptedFrom = Number.POSITIVE_INFINITY, acceptedTo = Number.NEGATIVE_INFINITY;
    for (const chunk of boundedWeatherChunks(hours)) {
      if (signal?.aborted) throw Object.assign(new Error('Weather preparation cancelled.'), { name: 'AbortError' });
      const ack = await new Promise<WeatherReadinessAck>((resolve, reject) => {
        const requestId = post({ type: 'weather', hours: chunk }, id => {
          pendingWeather.current = { requestId: id, generation: generation.current, requiredAt: chunk[0].at, resolve, reject };
        });
        if (requestId !== pendingWeather.current?.requestId) return;
        const onAbort = () => { signal?.removeEventListener('abort', onAbort); abortWeatherPreparation(); };
        signal?.addEventListener('abort', onAbort, { once: true });
      });
      const from = Date.parse(ack.acceptedFrom), to = Date.parse(ack.acceptedTo);
      if (!Number.isFinite(from) || !Number.isFinite(to) || from > to || !chunk.some(hour => weatherCoverageContains(ack, hour.at)))
        throw new Error('Simulation returned an invalid weather coverage.');
      acceptedFrom = Math.min(acceptedFrom, from); acceptedTo = Math.max(acceptedTo, to);
    }
    const aggregate: WeatherReadinessAck = { requestId: 0, generation: generation.current,
      acceptedFrom: new Date(acceptedFrom).toISOString(), acceptedTo: new Date(acceptedTo).toISOString() };
    if (!weatherCoverageContains(aggregate, requiredAt)) throw new Error('Simulation returned weather that does not cover the current hour.');
    weatherReady.current = true; weatherAcceptedThrough.current = acceptedTo; setWeatherReadyState(true);
  }, [abortWeatherPreparation, post]);
  const prepareAndAcceptWeather = useCallback(async (from: string, to: string, requiredAt: string) => {
    abortWeatherPreparation();
    const controller = new AbortController(); weatherAbort.current = controller;
    try {
      const hours = await optionsRef.current.prepareWeather(from, to, controller.signal);
      if (!hours) throw new Error('Prepare the required weather before playing.');
      await waitForWeather(hours, requiredAt, controller.signal);
      return hours;
    } finally {
      if (weatherAbort.current === controller) weatherAbort.current = null;
    }
  }, [abortWeatherPreparation, waitForWeather]);
  useEffect(() => {
    if (!binding || !initial.current) return;
    const instance = new Worker(new URL('./dualClock.worker.ts', import.meta.url), { type: 'module' });
    const waiters = pending.current;
    const snowAddWaiters = pendingSnowAdd.current;
    const preparationToken = preparationGeneration;
    worker.current = instance; generation.current++; const active = generation.current;
    weatherReady.current = false; weatherAcceptedThrough.current = null; setWeatherReadyState(false); setReady(false); setError(null);
    instance.onmessage = (event: MessageEvent<DualWorkerResponse>) => {
      const response = event.data;
      if (response.generation !== active || instance !== worker.current) return;
      if (response.weatherAck) {
        const pending = pendingWeather.current;
        const requiredAt = pending?.requiredAt ?? response.publication?.clock.at ?? initial.current?.at;
        const requestId = pending?.requestId ?? initialWeatherRequestId.current;
        if (requiredAt && matchesWeatherReadinessAck(response.weatherAck, requestId, active, requiredAt)) {
          if (pending) { pendingWeather.current = null; pending.resolve(response.weatherAck); }
          else { weatherReady.current = true; weatherAcceptedThrough.current = Date.parse(response.weatherAck.acceptedTo); setWeatherReadyState(true); }
        }
      }
      if (response.movement) {
        if (response.id >= minimumPublicationId.current && response.publication) response.publication.points = decodeDualMovement(response.movement);
        instance.postMessage({ type: 'recycle-movement', buffer: response.movement.buffer, generation: active,
          requestId: response.id, committedRevision: response.committedRevision } satisfies DualWorkerRequest, [response.movement.buffer]);
      }
      if (response.checkpoint) { pending.current.get(response.id)?.resolve(response.checkpoint); pending.current.delete(response.id); }
      if (response.id < minimumPublicationId.current) return;
      busy.current = response.busy ?? false;
      if (response.type === 'error') {
        setError(response.error ?? 'Simulation failed.'); paused.current = true;
        if (pendingWeather.current) {
          const pending = pendingWeather.current; pendingWeather.current = null;
          pending.reject(new Error(response.error ?? 'Weather preparation failed.'));
        }
        for (const waiter of pending.current.values()) waiter.reject(new Error(response.error)); pending.current.clear();
        for (const waiter of snowAddWaiters.values()) waiter.reject(new Error(response.error)); snowAddWaiters.clear();
      }
      if (response.publication) {
        published.current = response.publication; paused.current = response.publication.clock.paused;
        setPublication(response.publication); setReady(true);
      }
      if (response.snow) optionsRef.current.snow.replace(response.snow, true);
      else if (response.snowPatch && optionsRef.current.snow.gridRef.current) optionsRef.current.snow.replace(applyDualSnowPatch(optionsRef.current.snow.gridRef.current, response.snowPatch), true);
      if (response.geometry) setGeometry(response.geometry);
      if (response.snowAdd) { snowAddWaiters.get(response.id)?.resolve(response.snowAdd); snowAddWaiters.delete(response.id); }
    };
    instance.onerror = (event) => { paused.current = true; busy.current = false; setError(event.message || 'Simulation worker failed.');
      if (pendingWeather.current) { const pending = pendingWeather.current; pendingWeather.current = null; pending.reject(new Error('Simulation worker failed.')); }
      for (const waiter of pending.current.values()) waiter.reject(new Error('Simulation worker failed.')); pending.current.clear();
      for (const waiter of snowAddWaiters.values()) waiter.reject(new Error('Simulation worker failed.')); snowAddWaiters.clear(); };
    lastSentResortKey.current = resortSimulationInputKey(initial.current.resort);
    lastWeatherReference.current = initial.current.weather;
    initialWeatherRequestId.current = post({ type: 'initialize', input: initial.current });
    preparedTerrain.current = initial.current.terrain;
    return () => { preparationToken.current++; abortWeatherPreparation(); instance.terminate(); if (worker.current === instance) worker.current = null;
      for (const waiter of waiters.values()) waiter.reject(new Error('Simulation replaced.')); waiters.clear();
      for (const waiter of snowAddWaiters.values()) waiter.reject(new Error('Simulation replaced.')); snowAddWaiters.clear(); };
  }, [abortWeatherPreparation, binding, post]);
  useEffect(() => {
    const terrain = options.initialization?.terrain;
    if (ready && terrain && preparedTerrain.current !== terrain) { preparedTerrain.current = terrain; post({ type: 'terrain', terrain }); }
  }, [ready, options.initialization?.terrain, post]);
  useEffect(() => {
    if (ready && options.initialization?.timezone && !options.initialization?.checkpoint) post({ type: 'timezone', timezone: options.initialization.timezone });
  }, [ready, options.initialization?.timezone, options.initialization?.checkpoint, post]);
  useEffect(() => {
    const weather = options.initialization?.weather;
    if (!worker.current || !weather?.length || weather === lastWeatherReference.current || pendingWeather.current) return;
    lastWeatherReference.current = weather;
    void waitForWeather(weather, published.current?.clock.at ?? initial.current?.at ?? new Date().toISOString())
      .catch(() => undefined);
  }, [options.initialization?.weather, waitForWeather]);
  useEffect(() => {
    if (!options.enabled) return;
    let frame = 0, last = performance.now(), accumulated = 0;
    const tick = (now: number) => {
      const state = published.current;
      if (document.hidden || paused.current || !weatherReady.current || state?.advance?.state === 'running') { last = now; accumulated = 0; }
      else {
        let preparingWeather = false;
        if (state && weatherAcceptedThrough.current != null && Date.parse(state.clock.at) + 6 * 3600000 >= weatherAcceptedThrough.current
          && !pendingWeather.current && !weatherAbort.current) {
          const preparation = preparationGeneration.current;
          paused.current = true;
          preparingWeather = true;
          void prepareAndAcceptWeather(state.clock.at, new Date(Date.parse(state.clock.at) + 72 * 3600000).toISOString(), state.clock.at)
            .then(() => { if (preparation === preparationGeneration.current) paused.current = false; })
            .catch(reason => { if (preparation === preparationGeneration.current) setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); });
        }
        if (preparingWeather) { last = now; accumulated = 0; }
        else accumulated += Math.min(250, Math.max(0, now - last)); last = now;
        if (!preparingWeather && state && !busy.current && accumulated >= 100) {
          const elapsed = accumulated; accumulated = 0; busy.current = true;
          const macroRate = initial.current?.checkpoint?.config.macroSecondsPerSecond ?? initial.current?.config?.macroSecondsPerSecond ?? 40;
          post({ type: 'advance', targetMs: Date.parse(state.clock.at) + elapsed * macroRate * state.clock.speed });
        }
      }
      frame = requestAnimationFrame(tick);
    };
    const visibility = () => {
      last = performance.now(); accumulated = 0;
      if (document.hidden && published.current?.advance?.state !== 'running' && !paused.current) { paused.current = true; post({ type: 'pause' }); }
    };
    document.addEventListener('visibilitychange', visibility); frame = requestAnimationFrame(tick);
    return () => { cancelAnimationFrame(frame); document.removeEventListener('visibilitychange', visibility); };
  }, [options.enabled, post, prepareAndAcceptWeather]);
  const pause = useCallback(() => { preparationGeneration.current++; abortWeatherPreparation(); paused.current = true; post({ type: 'pause' }); }, [abortWeatherPreparation, post]);
  const checkpoint = useCallback((): Promise<DualCheckpoint> => {
    if (!worker.current) return Promise.reject(new Error('Wait for the simulation to initialize before saving.'));
    preparationGeneration.current++; paused.current = true;
    return new Promise((resolve, reject) => post({ type: 'checkpoint' }, requestId => pending.current.set(requestId, { resolve, reject })));
  }, [post]);
  const addSnow = useCallback((meters: number): Promise<SnowAddResult> => {
    if (!worker.current) return Promise.reject(new Error('Wait for the simulation to initialize before adding snow.'));
    if (!weatherReady.current) return Promise.reject(new Error('Wait for the weather window to be ready before changing snow.'));
    preparationGeneration.current++; abortWeatherPreparation(); paused.current = true;
    return new Promise((resolve, reject) => post({ type: 'snow-add', meters }, requestId => pendingSnowAdd.current.set(requestId, { resolve, reject })));
  }, [abortWeatherPreparation, post]);
  const updateResort = useCallback((input: ResortSimulationInput) => {
    const key = resortSimulationInputKey(input);
    if (key === lastSentResortKey.current) return;
    lastSentResortKey.current = key;
    post({ type: 'resort', input });
  }, [post]);
  const playPrepared = async () => {
    const state = published.current; if (!state) return;
    const preparation = ++preparationGeneration.current;
    try {
      if (!weatherReady.current) {
        await prepareAndAcceptWeather(state.clock.at,
          new Date(Date.parse(state.clock.at) + 86400000).toISOString(), state.clock.at);
        if (preparation !== preparationGeneration.current) return;
      }
      paused.current = false; setError(null); post({ type: 'play' });
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); }
  };
  return { publication, geometry, error, ready, weatherReady: weatherReadyState,
    initialPortal: options.initialization?.checkpoint?.portal ?? null,
    initialTicketPriceCents: options.initialization?.checkpoint?.nextTicketPriceCents ?? 10000, updateResort,
    setSpeed: speed => post({ type: 'speed', speed }), pause,
    togglePlayback: () => { if (!published.current) return; if (!paused.current) { preparationGeneration.current++; pause(); } else void playPrepared(); },
    advance: async destination => {
      const state = published.current; if (!state) return;
      pause();
      const preparation = preparationGeneration.current;
      const target = advanceDestination(state.clock, destination);
      let hours: DualInitialization['weather'] | null;
      try { hours = await prepareAndAcceptWeather(state.clock.at, target, state.clock.at); }
      catch (reason) { if (preparation === preparationGeneration.current) setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); return; }
      if (preparation !== preparationGeneration.current) return;
      if (!hours) { setError('Prepare the required weather before advancing.'); return; }
      setError(null); post({ type: 'skip', request: { destination, target } });
    },
    cancel: () => { preparationGeneration.current++; abortWeatherPreparation(); paused.current = true; busy.current = false;
      setPublication(p => p ? { ...p, clock: { ...p.clock, paused: true }, advance: p.advance ? { ...p.advance, state: 'cancelled' } : null } : p);
      post({ type: 'cancel' }); },
    resume: () => {
      const state = published.current; if (!state?.advance) return;
      pause(); const preparation = preparationGeneration.current;
      void prepareAndAcceptWeather(state.clock.at, state.advance.request.target, state.clock.at).then(hours => {
        if (preparation !== preparationGeneration.current) return;
        if (!hours) { setError('Prepare the required weather before resuming.'); return; }
        setError(null); post({ type: 'resume' });
      }).catch(reason => { if (preparation === preparationGeneration.current) setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); });
    }, select: (guestId, autoTrack) => post({ type: 'select', id: guestId, autoTrack }),
    acknowledge: signalId => post({ type: 'acknowledge', id: signalId }),
    follow: async () => {
      const state = published.current; if (!state) return;
      if (!weatherReady.current) {
        try { await prepareAndAcceptWeather(state.clock.at, new Date(Date.parse(state.clock.at) + 86400000).toISOString(), state.clock.at); }
        catch { return; }
      }
      preparationGeneration.current++; post({ type: 'follow' });
    }, checkpoint, addSnow };
}
