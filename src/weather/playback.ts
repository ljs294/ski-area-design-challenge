import type { HistoricalWeatherYear, SyntheticWeatherPlan, WeatherDataPackage, WeatherReferenceHour } from './weatherModel';
import { addWeatherLocalTime, localWeatherClockOffsetKey } from './localTime';

export const WEATHER_LAB_SPEEDS = [1, 2, 4, 8, 16, 32, 64] as const;
export type WeatherLabSpeed = typeof WEATHER_LAB_SPEEDS[number];

/**
 * UI/game-agnostic controller state. `cursor` is an instant, while all skips
 * use `timezone` to preserve the selected map's local clock time.
 */
export interface WeatherPlaybackState {
  cursor: string;
  running: boolean;
  speed: WeatherLabSpeed;
  historicalYear: number;
  timezone: string;
}

export function createWeatherPlayback(plan: SyntheticWeatherPlan, historicalYear: number): WeatherPlaybackState {
  return { cursor: plan.startsAt, running: false, speed: 1, historicalYear, timezone: plan.timezone || 'UTC' };
}

function indexAtOrBefore(hours: readonly WeatherReferenceHour[], at: string): number {
  const target = new Date(at).getTime();
  if (!Number.isFinite(target) || hours.length === 0) return -1;
  let low = 0;
  let high = hours.length - 1;
  let result = -1;
  while (low <= high) {
    const middle = Math.floor((low + high) / 2);
    if (new Date(hours[middle].at).getTime() <= target) {
      result = middle;
      low = middle + 1;
    } else high = middle - 1;
  }
  return result;
}

/** Return the active hourly value while playback is between exact hour stamps. */
export function weatherAt(plan: SyntheticWeatherPlan, at: string): WeatherReferenceHour | null {
  const index = indexAtOrBefore(plan.hours, at);
  return index >= 0 ? plan.hours[index] : null;
}

/** Match selected reference history by the map's local month/day/hour. */
export function historicalAt(
  weatherPackage: WeatherDataPackage,
  year: number,
  cursor: string,
): WeatherReferenceHour | null {
  const history: HistoricalWeatherYear | undefined = weatherPackage.historicalYears?.find((candidate) => candidate.year === year);
  if (!history) return null;
  const timezone = weatherPackage.manifest.timezone;
  const clock = localWeatherClockOffsetKey(cursor, timezone);
  return history.hours.find((hour) => localWeatherClockOffsetKey(hour.at, timezone) === clock) ?? null;
}

function clampToPlan(plan: SyntheticWeatherPlan, candidate: Date): string {
  const start = new Date(plan.startsAt).getTime();
  const end = new Date(plan.endsAt).getTime();
  const value = candidate.getTime();
  if (!Number.isFinite(value)) return plan.startsAt;
  return new Date(Math.max(start, Math.min(end, value))).toISOString();
}

/** Seeking never mutates simulation state; consumers recompute forecast/events from the plan. */
export function seekWeatherPlayback(plan: SyntheticWeatherPlan, state: WeatherPlaybackState, cursor: string): WeatherPlaybackState {
  return { ...state, cursor: clampToPlan(plan, new Date(cursor)), running: false };
}

export function skipWeatherPlayback(
  plan: SyntheticWeatherPlan,
  state: WeatherPlaybackState,
  kind: 'hour' | 'day' | 'week' | 'month',
): WeatherPlaybackState {
  const delta = kind === 'hour' ? { hours: 1 } : kind === 'day' ? { days: 1 } :
    kind === 'week' ? { weeks: 1 } : { months: 1 };
  const candidate = addWeatherLocalTime(state.cursor, state.timezone || plan.timezone || 'UTC', delta);
  return { ...state, cursor: clampToPlan(plan, new Date(candidate)), running: false };
}

/**
 * One real millisecond equals one simulated minute at 1x. The full fractional
 * instant is retained, so repeated animation frames accumulate exactly instead
 * of losing progress until an hour boundary.
 */
export function advanceWeatherPlayback(plan: SyntheticWeatherPlan, state: WeatherPlaybackState, wallDeltaMs: number): WeatherPlaybackState {
  if (!state.running) return state;
  const simulatedMs = Math.max(0, Math.min(1_000, wallDeltaMs)) * state.speed * 60;
  const cursor = clampToPlan(plan, new Date(new Date(state.cursor).getTime() + simulatedMs));
  return { ...state, cursor, running: cursor !== plan.endsAt };
}
