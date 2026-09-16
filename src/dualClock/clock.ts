import { addWeatherLocalTime, weatherInstantForLocal, weatherLocalParts } from '../weather/localTime';
import type { SimulationClock } from '../types/simulation';
import { DEFAULT_DUAL_CONFIG, type AdvanceDestination, type DualClockSnapshot, type MacroSecond, type MicroSecond,
  type SimulationSpeedProfile } from './model';

/**
 * Runtime-only movement pacing.  The authored `microRates` values are part of
 * the schema-17 checkpoint contract and describe the original presentation
 * profile, so the approved pacing adjustment belongs here rather than in the
 * persisted configuration.
 */
export const DUAL_MICRO_RUNTIME_MULTIPLIER = 3;

export function winterBounds(at: string, timezone: string, weeks = 24): { start: string; end: string } {
  const local = weatherLocalParts(at, timezone);
  const startFor = (year: number) => {
    const weekday = new Date(Date.UTC(year, 10, 1)).getUTCDay();
    return weatherInstantForLocal({ year, month: 11, day: 1 + (8 - weekday) % 7, hour: 8, minute: 0, second: 0 }, timezone);
  };
  let start = startFor(local.year);
  if (Date.parse(at) < Date.parse(start)) {
    const previous = startFor(local.year - 1);
    if (Date.parse(at) < Date.parse(addWeatherLocalTime(previous, timezone, { weeks }))) start = previous;
  }
  return { start, end: addWeatherLocalTime(start, timezone, { weeks }) };
}
export function createDualClock(at: string, timezone: string, config = DEFAULT_DUAL_CONFIG): DualClockSnapshot {
  const { start, end } = winterBounds(at, timezone, config.winterWeeks);
  return { revision: 0, macroSecond: 0 as MacroSecond, microSecond: 0 as MicroSecond,
    at, timezone, winterStart: start, winterEnd: end, speed: 1, paused: true,
    season: Date.parse(at) >= Date.parse(start) && Date.parse(at) < Date.parse(end) ? 'winter' : 'summer' };
}
export function advanceDestination(clock: DualClockSnapshot, destination: AdvanceDestination): string {
  const { at, timezone } = clock;
  const stopAtWinterEnd = (target: string) => clock.season === 'winter' && Date.parse(target) > Date.parse(clock.winterEnd) ? clock.winterEnd : target;
  if (destination === 'season') return clock.winterEnd;
  if (destination === 'winter') {
    if (Date.parse(clock.winterStart) > Date.parse(at)) return clock.winterStart;
    return winterBounds(addWeatherLocalTime(clock.winterStart, timezone, { months: 12 }), timezone).start;
  }
  if (destination === 'opening') {
    const local = weatherLocalParts(at, timezone);
    let next = weatherInstantForLocal({ ...local, hour: 8, minute: 0, second: 0 }, timezone);
    if (Date.parse(next) <= Date.parse(at)) next = addWeatherLocalTime(next, timezone, { days: 1 });
    return stopAtWinterEnd(next);
  }
  return stopAtWinterEnd(addWeatherLocalTime(at, timezone, destination === 'day' ? { days: 1 } : destination === 'week' ? { weeks: 1 } : { months: 1 }));
}
export function microRate(clock: DualClockSnapshot, headless: boolean, config: SimulationSpeedProfile): number {
  // Headless advances have an explicit calendar target rather than a wall
  // clock selected by the player.  Keep their movement ratio at the 1x base
  // while applying the same runtime acceleration as ordinary play.  At 8x and
  // above ordinary presentation already follows the selected calendar rate;
  // the existing high-speed branch therefore remains speed-based.
  const configuredMicroPerRealSecond = headless ? 1
    : clock.speed >= 8 ? clock.speed : config.microRates[clock.speed];
  const configuredMacroPerRealSecond = headless ? config.macroSecondsPerSecond
    : config.macroSecondsPerSecond * clock.speed;
  return DUAL_MICRO_RUNTIME_MULTIPLIER * configuredMicroPerRealSecond / configuredMacroPerRealSecond;
}
/** Compatibility presentation only. The legacy time engine never advances this projection. */
export function projectDualClock(clock: DualClockSnapshot): SimulationClock {
  const local = weatherLocalParts(clock.at, clock.timezone), minutes = local.hour * 60 + local.minute;
  return { schemaVersion: 3, timezone: clock.timezone, resortYear: local.year, completedWinterSeasons: 0,
    season: clock.season, seasonStartedAt: clock.winterStart, summerPeriod: clock.season === 'summer' ? 1 : null,
    winterWeek: clock.season === 'winter' ? Math.floor((Date.parse(clock.at) - Date.parse(clock.winterStart)) / 604800000) + 1 : null,
    elapsedSimSecond: clock.macroSecond, weekSecond: minutes * 60 + local.second,
    absoluteGameMinute: Math.floor(Date.parse(clock.at) / 60000), calendarDate: clock.at, minuteOfDay: minutes,
    weekday: new Date(Date.UTC(local.year, local.month - 1, local.day)).getUTCDay(),
    dailyPhase: minutes < 420 ? 'overnight' : minutes < 480 ? 'preOpen' : minutes < 960 ? 'operating' : 'evening',
    speed: 'normal', runState: clock.paused ? 'paused' : 'running', transitionPending: null };
}
