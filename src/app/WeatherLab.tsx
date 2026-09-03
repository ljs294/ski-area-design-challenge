import { useEffect, useMemo, useRef, useState } from 'react';
import { listTerrains, loadTerrain } from '../terrainStorageClient';
import {
  cancelWeatherPackageJob,
  prepareWeatherPackage,
  WeatherServiceError,
  type WeatherBuildJob,
} from '../weatherServiceClient';
import { loadWeatherPackage, saveWeatherPackage } from '../weatherStorageClient';
import { createTerrainThermalModel, temperatureFieldForHour } from '../weather/terrainThermal';
import {
  WEATHER_LAB_SPEEDS,
  advanceWeatherPlayback,
  createWeatherPlayback,
  skipWeatherPlayback,
  type WeatherLabSpeed,
} from '../weather/playback';
import {
  forecastForSession,
  historicalAtSession,
  loadWeatherSession,
  weatherAtSession,
  type WeatherSession,
} from '../weather/weatherSession';
import { weatherInstantForLocal } from '../weather/localTime';
import type { ResolvedWeatherHour, WeatherDataPackage, WeatherFieldProvenance } from '../weather/weatherModel';
import { weatherTerrainBinding } from '../weather/terrainBinding';
import type { TerrainRecord, TerrainSummary } from '../types';
import { useSettings, type Units } from './SettingsContext';
import { formatLiquidPrecipitationRate, formatSnowfall, formatTemperature, formatTemperatureDelta,
  formatVelocity, formatWindSpeed } from './unitFormat';

export interface WeatherLabInitialState {
  terrain: TerrainRecord;
  weatherPackage: WeatherDataPackage;
  session?: WeatherSession;
  cursor?: string;
}

function format(value: number | undefined, digits = 1): string {
  return value == null || !Number.isFinite(value) ? '--' : value.toFixed(digits);
}

function localTime(at: string | undefined, timezone: string): string {
  if (!at) return '--';
  try {
    return new Intl.DateTimeFormat('en-US', {
      timeZone: timezone,
      weekday: 'short', month: 'short', day: 'numeric', year: 'numeric',
      hour: 'numeric', minute: '2-digit', hour12: true,
    }).format(new Date(at));
  } catch {
    return new Date(at).toISOString();
  }
}

function historicalYearsOf(weatherPackage: WeatherDataPackage, session: WeatherSession | null = null): number[] {
  const years = session?.historicalYears?.map((year) => year.year)
    ?? weatherPackage.historicalYears?.map((year) => year.year)
    ?? weatherPackage.manifest.chunks?.map((chunk) => chunk.year)
    ?? [];
  return [...new Set(years)].sort((left, right) => left - right);
}

function firstHistoricalYear(weatherPackage: WeatherDataPackage): number {
  return historicalYearsOf(weatherPackage)[0] ?? weatherPackage.manifest.historicalStartYear;
}

function valueRange(values: Float32Array): { min: number; max: number } {
  let min = Infinity;
  let max = -Infinity;
  for (const value of values) {
    if (!Number.isFinite(value)) continue;
    min = Math.min(min, value);
    max = Math.max(max, value);
  }
  return Number.isFinite(min) && Number.isFinite(max) ? { min, max } : { min: 0, max: 1 };
}

function FieldCanvas({
  values,
  onSample,
}: {
  values: Float32Array | null;
  onSample: ((sample: { u: number; v: number; temperatureC: number }) => void) | null;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !values || !values.length) return;
    const side = Math.round(Math.sqrt(values.length));
    const context = canvas.getContext('2d');
    if (!context) return;
    const image = context.createImageData(side, side);
    const { min, max } = valueRange(values);
    const span = Math.max(0.1, max - min);
    for (let index = 0; index < values.length; index += 1) {
      const t = Math.max(0, Math.min(1, (values[index] - min) / span));
      image.data[index * 4] = Math.round(30 + 220 * t);
      image.data[index * 4 + 1] = Math.round(90 + 110 * (1 - Math.abs(t - 0.5) * 2));
      image.data[index * 4 + 2] = Math.round(240 - 190 * t);
      image.data[index * 4 + 3] = 255;
    }
    const scratch = document.createElement('canvas');
    scratch.width = scratch.height = side;
    scratch.getContext('2d')?.putImageData(image, 0, 0);
    context.imageSmoothingEnabled = true;
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.drawImage(scratch, 0, 0, canvas.width, canvas.height);
  }, [values]);

  return <canvas
    className="weather-lab-field"
    ref={canvasRef}
    width={600}
    height={420}
    aria-label="Terrain-adjusted temperature raster; move the pointer to sample conditions"
    onPointerMove={(event) => {
      if (!values || !onSample) return;
      const bounds = event.currentTarget.getBoundingClientRect();
      const u = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width));
      const v = Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height));
      const side = Math.round(Math.sqrt(values.length));
      const column = Math.min(side - 1, Math.floor(u * side));
      const row = Math.min(side - 1, Math.floor(v * side));
      onSample({ u, v, temperatureC: values[row * side + column] });
    }}
  />;
}

function SourceBadges({ hour, fallback }: { hour: ResolvedWeatherHour | null; fallback: WeatherDataPackage['manifest'] }) {
  const fields = hour?.provenance.fields;
  const entries: Array<{ label: string; provenance: WeatherFieldProvenance }> = [];
  if (fields?.airTemperatureC) entries.push({ label: 'Temperature', provenance: fields.airTemperatureC });
  if (fields?.windUms) entries.push({ label: 'Wind', provenance: fields.windUms });
  if (fields?.cloudCoverPct) entries.push({ label: 'Cloud', provenance: fields.cloudCoverPct });
  if (fields?.globalHorizontalIrradianceWm2) entries.push({ label: 'Radiation', provenance: fields.globalHorizontalIrradianceWm2 });
  return <div className="weather-source-badges" aria-label="Weather source quality">
    {entries.length > 0
      ? entries.map(({ label, provenance }) => <span key={label} title={provenance.sourceVersion}>
        {label}: {provenance.provider} / {provenance.quality}{provenance.correction !== 'none' ? ` / ${provenance.correction}` : ''}
      </span>)
      : <span>{fallback.quality} / {fallback.sourceSummary}</span>}
  </div>;
}

function ConditionRows({ hour, units }: { hour: ResolvedWeatherHour | null; units: Units }) {
  return <>
    <p>{formatTemperature(hour?.temperatureC, units)} air / {formatTemperature(hour?.wetBulbC, units)} wet-bulb / {format(hour?.humidityPct)}% RH</p>
    <p>{formatLiquidPrecipitationRate(hour?.precipitationMm, units)} {hour?.precipitationType ?? '--'} / {formatSnowfall(hour?.snowfallCm, units, 2)} snow</p>
    <p>Wind {formatWindSpeed(hour?.windSpeedKph, units)}, U {formatVelocity(hour?.windUms, units)} / V {formatVelocity(hour?.windVms, units)}</p>
  </>;
}

export function WeatherLab({
  onExit,
  initialState,
}: {
  onExit: () => void;
  initialState?: WeatherLabInitialState;
}) {
  const { settings: { units } } = useSettings();
  const [summaries, setSummaries] = useState<TerrainSummary[]>([]);
  const [terrain, setTerrain] = useState<TerrainRecord | null>(initialState?.terrain ?? null);
  const [weatherPackage, setWeatherPackage] = useState<WeatherDataPackage | null>(initialState?.weatherPackage ?? null);
  const [session, setSession] = useState<WeatherSession | null>(initialState?.session ?? null);
  const [error, setError] = useState<string | null>(null);
  const [preparing, setPreparing] = useState(false);
  const [starting, setStarting] = useState(false);
  const [buildJob, setBuildJob] = useState<WeatherBuildJob | null>(null);
  const [seed, setSeed] = useState('weather-lab');
  const [historicalYear, setHistoricalYear] = useState(() => initialState ? firstHistoricalYear(initialState.weatherPackage) : 2001);
  const [mapMode, setMapMode] = useState<'simulated' | 'historical' | 'difference'>('simulated');
  const [playback, setPlayback] = useState<ReturnType<typeof createWeatherPlayback> | null>(() => {
    if (!initialState?.session || !initialState.cursor) return null;
    return {
      ...createWeatherPlayback(initialState.session.plan, firstHistoricalYear(initialState.weatherPackage)),
      cursor: initialState.cursor,
    };
  });
  const [cursorSample, setCursorSample] = useState<{ longitude: number; latitude: number; temperatureC: number } | null>(null);
  const lastFrame = useRef<number | null>(null);
  const prepareAbortRef = useRef<AbortController | null>(null);
  const buildJobRef = useRef<WeatherBuildJob | null>(null);

  useEffect(() => { void listTerrains().then(setSummaries).catch((reason: unknown) => setError(String(reason))); }, []);
  useEffect(() => {
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape') onExit(); };
    window.addEventListener('keydown', close);
    return () => window.removeEventListener('keydown', close);
  }, [onExit]);
  useEffect(() => { buildJobRef.current = buildJob; }, [buildJob]);
  useEffect(() => () => {
    prepareAbortRef.current?.abort();
    if (buildJobRef.current?.id) void cancelWeatherPackageJob(buildJobRef.current.id).catch(() => {});
  }, []);
  useEffect(() => {
    if (!playback?.running || !session) return;
    let frame = 0;
    const tick = (now: number) => {
      const previous = lastFrame.current ?? now;
      lastFrame.current = now;
      setPlayback((current) => current ? advanceWeatherPlayback(session.plan, current, now - previous) : current);
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => { cancelAnimationFrame(frame); lastFrame.current = null; };
  }, [session, playback?.running]);

  const current = useMemo(() => session && playback ? weatherAtSession(session, playback.cursor) : null, [session, playback]);
  const historical = useMemo(() => session && playback ? historicalAtSession(session, playback.historicalYear, playback.cursor) : null, [session, playback]);
  const forecast = useMemo(() => session && playback ? forecastForSession(session, playback.cursor, 24) : null, [session, playback]);
  const thermalModel = useMemo(() => terrain ? createTerrainThermalModel(terrain) : null, [terrain]);
  const temperature = useMemo(() => {
    if (!thermalModel || !current) return null;
    const simulated = temperatureFieldForHour(thermalModel, current).temperatureC;
    if (mapMode === 'simulated' || !historical) return simulated;
    const observed = temperatureFieldForHour(thermalModel, historical).temperatureC;
    if (mapMode === 'historical') return observed;
    return Float32Array.from(simulated, (value, index) => value - observed[index]);
  }, [current, historical, mapMode, thermalModel]);
  const years = useMemo(() => weatherPackage ? historicalYearsOf(weatherPackage, session) : [], [weatherPackage, session]);

  const selectTerrain = async (key: string) => {
    prepareAbortRef.current?.abort();
    setError(null); setSession(null); setPlayback(null); setWeatherPackage(null); setBuildJob(null); setCursorSample(null);
    const next = await loadTerrain(key);
    if (!next) { setError('The selected terrain package is unavailable.'); return; }
    setTerrain(next);
    const cached = await loadWeatherPackage(key);
    if (cached && cached.manifest.terrainBinding === weatherTerrainBinding(next)) {
      setWeatherPackage(cached);
      setHistoricalYear(firstHistoricalYear(cached));
    } else if (cached) {
      setError('The cached weather package belongs to a different map revision and must be prepared again.');
    }
  };

  const prepare = async () => {
    if (!terrain) return;
    prepareAbortRef.current?.abort();
    const controller = new AbortController();
    prepareAbortRef.current = controller;
    setPreparing(true); setError(null); setBuildJob(null);
    try {
      const downloaded = await prepareWeatherPackage(terrain, { signal: controller.signal, onProgress: setBuildJob });
      await saveWeatherPackage(downloaded);
      setWeatherPackage(downloaded);
      setHistoricalYear(firstHistoricalYear(downloaded));
    } catch (reason) {
      if (reason instanceof DOMException && reason.name === 'AbortError') return;
      const message = reason instanceof WeatherServiceError
        ? `${reason.code}: ${reason.message}${reason.retryable ? ' You can retry preparation.' : ''}`
        : reason instanceof Error ? reason.message : String(reason);
      setError(message);
    } finally {
      if (prepareAbortRef.current === controller) prepareAbortRef.current = null;
      setPreparing(false);
    }
  };

  const cancelPreparation = () => {
    const id = buildJob?.id;
    prepareAbortRef.current?.abort();
    if (id) void cancelWeatherPackageJob(id).catch(() => {});
  };

  const startSession = async () => {
    if (!weatherPackage || !terrain) return;
    setStarting(true); setError(null);
    try {
      const startsAt = weatherInstantForLocal({ year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0 }, weatherPackage.manifest.timezone);
      const next = await loadWeatherSession(weatherPackage, {
        seed,
        startsAt,
        latitude: terrain.latitude,
        longitude: terrain.longitude,
      });
      const sessionYears = historicalYearsOf(weatherPackage, next);
      const selectedYear = sessionYears.includes(historicalYear) ? historicalYear : sessionYears[0] ?? historicalYear;
      setHistoricalYear(selectedYear);
      setSession(next);
      setPlayback(createWeatherPlayback(next.plan, selectedYear));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setStarting(false);
    }
  };

  const changeYear = (year: number) => {
    setHistoricalYear(year);
    setPlayback((state) => state ? { ...state, historicalYear: year } : state);
  };

  const historyLabel = weatherPackage?.manifest.quality === 'limited'
    ? 'Development fixture hourly archive (not observed weather).'
    : 'Daymet-constrained daily totals with MERRA-2 hourly atmospheric timing.';

  return <main className={`weather-lab screen-view${initialState ? ' weather-lab-current-game' : ''}`}>
    <header className="weather-lab-bar">
      <div><strong>Weather Lab</strong><span>{initialState?.session
        ? `Current game weather at ${localTime(playback?.cursor, initialState.session.timezone)}`
        : initialState
          ? 'Current game historical package loaded; start a simulation when ready.'
        : 'Offline historical comparison and deterministic simulation'}</span></div>
      <button className="ghost-btn" onClick={onExit}>Close (Esc)</button>
    </header>
    <section className="weather-lab-controls screen-panel">
      <label>Terrain map<select value={terrain?.key ?? ''} onChange={(event) => void selectTerrain(event.target.value)}>
        <option value="">Choose a terrain map</option>
        {summaries.map((summary) => <option key={summary.key} value={summary.key}>{summary.mountainName}</option>)}
      </select></label>
      {terrain && !weatherPackage && <button onClick={() => void prepare()} disabled={preparing}>
        {preparing ? 'Preparing offline weather...' : 'Prepare offline weather'}
      </button>}
      {preparing && <><button className="ghost-btn" onClick={cancelPreparation}>Cancel preparation</button>
        <span className="weather-lab-progress" role="status">{buildJob?.progress.stage ?? 'queued'}: {buildJob?.progress.message ?? 'Waiting for the package builder'}
          {buildJob?.progress.total ? ` (${buildJob.progress.completed}/${buildJob.progress.total})` : ''}</span></>}
      {weatherPackage && <><label>Seed<input value={seed} onChange={(event) => setSeed(event.target.value)} /></label>
        <label>Historical year<select value={historicalYear} onChange={(event) => changeYear(Number(event.target.value))}>
          {years.map((year) => <option key={year} value={year}>{year}</option>)}
        </select></label><button onClick={() => void startSession()} disabled={starting}>{starting ? 'Loading offline chunks...' : session ? 'Restart simulation' : 'Start simulation'}</button></>}
    </section>
    {error && <p className="weather-lab-error" role="alert">{error}</p>}
    {weatherPackage && <section className="weather-lab-provenance screen-panel">
      <strong>{weatherPackage.manifest.quality} package</strong><span>{historyLabel}</span><span>{weatherPackage.manifest.sourceSummary}</span>
    </section>}
    {weatherPackage && session && playback && <>
      <section className="weather-lab-controls screen-panel" aria-label="Weather playback controls">
        <button onClick={() => setPlayback({ ...playback, running: !playback.running })}>{playback.running ? 'Pause' : 'Play'}</button>
        {WEATHER_LAB_SPEEDS.map((speed) => <button key={speed} className={playback.speed === speed ? 'seg-btn seg-btn-active' : 'seg-btn'} onClick={() => setPlayback({ ...playback, speed: speed as WeatherLabSpeed })}>{speed}x</button>)}
        <button onClick={() => setPlayback(skipWeatherPlayback(session.plan, playback, 'hour'))}>Step hour</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(session.plan, playback, 'day'))}>Skip day</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(session.plan, playback, 'week'))}>Skip week</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(session.plan, playback, 'month'))}>Skip month</button>
      </section>
      <section className="weather-lab-grid">
        <div className="screen-panel"><h2>Simulated</h2><p>{localTime(playback.cursor, session.timezone)}</p><ConditionRows hour={current} units={units} /></div>
        <div className="screen-panel"><h2>Historical {playback.historicalYear}</h2><p>{historyLabel}</p><ConditionRows hour={historical} units={units} />
          <p>Delta: {current && historical ? `${formatTemperatureDelta(current.temperatureC - historical.temperatureC, units)} / ${formatLiquidPrecipitationRate(current.precipitationMm - historical.precipitationMm, units)}` : '--'}</p></div>
        <div className="screen-panel"><h2>Radiation and sky</h2><p>Global {format(current?.globalRadiationWm2)} W/m2 / direct {format(current?.directRadiationWm2)} / diffuse {format(current?.diffuseRadiationWm2)}</p>
          <p>Cloud {format(current?.cloudCoverPct)}%, transmission {format(current?.cloudTransmissionPct)}%, sun elevation {format(current?.solarElevationDeg)} deg</p>
          <SourceBadges hour={current} fallback={weatherPackage.manifest} /></div>
      </section>
      <section className="weather-lab-grid">
        <div className="screen-panel"><h2>Current events</h2>{session.plan.events.filter((event) => event.startsAt <= playback.cursor && event.endsAt >= playback.cursor).map((event) => <p key={event.id}>{event.type}{event.stormStyle ? ` / ${event.stormStyle}` : ''} / {event.severity}</p>)}
          {!session.plan.events.some((event) => event.startsAt <= playback.cursor && event.endsAt >= playback.cursor) && <p>No active event</p>}</div>
        <div className="screen-panel"><h2>24-hour forecast</h2><p>{forecast?.hours.length ?? 0} hourly values through {localTime(forecast?.endsAt, session.timezone)}</p>
          {forecast?.events.slice(0, 3).map((event) => <p key={event.id}>{event.type}{event.stormStyle ? ` / ${event.stormStyle}` : ''}: {localTime(event.startsAt, session.timezone)}</p>)}
          {!forecast?.events.length && <p>No forecast event</p>}</div>
        <div className="screen-panel"><h2>Package</h2><p>{weatherPackage.manifest.contentHash.slice(0, 12)} / {weatherPackage.manifest.timezone}</p><p>{years.length} reference years / {weatherPackage.manifest.chunks?.length ?? 0} immutable chunks</p></div>
      </section>
      <section className="weather-lab-map screen-panel"><h2>Terrain-adjusted temperature raster</h2>
        <div className="segmented" aria-label="Temperature map mode">
          {(['simulated', 'historical', 'difference'] as const).map((mode) => <button key={mode} className={mapMode === mode ? 'seg-btn seg-btn-active' : 'seg-btn'} onClick={() => setMapMode(mode)}>{mode}</button>)}
        </div>
        {cursorSample && <p className="weather-lab-cursor">Cursor: {formatTemperature(cursorSample.temperatureC, units)} at {format(cursorSample.latitude, 4)}, {format(cursorSample.longitude, 4)}</p>}
        <FieldCanvas values={temperature} onSample={thermalModel ? (sample) => setCursorSample({
          temperatureC: sample.temperatureC,
          longitude: thermalModel.bounds.west + sample.u * (thermalModel.bounds.east - thermalModel.bounds.west),
          latitude: thermalModel.bounds.north - sample.v * (thermalModel.bounds.north - thermalModel.bounds.south),
        }) : null} />
      </section>
    </>}
  </main>;
}
