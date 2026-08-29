import { useEffect, useMemo, useState } from 'react';
import type {
  ForecastHourV1,
  ForecastIssueV1,
  ObservedWeatherHourV1,
  SimulatedWeatherHourV1,
} from '../../weather-engine/src/contracts.ts';
import {
  displayValue,
  fiveForecastDays,
  forecastLocalDate,
  forecastLocalHour,
  metricLabel,
  metricUnit,
  metricValue,
  precipitationColor,
  type ForecastMetric,
  type ForecastMetricHour,
  type ForecastUnits,
} from './forecastViewModel.ts';

export interface ForecastBrowserProps {
  issue: ForecastIssueV1;
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

function dominantCondition(hours: readonly ForecastHourV1[]) {
  const counts = new Map<string, number>();
  for (const hour of hours) counts.set(hour.condition, (counts.get(hour.condition) ?? 0) + 1);
  return [...counts].sort(([leftName, left], [rightName, right]) => right - left || leftName.localeCompare(rightName))[0]?.[0] ?? 'unknown';
}

function sourceDate(hour: SimulatedWeatherHourV1 | ObservedWeatherHourV1) {
  return hour.localDateTime.slice(0, 10);
}

function sourceHours<T extends SimulatedWeatherHourV1 | ObservedWeatherHourV1>(hours: readonly T[], date: string) {
  return hours.filter((hour) => sourceDate(hour) === date);
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
  hours.forEach((hour, index) => {
    const raw = gust ? hour.windGustKph : metricValue(hour, metric);
    if (raw == null || !Number.isFinite(raw)) return;
    const x = PAD.left + innerWidth * (hours.length === 1 ? 0 : index / (hours.length - 1));
    const y = PAD.top + innerHeight * (1 - (displayValue(raw, metric, units) - minimum) / (maximum - minimum));
    path += `${path ? ' L' : 'M'} ${x.toFixed(1)} ${y.toFixed(1)}`;
  });
  return path;
}

function dayLabel(date: string) {
  return new Date(`${date}T12:00:00`).toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
}

export function ForecastBrowser({ issue, timezone, units, simulation, observed, baseline, onSelectedDateChange }: ForecastBrowserProps) {
  const days = useMemo(() => fiveForecastDays(issue, timezone), [issue, timezone]);
  const dates = useMemo(() => days.map((hours) => forecastLocalDate(hours[0].at, timezone)), [days, timezone]);
  const [selectedDate, setSelectedDate] = useState(dates[0] ?? '');
  const [metric, setMetric] = useState<ForecastMetric>('temperature');
  const [focusedIndex, setFocusedIndex] = useState(0);
  const [visible, setVisible] = useState({ simulation: true, observed: true, baseline: true });

  useEffect(() => {
    if (!dates.includes(selectedDate)) setSelectedDate(dates[0] ?? '');
  }, [dates, selectedDate]);
  useEffect(() => {
    if (selectedDate) onSelectedDateChange?.(selectedDate);
  }, [onSelectedDateChange, selectedDate]);

  const selectedForecast = days[dates.indexOf(selectedDate)] ?? [];
  const selectedSimulation = sourceHours(simulation, selectedDate);
  const selectedObserved = sourceHours(observed, selectedDate);
  const selectedBaseline = baseline ? sourceHours(baseline, selectedDate) : [];
  const plotted = [
    selectedForecast,
    ...(visible.simulation ? [selectedSimulation] : []),
    ...(visible.observed ? [selectedObserved] : []),
    ...(visible.baseline && baseline ? [selectedBaseline] : []),
  ];
  const allValues = plotted.flatMap((hours) => valuesFor(hours, metric, units));
  let minimum = allValues.length ? Math.min(...allValues) : 0;
  let maximum = allValues.length ? Math.max(...allValues) : 1;
  if (metric === 'precipitation' || metric === 'snowfall' || metric === 'wind' || metric === 'cloud' || metric === 'humidity') minimum = 0;
  if (metric === 'cloud' || metric === 'humidity') maximum = 100;
  if (maximum <= minimum) maximum = minimum + 1;
  const rangePad = (maximum - minimum) * .08;
  if (metric === 'temperature' || metric === 'wetBulb') {
    minimum -= rangePad;
    maximum += rangePad;
  } else maximum += rangePad;
  const ticks = Array.from({ length: 5 }, (_, index) => maximum - (maximum - minimum) * index / 4);
  const selectedReadout = selectedForecast[Math.min(focusedIndex, Math.max(0, selectedForecast.length - 1))];
  const unit = metricUnit(metric, units);
  const isBars = metric === 'precipitation' || metric === 'snowfall';
  const innerWidth = WIDTH - PAD.left - PAD.right;
  const innerHeight = HEIGHT - PAD.top - PAD.bottom;
  const exactReadout = (hours: readonly ForecastMetricHour[], at: string) => {
    const hour = hours.find((entry) => entry.at === at);
    const value = hour ? metricValue(hour, metric) : null;
    return value == null ? 'Unavailable' : `${displayValue(value, metric, units).toFixed(1)} ${unit}`;
  };

  const line = (hours: readonly ForecastMetricHour[], color: string, dash?: string, width = 2.5, gust = false) => {
    const path = pointPath(hours, metric, units, minimum, maximum, gust);
    return path ? <path d={path} fill="none" stroke={color} strokeWidth={width} strokeDasharray={dash} vectorEffect="non-scaling-stroke"/> : null;
  };

  return <section aria-labelledby="forecast-browser-title" className="forecast-browser">
    <div className="panel-title">
      <div><h2 id="forecast-browser-title">Five-day hourly forecast</h2><p>Forecast values lead; exact Simulation and observed values can be layered for review.</p></div>
      <div className="forecast-issue"><span>Forecast issued</span><time dateTime={issue.issuedAt}>{new Date(issue.issuedAt).toLocaleString([], { timeZone: timezone, dateStyle: 'medium', timeStyle: 'short' })}</time></div>
    </div>
    <div className="forecast-days" role="tablist" aria-label="Forecast days">{days.map((hours, index) => {
      const date = dates[index];
      const temperatures = hours.map((hour) => displayValue(hour.temperatureC, 'temperature', units));
      const precip = displayValue(hours.reduce((sum, hour) => sum + hour.precipitationMm, 0), 'precipitation', units);
      const snow = displayValue(hours.reduce((sum, hour) => sum + (hour.snowfallCm ?? 0), 0), 'snowfall', units);
      const confidence = hours.reduce((sum, hour) => sum + hour.confidencePct, 0) / Math.max(1, hours.length);
      return <button key={date} id={`forecast-tab-${date}`} role="tab" aria-controls="forecast-day-chart" aria-selected={date === selectedDate} onClick={() => { setSelectedDate(date); setFocusedIndex(0); }}>
        <strong>{dayLabel(date)}</strong><span>{titleCase(dominantCondition(hours))}</span>
        <b>{Math.round(Math.max(...temperatures))}° / {Math.round(Math.min(...temperatures))}°</b>
        <small>{precip.toFixed(units === 'us' ? 2 : 1)} {metricUnit('precipitation', units)} liquid · {snow.toFixed(1)} {metricUnit('snowfall', units)} snow</small>
        <small>{Math.round(confidence)}% confidence</small>
        {(hours.length < 23 || index === days.length - 1 && hours.length < 24) && <em>Incomplete period</em>}
      </button>;
    })}</div>
    <nav className="forecast-metrics" aria-label="Hourly chart metric">{METRICS.map((item) => <button key={item} aria-pressed={item === metric} onClick={() => setMetric(item)}>{metricLabel(item, units)}</button>)}</nav>
    <div className="forecast-series-toggles" aria-label="Chart series">
      <span className="forecast-key">Forecast</span>
      <label><input type="checkbox" checked={visible.simulation} onChange={(event) => setVisible((current) => ({ ...current, simulation: event.target.checked }))}/> Simulation truth</label>
      <label><input type="checkbox" checked={visible.observed} onChange={(event) => setVisible((current) => ({ ...current, observed: event.target.checked }))}/> Observed actuals</label>
      {baseline && <label><input type="checkbox" checked={visible.baseline} onChange={(event) => setVisible((current) => ({ ...current, baseline: event.target.checked }))}/> Pinned Baseline</label>}
    </div>
    <div id="forecast-day-chart" role="tabpanel" aria-labelledby={`forecast-tab-${selectedDate}`} className="forecast-chart">
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-label={`${metricLabel(metric, units)} for ${selectedDate}`}>
        {ticks.map((tick, index) => {
          const y = PAD.top + innerHeight * index / 4;
          return <g key={tick}><line x1={PAD.left} x2={WIDTH - PAD.right} y1={y} y2={y} className="forecast-grid"/><text x={PAD.left - 8} y={y + 4} textAnchor="end" className="forecast-axis-label">{tick.toFixed(metric === 'precipitation' ? 2 : 0)}</text></g>;
        })}
        {selectedForecast.map((hour, index) => index % 3 === 0 ? <text key={hour.at} x={PAD.left + innerWidth * index / Math.max(1, selectedForecast.length - 1)} y={HEIGHT - 14} textAnchor="middle" className="forecast-axis-label">{forecastLocalHour(hour.at, timezone).replace(':00 ', '')}</text> : null)}
        {isBars ? selectedForecast.map((hour, index) => {
          const raw = metricValue(hour, metric) ?? 0;
          const value = displayValue(raw, metric, units);
          const barWidth = innerWidth / Math.max(1, selectedForecast.length) * .72;
          const height = innerHeight * value / Math.max(.0001, maximum - minimum);
          return <rect key={hour.at} x={PAD.left + innerWidth * index / Math.max(1, selectedForecast.length) + 2} y={PAD.top + innerHeight - height} width={barWidth} height={height} fill={precipitationColor(hour.precipitationPhase)}/>;
        }) : line(selectedForecast, '#49a9f8', undefined, 3.5)}
        {metric === 'wind' && line(selectedForecast, '#9dd7ff', '4 4', 1.5, true)}
        {visible.simulation && line(selectedSimulation, '#f28e52')}
        {visible.observed && line(selectedObserved, '#e7eef5', '7 4')}
        {visible.baseline && baseline && line(selectedBaseline, '#d7b45a', '2 4')}
      </svg>
      <div className="forecast-hour-targets" aria-label="Exact hourly forecast values">{selectedForecast.map((hour, index) => {
        const value = metricValue(hour, metric);
        return <button key={hour.at} className={focusedIndex === index ? 'active' : ''} onFocus={() => setFocusedIndex(index)} onMouseEnter={() => setFocusedIndex(index)} aria-label={`${forecastLocalHour(hour.at, timezone)}: ${value == null ? 'unavailable' : `${displayValue(value, metric, units).toFixed(1)} ${unit}`}`}>{forecastLocalHour(hour.at, timezone)}</button>;
      })}</div>
      {selectedReadout && <output className="forecast-crosshair" aria-live="polite"><strong>{forecastLocalHour(selectedReadout.at, timezone)}</strong><span>Forecast {exactReadout(selectedForecast, selectedReadout.at)}</span>{visible.simulation && <span>Simulation {exactReadout(selectedSimulation, selectedReadout.at)}</span>}{visible.observed && <span>Observed {exactReadout(selectedObserved, selectedReadout.at)}</span>}{visible.baseline && baseline && <span>Baseline {exactReadout(selectedBaseline, selectedReadout.at)}</span>}<span>{titleCase(selectedReadout.condition)} · {Math.round(selectedReadout.confidencePct)}% confidence</span></output>}
    </div>
  </section>;
}
