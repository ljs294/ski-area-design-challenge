import type {
  MacroAirMassId,
  MonthlyClimateModelV1,
  WeatherCondition,
  WeatherConditionDiagnosticsV1,
  WeatherDiagnosticsV2,
  WeatherSimulationTuningV1,
} from '../../weather-engine/src/index.ts';
import { MACRO_AIR_MASSES, WEATHER_CONDITIONS } from './weatherLabViewModel.ts';

export type AdjustedConditionTransitionRow = (
  month: MonthlyClimateModelV1,
  macro: MacroAirMassId,
  priorCondition: WeatherCondition,
  tuning: WeatherSimulationTuningV1,
) => readonly number[];

export type AdjustedMacroTransitionRow = (
  month: MonthlyClimateModelV1,
  priorMacro: MacroAirMassId,
  tuning: WeatherSimulationTuningV1,
) => readonly number[];

export interface MarkovDiagnosticsPanelProps {
  observed?: WeatherConditionDiagnosticsV1;
  baseline?: WeatherDiagnosticsV2;
  candidate?: WeatherDiagnosticsV2;
  monthModel?: MonthlyClimateModelV1;
  tuning?: WeatherSimulationTuningV1;
  adjustedRow?: AdjustedConditionTransitionRow;
  adjustedMacroRow?: AdjustedMacroTransitionRow;
}

function percentage(value: number | undefined): string {
  return value == null || !Number.isFinite(value) ? 'Unavailable' : `${(value * 100).toFixed(1)}%`;
}

function mean(values: readonly number[] | undefined): string {
  if (!values || values.length === 0) return 'Unavailable';
  return `${(values.reduce((sum, value) => sum + value, 0) / values.length).toFixed(1)} h`;
}

function maximum(values: readonly number[] | undefined): string {
  return !values || values.length === 0 ? 'Unavailable' : `${Math.max(...values).toFixed(0)} h`;
}

function transitionKeys(...diagnostics: readonly (WeatherConditionDiagnosticsV1 | WeatherDiagnosticsV2 | undefined)[]): readonly string[] {
  const keys = new Set<string>();
  for (const entry of diagnostics) for (const [key, count] of Object.entries(entry?.transitionCounts ?? {})) if (count > 0) keys.add(key);
  return [...keys].sort();
}

export function MarkovDiagnosticsPanel({
  observed, baseline, candidate, monthModel, tuning, adjustedRow, adjustedMacroRow,
}: MarkovDiagnosticsPanelProps) {
  const transitions = transitionKeys(observed, baseline, candidate);
  const macroTransitions = [...new Set([
    ...Object.keys(baseline?.macroTransitionCounts ?? {}),
    ...Object.keys(candidate?.macroTransitionCounts ?? {}),
  ])].sort();
  return <section className="weather-markov-diagnostics">
    <h2>Markov-chain diagnostics</h2>
    <p>Occupancy and spell lengths describe generated outcomes. Transition matrices remain read-only.</p>
    <div className="weather-table-scroll"><table>
      <caption>Local-condition occupancy and spell length</caption>
      <thead><tr><th scope="col">Condition</th><th scope="col">Observed occupancy</th><th scope="col">Baseline occupancy</th><th scope="col">Candidate occupancy</th><th scope="col">Observed mean / max spell</th><th scope="col">Baseline mean / max spell</th><th scope="col">Candidate mean / max spell</th></tr></thead>
      <tbody>{WEATHER_CONDITIONS.map((condition) => <tr key={condition}>
        <th scope="row">{condition}</th>
        <td>{percentage(observed?.conditionOccupancy[condition])}</td>
        <td>{percentage(baseline?.conditionOccupancy[condition])}</td>
        <td>{percentage(candidate?.conditionOccupancy[condition])}</td>
        <td>{mean(observed?.spellLengths[condition])} / {maximum(observed?.spellLengths[condition])}</td>
        <td>{mean(baseline?.spellLengths[condition])} / {maximum(baseline?.spellLengths[condition])}</td>
        <td>{mean(candidate?.spellLengths[condition])} / {maximum(candidate?.spellLengths[condition])}</td>
      </tr>)}</tbody>
    </table></div>
    {(baseline || candidate) && <div className="weather-table-scroll"><table>
      <caption>Macro air-mass occupancy</caption>
      <thead><tr><th scope="col">Air mass</th><th scope="col">Baseline occupancy</th><th scope="col">Candidate occupancy</th><th scope="col">Baseline mean / max spell</th><th scope="col">Candidate mean / max spell</th></tr></thead>
      <tbody>{MACRO_AIR_MASSES.map((macro) => <tr key={macro}><th scope="row">{macro}</th><td>{percentage(baseline?.macroOccupancy[macro])}</td><td>{percentage(candidate?.macroOccupancy[macro])}</td><td>{mean(baseline?.macroSpellLengths[macro])} / {maximum(baseline?.macroSpellLengths[macro])}</td><td>{mean(candidate?.macroSpellLengths[macro])} / {maximum(candidate?.macroSpellLengths[macro])}</td></tr>)}</tbody>
    </table></div>}
    <details><summary>Generated macro transition counts ({macroTransitions.length})</summary>
      <div className="weather-table-scroll"><table>
        <caption>Baseline and candidate macro transition counts</caption>
        <thead><tr><th scope="col">Transition</th><th scope="col">Baseline</th><th scope="col">Candidate</th></tr></thead>
        <tbody>{macroTransitions.length === 0 ? <tr><td colSpan={3}>No macro transition counts are available.</td></tr> : macroTransitions.map((key) => <tr key={key}>
          <th scope="row">{key}</th><td>{baseline?.macroTransitionCounts[key] ?? 0}</td><td>{candidate?.macroTransitionCounts[key] ?? 0}</td>
        </tr>)}</tbody>
      </table></div>
    </details>
    <details><summary>Generated transition counts ({transitions.length})</summary>
      <div className="weather-table-scroll"><table>
        <caption>Observed, baseline, and candidate transition counts</caption>
        <thead><tr><th scope="col">Transition</th><th scope="col">Observed</th><th scope="col">Baseline</th><th scope="col">Candidate</th></tr></thead>
        <tbody>{transitions.length === 0 ? <tr><td colSpan={4}>No transition counts are available.</td></tr> : transitions.map((key) => <tr key={key}>
          <th scope="row">{key}</th><td>{observed?.transitionCounts[key] ?? 0}</td><td>{baseline?.transitionCounts[key] ?? 0}</td><td>{candidate?.transitionCounts[key] ?? 0}</td>
        </tr>)}</tbody>
      </table></div>
    </details>
    {monthModel && <details><summary>Read-only month {monthModel.month} transition matrices</summary>
      <div className="weather-table-scroll"><table>
        <caption>Macro air-mass transition probabilities. Each cell shows fitted{tuning && adjustedMacroRow ? ' / candidate-adjusted' : ''}.</caption>
        <thead><tr><th scope="col">From \ to</th>{monthModel.macro.states.map(({ id }) => <th scope="col" key={id}>{id}</th>)}</tr></thead>
        <tbody>{monthModel.macro.states.map(({ id: prior }, rowIndex) => {
          const fitted = monthModel.macro.transitionMatrix[rowIndex];
          const adjusted = tuning && adjustedMacroRow ? adjustedMacroRow(monthModel, prior, tuning) : null;
          return <tr key={prior}><th scope="row">{prior}</th>{monthModel.macro.states.map(({ id: target }, columnIndex) => <td key={target}>
            {percentage(fitted[columnIndex])}{adjusted && <> / {percentage(adjusted[columnIndex])}</>}
          </td>)}</tr>;
        })}</tbody>
      </table></div>
      {MACRO_AIR_MASSES.map((macro) => <details key={macro}><summary>{macro}</summary>
        <div className="weather-table-scroll"><table>
          <caption>{macro} transition probabilities. Each cell shows fitted{tuning && adjustedRow ? ' / candidate-adjusted' : ''}.</caption>
          <thead><tr><th scope="col">From \ to</th>{monthModel.local.states.map((condition) => <th scope="col" key={condition}>{condition}</th>)}</tr></thead>
          <tbody>{monthModel.local.states.map((prior, rowIndex) => {
            const fitted = monthModel.local.hourlyMatricesByMacro[macro][rowIndex] ?? monthModel.local.backoffRows;
            const adjusted = tuning && adjustedRow ? adjustedRow(monthModel, macro, prior, tuning) : null;
            return <tr key={prior}><th scope="row">{prior}</th>{monthModel.local.states.map((target, columnIndex) => <td key={target}>
              {percentage(fitted[columnIndex])}{adjusted && <> / {percentage(adjusted[columnIndex])}</>}
            </td>)}</tr>;
          })}</tbody>
        </table></div>
      </details>)}
    </details>}
  </section>;
}
