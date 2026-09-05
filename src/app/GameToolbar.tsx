import { Dialog } from './ui';
import { lazy, Suspense, useMemo, useState } from 'react';
import type { TerrainRecord } from '../types/terrain';
import type { SimulationSpeed } from '../types/simulation';
import { createTerrainThermalModel, temperatureFieldForHour } from '../weather/terrainThermal';
import type { Readout } from './CursorReadout';
import type { Units } from './SettingsContext';
import type { GameSimulationController } from './useGameSimulation';
import { formatElevation, formatLiquidPrecipitation, formatLiquidPrecipitationRate, formatSnowfall,
  formatTemperature, formatWindSpeed } from './unitFormat';

const CurrentGameWeatherLab = lazy(() => import('./WeatherLab').then((module) => ({ default: module.WeatherLab })));

const SIMULATION_SPEEDS: readonly SimulationSpeed[] = ['slow', 'normal', 'fast', 'ultrafast'];

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

function formatForecastDay(date: string): string {
  const parsed = new Date(`${date}T12:00:00.000Z`);
  if (!Number.isFinite(parsed.getTime())) return date;
  return new Intl.DateTimeFormat('en-US', {
    weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC',
  }).format(parsed);
}

function formatForecastHour(at: string, timezone: string | undefined): string {
  try {
    return new Intl.DateTimeFormat('en-US', {
      timeZone: timezone, hour: 'numeric', hour12: true,
    }).format(new Date(at)).replace(' ', '').toLowerCase();
  } catch {
    return new Date(at).getUTCHours().toString().padStart(2, '0');
  }
}

function titleCase(value: string): string {
  return value.replace(/[-_]/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
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
  const elev = formatElevation(readout?.elevationM, units);

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

export function GameWeatherOverlay({
  terrain,
  weather,
  units,
}: {
  terrain: TerrainRecord | null;
  weather: GameSimulationController;
  units: Units;
}) {
  const [selectedDate, setSelectedDate] = useState<string | null>(null);
  const [labOpen, setLabOpen] = useState(false);
  const model = useMemo(() => terrain ? createTerrainThermalModel(terrain) : null, [terrain]);
  const current = weather.current;
  const temperatureRange = useMemo(() => {
    if (!model || !current) return null;
    return range(temperatureFieldForHour(model, current).temperatureC);
  }, [model, current]);
  const forecastDays = weather.forecast?.days ?? [];
  const activeDate = forecastDays.some((day) => day.date === selectedDate)
    ? selectedDate
    : forecastDays[0]?.date ?? null;
  const activeDay = forecastDays.find((day) => day.date === activeDate);
  if (!weather.analysisOpen) return null;
  const source = current?.provenance.fields?.airTemperatureC;
  const labState = terrain && weather.weatherPackage ? {
    terrain,
    weatherPackage: weather.weatherPackage,
    ...(weather.session ? { session: weather.session, cursor: weather.clock.calendarDate } : {}),
  } : null;
  return <><aside className="game-weather-overlay screen-panel" aria-label="Weather analysis overlay">
    <div className="game-weather-overlay-head"><strong>Weather analysis</strong><div className="game-weather-overlay-actions">
      <button className="ghost-btn" onClick={weather.toggleAnalysis}>Close</button>
    </div></div>
    {weather.status !== 'ready' || !current
      ? <><p>{weather.message}</p>{!weather.weatherPackage && weather.status !== 'loading' && weather.status !== 'working' &&
        <button className="ghost-btn" onClick={weather.prepareWeather}>Retry / Prepare Weather</button>}</>
      : <>
        <p>{formatGameTime(weather.clock.calendarDate, weather.session?.timezone)}</p>
        <p>{formatTemperature(current.temperatureC, units)} air / {formatTemperature(current.wetBulbC, units)} wet-bulb / {format(current.humidityPct)}% RH</p>
        <p>Wind {formatWindSpeed(current.windSpeedKph, units)} / {formatLiquidPrecipitationRate(current.precipitationMm, units)} {current.precipitationType}</p>
        {weather.weeklyOutlook && <section className="game-weekly-outlook" aria-label="Weekly weather outlook">
          <div className="game-forecast-title"><h3>Weekly outlook</h3><span>Composite source week</span></div>
          <p>{formatTemperature(weather.weeklyOutlook.temperatureRangeC.minimum, units)} to {formatTemperature(weather.weeklyOutlook.temperatureRangeC.maximum, units)}
            {' · '}{formatSnowfall(weather.weeklyOutlook.snowfallCm, units)} snow · {formatLiquidPrecipitation(weather.weeklyOutlook.rainMm, units)} rain</p>
          <p>Wind max {formatWindSpeed(weather.weeklyOutlook.maxWindKph, units)} / gust {formatWindSpeed(weather.weeklyOutlook.maxWindGustKph, units)}
            {' · '}{weather.weeklyOutlook.freezeThawTransitions} freeze/thaw · {weather.weeklyOutlook.snowmakingEligibleHours} snowmaking hours</p>
        </section>}
        <div className="game-forecast-title"><h3>Seven-day forecast</h3><span>Issued {formatGameTime(weather.forecast?.issuedAt, weather.session?.timezone)}</span></div>
        <div className="game-forecast-days" role="tablist" aria-label="Seven-day weather forecast">{forecastDays.map((day) => <button
          key={day.date} id={`game-forecast-tab-${day.date}`} role="tab"
          aria-selected={activeDate === day.date} aria-controls="game-forecast-hourly" tabIndex={activeDate === day.date ? 0 : -1}
          onKeyDown={(event) => {
            if (!['ArrowRight', 'ArrowLeft', 'Home', 'End'].includes(event.key)) return;
            event.preventDefault(); event.stopPropagation();
            const index = forecastDays.findIndex((item) => item.date === day.date);
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? forecastDays.length - 1
              : (index + (event.key === 'ArrowRight' ? 1 : -1) + forecastDays.length) % forecastDays.length;
            setSelectedDate(forecastDays[next].date);
            event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role=tab]')[next]?.focus();
          }}
          onClick={() => setSelectedDate(day.date)}>
          <strong>{formatForecastDay(day.date)}</strong>
          <div className="game-forecast-card">
            <span className="game-forecast-card-source">Forecast</span>
            <span>{titleCase(day.condition)}</span>
            <b>{formatTemperature(day.highC, units, 0)} / {formatTemperature(day.lowC, units, 0)}</b>
            <small>{formatLiquidPrecipitation(day.precipitationMm, units)} liquid &middot; {formatSnowfall(day.snowfallCm, units)} snow</small>
            <small>Wind {formatWindSpeed(day.windSpeedKph, units)} &middot; gust {formatWindSpeed(day.windGustKph, units)}</small>
          </div>
          <em>{day.confidencePct}% confidence</em>
        </button>)}</div>
        {activeDate && <div id="game-forecast-hourly" role="tabpanel" aria-labelledby={`game-forecast-tab-${activeDate}`} className="game-forecast-hourly">
          <strong>{formatForecastDay(activeDate)} hourly forecast</strong>
          <div className="game-forecast-hour-grid">{activeDay?.hours.map((hour) =>
            <div key={hour.at} title={`${formatLiquidPrecipitationRate(hour.precipitationMm, units)} ${hour.precipitationType}; wind ${formatWindSpeed(hour.windSpeedKph, units)}`}>
              <time>{formatForecastHour(hour.at, weather.session?.timezone)}</time>
              <b>{formatTemperature(hour.temperatureC, units, 0)}</b>
              <small>{hour.precipitationType === 'none' ? 'Dry' : titleCase(hour.precipitationType)}</small>
              <small>{formatLiquidPrecipitationRate(hour.precipitationMm, units)}</small><small>Wind {formatWindSpeed(hour.windSpeedKph, units)}</small>
            </div>)}</div>
        </div>}
        {!weather.forecast?.hours.length && <p>No forecast available.</p>}
      </>}
    <details className="weather-technical"><summary>Technical details</summary>
      <button className="ghost-btn" onClick={() => setLabOpen(true)} disabled={!labState}
        title={labState ? 'Inspect this game weather package' : 'Prepare weather before opening Weather Lab'}>Open in Weather Lab</button>
      {current && <>
        <p>Solar {format(current.globalRadiationWm2)} global / {format(current.directRadiationWm2)} direct / {format(current.diffuseRadiationWm2)} diffuse W/m2</p>
        <p>Cloud transmission {format(current.cloudTransmissionPct)}% / sun elevation {format(current.solarElevationDeg)} deg</p>
        {temperatureRange && <p>Terrain temperature field: {formatTemperature(temperatureRange.min, units)} to {formatTemperature(temperatureRange.max, units)}</p>}
        <p className="game-weather-source">{weather.weatherPackage?.manifest.quality === 'limited'
          ? `Limited/development package — ${weather.weatherPackage.manifest.sourceSummary}`
          : source
            ? `${source.provider} / ${source.quality}${source.correction !== 'none' ? ` / ${source.correction}` : ''}`
            : `${weather.weatherPackage?.manifest.quality ?? 'unknown'} / ${weather.weatherPackage?.manifest.sourceSummary ?? 'no source metadata'}`}</p>
      </>}
    </details>
  </aside>
  {labOpen && labState && <Suspense fallback={<div className="weather-lab-loading">Loading current game weather...</div>}>
    <CurrentGameWeatherLab initialState={labState} onExit={() => setLabOpen(false)} />
  </Suspense>}</>;
}

export function GameToolbar({
  resortName,
  onOpenStats,
  readout,
  units,
  terrain,
  simulation,
  onOpenWeather,
  showWeatherOverlay = true,
  saveStatus,
}: {
  resortName: string;
  onOpenStats: () => void;
  readout: Readout | null;
  units: Units;
  terrain: TerrainRecord | null;
  simulation: GameSimulationController;
  onOpenWeather?: () => void;
  showWeatherOverlay?: boolean;
  saveStatus?: string;
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
          aria-label={playTitle} title={playTitle}
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
            title={`${titleCase(speed)} simulation speed`}
          >{titleCase(speed)}</button>)}
        </div>
      </div>
      <div className="tb-group tb-weather-group">
        <button className="tb-weather" onClick={onOpenWeather ?? weather.toggleAnalysis} title="Toggle weather analysis overlay">
          {current ? formatTemperature(current.temperatureC, units) : 'Weather'}
        </button>
      </div>
      <div className="tb-group">
        <button className="tb-resort" onClick={onOpenStats} title="Ski area details"><span className="hud-resort tb-resort-name">{resortName}</span><small className="tb-save-status" role="status">{saveStatus}</small></button>
      </div>
      <div className="tb-group tb-group-right"><ToolbarReadout readout={readout} units={units} /></div>
    </div>
    {showWeatherOverlay && <GameWeatherOverlay terrain={terrain} weather={weather} units={units} />}
    {planningConfirmationOpen && <Dialog title="Finish summer planning?" onClose={() => setPlanningConfirmationOpen(false)} className="game-time-confirm">
        <p>This skips once to September 1, fixes the coming weather year, and enables the running game clock.</p>
        <div className="game-time-confirm-actions"><button className="ghost-btn" onClick={() => setPlanningConfirmationOpen(false)}>Keep planning</button>
          <button className="primary-btn" onClick={() => { setPlanningConfirmationOpen(false); void weather.advancePlanningPeriod(); }}>Skip to September 1</button></div>
    </Dialog>}
  </>;
}
