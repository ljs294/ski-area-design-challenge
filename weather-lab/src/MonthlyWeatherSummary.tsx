import { useMemo } from 'react';
import type { WeatherDailySummaryV1, WeatherEventV1 } from '../../weather-engine/src/contracts.ts';
import { displayValue, metricUnit, type ForecastUnits } from './forecastViewModel.ts';

export interface MonthlyWeatherSummaryProps {
  month: number;
  units: ForecastUnits;
  simulation: readonly WeatherDailySummaryV1[];
  observed: readonly WeatherDailySummaryV1[];
  baseline?: readonly WeatherDailySummaryV1[];
  simulationEvents: readonly WeatherEventV1[];
  observedEvents: readonly WeatherEventV1[];
  baselineEvents?: readonly WeatherEventV1[];
}

function inMonth(date: string, month: number) {
  return Number(date.slice(5, 7)) === month;
}

function sum(values: readonly (number | null)[]) {
  const available = values.filter((value): value is number => value != null && Number.isFinite(value));
  return available.length ? available.reduce((total, value) => total + value, 0) : null;
}

function stats(points: readonly WeatherDailySummaryV1[], events: readonly WeatherEventV1[], month: number) {
  const days = points.filter((point) => inMonth(point.localDate, month));
  const temperatures = days.flatMap((point) => point.temperatureC ? [point.temperatureC] : []);
  const wetBulbs = days.flatMap((point) => point.wetBulbC ? [point.wetBulbC] : []);
  const conditionHours = new Map<string, number>();
  for (const day of days) for (const [condition, hours] of Object.entries(day.conditionHours)) conditionHours.set(condition, (conditionHours.get(condition) ?? 0) + (hours ?? 0));
  const dominantCondition = [...conditionHours].sort(([leftName, left], [rightName, right]) => right - left || leftName.localeCompare(rightName))[0]?.[0] ?? 'Unavailable';
  const precipitation = sum(days.map((day) => day.precipitationMm));
  const phaseTotals = Object.fromEntries(['rain', 'snow', 'mixed', 'freezing-rain'].map((phase) => [phase, sum(days.map((day) => day.precipitationByPhaseMm?.[phase as keyof NonNullable<WeatherDailySummaryV1['precipitationByPhaseMm']>] ?? null)) ?? 0]));
  const eventList = events.filter((event) => inMonth(event.localStartDate, month));
  const severity = { minor: 0, notable: 0, major: 0 };
  for (const event of eventList) severity[event.severity] += 1;
  return {
    days: days.length,
    temperature: temperatures.length ? { minimum: Math.min(...temperatures.map((value) => value.minimum)), mean: temperatures.reduce((total, value) => total + value.mean, 0) / temperatures.length, maximum: Math.max(...temperatures.map((value) => value.maximum)) } : null,
    wetBulb: wetBulbs.length ? { minimum: Math.min(...wetBulbs.map((value) => value.minimum)), mean: wetBulbs.reduce((total, value) => total + value.mean, 0) / wetBulbs.length, maximum: Math.max(...wetBulbs.map((value) => value.maximum)) } : null,
    precipitation,
    phaseTotals,
    snowfall: sum(days.map((day) => day.snowfallCm)),
    snowmaking: sum(days.map((day) => day.snowmakingHours)),
    dominantCondition,
    eventCount: eventList.length,
    severity,
  };
}

function formatRange(value: ReturnType<typeof stats>['temperature'], units: ForecastUnits, metric: 'temperature' | 'wetBulb') {
  if (!value) return 'Unavailable';
  return `${displayValue(value.minimum, metric, units).toFixed(1)} / ${displayValue(value.mean, metric, units).toFixed(1)} / ${displayValue(value.maximum, metric, units).toFixed(1)} ${metricUnit(metric, units)}`;
}

function titleCase(value: string) {
  return value.replace(/-/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
}

export function MonthlyWeatherSummary(props: MonthlyWeatherSummaryProps) {
  const rows = useMemo(() => [
    { label: 'Simulation', value: stats(props.simulation, props.simulationEvents, props.month) },
    { label: 'Observed', value: stats(props.observed, props.observedEvents, props.month) },
    ...(props.baseline && props.baselineEvents ? [{ label: 'Pinned Baseline', value: stats(props.baseline, props.baselineEvents, props.month) }] : []),
  ], [props.baseline, props.baselineEvents, props.month, props.observed, props.observedEvents, props.simulation, props.simulationEvents]);
  const monthName = new Date(2000, props.month - 1, 1).toLocaleDateString([], { month: 'long' });
  return <section className="panel monthly-weather-summary" aria-labelledby="monthly-weather-summary-title">
    <div className="panel-title"><div><h2 id="monthly-weather-summary-title">{monthName} weather overview</h2><p>Complete-month weather totals synchronized to the selected forecast day.</p></div></div>
    <div className="monthly-summary-grid">{rows.map(({ label, value }) => <article key={label}>
      <h3>{label}</h3><dl>
        <dt>Daily low / mean / high</dt><dd>{formatRange(value.temperature, props.units, 'temperature')}</dd>
        <dt>Wet bulb low / mean / high</dt><dd>{formatRange(value.wetBulb, props.units, 'wetBulb')}</dd>
        <dt>Liquid precipitation</dt><dd>{value.precipitation == null ? 'Unavailable' : `${displayValue(value.precipitation, 'precipitation', props.units).toFixed(props.units === 'us' ? 2 : 1)} ${metricUnit('precipitation', props.units)}`}</dd>
        <dt>Phase liquid</dt><dd>Rain {displayValue(value.phaseTotals.rain, 'precipitation', props.units).toFixed(2)} · Snow {displayValue(value.phaseTotals.snow, 'precipitation', props.units).toFixed(2)} · Mixed {displayValue(value.phaseTotals.mixed, 'precipitation', props.units).toFixed(2)}</dd>
        <dt>Snowfall</dt><dd>{value.snowfall == null ? 'Unavailable' : `${displayValue(value.snowfall, 'snowfall', props.units).toFixed(1)} ${metricUnit('snowfall', props.units)}`}</dd>
        <dt>Snowmaking hours</dt><dd>{value.snowmaking?.toFixed(0) ?? 'Unavailable'}</dd>
        <dt>Dominant condition</dt><dd>{titleCase(value.dominantCondition)}</dd>
        <dt>Events</dt><dd>{value.eventCount} total · {value.severity.minor} minor · {value.severity.notable} notable · {value.severity.major} major</dd>
      </dl>
    </article>)}</div>
  </section>;
}
