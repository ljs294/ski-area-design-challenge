import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { TerrainRecord } from '../types/terrain';
import type { SavedWeatherRun } from '../types/gameSave';
import {
  loadWeatherPackageByContentHash,
  loadWeatherPackageResult,
  type WeatherPackageLoadStatus,
} from '../weatherStorageClient';
import {
  advanceWeatherPlayback,
  createWeatherPlayback,
  seekWeatherPlayback,
  type WeatherLabSpeed,
  type WeatherPlaybackState,
} from '../weather/playback';
import {
  forecastForSession,
  loadWeatherSession,
  weatherAtSession,
  type WeatherForecast,
  type WeatherSession,
} from '../weather/weatherSession';
import { weatherInstantForLocal } from '../weather/localTime';
import { weatherTerrainBinding } from '../weather/terrainBinding';
import type { ResolvedWeatherHour, WeatherDataPackage } from '../weather/weatherModel';

const HOUR_MS = 3_600_000;

export type GameWeatherStatus =
  | 'no-terrain'
  | 'loading'
  | 'unprepared'
  | 'prepared'
  | 'ready'
  | 'package-unavailable'
  | 'binding-mismatch'
  | 'version-mismatch'
  | 'corrupt';

interface GameWeatherState {
  status: GameWeatherStatus;
  message: string;
  weatherPackage: WeatherDataPackage | null;
  session: WeatherSession | null;
  playback: WeatherPlaybackState | null;
}

export interface GameWeatherController {
  status: GameWeatherStatus;
  message: string;
  weatherPackage: WeatherDataPackage | null;
  session: WeatherSession | null;
  playback: WeatherPlaybackState | null;
  current: ResolvedWeatherHour | null;
  forecast: WeatherForecast | null;
  analysisOpen: boolean;
  start(): void;
  togglePlayback(): void;
  setSpeed(speed: WeatherLabSpeed): void;
  toggleAnalysis(): void;
}

function messageForLoadStatus(status: WeatherPackageLoadStatus, error?: string): string {
  if (status === 'not-found') return 'Weather package unavailable. Prepare this terrain in Weather Lab; no download occurs while playing.';
  if (status === 'binding-mismatch') return error ?? 'The prepared weather package belongs to a different terrain revision.';
  if (status === 'corrupt') return error ?? 'The offline weather package is corrupt and must be prepared again.';
  return error ?? 'Weather package unavailable.';
}

function cursorHour(planStartsAt: string, cursor: string): number {
  const elapsed = new Date(cursor).getTime() - new Date(planStartsAt).getTime();
  return Math.max(0, Math.floor(elapsed / HOUR_MS));
}

function runIdentity(run: SavedWeatherRun | undefined): string | null {
  if (!run) return null;
  return [
    run.packageContentHash,
    run.terrainBinding,
    run.seed,
    run.generatorVersion,
    run.configurationVersion,
    run.localStartAt,
  ].join('|');
}

/**
 * The game-side weather adapter reads only an installed package. It owns a
 * presentation clock, not provider access, game effects, or storage writes.
 */
export function useGameWeather({
  terrain,
  weatherRun,
  onWeatherRunChange,
}: {
  terrain: TerrainRecord | null;
  weatherRun: SavedWeatherRun | undefined;
  onWeatherRunChange(run: SavedWeatherRun): void;
}): GameWeatherController {
  const terrainBinding = useMemo(() => terrain ? weatherTerrainBinding(terrain) : null, [terrain]);
  const [state, setState] = useState<GameWeatherState>({
    status: terrain ? 'loading' : 'no-terrain',
    message: terrain ? 'Loading offline weather package...' : 'Weather requires a terrain map.',
    weatherPackage: null,
    session: null,
    playback: null,
  });
  const [analysisOpen, setAnalysisOpen] = useState(false);
  const latestRunRef = useRef<SavedWeatherRun | undefined>(weatherRun);
  const onWeatherRunChangeRef = useRef(onWeatherRunChange);
  const persistedCursorHourRef = useRef<number>(weatherRun?.cursorHour ?? -1);
  const lastFrameRef = useRef<number | null>(null);
  const identity = runIdentity(weatherRun);

  useEffect(() => { latestRunRef.current = weatherRun; }, [weatherRun]);
  useEffect(() => { onWeatherRunChangeRef.current = onWeatherRunChange; }, [onWeatherRunChange]);

  useEffect(() => {
    let cancelled = false;
    const publish = (next: GameWeatherState) => { if (!cancelled) setState(next); };
    const load = async () => {
      if (!terrain || !terrainBinding) {
        publish({ status: 'no-terrain', message: 'Weather requires a terrain map.', weatherPackage: null, session: null, playback: null });
        return;
      }
      publish({ status: 'loading', message: 'Loading installed offline weather package...', weatherPackage: null, session: null, playback: null });
      try {
        if (weatherRun && weatherRun.terrainBinding !== terrainBinding) {
          publish({ status: 'binding-mismatch', message: 'This saved weather run belongs to a different terrain revision.', weatherPackage: null, session: null, playback: null });
          return;
        }
        let weatherPackage: WeatherDataPackage | null = null;
        if (weatherRun) {
          weatherPackage = await loadWeatherPackageByContentHash(weatherRun.packageContentHash);
          if (!weatherPackage) {
            publish({ status: 'package-unavailable', message: 'The weather package pinned by this save is unavailable locally.', weatherPackage: null, session: null, playback: null });
            return;
          }
        } else {
          const result = await loadWeatherPackageResult(terrain.key, terrainBinding);
          if (result.status !== 'ready') {
            publish({ status: result.status === 'not-found' ? 'unprepared' : result.status, message: messageForLoadStatus(result.status, result.error), weatherPackage: null, session: null, playback: null });
            return;
          }
          weatherPackage = result.weatherPackage;
        }
        if (weatherPackage.manifest.terrainBinding !== terrainBinding) {
          publish({ status: 'binding-mismatch', message: 'The installed weather package belongs to a different terrain revision.', weatherPackage: null, session: null, playback: null });
          return;
        }
        if (!weatherRun) {
          publish({ status: 'prepared', message: 'Offline weather is prepared. Start weather to create a deterministic game run.', weatherPackage, session: null, playback: null });
          return;
        }
        if (weatherRun.generatorVersion !== weatherPackage.manifest.generatorVersion) {
          publish({ status: 'version-mismatch', message: 'This save is pinned to an incompatible weather generator version.', weatherPackage: null, session: null, playback: null });
          return;
        }
        const session = await loadWeatherSession(weatherPackage, {
          seed: weatherRun.seed,
          startsAt: weatherRun.localStartAt,
          latitude: terrain.latitude,
          longitude: terrain.longitude,
        });
        const historicalYear = session.historicalYears[0]?.year ?? weatherPackage.manifest.historicalStartYear;
        const basePlayback = createWeatherPlayback(session.plan, historicalYear);
        const requestedCursor = new Date(new Date(session.plan.startsAt).getTime() + weatherRun.cursorHour * HOUR_MS).toISOString();
        const playback = seekWeatherPlayback(session.plan, basePlayback, requestedCursor);
        persistedCursorHourRef.current = cursorHour(session.plan.startsAt, playback.cursor);
        publish({ status: 'ready', message: 'Offline weather session ready.', weatherPackage, session, playback });
      } catch (error) {
        publish({ status: 'corrupt', message: error instanceof Error ? error.message : 'Unable to read the offline weather package.', weatherPackage: null, session: null, playback: null });
      }
    };
    void load();
    return () => { cancelled = true; };
  }, [terrain, terrainBinding, identity]);

  useEffect(() => {
    if (!state.session || !state.playback?.running) return;
    let frame = 0;
    const tick = (now: number) => {
      const previous = lastFrameRef.current ?? now;
      lastFrameRef.current = now;
      setState((current) => {
        if (!current.session || !current.playback) return current;
        const playback = advanceWeatherPlayback(current.session.plan, current.playback, now - previous);
        const hour = cursorHour(current.session.plan.startsAt, playback.cursor);
        const run = latestRunRef.current;
        if (run && hour !== persistedCursorHourRef.current) {
          persistedCursorHourRef.current = hour;
          onWeatherRunChangeRef.current({ ...run, cursorHour: hour });
        }
        return { ...current, playback };
      });
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(frame);
      lastFrameRef.current = null;
    };
  }, [state.session, state.playback?.running]);

  const start = useCallback(() => {
    if (!terrain || !terrainBinding || !state.weatherPackage || state.status !== 'prepared') return;
    const weatherPackage = state.weatherPackage;
    const localStartAt = weatherInstantForLocal(
      { year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0 },
      weatherPackage.manifest.timezone,
    );
    onWeatherRunChangeRef.current({
      packageContentHash: weatherPackage.manifest.contentHash,
      terrainBinding,
      seed: `game-${terrain.key}`,
      generatorVersion: weatherPackage.manifest.generatorVersion,
      configurationVersion: 1,
      localStartAt,
      cursorHour: 0,
    });
  }, [state.status, state.weatherPackage, terrain, terrainBinding]);

  const togglePlayback = useCallback(() => {
    setState((current) => current.playback ? {
      ...current,
      playback: { ...current.playback, running: !current.playback.running },
    } : current);
  }, []);

  const setSpeed = useCallback((speed: WeatherLabSpeed) => {
    setState((current) => current.playback ? { ...current, playback: { ...current.playback, speed } } : current);
  }, []);

  const current = useMemo(() => state.session && state.playback
    ? weatherAtSession(state.session, state.playback.cursor) : null, [state.session, state.playback]);
  const forecast = useMemo(() => state.session && state.playback
    ? forecastForSession(state.session, state.playback.cursor, 24) : null, [state.session, state.playback]);

  return {
    ...state,
    current,
    forecast,
    analysisOpen,
    start,
    togglePlayback,
    setSpeed,
    toggleAnalysis: () => setAnalysisOpen((open) => !open),
  };
}
