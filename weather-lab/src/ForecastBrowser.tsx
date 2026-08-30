import { useEffect, useMemo, useState } from 'react';
import type { ObservedWeatherHourV1, SimulatedWeatherHourV1 } from '../../weather-engine/src/contracts.ts';
import {
  displayValue,
  circularMeanWindDirection,
  formatWindDirection,
  localHourLabel,
  metricLabel,
  metricUnit,
  metricValue,
  precipitationColor,
  type ForecastMetric,
  type ForecastMetricHour,
  type ForecastUnits,
} from './forecastViewModel.ts';

export interface ForecastBrowserProps {
  timezone: string;
  units: ForecastUnits;
  simulation: readonly SimulatedWeatherHourV1[];
  observed: readonly ObservedWeatherHourV1[];
  baseline?: readonly SimulatedWeatherHourV1[];
  onSelectedDateChange?: (date: string) => void;
}

const METRICS: readonly ForecastMetric[] = ['temperature', 'wetBulb', 'precipitation', 'snowfall', 'wind', 'cloud', 'humidity'];
const WIDTH = 920;
const HEIGHT = 270;
const PAD = { left: 58, right: 18, top: 20, bottom: 42 };

function titleCase(value: string) {
  return value.replace(/-/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function sourceDate(hour: SimulatedWeatherHourV1 | ObservedWeatherHourV1) {
  return hour.localDateTime.slice(0, 10);
}

function sourceHours<T extends SimulatedWeatherHourV1 | ObservedWeatherHourV1>(hours: readonly T[], date: string) {
  return hours.filter((hour) => sourceDate(hour) === date);
}

function dominantCondition(hours: readonly (SimulatedWeatherHourV1 | ObservedWeatherHourV1)[]) {
  const counts = new Map<string, number>();
  for (const hour of hours) if (hour.condition) counts.set(hour.condition, (counts.get(hour.condition) ?? 0) + 1);
  return [...counts].sort(([leftName, left], [rightName, right]) => right - left || leftName.localeCompare(rightName))[0]?.[0] ?? null;
}

function valuesFor(hours: readonly ForecastMetricHour[], metric: ForecastMetric, units: ForecastUnits) {
  return hours.flatMap((hour) => {
    const value = metricValue(hour, metric);
    const gust = metric === 'wind' ? hour.windGustKph : null;
    return [value, gust].filter((item): item is number => item != null && Number.isFinite(item)).map((item) => displayValue(item, metric, units));
  });
}

function pointPath(hours: readonly ForecastMetricHour[], metric: ForecastMetric, units: ForecastUnits, minimum: number, maximum: number, gust = false) {
  const innerWidth = WIDTH - PAD.left - PAD.right;
  const innerHeight = HEIGHT - PAD.top - PAD.bottom;
  let path = '';
  let drawing = false;
  hours.forEach((hour, index) => {
    const raw = gust ? hour.windGustKph : metricValue(hour, metric);
    if (raw == null || !Number.isFinite(raw)) { drawing = false; return; }
    const x = PAD.left + innerWidth * (hours.length === 1 ? 0 : index / (hours.length - 1));
    const y = PAD.top + innerHeight * (1 - (displayValue(raw, metric, units) - minimum) / (maximum - minimum));
    path += `${drawing ? ' L' : ' M'} ${x.toFixed(1)} ${y.toFixed(1)}`;
    drawing = true;
  });
  return path;
}

function dayLabel(date: string) {
  return new Date(`${date}T12:00:00`).toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
}

function total(values: readonly (number | null)[]) {
  const available = values.filter((value): value is number => value != null && Number.isFinite(value));
  return available.length ? available.reduce((sum, value) => sum + value, 0) : null;
}

function mean(values: readonly (number | null | undefined)[]) {
  const available = values.filter((value): value is number => value != null && Number.isFinite(value));
  return available.length ? available.reduce((sum, value) => sum + value, 0) / available.length : null;
}

function dailyWind(hours: readonly ForecastMetricHour[], units: ForecastUnits) {
  const speed = mean(hours.map((hour) => hour.windSpeedKph));
  if (speed == null) return 'Wind unavailable';
  const direction = circularMeanWindDirection(hours.map((hour) => hour.windDirectionDeg));
  return `Wind ${displayValue(speed, 'wind', units).toFixed(1)} ${metricUnit('wind', units)} · ${formatWindDirection(direction)}`;
}

export function ForecastBrowser({ timezone, units, simulation, observed, baseline, onSelectedDateChange }: ForecastBrowserProps) {
  const allDates = useMemo(() => [...new Set(simulation.map(sourceDate))], [simulation]);
  const [windowStart, setWindowStart] = useState(allDates[0] ?? '');
  const startIndex = Math.max(0, allDates.indexOf(windowStart));
  const dates = allDates.slice(startIndex, startIndex + 5);
  const [selectedDate, setSelectedDate] = useState(dates[0] ?? '');
  const [metric, setMetric] = useState<ForecastMetric>('temperature');
  const [focusedIndex, setFocusedIndex] = useState(0);
  const [visible, setVisible] = useState({ observed: true, baseline: true });

  useEffect(() => {
    if (!allDates.includes(windowStart)) setWindowStart(allDates[0] ?? '');
  }, [allDates, windowStart]);
  useEffect(() => {
    if (!dates.includes(selectedDate)) setSelectedDate(dates[0] ?? '');
  }, [dates, selectedDate]);
  useEffect(() => {
    if (selectedDate) onSelectedDateChange?.(selectedDate);
  }, [onSelectedDateChange, selectedDate]);

  const selectedSimulation = sourceHours(simulation, selectedDate);
  const selectedObserved = sourceHours(observed, selectedDate);
  const selectedBaseline = baseline ? sourceHours(baseline, selectedDate) : [];
  const plotted = [selectedSimulation, ...(visible.observed ? [selectedObserved] : []), ...(visible.baseline && baseline ? [selectedBaseline] : [])];
  const allValues = plotted.flatMap((hours) => valuesFor(hours, metric, units));
  let minimum = allValues.length ? Math.min(...allValues) : 0;
  let maximum = allValues.length ? Math.max(...allValues) : 1;
  if (metric === 'precipitation' || metric === 'snowfall' || metric === 'wind' || metric === 'cloud' || metric === 'humidity') minimum = 0;
  if (metric === 'cloud' || metric === 'humidity') maximum = 100;
  if (maximum <= minimum) maximum = minimum + 1;
  const rangePad = (maximum - minimum) * .08;
  if (metric === 'temperature' || metric === 'wetBulb') { minimum -= rangePad; maximum += rangePad; } else maximum += rangePad;
  const ticks = Array.from({ length: 5 }, (_, index) => maximum - (maximum - minimum) * index / 4);
  const selectedReadout = selectedSimulation[Math.min(focusedIndex, Math.max(0, selectedSimulation.length - 1))];
  const unit = metricUnit(metric, units);
  const isBars = metric === 'precipitation' || metric === 'snowfall';
  const innerWidth = WIDTH - PAD.left - PAD.right;
  const innerHeight = HEIGHT - PAD.top - PAD.bottom;
  const exactReadout = (hours: readonly ForecastMetricHour[], at: string) => {
    const hour = hours.find((entry) => entry.at === at);
    const value = hour ? metricValue(hour, metric) : null;
    if (value == null) return 'Unavailable';
    if (metric === 'wind' && hour) {
      const gust = hour.windGustKph == null ? '' : ` · gust ${displayValue(hour.windGustKph, 'wind', units).toFixed(1)} ${unit}`;
      return `${displayValue(value, metric, units).toFixed(1)} ${unit}${gust} · ${formatWindDirection(hour.windDirectionDeg)}`;
    }
    return `${displayValue(value, metric, units).toFixed(1)} ${unit}`;
  };
  const line = (hours: readonly ForecastMetricHour[], color: string, dash?: string, width = 2.5, gust = false) => {
    const path = pointPath(hours, metric, units, minimum, maximum, gust);
    return path ? <path d={path} fill="none" stroke={color} strokeWidth={width} strokeDasharray={dash} vectorEffect="non-scaling-stroke"/> : null;
  };

  return <section aria-labelledby="simulation-browser-title" className="forecast-browser">
    <div className="panel-title">
      <div><h2 id="simulation-browser-title">Five-day Simulation weather</h2><p>The generated Simulation is shown directly as truth, with actual historical observations beneath it.</p></div>
      <label className="simulation-period-start">Five-day period starts<select value={windowStart} onChange={(event) => { setWindowStart(event.target.value); setSelectedDate(event.target.value); setFocusedIndex(0); }}>{allDates.map((date) => <option key={date} value={date}>{date}</option>)}</select></label>
    </div>
    <div className="forecast-days" role="tablist" aria-label="Simulation days">{dates.map((date) => {
      const hours = sourceHours(simulation, date);
      const actualHours = sourceHours(observed, date);
      const temperatures = hours.map((hour) => displayValue(hour.temperatureC, 'temperature', units));
      const precipitation = displayValue(hours.reduce((sum, hour) => sum + hour.precipitationMm, 0), 'precipitation', units);
      const snow = displayValue(hours.reduce((sum, hour) => sum + hour.snowfallCm, 0), 'snowfall', units);
      const actualTemperatures = actualHours.flatMap((hour) => hour.temperatureC == null ? [] : [displayValue(hour.temperatureC, 'temperature', units)]);
      const actualPrecipitation = total(actualHours.map((hour) => hour.precipitationMm));
      const actualSnowfall = total(actualHours.map((hour) => hour.snowfallCm));
      return <button key={date} id={`simulation-tab-${date}`} role="tab" aria-controls="simulation-day-chart" aria-selected={date === selectedDate} onClick={() => { setSelectedDate(date); setFocusedIndex(0); }}>
        <strong>{dayLabel(date)}</strong>
        <div className="forecast-card-simulation"><span className="forecast-card-source">Simulation</span><span>{titleCase(dominantCondition(hours) ?? 'unknown')}</span><b>{Math.round(Math.max(...temperatures))}° / {Math.round(Math.min(...temperatures))}°</b><small>{precipitation.toFixed(units === 'us' ? 2 : 1)} {metricUnit('precipitation', units)} liquid · {snow.toFixed(1)} {metricUnit('snowfall', units)} snow</small><small>{dailyWind(hours, units)}</small></div>
        <div className="forecast-card-actual"><span className="forecast-card-source">Actual historical</span>{actualTemperatures.length ? <><span>{titleCase(dominantCondition(actualHours) ?? 'condition unavailable')}</span><b>{Math.round(Math.max(...actualTemperatures))}° / {Math.round(Math.min(...actualTemperatures))}°</b><small>{actualPrecipitation == null ? 'Liquid unavailable' : `${displayValue(actualPrecipitation, 'precipitation', units).toFixed(units === 'us' ? 2 : 1)} ${metricUnit('precipitation', units)} liquid`} · {actualSnowfall == null ? 'Snow unavailable' : `${displayValue(actualSnowfall, 'snowfall', units).toFixed(1)} ${metricUnit('snowfall', units)} snow`}</small><small>{dailyWind(actualHours, units)}</small></> : <small>Actual observations unavailable</small>}</div>
        {hours.length < 23 && <em>Incomplete period</em>}
      </button>;
    })}</div>
    <nav className="forecast-metrics" aria-label="Hourly chart metric">{METRICS.map((item) => <button key={item} aria-pressed={item === metric} onClick={() => setMetric(item)}>{metricLabel(item, units)}</button>)}</nav>
    <div className="forecast-series-toggles" aria-label="Chart series"><span className="forecast-series-label simulation-truth"><i/>Simulation weather</span><label className="forecast-series-label historical-actual"><input type="checkbox" checked={visible.observed} onChange={(event) => setVisible((current) => ({ ...current, observed: event.target.checked }))}/><i/>Actual historical weather</label>{baseline && <label className="forecast-series-label pinned-baseline"><input type="checkbox" checked={visible.baseline} onChange={(event) => setVisible((current) => ({ ...current, baseline: event.target.checked }))}/><i/>Pinned Baseline</label>}</div>
    <div id="simulation-day-chart" role="tabpanel" aria-labelledby={`simulation-tab-${selectedDate}`} className="forecast-chart">
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-label={`${metricLabel(metric, units)} for Simulation and actual historical weather on ${selectedDate}`}>
        {ticks.map((tick, index) => { const y = PAD.top + innerHeight * index / 4; return <g key={tick}><line x1={PAD.left} x2={WIDTH - PAD.right} y1={y} y2={y} className="forecast-grid"/><text x={PAD.left - 8} y={y + 4} textAnchor="end" className="forecast-axis-label">{tick.toFixed(metric === 'precipitation' ? 2 : 0)}</text></g>; })}
        {selectedSimulation.map((hour, index) => index % 3 === 0 ? <text key={hour.at} x={PAD.left + innerWidth * index / Math.max(1, selectedSimulation.length - 1)} y={HEIGHT - 14} textAnchor="middle" className="forecast-axis-label">{localHourLabel(hour.at, timezone).replace(':00 ', '')}</text> : null)}
        {isBars ? selectedSimulation.map((hour, index) => { const value = displayValue(metricValue(hour, metric) ?? 0, metric, units); const barWidth = innerWidth / Math.max(1, selectedSimulation.length) * .72; const height = innerHeight * value / Math.max(.0001, maximum - minimum); return <rect key={hour.at} x={PAD.left + innerWidth * index / Math.max(1, selectedSimulation.length) + 2} y={PAD.top + innerHeight - height} width={barWidth} height={height} fill={precipitationColor(hour.precipitationPhase)}/>; }) : line(selectedSimulation, '#ff874f', undefined, 3.5)}
        {metric === 'wind' && line(selectedSimulation, '#ffc09f', '4 4', 1.5, true)}
        {visible.observed && line(selectedObserved, '#9aa9b5', '8 5', 2.25)}
        {visible.baseline && baseline && line(selectedBaseline, '#d7b45a', '2 4')}
      </svg>
      <div className="forecast-hour-targets" aria-label="Exact hourly Simulation values">{selectedSimulation.map((hour, index) => <button key={hour.at} className={focusedIndex === index ? 'active' : ''} onFocus={() => setFocusedIndex(index)} onMouseEnter={() => setFocusedIndex(index)} aria-label={`${localHourLabel(hour.at, timezone)}: ${exactReadout(selectedSimulation, hour.at)}`}>{localHourLabel(hour.at, timezone)}</button>)}</div>
      {selectedReadout && <output className="forecast-crosshair" aria-live="polite"><strong>{localHourLabel(selectedReadout.at, timezone)}</strong><span className="simulation-truth-readout">Simulation {exactReadout(selectedSimulation, selectedReadout.at)}</span>{visible.observed && <span className="historical-actual-readout">Actual historical {exactReadout(selectedObserved, selectedReadout.at)}</span>}{visible.baseline && baseline && <span>Baseline {exactReadout(selectedBaseline, selectedReadout.at)}</span>}<span>{titleCase(selectedReadout.condition)}</span></output>}
    </div>
  </section>;
}
