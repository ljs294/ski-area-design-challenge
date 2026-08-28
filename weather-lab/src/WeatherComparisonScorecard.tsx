import type { WeatherComparisonScoresV1 } from '../../weather-engine/src/index.ts';
import { comparisonScoreRows } from './weatherLabViewModel.ts';

export interface WeatherComparisonScorecardProps {
  baseline?: WeatherComparisonScoresV1;
  candidate?: WeatherComparisonScoresV1;
}

function display(value: number | null | undefined, unit: string): string {
  if (value == null || !Number.isFinite(value)) return 'Unavailable';
  const adjusted = unit === '%' ? value * 100 : value;
  return `${adjusted.toFixed(unit === '' ? 0 : 2)}${unit ? ` ${unit}` : ''}`;
}

export function WeatherComparisonScorecard({ baseline, candidate }: WeatherComparisonScorecardProps) {
  const template = candidate ?? baseline;
  if (!template) return <p role="status">No comparison scores are available.</p>;
  return <div className="weather-table-scroll"><table className="weather-comparison-scorecard">
    <caption>Daily and event comparison scores against observed weather</caption>
    <thead><tr><th scope="col">Metric</th><th scope="col">Baseline</th><th scope="col">Candidate</th><th scope="col">Candidate change</th></tr></thead>
    <tbody>{comparisonScoreRows(template).map((row) => {
      const baselineValue = baseline?.[row.key];
      const candidateValue = candidate?.[row.key];
      const difference = baselineValue == null || candidateValue == null ? null : candidateValue - baselineValue;
      return <tr key={row.key}><th scope="row">{row.label}</th><td>{display(baselineValue, row.unit)}</td><td>{display(candidateValue, row.unit)}</td><td>{display(difference, row.unit)}</td></tr>;
    })}</tbody>
  </table></div>;
}
