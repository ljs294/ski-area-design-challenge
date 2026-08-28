import type { WeatherDailySummaryV1 } from '../../weather-engine/src/index.ts';
import {
  alignDailyComparison,
  completenessForMetric,
  formatCompleteness,
  formatDailyMetric,
} from './weatherLabViewModel.ts';
import type { DailyComparisonSeries, DailyMetric } from './weatherLabViewModel.ts';

export interface DailyComparisonTableProps {
  series: DailyComparisonSeries;
  metric: DailyMetric;
  month?: number;
  caption?: string;
}

export const DAILY_METRIC_LABELS: Readonly<Record<DailyMetric, string>> = Object.freeze({
  temperature: 'Temperature (minimum / mean / maximum)',
  'wet-bulb': 'Wet bulb (minimum / mean / maximum)',
  precipitation: 'Liquid-equivalent precipitation by phase',
  snowfall: 'Snowfall depth and source',
  conditions: 'Dominant condition and hourly occupancy',
  snowmaking: 'Snowmaking hours',
  macro: 'Dominant air mass and hourly occupancy',
});

function MetricCell({ day, metric }: { day: WeatherDailySummaryV1 | null; metric: DailyMetric }) {
  return <td>
    <span>{formatDailyMetric(day, metric)}</span>
    <small>{formatCompleteness(completenessForMetric(day, metric))}</small>
  </td>;
}

export function DailyComparisonTable({ series, metric, month, caption }: DailyComparisonTableProps) {
  const rows = alignDailyComparison(series, month);
  const label = DAILY_METRIC_LABELS[metric];
  return <div className="weather-table-scroll">
    <table className="weather-daily-table">
      <caption>{caption ?? `Daily ${label.toLocaleLowerCase()} comparison`}</caption>
      <thead><tr><th scope="col">Local date</th><th scope="col">Observed</th><th scope="col">Baseline</th><th scope="col">Candidate</th></tr></thead>
      <tbody>{rows.length === 0
        ? <tr><td colSpan={4}>No daily values are available for this period.</td></tr>
        : rows.map((row) => <tr key={row.localDate}>
          <th scope="row"><time dateTime={row.localDate}>{row.localDate}</time></th>
          <MetricCell day={row.observed} metric={metric}/>
          <MetricCell day={row.baseline} metric={metric}/>
          <MetricCell day={row.candidate} metric={metric}/>
        </tr>)}</tbody>
    </table>
  </div>;
}
