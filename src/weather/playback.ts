import type { HistoricalWeatherYear, SyntheticWeatherPlan, WeatherDataPackage, WeatherReferenceHour } from './weatherModel';

export const WEATHER_LAB_SPEEDS = [1, 2, 4, 8, 16, 32, 64] as const;
export type WeatherLabSpeed = typeof WEATHER_LAB_SPEEDS[number];

export interface WeatherPlaybackState {
  cursor: string;
  running: boolean;
  speed: WeatherLabSpeed;
  historicalYear: number;
}

export function createWeatherPlayback(plan: SyntheticWeatherPlan, historicalYear: number): WeatherPlaybackState {
  return { cursor: plan.startsAt, running: false, speed: 1, historicalYear };
}

function hourAt(hours: readonly WeatherReferenceHour[], at: string): WeatherReferenceHour | null {
  const target = new Date(at).getTime();
  return hours.find((hour) => new Date(hour.at).getTime() === target) ?? null;
}

export function weatherAt(plan: SyntheticWeatherPlan, at: string): WeatherReferenceHour | null {
  return hourAt(plan.hours, at);
}

/** Match by local calendar month/day/hour. The package's source year is immutable. */
export function historicalAt(
  weatherPackage: WeatherDataPackage,
  year: number,
  cursor: string,
): WeatherReferenceHour | null {
  const history: HistoricalWeatherYear | undefined = weatherPackage.historicalYears.find((candidate) => candidate.year === year);
  if (!history) return null;
  const source = new Date(cursor);
  const match = history.hours.find((hour) => {
    const date = new Date(hour.at);
    return date.getUTCMonth() === source.getUTCMonth() && date.getUTCDate() === source.getUTCDate() &&
      date.getUTCHours() === source.getUTCHours();
  });
  return match ?? null;
}

function clampToPlan(plan: SyntheticWeatherPlan, candidate: Date): string {
  const start = new Date(plan.startsAt).getTime();
  const end = new Date(plan.endsAt).getTime();
  return new Date(Math.max(start, Math.min(end, candidate.getTime()))).toISOString();
}

export function seekWeatherPlayback(plan: SyntheticWeatherPlan, state: WeatherPlaybackState, cursor: string): WeatherPlaybackState {
  return { ...state, cursor: clampToPlan(plan, new Date(cursor)), running: false };
}

export function skipWeatherPlayback(
  plan: SyntheticWeatherPlan,
  state: WeatherPlaybackState,
  kind: 'hour' | 'day' | 'week' | 'month',
): WeatherPlaybackState {
  const date = new Date(state.cursor);
  if (kind === 'hour') date.setUTCHours(date.getUTCHours() + 1);
  if (kind === 'day') date.setUTCDate(date.getUTCDate() + 1);
  if (kind === 'week') date.setUTCDate(date.getUTCDate() + 7);
  if (kind === 'month') {
    const originalDay = date.getUTCDate();
    date.setUTCDate(1);
    date.setUTCMonth(date.getUTCMonth() + 1);
    const finalDay = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + 1, 0)).getUTCDate();
    date.setUTCDate(Math.min(originalDay, finalDay));
  }
  return { ...state, cursor: clampToPlan(plan, date), running: false };
}

export function advanceWeatherPlayback(plan: SyntheticWeatherPlan, state: WeatherPlaybackState, wallDeltaMs: number): WeatherPlaybackState {
  if (!state.running) return state;
  const simulatedMs = Math.max(0, Math.min(1_000, wallDeltaMs)) * state.speed * 60;
  const cursor = clampToPlan(plan, new Date(new Date(state.cursor).getTime() + simulatedMs));
  return { ...state, cursor, running: cursor !== plan.endsAt };
}
