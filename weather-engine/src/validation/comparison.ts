import type {
  ForecastIssueV1, HistoricalWeatherSeriesV1, MacroAirMassId, MonthlyComparisonV1,
  LocationClimateModelV1, SimulatedWeatherHourV1, WeatherComparisonScoresV1, WeatherCondition,
  WeatherConditionDiagnosticsV1, WeatherDiagnosticsV1, WeatherDiagnosticsV2, WeatherEngineSnapshotV2, WeatherEventV1,
  WeatherLabResultV1, WeatherLabResultV2, WeatherLabRunRequestV1, WeatherLabRunRequestV2,
  WeatherMetricComparisonV1, WeatherMonth, WeatherVariable,
} from '../contracts.ts';
import { sha256Hex } from '../engine/canonical.ts';
import { summarizeObservedWeatherDays, summarizeSimulatedWeatherDays } from './daily.ts';
import { detectObservedWeatherEvents, detectSimulatedWeatherEvents,
  fallbackWeatherEventThresholds } from './events.ts';

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

function dailySumMetric(
  variable: 'precipitationMm' | 'snowfallCm', tolerance: number,
  simulatedDays: readonly ReturnType<typeof summarizeSimulatedWeatherDays>[number][],
  observedDays: readonly ReturnType<typeof summarizeObservedWeatherDays>[number][], month?: WeatherMonth,
): WeatherMetricComparisonV1 {
  const observedByDate = new Map(observedDays.map((day) => [day.localDate, day]));
  const pairs = simulatedDays.flatMap((day) => {
    if (month && Number(day.localDate.slice(5, 7)) !== month) return [];
    const observed = observedByDate.get(day.localDate); const simulatedValue = day[variable]; const observedValue = observed?.[variable];
    return simulatedValue != null && observedValue != null ? [[simulatedValue, observedValue] as const] : [];
  });
  const minimumDays = month ? 7 : 30;
  if (pairs.length < minimumDays) {
    return { variable, metric: 'sum', simulated: null, observed: null, difference: null, status: 'unavailable' };
  }
  const simulated = pairs.reduce((sum, [value]) => sum + value, 0);
  const observed = pairs.reduce((sum, [, value]) => sum + value, 0);
  const difference = simulated - observed;
  return { variable, metric: 'sum', simulated, observed, difference,
    status: Math.abs(difference) <= Math.max(1, Math.abs(observed) * tolerance) ? 'pass' : 'warn' };
}

function comparisonsV2(
  simulated: readonly SimulatedWeatherHourV1[], observed: HistoricalWeatherSeriesV1,
  simulatedDays: readonly ReturnType<typeof summarizeSimulatedWeatherDays>[number][],
  observedDays: readonly ReturnType<typeof summarizeObservedWeatherDays>[number][], month?: WeatherMonth,
): WeatherMetricComparisonV1[] {
  const precipitation = dailySumMetric('precipitationMm', 0.35, simulatedDays, observedDays, month);
  const snowfall = dailySumMetric('snowfallCm', 0.4, simulatedDays, observedDays, month);
  return comparisons(simulated, observed, month).map((entry) => entry.variable === 'precipitationMm'
    ? precipitation : entry.variable === 'snowfallCm' ? snowfall : entry);
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

export function weatherDiagnosticsV2(hours: readonly SimulatedWeatherHourV1[]): WeatherDiagnosticsV2 {
  const base = weatherDiagnostics(hours);
  const transitions: Record<string, number> = {};
  const spells = Object.fromEntries(MACROS.map((macro) => [macro, [] as number[]])) as Record<MacroAirMassId, number[]>;
  let current: MacroAirMassId | undefined;
  let length = 0;
  for (const hour of hours) {
    if (current === hour.macroAirMass) {
      length += 1;
      continue;
    }
    if (current) {
      spells[current].push(length);
      transitions[`${current}->${hour.macroAirMass}`] = (transitions[`${current}->${hour.macroAirMass}`] ?? 0) + 1;
    }
    current = hour.macroAirMass;
    length = 1;
  }
  if (current) spells[current].push(length);
  return { ...base, macroTransitionCounts: transitions, macroSpellLengths: spells };
}

export function observedWeatherDiagnostics(observed: HistoricalWeatherSeriesV1): WeatherConditionDiagnosticsV1 {
  const conditionCounts = Object.fromEntries(CONDITIONS.map((condition) => [condition, 0])) as Record<WeatherCondition, number>;
  const transitions: Record<string, number> = {};
  const spells = Object.fromEntries(CONDITIONS.map((condition) => [condition, [] as number[]])) as Record<WeatherCondition, number[]>;
  let current: WeatherCondition | undefined; let length = 0; let previousAt: number | undefined;
  const finish = () => { if (current) spells[current].push(length); current = undefined; length = 0; };
  for (const hour of [...observed.hours].sort((left, right) => left.at.localeCompare(right.at))) {
    const accepted = hour.condition != null && hour.quality.condition !== 'missing' && hour.quality.condition !== 'suspect';
    const at = new Date(hour.at).getTime();
    if (!accepted || (previousAt != null && at - previousAt !== 3_600_000)) { finish(); previousAt = accepted ? at : undefined; }
    if (!accepted) continue;
    conditionCounts[hour.condition!] += 1;
    if (current === hour.condition) length += 1;
    else {
      if (current) {
        spells[current].push(length);
        transitions[`${current}->${hour.condition}`] = (transitions[`${current}->${hour.condition}`] ?? 0) + 1;
      }
      current = hour.condition!; length = 1;
    }
    previousAt = at;
  }
  finish();
  const total = Object.values(conditionCounts).reduce((sum, value) => sum + value, 0);
  return {
    conditionOccupancy: Object.fromEntries(CONDITIONS.map((condition) =>
      [condition, total ? conditionCounts[condition] / total : 0])) as Record<WeatherCondition, number>,
    transitionCounts: transitions, spellLengths: spells,
  };
}

function truthHashFor(simulated: readonly SimulatedWeatherHourV1[]): string {
  const truthRows = simulated.map((hour) => [hour.at, hour.localDateTime, hour.utcOffsetMinutes, hour.fold,
    hour.macroAirMass, hour.condition, hour.hazards, hour.temperatureC, hour.dewPointC, hour.pressureHpa,
    hour.relativeHumidityPct, hour.wetBulbC, hour.precipitationMm, hour.precipitationPhase, hour.snowfallCm,
    hour.windSpeedKph, hour.windDirectionDeg, hour.windGustKph, hour.shortwaveRadiationWm2, hour.cloudCoverPct,
    hour.visibilityKm, hour.bands ? [hour.bands.base, hour.bands.mid, hour.bands.summit].map((band) =>
      [band.elevationM, band.temperatureC, band.wetBulbC, band.pressureHpa, band.precipitationMm, band.snowfallCm, band.windSpeedKph]) : null]);
  return sha256Hex(JSON.stringify(truthRows));
}

export function compareWeatherSeries(run: WeatherLabRunRequestV1, simulated: readonly SimulatedWeatherHourV1[],
  observed: HistoricalWeatherSeriesV1, forecasts: readonly ForecastIssueV1[], finalSnapshot: WeatherEngineSnapshotV2): WeatherLabResultV1 {
  const monthly: MonthlyComparisonV1[] = Array.from({ length: 12 }, (_, index) => ({ month: (index + 1) as WeatherMonth,
    metrics: comparisons(simulated, observed, (index + 1) as WeatherMonth) }));
  // Every row is a fixed-order tuple of JSON primitives/arrays, so native
  // serialization is already canonical and avoids recursively sorting keys.
  const truthHash = truthHashFor(simulated);
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

function pairedDailyMetric(
  simulated: readonly ReturnType<typeof summarizeSimulatedWeatherDays>[number][],
  observed: readonly ReturnType<typeof summarizeObservedWeatherDays>[number][],
  value: (day: ReturnType<typeof summarizeSimulatedWeatherDays>[number]) => number | null,
): { bias: number | null; mae: number | null } {
  const observedByDate = new Map(observed.map((day) => [day.localDate, day]));
  const differences = simulated.flatMap((day) => {
    const observedDay = observedByDate.get(day.localDate); const left = value(day);
    const right = observedDay ? value(observedDay) : null;
    return left != null && right != null && Number.isFinite(left) && Number.isFinite(right) ? [left - right] : [];
  });
  if (!differences.length) return { bias: null, mae: null };
  return {
    bias: differences.reduce((sum, difference) => sum + difference, 0) / differences.length,
    mae: differences.reduce((sum, difference) => sum + Math.abs(difference), 0) / differences.length,
  };
}

type Interval = readonly [number, number];

function mergedIntervals(events: readonly WeatherEventV1[]): Interval[] {
  const sorted = events.map((event): Interval => [new Date(event.startsAt).getTime(), new Date(event.endsAt).getTime()])
    .filter(([start, end]) => Number.isFinite(start) && end > start).sort((left, right) => left[0] - right[0]);
  const merged: Array<[number, number]> = [];
  for (const interval of sorted) {
    const previous = merged.at(-1);
    if (previous && interval[0] <= previous[1]) previous[1] = Math.max(previous[1], interval[1]);
    else merged.push([interval[0], interval[1]]);
  }
  return merged;
}

function intervalLength(intervals: readonly Interval[]): number {
  return intervals.reduce((sum, [start, end]) => sum + end - start, 0);
}

function overlapMillis(left: readonly Interval[], right: readonly Interval[]): number {
  let leftIndex = 0; let rightIndex = 0; let total = 0;
  while (leftIndex < left.length && rightIndex < right.length) {
    const [leftStart, leftEnd] = left[leftIndex]; const [rightStart, rightEnd] = right[rightIndex];
    total += Math.max(0, Math.min(leftEnd, rightEnd) - Math.max(leftStart, rightStart));
    if (leftEnd <= rightEnd) leftIndex += 1; else rightIndex += 1;
  }
  return total;
}

function eventOverlap(left: WeatherEventV1, right: WeatherEventV1): number {
  const leftStart = new Date(left.startsAt).getTime(); const leftEnd = new Date(left.endsAt).getTime();
  const rightStart = new Date(right.startsAt).getTime(); const rightEnd = new Date(right.endsAt).getTime();
  return Math.max(0, Math.min(leftEnd, rightEnd) - Math.max(leftStart, rightStart));
}

function pairedStorms(
  simulatedEvents: readonly WeatherEventV1[], observedEvents: readonly WeatherEventV1[],
): readonly (readonly [WeatherEventV1, WeatherEventV1])[] {
  const available = simulatedEvents.filter((event) => event.type === 'storm');
  const used = new Set<string>(); const pairs: Array<readonly [WeatherEventV1, WeatherEventV1]> = [];
  for (const observed of observedEvents.filter((event) => event.type === 'storm')) {
    const candidate = available.filter((event) => !used.has(event.id))
      .map((event) => ({ event, overlap: eventOverlap(event, observed) }))
      .filter(({ overlap }) => overlap > 0)
      .sort((left, right) => right.overlap - left.overlap || left.event.startsAt.localeCompare(right.event.startsAt))[0]?.event;
    if (!candidate) continue;
    used.add(candidate.id); pairs.push([candidate, observed]);
  }
  return pairs;
}

export function weatherComparisonScores(
  simulatedDays: readonly ReturnType<typeof summarizeSimulatedWeatherDays>[number][],
  observedDays: readonly ReturnType<typeof summarizeObservedWeatherDays>[number][],
  simulatedEvents: readonly WeatherEventV1[], observedEvents: readonly WeatherEventV1[],
): WeatherComparisonScoresV1 {
  const temperature = pairedDailyMetric(simulatedDays, observedDays, (day) => day.temperatureC?.mean ?? null);
  const wetBulb = pairedDailyMetric(simulatedDays, observedDays, (day) => day.wetBulbC?.mean ?? null);
  const precipitation = pairedDailyMetric(simulatedDays, observedDays, (day) => day.precipitationMm);
  const observedByDate = new Map(observedDays.map((day) => [day.localDate, day]));
  const conditionPairs = simulatedDays.flatMap((day) => {
    const observed = observedByDate.get(day.localDate);
    return day.dominantCondition && observed?.dominantCondition
      ? [day.dominantCondition === observed.dominantCondition ? 1 : 0] : [];
  });
  let intersection = 0; let union = 0;
  for (const type of ['storm', 'cold-snap', 'warm-up', 'dry-spell'] as const) {
    const simulatedIntervals = mergedIntervals(simulatedEvents.filter((event) => event.type === type));
    const observedIntervals = mergedIntervals(observedEvents.filter((event) => event.type === type));
    const overlap = overlapMillis(simulatedIntervals, observedIntervals);
    intersection += overlap; union += intervalLength(simulatedIntervals) + intervalLength(observedIntervals) - overlap;
  }
  const storms = pairedStorms(simulatedEvents, observedEvents);
  const classifiedStorms = storms.filter(([, observed]) => observed.stormStyle != null
    && observed.styleConfidence !== 'low' && observed.styleConfidence != null);
  return {
    temperatureMeanBiasC: temperature.bias, temperatureMeanMaeC: temperature.mae,
    wetBulbMeanBiasC: wetBulb.bias, wetBulbMeanMaeC: wetBulb.mae,
    precipitationBiasMm: precipitation.bias, precipitationMaeMm: precipitation.mae,
    dominantConditionAgreement: conditionPairs.length
      ? conditionPairs.reduce((sum, value) => sum + value, 0) / conditionPairs.length : null,
    eventCountDifference: simulatedEvents.length - observedEvents.length,
    eventDurationDifferenceHours: simulatedEvents.reduce((sum, event) => sum + event.durationHours, 0)
      - observedEvents.reduce((sum, event) => sum + event.durationHours, 0),
    eventOverlapScore: union > 0 ? intersection / union : 1,
    stormSeverityAgreement: storms.length
      ? storms.filter(([simulated, observed]) => simulated.severity === observed.severity).length / storms.length : null,
    stormStyleAgreement: classifiedStorms.length
      ? classifiedStorms.filter(([simulated, observed]) => simulated.stormStyle === observed.stormStyle).length / classifiedStorms.length : null,
  };
}

/** Builds the expanded Lab result without altering the legacy V1 comparison path. */
export function compareWeatherSeriesV2(
  run: WeatherLabRunRequestV2, simulated: readonly SimulatedWeatherHourV1[],
  observed: HistoricalWeatherSeriesV1, forecasts: readonly ForecastIssueV1[],
  finalSnapshot: WeatherEngineSnapshotV2, model?: LocationClimateModelV1,
): WeatherLabResultV2 {
  const eventThresholds = model?.eventThresholds
    ?? fallbackWeatherEventThresholds(model?.trainingPeriod.years ?? []);
  if (eventThresholds.fittedFromYears.includes(run.validationYear)) {
    throw new Error('Validation year leaked into weather event thresholds');
  }
  const simulatedEvents = detectSimulatedWeatherEvents(simulated, eventThresholds);
  const observedEvents = detectObservedWeatherEvents(observed, eventThresholds);
  const simulatedDays = summarizeSimulatedWeatherDays(simulated, simulatedEvents);
  const observedDays = summarizeObservedWeatherDays(observed, observedEvents);
  const monthly: MonthlyComparisonV1[] = Array.from({ length: 12 }, (_, index) => ({
    month: (index + 1) as WeatherMonth,
    metrics: comparisonsV2(simulated, observed, simulatedDays, observedDays, (index + 1) as WeatherMonth),
  }));
  const annual = comparisonsV2(simulated, observed, simulatedDays, observedDays);
  const diagnostics = weatherDiagnosticsV2(simulated);
  const observedDiagnostics = observedWeatherDiagnostics(observed);
  const scores = weatherComparisonScores(simulatedDays, observedDays, simulatedEvents, observedEvents);
  const truthHash = truthHashFor(simulated);
  const warnings = Object.entries(observed.completeness).filter(([, value]) => value < 0.8)
    .map(([key]) => `Observed ${key} completeness is below 80%`);
  if (!model?.eventThresholds) warnings.push('Event thresholds were unavailable; deterministic fallback thresholds were used');
  const comparisonHash = sha256Hex({
    version: 2, runIdentityHash: finalSnapshot.runIdentityHash, truthHash,
    observationHash: observed.observationHash, monthly, annual, diagnostics, observedDiagnostics,
    daily: { simulated: simulatedDays, observed: observedDays },
    events: { simulated: simulatedEvents, observed: observedEvents }, eventThresholds, scores, warnings,
  });
  return {
    version: 2, runIdentityHash: finalSnapshot.runIdentityHash, truthHash, comparisonHash, run,
    simulated, observed, forecasts, monthly, annual, diagnostics, warnings, finalSnapshot,
    daily: { simulated: simulatedDays, observed: observedDays },
    events: { simulated: simulatedEvents, observed: observedEvents }, observedDiagnostics,
    eventThresholds, scores,
  };
}
