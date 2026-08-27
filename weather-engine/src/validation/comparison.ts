import type {
  ForecastIssueV1, HistoricalWeatherSeriesV1, MacroAirMassId, MonthlyComparisonV1,
  SimulatedWeatherHourV1, WeatherCondition, WeatherDiagnosticsV1, WeatherLabResultV1,
  WeatherLabRunRequestV1, WeatherMetricComparisonV1, WeatherMonth, WeatherVariable,
  WeatherEngineSnapshotV2,
} from '../contracts.ts';
import { sha256Hex } from '../engine/canonical.ts';

const CONDITIONS: readonly WeatherCondition[] = ['clear', 'partly-cloudy', 'overcast', 'flurries', 'snow', 'heavy-snow', 'mixed', 'freezing-rain', 'rain'];
const MACROS: readonly MacroAirMassId[] = ['arctic', 'continental-polar', 'maritime-polar', 'warm-wet', 'frontal'];

type NumericVariable = Exclude<WeatherVariable, 'condition'>;
const METRICS: readonly { variable: NumericVariable; aggregate: 'mean' | 'sum'; tolerance: number }[] = [
  { variable: 'temperatureC', aggregate: 'mean', tolerance: 3 },
  { variable: 'dewPointC', aggregate: 'mean', tolerance: 4 },
  { variable: 'pressureHpa', aggregate: 'mean', tolerance: 8 },
  { variable: 'relativeHumidityPct', aggregate: 'mean', tolerance: 12 },
  { variable: 'precipitationMm', aggregate: 'sum', tolerance: 0.35 },
  { variable: 'snowfallCm', aggregate: 'sum', tolerance: 0.4 },
  { variable: 'windSpeedKph', aggregate: 'mean', tolerance: 8 },
  { variable: 'cloudCoverPct', aggregate: 'mean', tolerance: 18 },
  { variable: 'visibilityKm', aggregate: 'mean', tolerance: 8 },
];

function aggregate(values: readonly number[], kind: 'mean' | 'sum'): number | null {
  if (!values.length) return null;
  const total = values.reduce((sum, value) => sum + value, 0);
  return kind === 'sum' ? total : total / values.length;
}

function metric(variable: NumericVariable, kind: 'mean' | 'sum', tolerance: number,
  simulated: readonly SimulatedWeatherHourV1[], observedByAt: ReadonlyMap<string, HistoricalWeatherSeriesV1['hours'][number]>): WeatherMetricComparisonV1 {
  const pairs = simulated.flatMap((hour) => {
    const value = observedByAt.get(hour.at)?.[variable];
    return typeof value === 'number' && Number.isFinite(value) && observedByAt.get(hour.at)?.quality[variable] !== 'suspect'
      ? [[hour[variable] as number, value] as const] : [];
  });
  const simulatedValue = aggregate(pairs.map(([value]) => value), kind);
  const observedValue = aggregate(pairs.map(([, value]) => value), kind);
  if (pairs.length < Math.min(168, Math.max(24, simulated.length * 0.25)) || simulatedValue == null || observedValue == null) {
    return { variable, metric: kind, simulated: null, observed: null, difference: null, status: 'unavailable' };
  }
  const difference = simulatedValue - observedValue;
  const limit = kind === 'sum' ? Math.max(1, Math.abs(observedValue) * tolerance) : tolerance;
  return { variable, metric: kind, simulated: simulatedValue, observed: observedValue, difference,
    status: Math.abs(difference) <= limit ? 'pass' : 'warn' };
}

function comparisons(simulated: readonly SimulatedWeatherHourV1[], observed: HistoricalWeatherSeriesV1,
  month?: WeatherMonth): WeatherMetricComparisonV1[] {
  const simulatedHours = month ? simulated.filter((hour) => Number(hour.localDateTime.slice(5, 7)) === month) : simulated;
  const observedHours = month ? observed.hours.filter((hour) => Number(hour.localDateTime.slice(5, 7)) === month) : observed.hours;
  const overlap = new Map(observedHours.map((hour) => [hour.at, hour]));
  const result = METRICS.map((entry) => metric(entry.variable, entry.aggregate, entry.tolerance, simulatedHours, overlap));
  const thresholdMetric = (name: 'freezeThawCycles' | 'snowmakingHours' | 'stormHours', simulatedCount: number,
    observedCount: number, samples: number): WeatherMetricComparisonV1 => samples < 168
    ? { variable: name, metric: 'count', simulated: null, observed: null, difference: null, status: 'unavailable' }
    : { variable: name, metric: 'count', simulated: simulatedCount, observed: observedCount,
      difference: simulatedCount - observedCount, status: Math.abs(simulatedCount - observedCount) <= Math.max(5, observedCount * 0.35) ? 'pass' : 'warn' };
  const paired = simulatedHours.flatMap((hour) => overlap.has(hour.at) ? [[hour, overlap.get(hour.at)!] as const] : []);
  result.push(thresholdMetric('snowmakingHours', paired.filter(([hour]) => hour.wetBulbC <= -2).length,
    paired.filter(([, hour]) => hour.wetBulbC != null && hour.wetBulbC <= -2).length, paired.length));
  result.push(thresholdMetric('stormHours', paired.filter(([hour]) => hour.precipitationMm >= 1).length,
    paired.filter(([, hour]) => hour.precipitationMm != null && hour.precipitationMm >= 1).length, paired.length));
  return result;
}

export function weatherDiagnostics(hours: readonly SimulatedWeatherHourV1[]): WeatherDiagnosticsV1 {
  const conditionCounts = Object.fromEntries(CONDITIONS.map((condition) => [condition, 0])) as Record<WeatherCondition, number>;
  const macroCounts = Object.fromEntries(MACROS.map((macro) => [macro, 0])) as Record<MacroAirMassId, number>;
  const transitions: Record<string, number> = {};
  const spells = Object.fromEntries(CONDITIONS.map((condition) => [condition, [] as number[]])) as Record<WeatherCondition, number[]>;
  let current: WeatherCondition | undefined;
  let length = 0;
  for (const hour of hours) {
    conditionCounts[hour.condition] += 1; macroCounts[hour.macroAirMass] += 1;
    if (current === hour.condition) length += 1;
    else {
      if (current) { spells[current].push(length); transitions[`${current}->${hour.condition}`] = (transitions[`${current}->${hour.condition}`] ?? 0) + 1; }
      current = hour.condition; length = 1;
    }
  }
  if (current) spells[current].push(length);
  const divisor = Math.max(1, hours.length);
  return { conditionOccupancy: Object.fromEntries(CONDITIONS.map((key) => [key, conditionCounts[key] / divisor])) as Record<WeatherCondition, number>,
    macroOccupancy: Object.fromEntries(MACROS.map((key) => [key, macroCounts[key] / divisor])) as Record<MacroAirMassId, number>,
    transitionCounts: transitions, spellLengths: spells };
}

export function compareWeatherSeries(run: WeatherLabRunRequestV1, simulated: readonly SimulatedWeatherHourV1[],
  observed: HistoricalWeatherSeriesV1, forecasts: readonly ForecastIssueV1[], finalSnapshot: WeatherEngineSnapshotV2): WeatherLabResultV1 {
  const monthly: MonthlyComparisonV1[] = Array.from({ length: 12 }, (_, index) => ({ month: (index + 1) as WeatherMonth,
    metrics: comparisons(simulated, observed, (index + 1) as WeatherMonth) }));
  const truthRows = simulated.map((hour) => [hour.at, hour.localDateTime, hour.utcOffsetMinutes, hour.fold,
    hour.macroAirMass, hour.condition, hour.hazards, hour.temperatureC, hour.dewPointC, hour.pressureHpa,
    hour.relativeHumidityPct, hour.wetBulbC, hour.precipitationMm, hour.precipitationPhase, hour.snowfallCm,
    hour.windSpeedKph, hour.windDirectionDeg, hour.windGustKph, hour.shortwaveRadiationWm2, hour.cloudCoverPct,
    hour.visibilityKm, hour.bands ? [hour.bands.base, hour.bands.mid, hour.bands.summit].map((band) =>
      [band.elevationM, band.temperatureC, band.wetBulbC, band.pressureHpa, band.precipitationMm, band.snowfallCm, band.windSpeedKph]) : null]);
  // Every row is a fixed-order tuple of JSON primitives/arrays, so native
  // serialization is already canonical and avoids recursively sorting keys.
  const truthHash = sha256Hex(JSON.stringify(truthRows));
  const annual = comparisons(simulated, observed); const diagnostics = weatherDiagnostics(simulated);
  const warnings = Object.entries(observed.completeness).filter(([, value]) => value < 0.8).map(([key]) => `Observed ${key} completeness is below 80%`);
  // Hash only comparison identity and outputs. Including the embedded truth,
  // observations, and all forecasts would hash the same multi-megabyte inputs
  // twice and make chart reruns needlessly expensive.
  const comparisonHash = sha256Hex({ version: 1, runIdentityHash: finalSnapshot.runIdentityHash, truthHash,
    observationHash: observed.observationHash, monthly, annual, diagnostics, warnings });
  return { version: 1, runIdentityHash: finalSnapshot.runIdentityHash, truthHash, comparisonHash, run,
    simulated, observed, forecasts, monthly, annual, diagnostics, warnings, finalSnapshot };
}
