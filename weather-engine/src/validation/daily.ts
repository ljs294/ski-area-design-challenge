import type {
  HistoricalWeatherSeriesV1, MacroAirMassId, ObservedWeatherDayV1, ObservedWeatherHourV1,
  ObservationQuality, PrecipitationPhase, SimulatedWeatherHourV1, WeatherCondition,
  WeatherDailySummaryV1, WeatherEventV1, WeatherHazard, WeatherVariable,
} from '../contracts.ts';
import { weatherCalendarYear } from '../engine/calendar.ts';
import { quantize, snowfallCentimetresFromLiquid } from '../engine/psychrometrics.ts';

const CONDITIONS: readonly WeatherCondition[] = [
  'clear', 'partly-cloudy', 'overcast', 'flurries', 'snow', 'heavy-snow', 'mixed',
  'freezing-rain', 'rain',
];
const PHASES: readonly PrecipitationPhase[] = ['none', 'rain', 'mixed', 'snow', 'freezing-rain'];
const MACROS: readonly MacroAirMassId[] = ['arctic', 'continental-polar', 'maritime-polar', 'warm-wet', 'frontal'];

type NumericSummary = Readonly<{ minimum: number; mean: number; maximum: number }>;

function phaseRecord(): Record<PrecipitationPhase, number> {
  return Object.fromEntries(PHASES.map((phase) => [phase, 0])) as Record<PrecipitationPhase, number>;
}

function quantizedPhaseTotals(values: Readonly<Record<PrecipitationPhase, number>>, total: number): Record<PrecipitationPhase, number> {
  const result = phaseRecord();
  for (const phase of PHASES) result[phase] = quantize(values[phase], 4);
  const difference = quantize(total - PHASES.reduce((sum, phase) => sum + result[phase], 0), 4);
  if (difference !== 0) {
    const target = [...PHASES].sort((left, right) => result[right] - result[left])[0];
    result[target] = quantize(result[target] + difference, 4);
  }
  return result;
}

function summary(values: readonly number[]): NumericSummary | null {
  if (!values.length) return null;
  return {
    minimum: quantize(Math.min(...values), 3),
    mean: quantize(values.reduce((sum, value) => sum + value, 0) / values.length, 3),
    maximum: quantize(Math.max(...values), 3),
  };
}

function dominant<T extends string>(counts: Readonly<Partial<Record<T, number>>>, order: readonly T[]): T | null {
  let winner: T | null = null;
  let maximum = 0;
  for (const value of order) {
    const count = counts[value] ?? 0;
    if (count > maximum) { winner = value; maximum = count; }
  }
  return winner;
}

function eventIdsForDate(events: readonly WeatherEventV1[], localDate: string): string[] {
  return events.filter((event) => event.localStartDate <= localDate && event.localEndDate >= localDate)
    .map((event) => event.id);
}

function groupByLocalDate<T extends { localDateTime: string }>(hours: readonly T[]): Map<string, T[]> {
  const result = new Map<string, T[]>();
  for (const hour of hours) {
    const localDate = hour.localDateTime.slice(0, 10);
    const selected = result.get(localDate) ?? [];
    selected.push(hour); result.set(localDate, selected);
  }
  return result;
}

function expectedHoursByDate(series: HistoricalWeatherSeriesV1): Map<string, number> {
  try {
    const calendar = weatherCalendarYear(series.validationYear, series.station.timezone);
    const result = new Map<string, number>();
    for (const hour of calendar) {
      const date = hour.localDateTime.slice(0, 10);
      result.set(date, (result.get(date) ?? 0) + 1);
    }
    return result;
  } catch {
    return new Map([...groupByLocalDate(series.hours)].map(([date, hours]) => [date, hours.length]));
  }
}

function isAccepted(hour: ObservedWeatherHourV1, variable: WeatherVariable, value: unknown): value is number {
  const quality: ObservationQuality | undefined = hour.quality[variable];
  return typeof value === 'number' && Number.isFinite(value) && quality !== 'missing' && quality !== 'suspect';
}

function isConditionAccepted(hour: ObservedWeatherHourV1): boolean {
  return hour.condition != null && hour.quality.condition !== 'missing' && hour.quality.condition !== 'suspect';
}

function observedSnowfallFromWater(hour: ObservedWeatherHourV1, waterMm: number): number {
  const temperatureC = isAccepted(hour, 'temperatureC', hour.temperatureC) ? hour.temperatureC : 0;
  return snowfallCentimetresFromLiquid(waterMm, temperatureC, hour.precipitationPhase ?? 'none');
}

function observedPrecipitation(
  hours: readonly ObservedWeatherHourV1[], expectedHours: number, day: ObservedWeatherDayV1 | undefined,
): { total: number | null; byPhase: Record<PrecipitationPhase, number> | null; scaledWater: readonly number[] | null } {
  const accepted = hours.map((hour) => isAccepted(hour, 'precipitationMm', hour.precipitationMm));
  const water = hours.map((hour, index) => accepted[index] ? hour.precipitationMm! : 0);
  const acceptedCount = accepted.filter(Boolean).length;
  const hourlyTotal = water.reduce((sum, value) => sum + value, 0);
  const dailyTotal = day?.precipitationMm != null && Number.isFinite(day.precipitationMm)
    ? Math.max(0, day.precipitationMm) : null;
  const total = dailyTotal ?? (acceptedCount === expectedHours ? hourlyTotal : null);
  if (total == null) return { total: null, byPhase: null, scaledWater: null };
  if (total === 0) return { total: 0, byPhase: phaseRecord(), scaledWater: water.map(() => 0) };
  if (hourlyTotal <= 0 || acceptedCount !== expectedHours) {
    return { total: quantize(total, 4), byPhase: null, scaledWater: null };
  }
  const classified = hours.map((hour, index) => accepted[index] && (water[index] <= 0
    || hour.precipitationPhase != null && hour.precipitationPhase !== 'none'));
  if (classified.some((covered) => !covered)) {
    return { total: quantize(total, 4), byPhase: null, scaledWater: null };
  }
  const scale = total / hourlyTotal;
  const scaledWater = water.map((value) => value * scale);
  const byPhase = phaseRecord();
  for (let index = 0; index < hours.length; index += 1) {
    if (!classified[index]) continue;
    const phase = hours[index].precipitationPhase;
    if (phase) byPhase[phase] += scaledWater[index];
  }
  const quantizedTotal = quantize(total, 4);
  return { total: quantizedTotal, byPhase: quantizedPhaseTotals(byPhase, quantizedTotal), scaledWater };
}

/** Aggregates generated truth by local calendar date; DST days remain 23/25 hours. */
export function summarizeSimulatedWeatherDays(
  hours: readonly SimulatedWeatherHourV1[], events: readonly WeatherEventV1[] = [],
): readonly WeatherDailySummaryV1[] {
  return [...groupByLocalDate(hours)].map(([localDate, selected]) => {
    const expectedHours = selected.length;
    const phaseTotals = phaseRecord();
    const conditionHours: Partial<Record<WeatherCondition, number>> = {};
    const macroHours: Partial<Record<MacroAirMassId, number>> = {};
    const hazards = new Set<WeatherHazard>();
    for (const hour of selected) {
      phaseTotals[hour.precipitationPhase] += hour.precipitationMm;
      conditionHours[hour.condition] = (conditionHours[hour.condition] ?? 0) + 1;
      macroHours[hour.macroAirMass] = (macroHours[hour.macroAirMass] ?? 0) + 1;
      for (const hazard of hour.hazards) hazards.add(hazard);
    }
    const precipitationMm = quantize(selected.reduce((sum, hour) => sum + hour.precipitationMm, 0), 4);
    const quantizedPhases = quantizedPhaseTotals(phaseTotals, precipitationMm);
    return {
      localDate, expectedHours, availableHours: selected.length,
      completeness: {
        temperatureC: 1, wetBulbC: 1, precipitationMm: 1, snowfallCm: 1, condition: 1,
      },
      temperatureC: summary(selected.map((hour) => hour.temperatureC)),
      wetBulbC: summary(selected.map((hour) => hour.wetBulbC)),
      snowmakingHours: selected.filter((hour) => hour.wetBulbC <= -2).length,
      precipitationMm, precipitationByPhaseMm: quantizedPhases,
      snowfallCm: quantize(selected.reduce((sum, hour) => sum + hour.snowfallCm, 0), 3),
      snowfallSource: 'simulated', conditionHours,
      dominantCondition: dominant(conditionHours, CONDITIONS), hazards: [...hazards].sort(), macroHours,
      dominantMacro: dominant(macroHours, MACROS), eventIds: eventIdsForDate(events, localDate),
    };
  });
}

/** Aggregates accepted observations, preferring provider daily totals when supplied. */
export function summarizeObservedWeatherDays(
  series: HistoricalWeatherSeriesV1, events: readonly WeatherEventV1[] = [],
): readonly WeatherDailySummaryV1[] {
  const groups = groupByLocalDate(series.hours);
  const expected = expectedHoursByDate(series);
  const suppliedDays = new Map((series.days ?? []).map((day) => [day.localDate, day]));
  const dates = [...new Set([...expected.keys(), ...groups.keys(), ...suppliedDays.keys()])].sort();
  return dates.map((localDate) => {
    const selected = groups.get(localDate) ?? [];
    const expectedHours = expected.get(localDate) ?? selected.length;
    const day = suppliedDays.get(localDate);
    const values = <K extends keyof ObservedWeatherHourV1 & WeatherVariable>(key: K) => selected.flatMap((hour) =>
      isAccepted(hour, key, hour[key]) ? [hour[key] as number] : []);
    const temperatures = values('temperatureC');
    const wetBulbs = values('wetBulbC');
    const precipitation = observedPrecipitation(selected, expectedHours, day);
    const conditionHours: Partial<Record<WeatherCondition, number>> = {};
    const hazards = new Set<WeatherHazard>();
    for (const hour of selected) {
      if (isConditionAccepted(hour)) conditionHours[hour.condition!] = (conditionHours[hour.condition!] ?? 0) + 1;
      for (const hazard of hour.hazards) hazards.add(hazard);
    }
    const acceptedConditions = Object.values(conditionHours).reduce((sum, value) => sum + (value ?? 0), 0);
    const acceptedPrecipitation = selected.filter((hour) => isAccepted(hour, 'precipitationMm', hour.precipitationMm)).length;
    const acceptedSnowfall = selected.filter((hour) => isAccepted(hour, 'snowfallCm', hour.snowfallCm)).length;
    let snowfallCm: number | null = null;
    let snowfallSource: WeatherDailySummaryV1['snowfallSource'] = 'unavailable';
    if (day?.snowfallCm != null && Number.isFinite(day.snowfallCm) && day.sources.snowfall) {
      snowfallCm = Math.max(0, day.snowfallCm);
      snowfallSource = day.snowfallKind === 'derived' || day.sources.snowfall.toLocaleLowerCase().startsWith('derived')
        ? 'derived' : 'observed';
    } else if (precipitation.scaledWater) {
      snowfallCm = selected.reduce((sum, hour, index) => sum + observedSnowfallFromWater(hour, precipitation.scaledWater![index]), 0);
      snowfallSource = 'derived';
    } else if (acceptedSnowfall === expectedHours && expectedHours > 0) {
      snowfallCm = values('snowfallCm').reduce((sum, value) => sum + value, 0); snowfallSource = 'observed';
    }
    const availableHours = selected.filter((hour) =>
      isAccepted(hour, 'temperatureC', hour.temperatureC)
      || isAccepted(hour, 'wetBulbC', hour.wetBulbC)
      || isAccepted(hour, 'precipitationMm', hour.precipitationMm)
      || isConditionAccepted(hour)).length;
    const ratio = (count: number) => expectedHours > 0 ? quantize(count / expectedHours, 4) : 0;
    return {
      localDate, expectedHours, availableHours,
      completeness: {
        temperatureC: ratio(temperatures.length), wetBulbC: ratio(wetBulbs.length),
        precipitationMm: ratio(acceptedPrecipitation), snowfallCm: ratio(acceptedSnowfall),
        condition: ratio(acceptedConditions),
      },
      temperatureC: summary(temperatures), wetBulbC: summary(wetBulbs),
      snowmakingHours: wetBulbs.length ? wetBulbs.filter((value) => value <= -2).length : null,
      precipitationMm: precipitation.total, precipitationByPhaseMm: precipitation.byPhase,
      snowfallCm: snowfallCm == null ? null : quantize(snowfallCm, 3), snowfallSource,
      conditionHours, dominantCondition: dominant(conditionHours, CONDITIONS), hazards: [...hazards].sort(),
      macroHours: null, dominantMacro: null, eventIds: eventIdsForDate(events, localDate),
    };
  });
}
