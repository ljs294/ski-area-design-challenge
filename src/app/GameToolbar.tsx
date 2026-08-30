import { useMemo } from 'react';
import type { TerrainRecord } from '../types/terrain';
import type { SavedWeatherRun } from '../types/gameSave';
import { WEATHER_LAB_SPEEDS, type WeatherLabSpeed } from '../weather/playback';
import { createTerrainThermalModel, temperatureFieldForHour } from '../weather/terrainThermal';
import type { Readout } from './CursorReadout';
import type { Units } from './SettingsContext';
import { useGameWeather } from './useGameWeather';

const M_TO_FT = 3.28084;

function format(value: number | undefined, digits = 1): string {
  return value == null || !Number.isFinite(value) ? '--' : value.toFixed(digits);
}

function formatGameTime(at: string | undefined, timezone: string | undefined): string {
  if (!at || !timezone) return '--';
  try {
    return new Intl.DateTimeFormat('en-US', {
      timeZone: timezone, month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', hour12: true,
    }).format(new Date(at));
  } catch {
    return new Date(at).toISOString();
  }
}

function range(values: Float32Array): { min: number; max: number } {
  let min = Infinity;
  let max = -Infinity;
  for (const value of values) {
    if (!Number.isFinite(value)) continue;
    min = Math.min(min, value);
    max = Math.max(max, value);
  }
  return { min: Number.isFinite(min) ? min : 0, max: Number.isFinite(max) ? max : 0 };
}

/** Cursor readout for the toolbar's right edge. */
function ToolbarReadout({ readout, units }: { readout: Readout | null; units: Units }) {
  const elev = !readout
    ? '--'
    : units === 'imperial'
      ? `${Math.round(readout.elevationM * M_TO_FT).toLocaleString()} ft`
      : `${Math.round(readout.elevationM).toLocaleString()} m`;

  let ctx: { label: string; value: string } | null = null;
  if (readout) {
    if (readout.overlay === 'slope') ctx = { label: 'Slope', value: `${Math.round(readout.slopeDeg)} deg` };
    else if (readout.overlay === 'aspect') ctx = { label: 'Aspect', value: readout.aspectCompass };
    else if (readout.overlay === 'groundcover') ctx = { label: 'Cover', value: readout.coverLabel ?? '--' };
  }

  return <div className="tb-readout" role="group" aria-label="Cursor terrain readout">
    <div className="tb-readout-cell"><span className="tb-readout-label">Elev</span><span className="tb-readout-value">{elev}</span></div>
    <div className="tb-readout-cell tb-readout-ctx"><span className="tb-readout-label">{ctx?.label ?? ''}</span><span className="tb-readout-value">{ctx?.value ?? ''}</span></div>
  </div>;
}

function GameWeatherOverlay({
  terrain,
  weather,
}: {
  terrain: TerrainRecord | null;
  weather: ReturnType<typeof useGameWeather>;
}) {
  const model = useMemo(() => terrain ? createTerrainThermalModel(terrain) : null, [terrain]);
  const current = weather.current;
  const temperatureRange = useMemo(() => {
    if (!model || !current) return null;
    return range(temperatureFieldForHour(model, current).temperatureC);
  }, [model, current]);
  if (!weather.analysisOpen) return null;
  const source = current?.provenance.fields?.airTemperatureC;
  return <aside className="game-weather-overlay screen-panel" aria-label="Weather analysis overlay">
    <div className="game-weather-overlay-head"><strong>Weather analysis</strong><button className="ghost-btn" onClick={weather.toggleAnalysis}>Close</button></div>
    {weather.status !== 'ready' || !current
      ? <p>{weather.message}</p>
      : <>
        <p>{formatGameTime(weather.playback?.cursor, weather.session?.timezone)}</p>
        <p>{format(current.temperatureC)} C air / {format(current.wetBulbC)} C wet-bulb / {format(current.humidityPct)}% RH</p>
        <p>Wind {format(current.windSpeedKph)} kph / {format(current.precipitationMm, 2)} mm/h {current.precipitationType}</p>
        <p>Solar {format(current.globalRadiationWm2)} global / {format(current.directRadiationWm2)} direct / {format(current.diffuseRadiationWm2)} diffuse W/m2</p>
        <p>Cloud transmission {format(current.cloudTransmissionPct)}% / sun elevation {format(current.solarElevationDeg)} deg</p>
        {temperatureRange && <p>Terrain temperature field: {format(temperatureRange.min)} to {format(temperatureRange.max)} C</p>}
        <p className="game-weather-source">{weather.weatherPackage?.manifest.quality === 'limited'
          ? `Limited/development package — ${weather.weatherPackage.manifest.sourceSummary}`
          : source
            ? `${source.provider} / ${source.quality}${source.correction !== 'none' ? ` / ${source.correction}` : ''}`
            : `${weather.weatherPackage?.manifest.quality ?? 'unknown'} / ${weather.weatherPackage?.manifest.sourceSummary ?? 'no source metadata'}`}</p>
        <h3>24-hour forecast</h3>
        {weather.forecast?.hours.slice(1, 4).map((hour) => <p key={hour.at}>{formatGameTime(hour.at, weather.session?.timezone)}: {format(hour.temperatureC)} C, {format(hour.precipitationMm, 2)} mm/h</p>)}
        {!weather.forecast?.hours.length && <p>No forecast available.</p>}
      </>}
  </aside>;
}

export function GameToolbar({
  resortName,
  onOpenStats,
  readout,
  units,
  terrain,
  weatherRun,
  onWeatherRunChange,
}: {
  resortName: string;
  onOpenStats: () => void;
  readout: Readout | null;
  units: Units;
  terrain: TerrainRecord | null;
  weatherRun: SavedWeatherRun | undefined;
  onWeatherRunChange(run: SavedWeatherRun): void;
}) {
  const weather = useGameWeather({ terrain, weatherRun, onWeatherRunChange });
  const playback = weather.playback;
  const current = weather.current;
  const ready = weather.status === 'ready' && !!playback;
  const playing = ready && playback.running;
  const cursorHour = ready && weather.session
    ? Math.max(0, Math.floor((new Date(playback.cursor).getTime() - new Date(weather.session.plan.startsAt).getTime()) / 3_600_000))
    : 0;
  const playTitle = weather.status === 'prepared'
    ? 'Start deterministic offline weather'
    : playing ? 'Pause weather clock' : 'Play weather clock';

  return <>
    <div className="game-toolbar">
      <div className="tb-group">
        <button
          className="tb-play"
          onClick={weather.status === 'prepared' ? weather.start : weather.togglePlayback}
          aria-pressed={playing}
          disabled={!ready && weather.status !== 'prepared'}
          title={playTitle}
        >{playing ? '||' : '>'}</button>
      </div>
      <div className="tb-group">
        <div className="tb-clock" title={weather.message}>
          <span className="tb-day">{ready ? `Weather day ${Math.floor(cursorHour / 24) + 1}` : weather.status === 'prepared' ? 'Weather prepared' : 'Weather unavailable'}</span>
          <span className="tb-time">{ready ? formatGameTime(playback.cursor, weather.session?.timezone) : weather.message}</span>
        </div>
      </div>
      <div className="tb-group">
        <div className="tb-speeds" role="group" aria-label="Weather simulation speed">
          {WEATHER_LAB_SPEEDS.map((speed) => <button
            key={speed}
            className={`tb-speed${playback?.speed === speed ? ' is-active' : ''}`}
            onClick={() => weather.setSpeed(speed as WeatherLabSpeed)}
            disabled={!ready}
            title={`${speed}x weather speed`}
          >{speed}x</button>)}
        </div>
      </div>
      <div className="tb-group tb-weather-group">
        <button className="tb-weather" onClick={weather.toggleAnalysis} title="Toggle weather analysis overlay">
          {current ? `${format(current.temperatureC)} C` : 'Weather'}
        </button>
      </div>
      <div className="tb-group">
        <div className="tb-money"><span className="tb-balance">$0</span><span className="tb-income">Finance not simulated</span></div>
      </div>
      <div className="tb-group">
        <button className="tb-resort" onClick={onOpenStats} title="Ski area details"><span className="hud-resort tb-resort-name">{resortName}</span><span className="tb-caret" aria-hidden="true">&gt;</span></button>
      </div>
      <div className="tb-group tb-group-right"><ToolbarReadout readout={readout} units={units} /></div>
    </div>
    <GameWeatherOverlay terrain={terrain} weather={weather} />
  </>;
}
