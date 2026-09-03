import { useCallback, useEffect, useMemo, useRef, useState, type MutableRefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { GameSave, SavedWeatherRun } from '../types/gameSave';
import type { SimulationClock, SimulationSpeed, TimeEngineSnapshot, TimeScaleConfig } from '../types/simulation';
import type { TerrainRecord } from '../types/terrain';
import type { ResolvedWeatherHour, WeatherDataPackage } from '../weather/weatherModel';
import { loadWeatherPackageByContentHash, loadWeatherPackageResult, saveWeatherPackage } from '../weatherStorageClient';
import { prepareWeatherPackage } from '../weatherServiceClient';
import { forecastIssueAt, historicalAverageAnnualSnowfallCm, loadAnnualWeatherSession, weatherYearLabel,
  WEATHER_YEAR_CONFIGURATION_VERSION } from '../weather/annualWeather';
import { issueGameForecast, type GameForecastIssue } from '../weather/gameForecast';
import { weatherInstantForLocal } from '../weather/localTime';
import { weatherAtSession, type WeatherSession } from '../weather/weatherSession';
import { weatherTerrainBinding } from '../weather/terrainBinding';
import { generateBareSnowGrid } from '../snow';
import { advanceClock, advanceSummerToSeptember, confirmSeasonTransition, createClock, createTimeSnapshot,
  scaledSimulationMinutes,
  DEFAULT_TIME_CONFIG, restoreTimeSnapshot } from '../../time-engine/src/timeEngine';
import type { SnowLayerState } from './useSnowLayer';
import { SnowStepClient } from './snowStepClient';
import type { RenderQuality } from './renderProfile';
import { createTerrainThermalModel, terrainWeatherFieldForHour } from '../weather/terrainThermal';
import { setActiveTerrainWeather } from './terrainWeatherCache';

const HOUR_MS = 3_600_000;

export type GameSimulationStatus = 'no-terrain' | 'loading' | 'design-only' | 'prepared' | 'ready' |
  'package-unavailable' | 'binding-mismatch' | 'version-mismatch' | 'corrupt' | 'working';

export interface GameSimulationController {
  status: GameSimulationStatus;
  message: string;
  clock: SimulationClock;
  weatherPackage: WeatherDataPackage | null;
  session: WeatherSession | null;
  averageAnnualSnowfallCm: number | null;
  current: ResolvedWeatherHour | null;
  forecast: GameForecastIssue | null;
  analysisOpen: boolean;
  togglePlayback(): void;
  setSpeed(speed: SimulationSpeed): void;
  advancePlanningPeriod(): Promise<void>;
  confirmTransition(): Promise<void>;
  prepareWeather(): Promise<void>;
  prepareWeatherForTerrain(terrain: TerrainRecord, signal?: AbortSignal): Promise<{ ok: boolean; error?: string }>;
  toggleAnalysis(): void;
  snapshot(): { time: TimeEngineSnapshot; weatherRun?: SavedWeatherRun };
  pause(): void;
}

function configFor(timezone: string): TimeScaleConfig {
  return {
    ...DEFAULT_TIME_CONFIG,
    timezone,
    initialSummerStart: weatherInstantForLocal(
      { year: 2026, month: 5, day: 1, hour: 0, minute: 0, second: 0 }, timezone),
  };
}

function runIdentity(run: SavedWeatherRun): string {
  return `${run.packageContentHash}|${run.terrainBinding}|${run.seed}|${run.generatorVersion}|${run.configurationVersion}|${run.localStartAt}`;
}

function projectedHour(run: SavedWeatherRun, at: string): number {
  return Math.max(0, Math.floor((new Date(at).getTime() - new Date(run.localStartAt).getTime()) / HOUR_MS));
}

function hoursCrossed(session: WeatherSession, from: string, to: string): ResolvedWeatherHour[] {
  const fromMs = new Date(from).getTime();
  const toMs = new Date(to).getTime();
  return session.plan.hours
    .filter((hour) => new Date(hour.at).getTime() > fromMs && new Date(hour.at).getTime() <= toMs)
    .map((hour) => weatherAtSession(session, hour.at))
    .filter((hour): hour is ResolvedWeatherHour => hour !== null);
}

function applyMapEnvironment(map: maplibregl.Map | null, current: ResolvedWeatherHour | null): void {
  if (!map?.isStyleLoaded()) return;
  const daylight = current ? Math.max(0.08, Math.min(1, (current.solarElevationDeg + 8) / 50)) : 0.85;
  const cloud = current ? current.cloudCoverPct / 100 : 0;
  map.setLight({
    anchor: 'map',
    color: `rgb(${Math.round(180 + 60 * daylight)},${Math.round(190 + 55 * daylight)},${Math.round(210 + 40 * daylight)})`,
    intensity: Math.max(0.15, daylight * (1 - cloud * 0.45)),
    position: [1.5, current?.solarAzimuthDeg ?? 180, 90 - (current?.solarElevationDeg ?? 45)],
  });
  map.setSky({
    'sky-color': current && daylight < 0.2 ? '#071326' : '#5f9ed6',
    'horizon-color': current && daylight < 0.2 ? '#18263d' : '#eef4fb',
    'fog-color': current && daylight < 0.2 ? '#18263d' : '#eef4fb',
    'sky-horizon-blend': 0.6,
    'horizon-fog-blend': 0.1 + cloud * 0.2,
    'fog-ground-blend': 1,
    'atmosphere-blend': 0,
  });
}

export function useGameSimulation({
  terrain, initialTime, initialWeatherRun, snow, mapRef, renderQuality, reducedMotion,
}: {
  terrain: TerrainRecord | null;
  initialTime: GameSave['time'];
  initialWeatherRun: SavedWeatherRun | undefined;
  snow: SnowLayerState;
  mapRef: MutableRefObject<maplibregl.Map | null>;
  renderQuality: RenderQuality;
  reducedMotion: boolean;
}): GameSimulationController {
  const [clock, setClock] = useState(() => createClock());
  const clockRef = useRef(clock);
  const configRef = useRef<TimeScaleConfig>(DEFAULT_TIME_CONFIG);
  const runRef = useRef(initialTime && typeof initialWeatherRun?.configurationVersion === 'number' &&
    initialWeatherRun.configurationVersion >= 2
    ? initialWeatherRun : undefined);
  const legacyPackageHashRef = useRef(!initialTime ? initialWeatherRun?.packageContentHash : undefined);
  const sessionRef = useRef<WeatherSession | null>(null);
  const [session, setSession] = useState<WeatherSession | null>(null);
  const [weatherPackage, setWeatherPackage] = useState<WeatherDataPackage | null>(null);
  const [status, setStatus] = useState<GameSimulationStatus>(terrain ? 'loading' : 'no-terrain');
  const [message, setMessage] = useState('Loading simulation...');
  const [analysisOpen, setAnalysisOpen] = useState(false);
  const accumulatorRef = useRef(0);
  const busyRef = useRef(false);
  const snowWorkerRef = useRef(new SnowStepClient());
  const binding = useMemo(() => terrain ? weatherTerrainBinding(terrain) : null, [terrain]);

  const publishClock = useCallback((next: SimulationClock) => {
    clockRef.current = next;
    setClock(next);
  }, []);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      if (!terrain || !binding) {
        setStatus('no-terrain'); setMessage('Weather requires a terrain map.');
        return;
      }
      setStatus('loading'); setMessage('Loading installed offline weather package...');
      try {
        const savedRun = runRef.current;
        let weather: WeatherDataPackage | null;
        if (savedRun) weather = await loadWeatherPackageByContentHash(savedRun.packageContentHash);
        else {
          weather = legacyPackageHashRef.current
            ? await loadWeatherPackageByContentHash(legacyPackageHashRef.current) : null;
          if (!weather) {
            const result = await loadWeatherPackageResult(terrain.key, binding);
            weather = result.status === 'ready' ? result.weatherPackage : null;
          }
        }
        if (cancelled) return;
        if (!weather) {
          setStatus(savedRun ? 'package-unavailable' : 'design-only');
          setMessage(savedRun ? 'The weather package pinned by this save is unavailable locally.' :
            'Weather is not prepared. Design remains available, but time cannot cross September 1.');
          return;
        }
        if (weather.manifest.terrainBinding !== binding || savedRun?.terrainBinding !== binding) {
          setStatus('binding-mismatch'); setMessage('The installed weather package belongs to another terrain revision.');
          return;
        }
        const config = configFor(weather.manifest.timezone);
        configRef.current = config;
        const restored = initialTime ? restoreTimeSnapshot(initialTime, config) : createClock(config);
        publishClock(restored);
        setWeatherPackage(weather);
        if (!savedRun) {
          setStatus('prepared'); setMessage('Weather prepared. Complete summer planning to generate the September weather year.');
          return;
        }
        if (savedRun.generatorVersion !== weather.manifest.generatorVersion) {
          setStatus('version-mismatch'); setMessage('This save requires a different weather generator version.');
          return;
        }
        const year = weatherYearLabel(savedRun.localStartAt, weather.manifest.timezone);
        const loaded = await loadAnnualWeatherSession(weather, savedRun.seed, year);
        if (cancelled) return;
        sessionRef.current = loaded; setSession(loaded);
        setStatus('ready'); setMessage('Offline annual weather simulation ready.');
      } catch (error) {
        if (!cancelled) {
          setStatus('corrupt');
          setMessage(error instanceof Error ? error.message : 'Unable to load the weather simulation.');
        }
      }
    };
    void load();
    return () => { cancelled = true; };
  }, [terrain, binding, initialTime, publishClock]);

  const commitAdvance = useCallback(async (next: SimulationClock, refresh = true) => {
    if (!terrain || busyRef.current) return;
    busyRef.current = true;
    try {
      const activeSession = sessionRef.current;
      const crossed = activeSession ? hoursCrossed(activeSession, clockRef.current.calendarDate, next.calendarDate) : [];
      if (crossed.length > 0 && snow.gridRef.current) {
        const result = await snowWorkerRef.current.run(binding ?? '', terrain, snow.gridRef.current, crossed);
        snow.replace(result.grid, refresh && result.changedCells > 0);
      }
      const run = runRef.current;
      if (run) runRef.current = { ...run, cursorHour: projectedHour(run, next.calendarDate) };
      publishClock(next);
    } catch (error) {
      publishClock({ ...clockRef.current, runState: 'paused' });
      setStatus('corrupt'); setMessage(error instanceof Error ? error.message : 'Snow simulation failed.');
    } finally {
      busyRef.current = false;
    }
  }, [binding, publishClock, snow, terrain]);

  useEffect(() => () => snowWorkerRef.current?.cancel(), [terrain]);

  const ensureAnnualRun = useCallback(async (target: SimulationClock) => {
    if (!terrain || !weatherPackage || !binding) {
      setStatus('design-only');
      setMessage('Weather is not installed for this terrain. Prepare the historical package and try again.');
      return false;
    }
    const year = weatherYearLabel(target.calendarDate, weatherPackage.manifest.timezone);
    if (sessionRef.current && weatherYearLabel(sessionRef.current.plan.startsAt, sessionRef.current.timezone) === year) return true;
    setStatus('working'); setMessage(`Generating fixed ${year}-${year + 1} weather truth...`);
    const baseSeed = runRef.current?.seed ?? `game-${terrain.key}`;
    const loaded = await loadAnnualWeatherSession(weatherPackage, baseSeed, year);
    sessionRef.current = loaded; setSession(loaded);
    const nextRun: SavedWeatherRun = {
      packageContentHash: weatherPackage.manifest.contentHash,
      terrainBinding: binding,
      seed: baseSeed,
      generatorVersion: weatherPackage.manifest.generatorVersion,
      configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION,
      localStartAt: loaded.plan.startsAt,
      cursorHour: projectedHour({ localStartAt: loaded.plan.startsAt } as SavedWeatherRun, target.calendarDate),
    };
    if (!runRef.current && snow.gridRef.current) snow.replace(generateBareSnowGrid(terrain), false);
    runRef.current = nextRun;
    setStatus('ready'); setMessage('Offline annual weather simulation ready.');
    return true;
  }, [binding, snow, terrain, weatherPackage]);

  const advancePlanningPeriod = useCallback(async () => {
    if (clockRef.current.season !== 'summer' || busyRef.current) return;
    const result = advanceSummerToSeptember(clockRef.current, configRef.current);
    if (!(await ensureAnnualRun(result.clock))) {
      setMessage('Prepare weather before advancing beyond September 1.');
      setAnalysisOpen(true);
      return;
    }
    await commitAdvance(result.clock);
  }, [commitAdvance, ensureAnnualRun]);

  const confirmTransition = useCallback(async () => {
    const result = confirmSeasonTransition(clockRef.current, configRef.current);
    if (result.simulatedMinutesAdvanced > 0) await ensureAnnualRun(result.clock);
    await commitAdvance(result.clock);
  }, [commitAdvance, ensureAnnualRun]);

  const prepareWeather = useCallback(async () => {
    if (!terrain || !binding || status === 'working') return;
    setStatus('working'); setMessage('Preparing offline weather package...');
    try {
      const prepared = await prepareWeatherPackage(terrain, {
        onProgress: (job) => setMessage(job.progress.message || `Weather preparation: ${job.status}`),
      });
      await saveWeatherPackage(prepared);
      setWeatherPackage(prepared);
      configRef.current = configFor(prepared.manifest.timezone);
      publishClock(createClock(configRef.current));
      setStatus('prepared'); setMessage('Weather prepared. Advance planning to September 1.');
    } catch (error) {
      setStatus('design-only');
      const detail = error instanceof Error ? error.message : 'Weather preparation failed.';
      setMessage(detail === 'Failed to fetch'
        ? 'Weather preparation service is not reachable. Restart Mountain Planner and try again.' : detail);
    }
  }, [binding, publishClock, status, terrain]);

  const prepareWeatherForTerrain = useCallback(async (record: TerrainRecord, signal?: AbortSignal) => {
    try {
      await saveWeatherPackage(await prepareWeatherPackage(record, { signal }));
      return { ok: true };
    } catch (error) {
      if (signal?.aborted) throw error;
      return { ok: false, error: error instanceof Error ? error.message : 'Weather preparation failed.' };
    }
  }, []);

  useEffect(() => {
    if (clock.runState !== 'running' || clock.season !== 'winter') return;
    let last = performance.now();
    const timer = window.setInterval(() => {
      if (document.hidden) { last = performance.now(); return; }
      const now = performance.now();
      const wallMs = Math.min(configRef.current.maxWallDeltaMs, Math.max(0, now - last));
      last = now;
      accumulatorRef.current += scaledSimulationMinutes(wallMs, clockRef.current.speed, configRef.current);
      if (busyRef.current) return;
      const minutes = Math.floor(accumulatorRef.current);
      if (minutes < 1) return;
      accumulatorRef.current -= minutes;
      const result = advanceClock(clockRef.current, minutes, configRef.current);
      void commitAdvance(result.clock);
    }, 1000 / configRef.current.uiUpdateHz);
    return () => window.clearInterval(timer);
  }, [clock.runState, clock.season, commitAdvance]);

  const current = useMemo(() => session ? weatherAtSession(session, clock.calendarDate) : null,
    [session, clock.calendarDate]);
  const averageAnnualSnowfallCm = useMemo(() => session
    ? historicalAverageAnnualSnowfallCm(session.historicalYears, session.timezone) : null, [session]);
  useEffect(() => {
    setActiveTerrainWeather(terrain && current
      ? terrainWeatherFieldForHour(createTerrainThermalModel(terrain), current) : null);
    return () => setActiveTerrainWeather(null);
  }, [terrain, current]);
  const forecast = useMemo(() => {
    const run = runRef.current;
    if (!session || !run || !current) return null;
    const issuedAt = forecastIssueAt(clock.calendarDate, session.timezone);
    return issueGameForecast(session, issuedAt, runIdentity(run));
  }, [session, current, clock.calendarDate]);

  useEffect(() => {
    const map = mapRef.current;
    const apply = () => applyMapEnvironment(map, current);
    apply();
    map?.on('style.load', apply);
    return () => { map?.off('style.load', apply); };
  }, [mapRef, current]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !current || current.precipitationMm <= 0.01 || reducedMotion || renderQuality === 'performance' ||
      window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    const canvas = document.createElement('canvas');
    canvas.className = 'game-weather-effects';
    canvas.setAttribute('aria-hidden', 'true');
    map.getContainer().append(canvas);
    const context = canvas.getContext('2d');
    if (!context) { canvas.remove(); return; }
    let seed = 2166136261;
    const hourSeed = `${current.at}:${current.precipitationType}`;
    for (let index = 0; index < hourSeed.length; index += 1) {
      seed ^= hourSeed.charCodeAt(index); seed = Math.imul(seed, 16777619);
    }
    const random = () => { seed = Math.imul(seed ^ seed >>> 15, 2246822519); return (seed >>> 0) / 4_294_967_296; };
    const limit = renderQuality === 'ultra' ? 180 : renderQuality === 'high' ? 140 : 80;
    const count = Math.min(limit, Math.max(20, Math.round(current.precipitationMm * 35)));
    const particles = Array.from({ length: count }, () => ({ x: random(), y: random(), speed: 0.35 + random() * 0.65 }));
    let frame = 0, started = performance.now();
    const draw = (now: number) => {
      const rect = map.getContainer().getBoundingClientRect();
      const ratio = Math.min(2, window.devicePixelRatio || 1);
      if (canvas.width !== Math.round(rect.width * ratio) || canvas.height !== Math.round(rect.height * ratio)) {
        canvas.width = Math.round(rect.width * ratio); canvas.height = Math.round(rect.height * ratio);
        canvas.style.width = `${rect.width}px`; canvas.style.height = `${rect.height}px`;
      }
      context.setTransform(ratio, 0, 0, ratio, 0, 0); context.clearRect(0, 0, rect.width, rect.height);
      context.strokeStyle = 'rgba(215,230,245,.55)'; context.fillStyle = 'rgba(245,250,255,.8)';
      const elapsed = (now - started) / 1000;
      for (const particle of particles) {
        const y = ((particle.y + elapsed * particle.speed) % 1.1) * rect.height;
        const x = particle.x * rect.width;
        if (current.precipitationType === 'snow' || current.precipitationType === 'mixed') {
          context.beginPath(); context.arc(x, y, 1.2 + particle.speed, 0, Math.PI * 2); context.fill();
        } else {
          context.beginPath(); context.moveTo(x, y); context.lineTo(x - 3, y + 10); context.stroke();
        }
      }
      frame = requestAnimationFrame(draw);
    };
    frame = requestAnimationFrame(draw);
    return () => { cancelAnimationFrame(frame); canvas.remove(); };
  }, [mapRef, current, reducedMotion, renderQuality]);

  return {
    status, message, clock, weatherPackage, session, current, forecast, averageAnnualSnowfallCm, analysisOpen,
    togglePlayback: () => publishClock({ ...clockRef.current,
      runState: clockRef.current.runState === 'running' ? 'paused' : 'running' }),
    setSpeed: (speed) => publishClock({ ...clockRef.current, speed }),
    advancePlanningPeriod,
    confirmTransition,
    prepareWeather,
    prepareWeatherForTerrain,
    toggleAnalysis: () => setAnalysisOpen((open) => !open),
    snapshot: () => {
      const run = runRef.current;
      return { time: createTimeSnapshot(clockRef.current, configRef.current),
        ...(run ? { weatherRun: { ...run, cursorHour: projectedHour(run, clockRef.current.calendarDate) } } : {}) };
    },
    pause: () => publishClock({ ...clockRef.current, runState: 'paused' }),
  };
}
