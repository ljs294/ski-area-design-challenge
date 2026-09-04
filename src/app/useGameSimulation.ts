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
import { localWeatherDateKey, weatherInstantForLocal, weatherLocalParts } from '../weather/localTime';
import { resolveWeatherHour, weatherAtSession, type WeatherSession } from '../weather/weatherSession';
import { weatherTerrainBinding } from '../weather/terrainBinding';
import { generateBareSnowGrid } from '../snow';
import { advanceSummerToSeptember, confirmSeasonTransition, createClock, createTimeSnapshot,
  advanceClockSeconds, DEFAULT_TIME_CONFIG, restoreTimeSnapshot,
} from '../../time-engine/src/timeEngine';
import { ContinuousSimulationCoordinator } from './continuousSimulationCoordinator';
import { createCompositeWeekWeather, type CompositeWeekOutlook, type CompositeWeekWeather } from '../weather/compositeWeek';
import { SIMULATION_SECONDS_PER_WEEK } from '../types/simulation';
import type { SnowLayerState } from './useSnowLayer';
import { SnowStepClient } from './snowStepClient';
import type { RenderQuality } from './renderProfile';
import { createTerrainThermalModel, terrainWeatherFieldForHour } from '../weather/terrainThermal';
import { setActiveTerrainWeather } from './terrainWeatherCache';
import { isDeveloperConsoleEnabled, skipClockWithoutSimulation, type DeveloperClockSkip,
  type SimulationTimeDiscontinuity } from './developerConsoleCommands';

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
  weeklyOutlook: CompositeWeekOutlook | null;
  forecast: GameForecastIssue | null;
  timeDiscontinuity: SimulationTimeDiscontinuity | null;
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
  devSkipMinutes(minutes: number): DeveloperClockSkip;
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

function compositeWeekForDate(session: WeatherSession, at: string): CompositeWeekWeather | null {
  const dateKey = localWeatherDateKey(at, session.timezone);
  const startIndex = session.plan.hours.findIndex((hour) => {
    const local = weatherLocalParts(hour.at, session.timezone);
    return localWeatherDateKey(hour.at, session.timezone) === dateKey && local.hour === 0;
  });
  if (startIndex < 0) return null;
  const source = session.plan.hours.slice(startIndex, startIndex + 168)
    .map((hour) => resolveWeatherHour(hour, session.midpoint));
  if (source.length !== 168) return null;
  try {
    return createCompositeWeekWeather(source, { timezone: session.timezone });
  } catch {
    // DST weeks can contain 167 or 169 local records. Keep the existing
    // weather read model for that exceptional archive shape rather than
    // fabricating or dropping a source hour.
    return null;
  }
}

function physicsHoursCrossed(
  session: WeatherSession,
  before: SimulationClock,
  after: SimulationClock,
): ResolvedWeatherHour[] {
  if (before.season !== 'winter') return [];
  const end = after.season === 'winter' ? after.elapsedSimSecond : after.elapsedSimSecond;
  const start = Math.max(0, before.elapsedSimSecond);
  if (!(end > start)) return [];
  const firstWeek = Math.floor(start / SIMULATION_SECONDS_PER_WEEK);
  const lastWeek = Math.floor(Math.max(0, end - Number.EPSILON) / SIMULATION_SECONDS_PER_WEEK);
  const crossed: Array<{ due: number; hour: ResolvedWeatherHour }> = [];
  for (let weekIndex = firstWeek; weekIndex <= lastWeek; weekIndex += 1) {
    const weekStart = weekIndex * SIMULATION_SECONDS_PER_WEEK;
    const composite = compositeWeekForDate(session, before.calendarDate);
    // The source date advances with the synthetic calendar week. Re-resolve
    // from the session plan for later weeks instead of reusing week one.
    const weekDate = new Date(new Date(before.calendarDate).getTime() +
      (weekIndex - firstWeek) * 7 * 86_400_000).toISOString();
    const weekComposite = weekIndex === firstWeek ? composite : compositeWeekForDate(session, weekDate);
    if (!weekComposite) continue;
    const low = Math.max(start, weekStart);
    const high = Math.min(end, weekStart + SIMULATION_SECONDS_PER_WEEK);
    for (const step of weekComposite.physics) {
      const due = weekStart + step.dueSecond;
      if (due > low && due <= high) crossed.push({ due, hour: step.hour });
    }
  }
  crossed.sort((left, right) => left.due - right.due);
  return crossed.map((item) => item.hour);
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
  const [timeDiscontinuity, setTimeDiscontinuity] = useState<SimulationTimeDiscontinuity | null>(null);
  const busyRef = useRef(false);
  const coordinatorRef = useRef<ContinuousSimulationCoordinator | null>(null);
  const inFlightRef = useRef(false);
  const lastDispatchMsRef = useRef(0);
  const snowWorkerRef = useRef(new SnowStepClient());
  const binding = useMemo(() => terrain ? weatherTerrainBinding(terrain) : null, [terrain]);

  const publishClock = useCallback((next: SimulationClock, resetCoordinator = true) => {
    if (resetCoordinator && next.season === 'winter') {
      const committed = Math.max(0, Math.floor(next.elapsedSimSecond));
      if (coordinatorRef.current) coordinatorRef.current.reset(committed, performance.now());
      else coordinatorRef.current = new ContinuousSimulationCoordinator(committed,
        typeof next.speed === 'string' ? next.speed : 'normal');
    }
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

  const commitAdvance = useCallback(async (next: SimulationClock, refresh = true,
    expectedElapsedSecond?: number) => {
    if (!terrain || busyRef.current) return;
    if (expectedElapsedSecond != null && clockRef.current.elapsedSimSecond !== expectedElapsedSecond) return;
    busyRef.current = true;
    const startedAt = performance.now();
    const before = clockRef.current;
    try {
      const activeSession = sessionRef.current;
      const crossed = activeSession ? physicsHoursCrossed(activeSession, before, next) : [];
      if (crossed.length > 0 && snow.gridRef.current) {
        const result = await snowWorkerRef.current.run(binding ?? '', terrain, snow.gridRef.current, crossed);
        // Playback, skip, topology/load replacement, or another commit may
        // have changed the authoritative clock while snow work was in flight.
        // Never publish either half of a stale clock/snow transaction.
        if (clockRef.current !== before) return;
        snow.replace(result.grid, refresh && result.changedCells > 0);
      }
      if (clockRef.current !== before
        || (expectedElapsedSecond != null && before.elapsedSimSecond !== expectedElapsedSecond)) return;
      const run = runRef.current;
      if (run) runRef.current = { ...run, cursorHour: projectedHour(run, next.calendarDate) };
      publishClock(next, false);
      const coordinator = coordinatorRef.current;
      if (coordinator && next.season === 'winter') {
        const status = coordinator.acknowledge(Math.floor(next.elapsedSimSecond),
          performance.now() - startedAt, performance.now());
        if (status.throttled) {
          setMessage(`Catching up — effective speed ${status.effectiveSpeed}; no simulation time was dropped.`);
        }
      }
    } catch (error) {
      if (clockRef.current === before) {
        publishClock({ ...before, runState: 'paused' });
        setStatus('corrupt'); setMessage(error instanceof Error ? error.message : 'Snow simulation failed.');
      }
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
    const coordinator = coordinatorRef.current ?? new ContinuousSimulationCoordinator(
      Math.floor(clockRef.current.elapsedSimSecond),
      typeof clockRef.current.speed === 'string' ? clockRef.current.speed : 'normal');
    coordinatorRef.current = coordinator;
    let frame = 0;
    const onVisibilityChange = () => {
      // Reset on both edges. Browsers commonly suspend rAF while hidden, so
      // resetting only on hide would make the first visible frame catch up the
      // entire background interval.
      coordinator.advanceWall(performance.now(), true);
    };
    const tick = (now: number) => {
      if (document.hidden) {
        coordinator.advanceWall(now, true);
      } else {
        coordinator.advanceWall(now);
        const dispatchDue = now - lastDispatchMsRef.current >= 50;
        if (dispatchDue && !inFlightRef.current && !busyRef.current) {
          const target = coordinator.nextTarget();
          if (target != null) {
            const before = clockRef.current;
            const advance = advanceClockSeconds(before, target - before.elapsedSimSecond, configRef.current);
            if (advance.simulatedSecondsAdvanced > 0) {
              inFlightRef.current = true;
              lastDispatchMsRef.current = now;
              void commitAdvance(advance.clock, true, before.elapsedSimSecond)
                .finally(() => { inFlightRef.current = false; });
            }
          }
        }
      }
      frame = requestAnimationFrame(tick);
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    frame = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(frame);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [clock.runState, clock.season, commitAdvance]);

  const compositeWeek = useMemo(() => {
    if (!session || clock.season !== 'winter' || clock.winterWeek == null) return null;
    return compositeWeekForDate(session, clockRef.current.calendarDate);
  }, [session, clock.season, clock.winterWeek]);
  const current = useMemo(() => {
    if (!session) return null;
    if (compositeWeek && clock.season === 'winter') {
      const index = Math.min(compositeWeek.witness.operatingHours.length - 1,
        Math.floor(clock.weekSecond / 3_600));
      return compositeWeek.witness.operatingHours[Math.max(0, index)] ?? null;
    }
    return weatherAtSession(session, clock.calendarDate);
  }, [session, compositeWeek, clock.season, clock.weekSecond, clock.calendarDate]);
  const weeklyOutlook = compositeWeek?.outlook ?? null;
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

  const devSkipMinutes = useCallback((minutes: number): DeveloperClockSkip => {
    if (!isDeveloperConsoleEnabled()) throw new Error('Developer time skipping is disabled in this build.');
    if (busyRef.current) throw new Error('Wait for the current simulation update to finish.');
    const result = skipClockWithoutSimulation(clockRef.current, minutes, configRef.current);
    coordinatorRef.current?.reset(Math.floor(result.after.elapsedSimSecond), performance.now());
    lastDispatchMsRef.current = performance.now();
    const run = runRef.current;
    if (run) runRef.current = { ...run, cursorHour: projectedHour(run, result.after.calendarDate) };
    publishClock(result.after);
    setTimeDiscontinuity((previous) => Object.freeze({ revision: (previous?.revision ?? 0) + 1,
      absoluteGameMinute: result.after.absoluteGameMinute,
      localMidnightAbsoluteMinute: result.after.absoluteGameMinute - result.after.minuteOfDay }));
    return result;
  }, [publishClock]);

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
    status, message, clock, weatherPackage, session, current, weeklyOutlook, forecast, averageAnnualSnowfallCm, analysisOpen,
    timeDiscontinuity,
    togglePlayback: () => {
      const running = clockRef.current.runState === 'running';
      if (!running && clockRef.current.season !== 'winter') return;
      coordinatorRef.current?.reset(Math.floor(clockRef.current.elapsedSimSecond), performance.now());
      publishClock({ ...clockRef.current, runState: running ? 'paused' : 'running' });
    },
    setSpeed: (speed) => {
      coordinatorRef.current?.setSpeed(speed);
      publishClock({ ...clockRef.current, speed }, false);
    },
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
    pause: () => {
      coordinatorRef.current?.reset(Math.floor(clockRef.current.elapsedSimSecond), performance.now());
      publishClock({ ...clockRef.current, runState: 'paused' });
    },
    devSkipMinutes,
  };
}
