import { useEffect, useMemo, useRef, useState } from 'react';
import { listTerrains, loadTerrain } from '../terrainStorageClient';
import { downloadWeatherPackage } from '../weatherServiceClient';
import { loadWeatherPackage, saveWeatherPackage } from '../weatherStorageClient';
import { createTerrainThermalModel, temperatureFieldForHour } from '../weather/terrainThermal';
import {
  WEATHER_LAB_SPEEDS, advanceWeatherPlayback, createWeatherPlayback, historicalAt,
  skipWeatherPlayback, weatherAt, type WeatherLabSpeed,
} from '../weather/playback';
import { generateSyntheticWeather, type SyntheticWeatherPlan, type WeatherDataPackage } from '../weather/weatherModel';
import { weatherTerrainBinding } from '../weather/terrainBinding';
import type { TerrainRecord, TerrainSummary } from '../types';

function format(value: number | undefined, digits = 1): string {
  return value == null || !Number.isFinite(value) ? '—' : value.toFixed(digits);
}

function FieldCanvas({ values }: { values: Float32Array | null }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !values || !values.length) return;
    const side = Math.round(Math.sqrt(values.length));
    const context = canvas.getContext('2d');
    if (!context) return;
    const image = context.createImageData(side, side);
    const min = Math.min(...values), max = Math.max(...values), span = Math.max(0.1, max - min);
    for (let index = 0; index < values.length; index += 1) {
      const t = (values[index] - min) / span;
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
  return <canvas className="weather-lab-field" ref={canvasRef} width={600} height={420} aria-label="Terrain-adjusted temperature field" />;
}

export function WeatherLab({ onExit }: { onExit: () => void }) {
  const [summaries, setSummaries] = useState<TerrainSummary[]>([]);
  const [terrain, setTerrain] = useState<TerrainRecord | null>(null);
  const [weatherPackage, setWeatherPackage] = useState<WeatherDataPackage | null>(null);
  const [plan, setPlan] = useState<SyntheticWeatherPlan | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preparing, setPreparing] = useState(false);
  const [seed, setSeed] = useState('weather-lab');
  const [historicalYear, setHistoricalYear] = useState(1991);
  const [mapMode, setMapMode] = useState<'simulated' | 'historical' | 'difference'>('simulated');
  const [playback, setPlayback] = useState<ReturnType<typeof createWeatherPlayback> | null>(null);
  const lastFrame = useRef<number | null>(null);

  useEffect(() => { void listTerrains().then(setSummaries).catch((reason: unknown) => setError(String(reason))); }, []);
  useEffect(() => {
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape') onExit(); };
    window.addEventListener('keydown', close);
    return () => window.removeEventListener('keydown', close);
  }, [onExit]);
  useEffect(() => {
    if (!playback?.running || !plan) return;
    let frame = 0;
    const tick = (now: number) => {
      const previous = lastFrame.current ?? now;
      lastFrame.current = now;
      setPlayback((current) => current ? advanceWeatherPlayback(plan, current, now - previous) : current);
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => { cancelAnimationFrame(frame); lastFrame.current = null; };
  }, [plan, playback?.running]);

  const current = useMemo(() => plan && playback ? weatherAt(plan, playback.cursor) : null, [plan, playback]);
  const historical = useMemo(() => weatherPackage && playback ? historicalAt(weatherPackage, playback.historicalYear, playback.cursor) : null, [weatherPackage, playback]);
  const thermalModel = useMemo(() => terrain ? createTerrainThermalModel(terrain) : null, [terrain]);
  const temperature = useMemo(() => {
    if (!thermalModel || !current) return null;
    const simulated = temperatureFieldForHour(thermalModel, current).temperatureC;
    if (mapMode === 'simulated' || !historical) return simulated;
    const observed = temperatureFieldForHour(thermalModel, historical).temperatureC;
    if (mapMode === 'historical') return observed;
    return Float32Array.from(simulated, (value, index) => value - observed[index]);
  }, [current, historical, mapMode, thermalModel]);

  const selectTerrain = async (key: string) => {
    setError(null); setPlan(null); setPlayback(null); setWeatherPackage(null);
    const next = await loadTerrain(key);
    if (!next) { setError('The selected terrain package is unavailable.'); return; }
    setTerrain(next);
    const cached = await loadWeatherPackage(key);
    if (cached && cached.manifest.terrainBinding === weatherTerrainBinding(next)) {
      setWeatherPackage(cached);
      const first = cached.historicalYears[0]?.year ?? 1991;
      setHistoricalYear(first);
    } else if (cached) setError('The cached weather package belongs to a different map revision and must be downloaded again.');
  };

  const prepare = async () => {
    if (!terrain) return;
    setPreparing(true); setError(null);
    try {
      const downloaded = await downloadWeatherPackage(terrain);
      await saveWeatherPackage(downloaded);
      setWeatherPackage(downloaded);
      setHistoricalYear(downloaded.historicalYears[0]?.year ?? 1991);
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setPreparing(false); }
  };

  const startPlan = () => {
    if (!weatherPackage) return;
    try {
      const next = generateSyntheticWeather(weatherPackage, '2026-01-01T00:00:00.000Z', seed);
      setPlan(next); setPlayback(createWeatherPlayback(next, historicalYear)); setError(null);
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
  };

  const changeYear = (year: number) => {
    setHistoricalYear(year);
    setPlayback((currentPlayback) => currentPlayback ? { ...currentPlayback, historicalYear: year } : currentPlayback);
  };

  return <main className="weather-lab screen-view">
    <header className="weather-lab-bar">
      <div><strong>Weather Lab</strong><span>Offline historical simulation</span></div>
      <button className="ghost-btn" onClick={onExit}>Close (Esc)</button>
    </header>
    <section className="weather-lab-controls screen-panel">
      <label>Terrain map<select value={terrain?.key ?? ''} onChange={(event) => void selectTerrain(event.target.value)}>
        <option value="">Choose a prepared map</option>
        {summaries.map((summary) => <option key={summary.key} value={summary.key}>{summary.mountainName}</option>)}
      </select></label>
      {terrain && !weatherPackage && <button onClick={() => void prepare()} disabled={preparing}>{preparing ? 'Downloading offline weather…' : 'Prepare offline weather'}</button>}
      {weatherPackage && <><label>Seed<input value={seed} onChange={(event) => setSeed(event.target.value)} /></label>
        <label>Historical year<select value={historicalYear} onChange={(event) => changeYear(Number(event.target.value))}>
          {weatherPackage.historicalYears.map((year) => <option key={year.year} value={year.year}>{year.year}</option>)}
        </select></label><button onClick={startPlan}>Generate season</button></>}
    </section>
    {error && <p className="weather-lab-error" role="alert">{error}</p>}
    {weatherPackage && plan && playback && <>
      <section className="weather-lab-controls screen-panel" aria-label="Weather playback controls">
        <button onClick={() => setPlayback({ ...playback, running: !playback.running })}>{playback.running ? 'Pause' : 'Play'}</button>
        {WEATHER_LAB_SPEEDS.map((speed) => <button key={speed} className={playback.speed === speed ? 'seg-btn seg-btn-active' : 'seg-btn'} onClick={() => setPlayback({ ...playback, speed: speed as WeatherLabSpeed })}>{speed}×</button>)}
        <button onClick={() => setPlayback(skipWeatherPlayback(plan, playback, 'hour'))}>Step hour</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(plan, playback, 'day'))}>Skip day</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(plan, playback, 'week'))}>Skip week</button>
        <button onClick={() => setPlayback(skipWeatherPlayback(plan, playback, 'month'))}>Skip month</button>
      </section>
      <section className="weather-lab-grid">
        <div className="screen-panel"><h2>Simulated</h2><p>{new Date(playback.cursor).toUTCString()}</p><p>{format(current?.temperatureC)} °C · {format(current?.precipitationMm, 2)} mm/h</p><p>Wind {format(current?.windSpeedKph)} kph · {current?.precipitationType ?? '—'}</p></div>
        <div className="screen-panel"><h2>Historical {playback.historicalYear}</h2><p>{format(historical?.temperatureC)} °C · {format(historical?.precipitationMm, 2)} mm/h</p><p>Δ temperature {current && historical ? format(current.temperatureC - historical.temperatureC) : '—'} °C</p><p>{weatherPackage.manifest.quality} · {weatherPackage.manifest.sourceSummary}</p></div>
        <div className="screen-panel"><h2>Events</h2>{plan.events.filter((event) => event.startsAt <= playback.cursor && event.endsAt >= playback.cursor).map((event) => <p key={event.id}>{event.type}{event.stormStyle ? ` · ${event.stormStyle}` : ''}</p>)}{!plan.events.some((event) => event.startsAt <= playback.cursor && event.endsAt >= playback.cursor) && <p>No active event</p>}</div>
      </section>
      <section className="weather-lab-map screen-panel"><h2>Terrain-adjusted temperature</h2>
        <div className="segmented" aria-label="Temperature map mode">
          {(['simulated', 'historical', 'difference'] as const).map((mode) => <button key={mode} className={mapMode === mode ? 'seg-btn seg-btn-active' : 'seg-btn'} onClick={() => setMapMode(mode)}>{mode}</button>)}
        </div><FieldCanvas values={temperature} /></section>
    </>}
  </main>;
}
