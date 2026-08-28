import { useId } from 'react';
import { alignDailyComparison, MACRO_AIR_MASSES, WEATHER_CONDITIONS } from './weatherLabViewModel.ts';
import type { DailyComparisonSeries } from './weatherLabViewModel.ts';

export interface DailyStateRibbonsProps {
  series: DailyComparisonSeries;
  month?: number;
}

const CONDITION_COLORS = Object.freeze({
  clear: '#e6bd56', 'partly-cloudy': '#9bb5bd', overcast: '#607683', flurries: '#b8dcef',
  snow: '#77bce2', 'heavy-snow': '#3488bd', mixed: '#907bb5', 'freezing-rain': '#c76fa6', rain: '#4b78cf',
});
const MACRO_COLORS = Object.freeze({
  arctic: '#7dd9ef', 'continental-polar': '#709fc9', 'maritime-polar': '#5e8b9a',
  'warm-wet': '#d98166', frontal: '#c69b54',
});
const WIDTH = 900;
const LABEL_WIDTH = 100;
const ROW_HEIGHT = 24;

function words(value: string): string {
  return value.split('-').map((part) => `${part.slice(0, 1).toUpperCase()}${part.slice(1)}`).join(' ');
}

export function DailyStateRibbons({ series, month }: DailyStateRibbonsProps) {
  const conditionTitle = useId(); const macroTitle = useId();
  const rows = alignDailyComparison(series, month);
  const cellWidth = (WIDTH - LABEL_WIDTH) / Math.max(1, rows.length);
  const conditionSources = ['observed', 'baseline', 'candidate'] as const;
  const macroSources = ['baseline', 'candidate'] as const;
  if (rows.length === 0) return <p role="status">No daily state values are available for this period.</p>;
  return <section className="weather-state-ribbons">
    <h2>Daily condition and macro-state ribbons</h2>
    <p>Each cell shows the dominant reconciled state for one local calendar date.</p>
    <svg viewBox={`0 0 ${WIDTH} ${conditionSources.length * ROW_HEIGHT + 34}`} role="img" aria-labelledby={conditionTitle}>
      <title id={conditionTitle}>Dominant condition ribbons for observed, baseline, and candidate weather</title>
      {conditionSources.map((source, sourceIndex) => <g key={source}>
        <text x={LABEL_WIDTH - 8} y={18 + sourceIndex * ROW_HEIGHT} textAnchor="end" fontSize="11">{words(source)}</text>
        {rows.map((row, index) => {
          const state = row[source]?.dominantCondition;
          return <rect key={row.localDate} x={LABEL_WIDTH + index * cellWidth} y={7 + sourceIndex * ROW_HEIGHT}
            width={Math.max(1, cellWidth + .2)} height="15" fill={state ? CONDITION_COLORS[state] : '#263847'}>
            <title>{row.localDate}: {state ? words(state) : 'Unavailable'}</title>
          </rect>;
        })}
      </g>)}
    </svg>
    <ul className="weather-ribbon-legend" aria-label="Condition colors">{WEATHER_CONDITIONS.map((condition) =>
      <li key={condition}><i style={{ backgroundColor: CONDITION_COLORS[condition] }}/>{words(condition)}</li>)}</ul>
    <svg viewBox={`0 0 ${WIDTH} ${macroSources.length * ROW_HEIGHT + 34}`} role="img" aria-labelledby={macroTitle}>
      <title id={macroTitle}>Dominant macro air-mass ribbons for baseline and candidate weather</title>
      {macroSources.map((source, sourceIndex) => <g key={source}>
        <text x={LABEL_WIDTH - 8} y={18 + sourceIndex * ROW_HEIGHT} textAnchor="end" fontSize="11">{words(source)}</text>
        {rows.map((row, index) => {
          const state = row[source]?.dominantMacro;
          return <rect key={row.localDate} x={LABEL_WIDTH + index * cellWidth} y={7 + sourceIndex * ROW_HEIGHT}
            width={Math.max(1, cellWidth + .2)} height="15" fill={state ? MACRO_COLORS[state] : '#263847'}>
            <title>{row.localDate}: {state ? words(state) : 'Unavailable'}</title>
          </rect>;
        })}
      </g>)}
    </svg>
    <ul className="weather-ribbon-legend" aria-label="Macro-state colors">{MACRO_AIR_MASSES.map((macro) =>
      <li key={macro}><i style={{ backgroundColor: MACRO_COLORS[macro] }}/>{words(macro)}</li>)}</ul>
  </section>;
}
