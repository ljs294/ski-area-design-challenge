import type {
  HistoricalWeatherSeriesV1, MacroAirMassId, ObservedWeatherHourV1, PrecipitationPhase,
  SimulatedWeatherHourV1, StormStyle, StormStyleConfidence, WeatherEventSeverity,
  WeatherEventThresholdModelV1, WeatherEventThresholdMonthV1, WeatherEventV1, WeatherMonth,
} from '../contracts.ts';
import { quantize } from '../engine/psychrometrics.ts';

const MEASURABLE_PRECIPITATION_MM = 0.005 as const;
const STORM_DRY_GAP_HOURS = 3;
const PHASES: readonly PrecipitationPhase[] = ['none', 'rain', 'mixed', 'snow', 'freezing-rain'];

interface EventHour {
  at: string;
  localDateTime: string;
  temperatureC: number | null;
  pressureHpa: number | null;
  precipitationMm: number | null;
  precipitationPhase: PrecipitationPhase | null;
  snowfallCm: number | null;
  windSpeedKph: number | null;
  windGustKph: number | null;
  macroAirMass: MacroAirMassId | null;
  frontal: boolean;
}

interface Span {
  first: number;
  last: number;
}

interface StormSample extends Span {
  total: number;
  peak: number;
  duration: number;
  month: WeatherMonth;
}

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function monthFor(hour: EventHour): WeatherMonth {
  return Number(hour.localDateTime.slice(5, 7)) as WeatherMonth;
}

function elapsedHours(left: EventHour, right: EventHour): number {
  return (new Date(right.at).getTime() - new Date(left.at).getTime()) / 3_600_000;
}

function contiguous(left: EventHour | undefined, right: EventHour | undefined): boolean {
  return !!left && !!right && elapsedHours(left, right) === 1;
}

function exclusiveEnd(hour: EventHour): string {
  return new Date(new Date(hour.at).getTime() + 3_600_000).toISOString();
}

function percentile(values: readonly number[], probability: number, fallback: number): number {
  if (!values.length) return fallback;
  const sorted = [...values].sort((left, right) => left - right);
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position); const upper = Math.ceil(position);
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function fittedPercentile(value: number, p90: number, p98: number): number {
  if (value <= 0) return 0;
  if (value < p90) return 90 * value / Math.max(p90, 1e-9);
  if (value < p98) return 90 + 8 * (value - p90) / Math.max(p98 - p90, 1e-9);
  return Math.min(100, 98 + 2 * (value - p98) / Math.max(p98, 1));
}

function empiricalPercentile(value: number, sortedValues: readonly number[] | undefined): number | null {
  if (!sortedValues?.length) return null;
  let lower = 0; let upper = sortedValues.length;
  while (lower < upper) {
    const middle = Math.floor((lower + upper) / 2);
    if (sortedValues[middle] <= value) lower = middle + 1;
    else upper = middle;
  }
  return 100 * lower / sortedValues.length;
}

function eventPercentile(value: number, sortedValues: readonly number[] | undefined,
  p90: number, p98: number): number {
  return empiricalPercentile(value, sortedValues) ?? fittedPercentile(value, p90, p98);
}

function sortedQuantized(values: readonly number[], decimalPlaces: number): readonly number[] {
  return values.map((value) => quantize(value, decimalPlaces)).sort((left, right) => left - right);
}

function severity(percentileValue: number): WeatherEventSeverity {
  return percentileValue >= 98 ? 'major' : percentileValue >= 90 ? 'notable' : 'minor';
}

function phaseRecord(): Record<PrecipitationPhase, number> {
  return Object.fromEntries(PHASES.map((phase) => [phase, 0])) as Record<PrecipitationPhase, number>;
}

function acceptedObserved(hour: ObservedWeatherHourV1, key: keyof ObservedWeatherHourV1): number | null {
  const value = hour[key];
  const quality = hour.quality[key as keyof ObservedWeatherHourV1 & keyof ObservedWeatherHourV1['quality']];
  return finite(value) && quality !== 'missing' && quality !== 'suspect' ? value : null;
}

function observedRows(hours: readonly ObservedWeatherHourV1[]): EventHour[] {
  return hours.map((hour) => ({
    at: hour.at, localDateTime: hour.localDateTime,
    temperatureC: acceptedObserved(hour, 'temperatureC'),
    pressureHpa: acceptedObserved(hour, 'pressureHpa'),
    precipitationMm: acceptedObserved(hour, 'precipitationMm'),
    precipitationPhase: hour.quality.precipitationMm === 'missing' || hour.quality.precipitationMm === 'suspect'
      ? null : hour.precipitationPhase,
    snowfallCm: acceptedObserved(hour, 'snowfallCm'),
    windSpeedKph: acceptedObserved(hour, 'windSpeedKph'),
    windGustKph: acceptedObserved(hour, 'windGustKph'),
    macroAirMass: null, frontal: hour.hazards.includes('frontal-passage'),
  })).sort((left, right) => left.at.localeCompare(right.at));
}

function simulatedRows(hours: readonly SimulatedWeatherHourV1[]): EventHour[] {
  return hours.map((hour) => ({
    at: hour.at, localDateTime: hour.localDateTime, temperatureC: hour.temperatureC,
    pressureHpa: hour.pressureHpa, precipitationMm: hour.precipitationMm,
    precipitationPhase: hour.precipitationPhase, snowfallCm: hour.snowfallCm,
    windSpeedKph: hour.windSpeedKph, windGustKph: hour.windGustKph,
    macroAirMass: hour.macroAirMass, frontal: hour.hazards.includes('frontal-passage'),
  })).sort((left, right) => left.at.localeCompare(right.at));
}

function stormSamples(rows: readonly EventHour[], measurable: number, dryGapHours: number): StormSample[] {
  const samples: StormSample[] = [];
  let index = 0;
  while (index < rows.length) {
    if (!finite(rows[index].precipitationMm) || rows[index].precipitationMm! <= measurable) { index += 1; continue; }
    const first = index;
    let lastWet = index;
    let total = rows[index].precipitationMm!;
    let peak = total;
    let cursor = index + 1;
    let dry = 0;
    while (cursor < rows.length && contiguous(rows[cursor - 1], rows[cursor]) && finite(rows[cursor].precipitationMm)) {
      const water = rows[cursor].precipitationMm!;
      if (water > measurable) {
        lastWet = cursor; total += water; peak = Math.max(peak, water); dry = 0;
      } else {
        dry += 1;
        if (dry > dryGapHours) break;
      }
      cursor += 1;
    }
    const duration = Math.round(elapsedHours(rows[first], rows[lastWet]) + 1);
    samples.push({ first, last: lastWet, total, peak, duration, month: monthFor(rows[first]) });
    index = Math.max(first + 1, lastWet + 1);
  }
  return samples;
}

function drySamples(rows: readonly EventHour[], measurable: number): Array<Span & { duration: number; month: WeatherMonth }> {
  const result: Array<Span & { duration: number; month: WeatherMonth }> = [];
  let first = -1;
  const finish = (last: number) => {
    if (first >= 0 && last >= first) result.push({ first, last, duration: last - first + 1, month: monthFor(rows[first]) });
    first = -1;
  };
  for (let index = 0; index < rows.length; index += 1) {
    const validDry = finite(rows[index].precipitationMm) && rows[index].precipitationMm! <= measurable;
    if (!validDry || (first >= 0 && !contiguous(rows[index - 1], rows[index]))) finish(index - 1);
    if (validDry && first < 0) first = index;
  }
  finish(rows.length - 1);
  return result;
}

interface TemperatureSample {
  index: number;
  delta: number;
  change: number;
  month: WeatherMonth;
}

function temperatureSamples(rows: readonly EventHour[]): TemperatureSample[] {
  const result: TemperatureSample[] = [];
  for (let index = 6; index < rows.length; index += 1) {
    const before = rows[index - 6]; const current = rows[index];
    if (!finite(before.temperatureC) || !finite(current.temperatureC) || elapsedHours(before, current) !== 6) continue;
    if (rows.slice(index - 5, index + 1).some((hour, offset) => !finite(hour.temperatureC)
      || !contiguous(rows[index - 6 + offset], hour))) continue;
    const delta = current.temperatureC - before.temperatureC;
    result.push({ index, delta, change: Math.abs(delta), month: monthFor(current) });
  }
  return result;
}

function trainingTemperatureEvents(rows: readonly EventHour[], samples: readonly TemperatureSample[],
  rapidByMonth: ReadonlyMap<WeatherMonth, number>): Array<{ change: number; duration: number; month: WeatherMonth }> {
  const result: Array<{ change: number; duration: number; month: WeatherMonth }> = [];
  let blockedUntil = -1;
  for (const sample of samples) {
    const index = sample.index;
    if (index <= blockedUntil || sample.change < (rapidByMonth.get(sample.month) ?? 5) || index + 11 >= rows.length) continue;
    const before = rows[index - 6]; const crossing = rows[index];
    const cold = before.temperatureC! > 0 && crossing.temperatureC! <= 0 && sample.delta < 0;
    const warm = before.temperatureC! <= 0 && crossing.temperatureC! > 0 && sample.delta > 0;
    if (!cold && !warm) continue;
    const following = rows.slice(index, index + 12);
    if (following.some((hour, offset) => !finite(hour.temperatureC)
      || (offset > 0 && !contiguous(following[offset - 1], hour)))) continue;
    if (following.filter((hour) => cold ? hour.temperatureC! <= 0 : hour.temperatureC! > 0).length < 9) continue;
    let last = index + 11;
    while (last + 1 < rows.length && contiguous(rows[last], rows[last + 1]) && finite(rows[last + 1].temperatureC)
      && (cold ? rows[last + 1].temperatureC! <= 0 : rows[last + 1].temperatureC! > 0)) last += 1;
    result.push({ change: sample.change, duration: Math.round(elapsedHours(rows[index - 6], rows[last]) + 1),
      month: sample.month });
    blockedUntil = last;
  }
  return result;
}

function fallbackMonth(month: WeatherMonth): WeatherEventThresholdMonthV1 {
  return {
    month, stormMinimumTotalMm: 1, stormMinimumDurationHours: 3,
    rapidTemperatureChangeC: 5, temperatureMaintenanceHours: 9, drySpellMinimumHours: 72,
    stormSeverity: { totalP90Mm: 20, totalP98Mm: 45, peakP90Mm: 4, peakP98Mm: 9,
      durationP90Hours: 36, durationP98Hours: 72 },
    temperatureSeverity: { changeP90C: 8, changeP98C: 12, durationP90Hours: 36, durationP98Hours: 96 },
    drySeverity: { durationP90Hours: 168, durationP98Hours: 336 },
    sampleCounts: { wetSpells: 0, temperatureChanges: 0, drySpells: 0 },
  };
}

export function fallbackWeatherEventThresholds(fittedFromYears: readonly number[] = []): WeatherEventThresholdModelV1 {
  return {
    version: 1, measurablePrecipitationMm: MEASURABLE_PRECIPITATION_MM,
    stormDryGapHours: STORM_DRY_GAP_HOURS,
    months: Array.from({ length: 12 }, (_, index) => fallbackMonth((index + 1) as WeatherMonth)),
    fittedFromYears: [...new Set(fittedFromYears)].sort((left, right) => left - right),
  };
}

/** Fits event thresholds solely from the series supplied by climate compilation. */
export function fitWeatherEventThresholds(trainingSeries: readonly HistoricalWeatherSeriesV1[]): WeatherEventThresholdModelV1 {
  const years = [...new Set(trainingSeries.map((series) => series.validationYear))].sort((left, right) => left - right);
  // Each station-year is an independent chronology. Pool extracted samples, not
  // equal-timestamp rows from different stations, which would break contiguity.
  const groups = trainingSeries.map((series) => {
    const rows = observedRows(series.hours);
    return { rows, temperatures: temperatureSamples(rows) };
  });
  const wet = groups.flatMap(({ rows }) => stormSamples(rows, MEASURABLE_PRECIPITATION_MM, STORM_DRY_GAP_HOURS));
  const dry = groups.flatMap(({ rows }) => drySamples(rows, MEASURABLE_PRECIPITATION_MM));
  const temperatures = groups.flatMap((group) => group.temperatures);
  const rapidByMonth = new Map<WeatherMonth, number>(Array.from({ length: 12 }, (_, index) => {
    const month = (index + 1) as WeatherMonth;
    return [month, quantize(Math.max(5, percentile(temperatures.filter((sample) => sample.month === month)
      .map((sample) => sample.change), 0.9, 5)), 2)];
  }));
  const temperatureEvents = groups.flatMap(({ rows, temperatures: samples }) =>
    trainingTemperatureEvents(rows, samples, rapidByMonth));
  const months = Array.from({ length: 12 }, (_, index): WeatherEventThresholdMonthV1 => {
    const month = (index + 1) as WeatherMonth; const defaults = fallbackMonth(month);
    const wetMonth = wet.filter((sample) => sample.month === month);
    const dryMonth = dry.filter((sample) => sample.month === month);
    const temperatureMonth = temperatures.filter((sample) => sample.month === month);
    const rapid = rapidByMonth.get(month) ?? 5;
    const qualifyingTemperatures = temperatureEvents.filter((sample) => sample.month === month);
    const stormMinimumTotalMm = quantize(
      Math.max(1, percentile(wetMonth.map((sample) => sample.total), 0.5, 1)), 3,
    );
    const stormMinimumDurationHours = 3;
    const drySpellMinimumHours = Math.round(Math.max(72,
      percentile(dryMonth.map((sample) => sample.duration), 0.75, 72)));
    const qualifyingWet = wetMonth.filter((sample) => sample.duration >= stormMinimumDurationHours
      && sample.total >= stormMinimumTotalMm);
    const qualifyingDry = dryMonth.filter((sample) => sample.duration >= drySpellMinimumHours);
    return {
      month,
      stormMinimumTotalMm,
      stormMinimumDurationHours,
      rapidTemperatureChangeC: quantize(rapid, 2), temperatureMaintenanceHours: 9,
      drySpellMinimumHours,
      stormSeverity: {
        totalP90Mm: quantize(percentile(qualifyingWet.map((sample) => sample.total), 0.9, defaults.stormSeverity.totalP90Mm), 3),
        totalP98Mm: quantize(percentile(qualifyingWet.map((sample) => sample.total), 0.98, defaults.stormSeverity.totalP98Mm), 3),
        peakP90Mm: quantize(percentile(qualifyingWet.map((sample) => sample.peak), 0.9, defaults.stormSeverity.peakP90Mm), 3),
        peakP98Mm: quantize(percentile(qualifyingWet.map((sample) => sample.peak), 0.98, defaults.stormSeverity.peakP98Mm), 3),
        durationP90Hours: quantize(percentile(qualifyingWet.map((sample) => sample.duration), 0.9, defaults.stormSeverity.durationP90Hours), 1),
        durationP98Hours: quantize(percentile(qualifyingWet.map((sample) => sample.duration), 0.98, defaults.stormSeverity.durationP98Hours), 1),
      },
      temperatureSeverity: {
        changeP90C: quantize(percentile(qualifyingTemperatures.map((sample) => sample.change), 0.9, defaults.temperatureSeverity.changeP90C), 2),
        changeP98C: quantize(percentile(qualifyingTemperatures.map((sample) => sample.change), 0.98, defaults.temperatureSeverity.changeP98C), 2),
        durationP90Hours: quantize(percentile(qualifyingTemperatures.map((sample) => sample.duration), 0.9, defaults.temperatureSeverity.durationP90Hours), 1),
        durationP98Hours: quantize(percentile(qualifyingTemperatures.map((sample) => sample.duration), 0.98, defaults.temperatureSeverity.durationP98Hours), 1),
      },
      drySeverity: {
        durationP90Hours: quantize(percentile(qualifyingDry.map((sample) => sample.duration), 0.9, defaults.drySeverity.durationP90Hours), 1),
        durationP98Hours: quantize(percentile(qualifyingDry.map((sample) => sample.duration), 0.98, defaults.drySeverity.durationP98Hours), 1),
      },
      empiricalDistributions: {
        stormTotalMm: sortedQuantized(qualifyingWet.map((sample) => sample.total), 3),
        stormPeakHourlyMm: sortedQuantized(qualifyingWet.map((sample) => sample.peak), 3),
        stormDurationHours: sortedQuantized(qualifyingWet.map((sample) => sample.duration), 1),
        temperatureChangeC: sortedQuantized(qualifyingTemperatures.map((sample) => sample.change), 2),
        temperatureDurationHours: sortedQuantized(qualifyingTemperatures.map((sample) => sample.duration), 1),
        drySpellDurationHours: sortedQuantized(qualifyingDry.map((sample) => sample.duration), 1),
      },
      sampleCounts: { wetSpells: wetMonth.length, temperatureChanges: temperatureMonth.length, drySpells: dryMonth.length },
    };
  });
  return { version: 1, measurablePrecipitationMm: MEASURABLE_PRECIPITATION_MM,
    stormDryGapHours: STORM_DRY_GAP_HOURS, months, fittedFromYears: years };
}

function thresholdFor(model: WeatherEventThresholdModelV1, month: WeatherMonth): WeatherEventThresholdMonthV1 {
  return model.months.find((candidate) => candidate.month === month) ?? fallbackMonth(month);
}

function details(rows: readonly EventHour[], span: Span): Pick<WeatherEventV1,
  'totalPrecipitationMm' | 'peakPrecipitationMm' | 'precipitationByPhaseMm' | 'snowfallCm'
  | 'meanWindSpeedKph' | 'peakWindGustKph' | 'pressureChangeHpa'> {
  const selected = rows.slice(span.first, span.last + 1);
  const phaseTotals = phaseRecord(); const waters: number[] = []; const snow: number[] = [];
  const winds: number[] = []; const gusts: number[] = [];
  for (const hour of selected) {
    if (finite(hour.precipitationMm)) {
      waters.push(hour.precipitationMm);
      if (hour.precipitationPhase) phaseTotals[hour.precipitationPhase] += hour.precipitationMm;
    }
    if (finite(hour.snowfallCm)) snow.push(hour.snowfallCm);
    if (finite(hour.windSpeedKph)) winds.push(hour.windSpeedKph);
    if (finite(hour.windGustKph)) gusts.push(hour.windGustKph);
  }
  for (const phase of PHASES) phaseTotals[phase] = quantize(phaseTotals[phase], 4);
  const firstPressure = selected.find((hour) => finite(hour.pressureHpa))?.pressureHpa;
  const lastPressure = [...selected].reverse().find((hour) => finite(hour.pressureHpa))?.pressureHpa;
  return {
    totalPrecipitationMm: quantize(waters.reduce((sum, value) => sum + value, 0), 4),
    peakPrecipitationMm: quantize(waters.length ? Math.max(...waters) : 0, 4),
    precipitationByPhaseMm: phaseTotals,
    snowfallCm: quantize(snow.reduce((sum, value) => sum + value, 0), 3),
    meanWindSpeedKph: winds.length ? quantize(winds.reduce((sum, value) => sum + value, 0) / winds.length, 2) : null,
    peakWindGustKph: gusts.length ? quantize(Math.max(...gusts), 2) : null,
    pressureChangeHpa: finite(firstPressure) && finite(lastPressure) ? quantize(lastPressure - firstPressure, 2) : null,
  };
}

function stormStyle(rows: readonly EventHour[], span: Span): {
  stormStyle: StormStyle | null; styleConfidence: StormStyleConfidence | null; styleEvidence: readonly string[];
} {
  const selected = rows.slice(span.first, span.last + 1);
  if (selected.some((hour) => hour.macroAirMass === 'frontal' || hour.frontal)) {
    return { stormStyle: 'frontal', styleConfidence: 'high',
      styleEvidence: ['Generated frontal macro-state or explicit frontal-passage hazard overlaps the storm.'] };
  }
  return { stormStyle: null, styleConfidence: null, styleEvidence: [] };
}

function eventBase(type: WeatherEventV1['type'], rows: readonly EventHour[], span: Span,
  severityPercentile: number, intensityPercentile: number, temperatureChangeC: number): WeatherEventV1 {
  const first = rows[span.first]; const last = rows[span.last];
  const style = type === 'storm' ? stormStyle(rows, span)
    : { stormStyle: null, styleConfidence: null, styleEvidence: [] as readonly string[] };
  return {
    version: 1, id: `${type}:${first.at}`, type, startsAt: first.at, endsAt: exclusiveEnd(last),
    localStartDate: first.localDateTime.slice(0, 10), localEndDate: last.localDateTime.slice(0, 10),
    durationHours: Math.round(elapsedHours(first, last) + 1), severity: severity(severityPercentile),
    intensityPercentile: quantize(intensityPercentile, 2), ...details(rows, span),
    temperatureChangeC: quantize(temperatureChangeC, 2), ...style,
  };
}

function detect(rows: readonly EventHour[], model: WeatherEventThresholdModelV1): WeatherEventV1[] {
  const result: WeatherEventV1[] = [];
  for (const sample of stormSamples(rows, model.measurablePrecipitationMm, model.stormDryGapHours)) {
    const threshold = thresholdFor(model, sample.month);
    if (sample.duration < threshold.stormMinimumDurationHours || sample.total < threshold.stormMinimumTotalMm) continue;
    const severityRank = (
      fittedPercentile(sample.total, threshold.stormSeverity.totalP90Mm, threshold.stormSeverity.totalP98Mm)
      + fittedPercentile(sample.peak, threshold.stormSeverity.peakP90Mm, threshold.stormSeverity.peakP98Mm)
      + fittedPercentile(sample.duration, threshold.stormSeverity.durationP90Hours, threshold.stormSeverity.durationP98Hours)
    ) / 3;
    const distributions = threshold.empiricalDistributions;
    const intensityRank = (
      eventPercentile(sample.total, distributions?.stormTotalMm,
        threshold.stormSeverity.totalP90Mm, threshold.stormSeverity.totalP98Mm)
      + eventPercentile(sample.peak, distributions?.stormPeakHourlyMm,
        threshold.stormSeverity.peakP90Mm, threshold.stormSeverity.peakP98Mm)
      + eventPercentile(sample.duration, distributions?.stormDurationHours,
        threshold.stormSeverity.durationP90Hours, threshold.stormSeverity.durationP98Hours)
    ) / 3;
    const temperatureChange = finite(rows[sample.first].temperatureC) && finite(rows[sample.last].temperatureC)
      ? rows[sample.last].temperatureC! - rows[sample.first].temperatureC! : 0;
    result.push(eventBase('storm', rows, sample, severityRank, intensityRank, temperatureChange));
  }
  for (const sample of drySamples(rows, model.measurablePrecipitationMm)) {
    const threshold = thresholdFor(model, sample.month);
    if (sample.duration < threshold.drySpellMinimumHours) continue;
    const severityRank = fittedPercentile(
      sample.duration, threshold.drySeverity.durationP90Hours, threshold.drySeverity.durationP98Hours,
    );
    const intensityRank = eventPercentile(sample.duration,
      threshold.empiricalDistributions?.drySpellDurationHours,
      threshold.drySeverity.durationP90Hours, threshold.drySeverity.durationP98Hours);
    const temperatureChange = finite(rows[sample.first].temperatureC) && finite(rows[sample.last].temperatureC)
      ? rows[sample.last].temperatureC! - rows[sample.first].temperatureC! : 0;
    result.push(eventBase('dry-spell', rows, sample, severityRank, intensityRank, temperatureChange));
  }
  let blockedUntil = -1;
  for (let index = 6; index + 11 < rows.length; index += 1) {
    if (index <= blockedUntil) continue;
    const before = rows[index - 6]; const crossing = rows[index];
    if (!finite(before.temperatureC) || !finite(crossing.temperatureC) || elapsedHours(before, crossing) !== 6) continue;
    const preceding = rows.slice(index - 6, index + 1);
    const following = rows.slice(index, index + 12);
    if (preceding.some((hour, offset) => !finite(hour.temperatureC)
      || (offset > 0 && !contiguous(preceding[offset - 1], hour)))
      || following.some((hour, offset) => !finite(hour.temperatureC)
        || (offset > 0 && !contiguous(following[offset - 1], hour)))) continue;
    const threshold = thresholdFor(model, monthFor(crossing));
    const delta = crossing.temperatureC - before.temperatureC;
    const cold = before.temperatureC > 0 && crossing.temperatureC <= 0 && delta <= -threshold.rapidTemperatureChangeC;
    const warm = before.temperatureC <= 0 && crossing.temperatureC > 0 && delta >= threshold.rapidTemperatureChangeC;
    if (!cold && !warm) continue;
    const maintenance = following.filter((hour) => cold ? hour.temperatureC! <= 0 : hour.temperatureC! > 0).length;
    if (maintenance < threshold.temperatureMaintenanceHours) continue;
    let last = index + 11;
    while (last + 1 < rows.length && contiguous(rows[last], rows[last + 1]) && finite(rows[last + 1].temperatureC)
      && (cold ? rows[last + 1].temperatureC! <= 0 : rows[last + 1].temperatureC! > 0)) last += 1;
    const span = { first: index - 6, last };
    const duration = Math.round(elapsedHours(rows[span.first], rows[span.last]) + 1);
    const severityRank = (
      fittedPercentile(Math.abs(delta), threshold.temperatureSeverity.changeP90C, threshold.temperatureSeverity.changeP98C)
      + fittedPercentile(duration, threshold.temperatureSeverity.durationP90Hours, threshold.temperatureSeverity.durationP98Hours)
    ) / 2;
    const intensityRank = (
      eventPercentile(Math.abs(delta), threshold.empiricalDistributions?.temperatureChangeC,
        threshold.temperatureSeverity.changeP90C, threshold.temperatureSeverity.changeP98C)
      + eventPercentile(duration, threshold.empiricalDistributions?.temperatureDurationHours,
        threshold.temperatureSeverity.durationP90Hours, threshold.temperatureSeverity.durationP98Hours)
    ) / 2;
    result.push(eventBase(cold ? 'cold-snap' : 'warm-up', rows, span,
      severityRank, intensityRank, delta));
    blockedUntil = last;
  }
  return result.sort((left, right) => left.startsAt.localeCompare(right.startsAt) || left.type.localeCompare(right.type));
}

export function detectSimulatedWeatherEvents(
  hours: readonly SimulatedWeatherHourV1[], thresholds: WeatherEventThresholdModelV1,
): readonly WeatherEventV1[] {
  return detect(simulatedRows(hours), thresholds);
}

export function detectObservedWeatherEvents(
  seriesOrHours: HistoricalWeatherSeriesV1 | readonly ObservedWeatherHourV1[],
  thresholds: WeatherEventThresholdModelV1,
): readonly WeatherEventV1[] {
  const hours = 'hours' in seriesOrHours ? seriesOrHours.hours : seriesOrHours;
  return detect(observedRows(hours), thresholds);
}
