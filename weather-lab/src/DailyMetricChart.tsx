import { useId } from 'react';
import { DAILY_METRIC_LABELS } from './DailyComparisonTable.tsx';
import { alignDailyComparison, dailyNumericValue } from './weatherLabViewModel.ts';
import type {
  DailyComparisonRow,
  DailyComparisonSeries,
  DailyNumericMetric,
  DailyNumericValue,
} from './weatherLabViewModel.ts';

export interface DailyMetricChartProps {
  series: DailyComparisonSeries;
  metric: DailyNumericMetric;
  month?: number;
  ariaLabel?: string;
}

const WIDTH = 900;
const HEIGHT = 260;
const PLOT = Object.freeze({ left: 56, right: 18, top: 18, bottom: 36 });
const SERIES = Object.freeze([
  { key: 'observed', label: 'Observed', color: '#8aa4bd', dash: '7 4' },
  { key: 'baseline', label: 'Pinned Baseline', color: '#d6ad55', dash: '2 4' },
  { key: 'candidate', label: 'Simulation', color: '#ff7448', dash: undefined },
] as const);

function valuesFor(rows: readonly DailyComparisonRow[], source: typeof SERIES[number]['key'], metric: DailyNumericMetric): readonly (DailyNumericValue | null)[] {
  return rows.map((row) => dailyNumericValue(row[source], metric));
}

function linePath(values: readonly (DailyNumericValue | null)[], x: (index: number) => number, y: (value: number) => number): string {
  let drawing = false;
  return values.map((value, index) => {
    if (value == null) { drawing = false; return ''; }
    const command = drawing ? 'L' : 'M'; drawing = true;
    return `${command}${x(index).toFixed(2)},${y(value.mean).toFixed(2)}`;
  }).filter(Boolean).join(' ');
}

function bandPath(values: readonly (DailyNumericValue | null)[], x: (index: number) => number, y: (value: number) => number): string {
  const paths: string[] = [];
  let segment: { value: DailyNumericValue; index: number }[] = [];
  const flush = () => {
    if (segment.length === 0) return;
    const top = segment.map(({ value, index }, offset) => `${offset === 0 ? 'M' : 'L'}${x(index).toFixed(2)},${y(value.maximum).toFixed(2)}`).join(' ');
    const bottom = [...segment].reverse().map(({ value, index }) => `L${x(index).toFixed(2)},${y(value.minimum).toFixed(2)}`).join(' ');
    paths.push(`${top} ${bottom} Z`); segment = [];
  };
  values.forEach((value, index) => { if (value == null) flush(); else segment.push({ value, index }); });
  flush();
  return paths.join(' ');
}

function unit(metric: DailyNumericMetric): string {
  if (metric === 'temperature' || metric === 'wet-bulb') return '°C';
  if (metric === 'snowfall') return 'cm';
  if (metric === 'snowmaking') return 'hours';
  return 'mm';
}

export function DailyMetricChart({ series, metric, month, ariaLabel }: DailyMetricChartProps) {
  const titleId = useId();
  const rows = alignDailyComparison(series, month);
  const visibleSeries = series.baseline ? SERIES : SERIES.filter((entry) => entry.key !== 'baseline');
  const all = visibleSeries.flatMap((entry) => valuesFor(rows, entry.key, metric).flatMap((value) => value == null ? [] : [value.minimum, value.maximum]));
  const dataMinimum = all.length > 0 ? Math.min(...all) : 0;
  const dataMaximum = all.length > 0 ? Math.max(...all) : 1;
  const shouldStartAtZero = metric !== 'temperature' && metric !== 'wet-bulb';
  const minimum = shouldStartAtZero ? Math.min(0, dataMinimum) : dataMinimum;
  const maximum = Math.max(minimum + 1, dataMaximum);
  const padding = shouldStartAtZero ? Math.max(0.1, maximum * 0.04) : Math.max(0.5, (maximum - minimum) * 0.08);
  const low = shouldStartAtZero ? minimum : minimum - padding;
  const high = maximum + padding;
  const plotWidth = WIDTH - PLOT.left - PLOT.right;
  const plotHeight = HEIGHT - PLOT.top - PLOT.bottom;
  const x = (index: number) => PLOT.left + index / Math.max(1, rows.length - 1) * plotWidth;
  const y = (value: number) => PLOT.top + (high - value) / (high - low) * plotHeight;
  const title = ariaLabel ?? `Daily ${DAILY_METRIC_LABELS[metric].toLocaleLowerCase()} for observed and Simulation weather${series.baseline ? ' with pinned Baseline' : ''}`;
  const metricUnit = unit(metric);
  const yTicks = Array.from({ length: 5 }, (_, index) => high - (high - low) * index / 4);
  const xTicks = [...new Set(Array.from({ length: Math.min(6, rows.length) }, (_, index) => Math.round(index * (rows.length - 1) / Math.max(1, Math.min(5, rows.length - 1)))))];

  if (rows.length === 0 || all.length === 0) return <p role="status">No {DAILY_METRIC_LABELS[metric].toLocaleLowerCase()} values are available for this period.</p>;

  return <figure className="weather-daily-chart">
    <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-labelledby={titleId}>
      <title id={titleId}>{title}</title>
      {yTicks.map((tick) => <g key={tick}><line x1={PLOT.left} x2={WIDTH - PLOT.right} y1={y(tick)} y2={y(tick)} stroke="currentColor" opacity="0.16"/><text x={PLOT.left - 8} y={y(tick) + 4} textAnchor="end" fontSize="10">{tick.toFixed(1)}</text></g>)}
      {xTicks.map((index) => <g key={index}><line x1={x(index)} x2={x(index)} y1={PLOT.top} y2={HEIGHT - PLOT.bottom} stroke="currentColor" opacity="0.08"/><text x={x(index)} y={HEIGHT - 10} textAnchor={index === 0 ? 'start' : index === rows.length - 1 ? 'end' : 'middle'} fontSize="10">{rows[index].localDate.slice(5)}</text></g>)}
      {(metric === 'temperature' || metric === 'wet-bulb') && visibleSeries.map((entry) => {
        const values = valuesFor(rows, entry.key, metric);
        return <path key={`${entry.key}-band`} d={bandPath(values, x, y)} fill={entry.color} fillOpacity="0.12" stroke="none"/>;
      })}
      {visibleSeries.map((entry) => {
        const values = valuesFor(rows, entry.key, metric);
        return <g key={entry.key}><path d={linePath(values, x, y)} fill="none" stroke={entry.color} strokeWidth="2" strokeDasharray={entry.dash}/>{values.map((value, index) => value && <circle key={rows[index].localDate} cx={x(index)} cy={y(value.mean)} r="2.5" fill={entry.color} tabIndex={0}><title>{rows[index].localDate} · {entry.label}: {value.mean.toFixed(2)} {metricUnit} (min {value.minimum.toFixed(2)}, max {value.maximum.toFixed(2)})</title></circle>)}</g>;
      })}
    </svg>
    <figcaption>{DAILY_METRIC_LABELS[metric]} ({metricUnit}). Temperature charts include daily minimum/maximum bands.</figcaption>
    <ul className="weather-chart-legend" aria-label="Chart series">{visibleSeries.map((entry) => <li key={entry.key}><i style={{ backgroundColor: entry.color, borderTop: entry.dash ? `2px dashed ${entry.color}` : undefined }}/>{entry.label}</li>)}</ul>
  </figure>;
}
