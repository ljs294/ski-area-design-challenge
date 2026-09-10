import { useCallback, useEffect, useRef, useState } from 'react';
import type { DualCheckpoint, DualInitialization, DualPublication, DualSpeed, ResortSimulationInput, AdvanceDestination } from '../dualClock/model';
import { advanceDestination } from '../dualClock/clock';
import type { DualCommand, DualWorkerRequest, DualWorkerResponse } from './dualClockProtocol';
import type { SnowLayerState } from './useSnowLayer';
import type { PreparedRoute } from '../dualClock/geometry';
import { applyDualSnowPatch } from './dualSnowPublication';
import { decodeDualMovement } from './dualMovementPublication';
import type { SnowAddResult } from '../types/dualClock';

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
  follow(): void;
  checkpoint(): Promise<DualCheckpoint>;
  addSnow(meters: number): Promise<SnowAddResult>;
}
export function useDualClockRuntime(options: {
  enabled: boolean; initialization: DualInitialization | null; snow: SnowLayerState;
  prepareWeather(from: string, to: string): Promise<DualInitialization['weather'] | null>;
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
  const initial = useRef(options.initialization); initial.current = options.initialization;
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
  useEffect(() => {
    if (!binding || !initial.current) return;
    const instance = new Worker(new URL('./dualClock.worker.ts', import.meta.url), { type: 'module' });
    const waiters = pending.current;
    const snowAddWaiters = pendingSnowAdd.current;
    const preparationToken = preparationGeneration;
    worker.current = instance; generation.current++; const active = generation.current;
    setReady(false); setError(null);
    instance.onmessage = (event: MessageEvent<DualWorkerResponse>) => {
      const response = event.data;
      if (response.generation !== active || instance !== worker.current) return;
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
      for (const waiter of pending.current.values()) waiter.reject(new Error('Simulation worker failed.')); pending.current.clear();
      for (const waiter of snowAddWaiters.values()) waiter.reject(new Error('Simulation worker failed.')); snowAddWaiters.clear(); };
    post({ type: 'initialize', input: initial.current });
    preparedTerrain.current = initial.current.terrain;
    return () => { preparationToken.current++; instance.terminate(); if (worker.current === instance) worker.current = null;
      for (const waiter of waiters.values()) waiter.reject(new Error('Simulation replaced.')); waiters.clear();
      for (const waiter of snowAddWaiters.values()) waiter.reject(new Error('Simulation replaced.')); snowAddWaiters.clear(); };
  }, [binding, post]);
  useEffect(() => {
    const terrain = options.initialization?.terrain;
    if (ready && terrain && preparedTerrain.current !== terrain) { preparedTerrain.current = terrain; post({ type: 'terrain', terrain }); }
  }, [ready, options.initialization?.terrain, post]);
  useEffect(() => {
    if (ready && options.initialization?.timezone && !options.initialization?.checkpoint) post({ type: 'timezone', timezone: options.initialization.timezone });
  }, [ready, options.initialization?.timezone, options.initialization?.checkpoint, post]);
  useEffect(() => {
    if (ready && options.initialization?.weather.length) post({ type: 'weather', hours: options.initialization.weather });
  }, [ready, options.initialization?.weather, post]);
  useEffect(() => {
    if (!options.enabled) return;
    let frame = 0, last = performance.now(), accumulated = 0;
    const tick = (now: number) => {
      const state = published.current;
      if (document.hidden || paused.current || state?.advance?.state === 'running') { last = now; accumulated = 0; }
      else {
        accumulated += Math.min(250, Math.max(0, now - last)); last = now;
        if (state && !busy.current && accumulated >= 100) {
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
  }, [options.enabled, post]);
  const pause = useCallback(() => { preparationGeneration.current++; paused.current = true; post({ type: 'pause' }); }, [post]);
  const checkpoint = useCallback((): Promise<DualCheckpoint> => {
    if (!worker.current) return Promise.reject(new Error('Wait for the simulation to initialize before saving.'));
    preparationGeneration.current++; paused.current = true;
    return new Promise((resolve, reject) => post({ type: 'checkpoint' }, requestId => pending.current.set(requestId, { resolve, reject })));
  }, [post]);
  const addSnow = useCallback((meters: number): Promise<SnowAddResult> => {
    if (!worker.current) return Promise.reject(new Error('Wait for the simulation to initialize before adding snow.'));
    preparationGeneration.current++; paused.current = true;
    return new Promise((resolve, reject) => post({ type: 'snow-add', meters }, requestId => pendingSnowAdd.current.set(requestId, { resolve, reject })));
  }, [post]);
  const updateResort = useCallback((input: ResortSimulationInput) => { post({ type: 'resort', input }); }, [post]);
  const playPrepared = async () => {
    const state = published.current; if (!state) return;
    const preparation = ++preparationGeneration.current;
    try {
      if (!initial.current?.weather.length) {
        const hours = await optionsRef.current.prepareWeather(state.clock.at, new Date(Date.parse(state.clock.at) + 86400000).toISOString());
        if (preparation !== preparationGeneration.current) return;
        if (!hours) { setError('Prepare the required weather before playing.'); return; }
        post({ type: 'weather', hours });
      }
      paused.current = false; setError(null); post({ type: 'play' });
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); }
  };
  return { publication, geometry, error, ready, initialPortal: options.initialization?.checkpoint?.portal ?? null,
    initialTicketPriceCents: options.initialization?.checkpoint?.nextTicketPriceCents ?? 10000, updateResort,
    setSpeed: speed => post({ type: 'speed', speed }), pause,
    togglePlayback: () => { if (!published.current) return; if (!paused.current) { preparationGeneration.current++; pause(); } else void playPrepared(); },
    advance: async destination => {
      const state = published.current; if (!state) return;
      pause();
      const preparation = preparationGeneration.current;
      const target = advanceDestination(state.clock, destination);
      let hours: DualInitialization['weather'] | null;
      try { hours = await optionsRef.current.prepareWeather(state.clock.at, target); }
      catch (reason) { if (preparation === preparationGeneration.current) setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); return; }
      if (preparation !== preparationGeneration.current) return;
      if (!hours) { setError('Prepare the required weather before advancing.'); return; }
      post({ type: 'weather', hours });
      setError(null); post({ type: 'skip', request: { destination, target } });
    },
    cancel: () => { preparationGeneration.current++; paused.current = true; busy.current = false;
      setPublication(p => p ? { ...p, clock: { ...p.clock, paused: true }, advance: p.advance ? { ...p.advance, state: 'cancelled' } : null } : p);
      post({ type: 'cancel' }); },
    resume: () => {
      const state = published.current; if (!state?.advance) return;
      pause(); const preparation = preparationGeneration.current;
      void optionsRef.current.prepareWeather(state.clock.at, state.advance.request.target).then(hours => {
        if (preparation !== preparationGeneration.current) return;
        if (!hours) { setError('Prepare the required weather before resuming.'); return; }
        post({ type: 'weather', hours }); setError(null); post({ type: 'resume' });
      }).catch(reason => { if (preparation === preparationGeneration.current) setError(reason instanceof Error ? reason.message : 'Weather preparation failed.'); });
    }, select: (guestId, autoTrack) => post({ type: 'select', id: guestId, autoTrack }),
    acknowledge: signalId => post({ type: 'acknowledge', id: signalId }), follow: () => { preparationGeneration.current++; post({ type: 'follow' }); }, checkpoint, addSnow };
}
