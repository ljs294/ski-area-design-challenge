import { useMemo, useState } from 'react';
import type { TerrainRecord } from '../types/terrain';
import type { SimulationSpeed } from '../types/simulation';
import { createTerrainThermalModel, temperatureFieldForHour } from '../weather/terrainThermal';
import type { Readout } from './CursorReadout';
import type { Units } from './SettingsContext';
import type { GameSimulationController } from './useGameSimulation';

const SIMULATION_SPEEDS: readonly SimulationSpeed[] = [1, 2, 4, 8];

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
  const localWeather = readout?.temperatureC == null ? '' :
    `${readout.temperatureC.toFixed(1)} C / ${readout.precipitationType ?? 'none'}`;

  return <div className="tb-readout" role="group" aria-label="Cursor terrain readout">
    <div className="tb-readout-cell"><span className="tb-readout-label">Elev</span><span className="tb-readout-value">{elev}</span></div>
    <div className="tb-readout-cell tb-readout-ctx"><span className="tb-readout-label">{ctx?.label ?? ''}</span><span className="tb-readout-value">{ctx?.value ?? ''}</span></div>
    <div className="tb-readout-cell"><span className="tb-readout-label">Local weather</span><span className="tb-readout-value">{localWeather}</span></div>
  </div>;
}

function GameWeatherOverlay({
  terrain,
  weather,
}: {
  terrain: TerrainRecord | null;
  weather: GameSimulationController;
}) {
  const [selectedDate, setSelectedDate] = useState<string | null>(null);
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
      ? <><p>{weather.message}</p>{(weather.status === 'design-only' ||
        weather.status === 'package-unavailable' || weather.status === 'corrupt') &&
        <button className="ghost-btn" onClick={weather.prepareWeather}>Retry / Prepare Weather</button>}</>
      : <>
        <p>{formatGameTime(weather.clock.calendarDate, weather.session?.timezone)}</p>
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
        <h3>Seven-day forecast</h3>
        <div className="game-forecast-days">{weather.forecast?.days.map((day) => <button
          key={day.date} className={selectedDate === day.date ? 'is-active' : ''}
          onClick={() => setSelectedDate(selectedDate === day.date ? null : day.date)}>
          <strong>{day.date.slice(5)}</strong><span>{day.condition}</span>
          <span>{format(day.highC)} / {format(day.lowC)} C</span>
          <span>{format(day.precipitationMm)} mm / {format(day.snowfallCm)} cm snow</span>
          <span>{format(day.windSpeedKph)} / {format(day.windGustKph)} kph</span>
          <span>{day.confidencePct}% confidence</span>
        </button>)}</div>
        {weather.forecast?.days.find((day) => day.date === selectedDate)?.hours.map((hour) =>
          <p key={hour.at}>{formatGameTime(hour.at, weather.session?.timezone)}: {format(hour.temperatureC)} C,
            {' '}{format(hour.precipitationMm, 2)} mm/h {hour.precipitationType}, wind {format(hour.windSpeedKph)} kph</p>)}
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
  simulation,
}: {
  resortName: string;
  onOpenStats: () => void;
  readout: Readout | null;
  units: Units;
  terrain: TerrainRecord | null;
  simulation: GameSimulationController;
}) {
  const [planningConfirmationOpen, setPlanningConfirmationOpen] = useState(false);
  const weather = simulation;
  const current = weather.current;
  const ready = weather.status === 'ready';
  const playing = ready && weather.clock.runState === 'running';
  const planning = weather.clock.season === 'summer';
  const playTitle = planning ? 'Complete planning and skip to September 1' : playing ? 'Pause game clock' : 'Play game clock';

  return <>
    <div className="game-toolbar">
      <div className="tb-group">
        <button
          className="tb-play"
          onClick={planning ? () => setPlanningConfirmationOpen(true) : weather.togglePlayback}
          aria-pressed={playing}
          disabled={weather.status === 'loading' || weather.status === 'working'}
          title={playTitle}
        >{playing ? '||' : '>'}</button>
      </div>
      <div className="tb-group">
        <div className="tb-clock" title={weather.message}>
          <span className="tb-day">{planning ? `Summer planning ${weather.clock.summerPeriod ?? 1}` : `Winter week ${weather.clock.winterWeek ?? 1}`}</span>
          <span className="tb-time">{formatGameTime(weather.clock.calendarDate, weather.clock.timezone)}</span>
        </div>
      </div>
      <div className="tb-group">
        <div className="tb-speeds" role="group" aria-label="Weather simulation speed">
          {SIMULATION_SPEEDS.map((speed) => <button
            key={speed}
            className={`tb-speed${weather.clock.speed === speed ? ' is-active' : ''}`}
            onClick={() => weather.setSpeed(speed)}
            disabled={!ready || planning}
            title={`${speed}x simulation speed`}
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
    {planningConfirmationOpen && <div className="game-time-confirm-backdrop" role="presentation">
      <section className="game-time-confirm" role="dialog" aria-modal="true" aria-labelledby="game-time-confirm-title">
        <h2 id="game-time-confirm-title">Finish summer planning?</h2>
        <p>This skips once to September 1, fixes the coming weather year, and enables the running game clock.</p>
        <div className="game-time-confirm-actions"><button className="ghost-btn" onClick={() => setPlanningConfirmationOpen(false)}>Keep planning</button>
          <button className="primary-btn" onClick={() => { setPlanningConfirmationOpen(false); void weather.advancePlanningPeriod(); }}>Skip to September 1</button></div>
      </section>
    </div>}
  </>;
}
