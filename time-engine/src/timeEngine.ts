// Standalone simulation calendar core. This module deliberately has no game,
// browser, Electron, terminal, timer, or filesystem dependencies.
import type {
  AdvanceResult, DailyPhase, SimulationClock, SimulationSpeed, SimulationSpeedValue,
  TimeBoundaryEvent, TimeBoundaryTarget, TimeEngineSnapshot, TimeEventConsumer,
  TimeScaleConfig,
} from '../../src/types/simulation';
import { SIMULATION_SECONDS_PER_WEEK, SIMULATION_SPEED_RATES } from '../../src/types/simulation';
import { localWeatherDateKey, weatherInstantForLocal, weatherLocalParts } from '../../src/weather/localTime';

export type {
  AdvanceResult, ClockRunState, DailyPhase, DailyPhaseConfig, LegacySimulationSpeed, Season,
  SimulationClock, SimulationSpeed, SimulationSpeedValue, TimeAdvanceContext, TimeBoundaryEvent,
  TimeBoundaryTarget, TimeEngineSnapshot, TimeEventConsumer, TimeScaleConfig,
} from '../../src/types/simulation';
export { SIMULATION_SECONDS_PER_WEEK, SIMULATION_SPEED_RATES } from '../../src/types/simulation';

/** The old minute constant remains exported for callers that still use step 1w. */
export const MINUTES_PER_WEEK = 10_080;
export const SECONDS_PER_WEEK = SIMULATION_SECONDS_PER_WEEK;
export const OPERATING_DAY_START_MINUTE = 8 * 60;
export const OPERATING_DAY_END_MINUTE = 20 * 60;

export const DEFAULT_TIME_CONFIG: TimeScaleConfig = {
  schemaVersion: 3,
  configVersion: 3,
  timezone: 'UTC',
  winterWeeks: 24,
  summerPeriods: 16,
  // At the normal tier (60 simulation seconds/real second) a week is 12 min.
  realSecondsPerWinterWeek: SIMULATION_SECONDS_PER_WEEK / SIMULATION_SPEED_RATES.normal,
  speedMultipliers: ['slow', 'normal', 'fast', 'ultrafast'],
  // Retained as a compatibility hint for minute-based consumers. The v3
  // runtime itself advances fractional simulation seconds.
  clockStepMinutes: 1,
  uiUpdateHz: 20,
  maxWallDeltaMs: 100,
  initialSummerStart: '2026-05-01T00:00:00.000Z',
  winterStartMonth: 10,
  winterStartDay: 1,
  winterStartHour: 8,
  dailyPhases: {
    overnightStart: 0,
    preOpenStart: 6 * 60,
    operatingStart: 8 * 60,
    eveningStart: 16 * 60,
  },
};

const EPSILON = 1e-9;

/** Convert a legacy numeric speed at the persistence boundary. */
export function normalizeSimulationSpeed(speed: SimulationSpeedValue | unknown): SimulationSpeed {
  if (speed === 'slow' || speed === 'normal' || speed === 'fast' || speed === 'ultrafast') return speed;
  if (speed === 1) return 'normal';
  if (speed === 2) return 'fast';
  if (speed === 4 || speed === 8) return 'ultrafast';
  throw new Error('Invalid simulation speed');
}

export function simulationRate(speed: SimulationSpeedValue | unknown): number {
  return SIMULATION_SPEED_RATES[normalizeSimulationSpeed(speed)];
}

/** Continuous wall-time conversion. No rounding is performed here. */
export function scaledSimulationSeconds(wallMilliseconds: number, speed: SimulationSpeedValue): number {
  return Math.max(0, wallMilliseconds) / 1_000 * simulationRate(speed);
}

/** Minute conversion retained for old UI code; it intentionally remains fractional. */
export function scaledSimulationMinutes(
  wallMilliseconds: number,
  speed: SimulationSpeedValue,
  _config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): number {
  return scaledSimulationSeconds(wallMilliseconds, speed) / 60;
}

function asDate(iso: string): Date { return new Date(iso); }
function iso(date: Date): string { return date.toISOString(); }
function wholeMinutesBetween(from: Date, to: Date): number {
  return Math.max(0, Math.round((to.getTime() - from.getTime()) / 60_000));
}
function localMinuteOfDay(date: Date, timezone: string): number {
  const local = weatherLocalParts(date, timezone);
  return local.hour * 60 + local.minute;
}
function localWeekday(date: Date, timezone: string): number {
  const local = weatherLocalParts(date, timezone);
  return new Date(Date.UTC(local.year, local.month - 1, local.day)).getUTCDay();
}

export function phaseAt(minute: number, config: TimeScaleConfig): DailyPhase {
  const phases = config.dailyPhases;
  if (minute >= phases.eveningStart) return 'evening';
  if (minute >= phases.operatingStart) return 'operating';
  if (minute >= phases.preOpenStart) return 'preOpen';
  return 'overnight';
}

function winterSeasonSeconds(config: TimeScaleConfig): number {
  return config.winterWeeks * SIMULATION_SECONDS_PER_WEEK;
}
function winterWeekIndex(elapsedSecond: number): number {
  return Math.floor(Math.max(0, elapsedSecond) / SIMULATION_SECONDS_PER_WEEK);
}
function operatingDayStart(seasonStartedAt: string, weekIndex: number, timezone: string): string {
  const seasonLocal = weatherLocalParts(seasonStartedAt, timezone);
  const calendar = new Date(Date.UTC(seasonLocal.year, seasonLocal.month - 1, seasonLocal.day));
  // One composite week is represented by one operating day. Advancing seven
  // calendar days keeps compatibility weekday/date consumers stable while the
  // operating clock itself advances only from 08:00 through 20:00.
  calendar.setUTCDate(calendar.getUTCDate() + weekIndex * 7);
  return weatherInstantForLocal({
    year: calendar.getUTCFullYear(), month: calendar.getUTCMonth() + 1, day: calendar.getUTCDate(),
    hour: OPERATING_DAY_START_MINUTE / 60, minute: 0, second: 0,
  }, timezone);
}
function winterCalendarDate(seasonStartedAt: string, elapsedSecond: number, timezone: string): string {
  const safeElapsed = Math.max(0, elapsedSecond);
  const index = winterWeekIndex(safeElapsed);
  const second = safeElapsed - index * SIMULATION_SECONDS_PER_WEEK;
  const start = new Date(operatingDayStart(seasonStartedAt, index, timezone));
  return iso(new Date(start.getTime() + second * 1_000));
}
function compatibilityBase(clock: SimulationClock): number {
  const base = (clock as SimulationClock & { winterStartAbsoluteGameMinute?: number })
    .winterStartAbsoluteGameMinute;
  return Number.isFinite(base) ? base as number :
    clock.absoluteGameMinute - Math.max(0, clock.elapsedSimSecond) / 60;
}

/** Recompute all projections from elapsedSimSecond. */
function withDerived(clock: SimulationClock, config: TimeScaleConfig): SimulationClock {
  if (clock.season === 'winter') {
    const elapsed = Math.max(0, clock.elapsedSimSecond);
    const weekIndex = winterWeekIndex(elapsed);
    const second = elapsed - weekIndex * SIMULATION_SECONDS_PER_WEEK;
    const date = winterCalendarDate(clock.seasonStartedAt, elapsed, config.timezone);
    const minute = OPERATING_DAY_START_MINUTE + Math.floor(second / 60);
    const base = compatibilityBase(clock);
    return {
      ...clock,
      schemaVersion: 3,
      timezone: config.timezone,
      winterWeek: Math.min(config.winterWeeks, weekIndex + 1),
      weekSecond: second,
      calendarDate: date,
      absoluteGameMinute: Math.floor(base + elapsed / 60),
      minuteOfDay: minute,
      weekday: localWeekday(asDate(date), config.timezone),
      dailyPhase: phaseAt(minute, config),
      winterStartAbsoluteGameMinute: base,
    };
  }
  const date = asDate(clock.calendarDate);
  const minute = localMinuteOfDay(date, config.timezone);
  return {
    ...clock,
    schemaVersion: 3,
    timezone: config.timezone,
    winterWeek: null,
    weekSecond: 0,
    minuteOfDay: minute,
    weekday: localWeekday(date, config.timezone),
    dailyPhase: phaseAt(minute, config),
  };
}

function firstWinterStartAfter(date: Date, config: TimeScaleConfig): Date {
  let year = weatherLocalParts(date, config.timezone).year;
  const candidateForYear = (candidateYear: number): Date => {
    let day = config.winterStartDay;
    for (;;) {
      const value = new Date(weatherInstantForLocal({
        year: candidateYear, month: config.winterStartMonth + 1, day,
        hour: config.winterStartHour, minute: 0, second: 0,
      }, config.timezone));
      if (localWeekday(value, config.timezone) === 1) return value;
      day += 1;
    }
  };
  let candidate = candidateForYear(year);
  if (candidate < date) {
    year += 1;
    candidate = candidateForYear(year);
  }
  return candidate;
}

function result(clock: SimulationClock, events: TimeBoundaryEvent[], seconds: number): AdvanceResult {
  return { clock, events, simulatedSecondsAdvanced: seconds, simulatedMinutesAdvanced: seconds / 60 };
}

function transitionToSummer(clock: SimulationClock, config: TimeScaleConfig, events: TimeBoundaryEvent[]): SimulationClock {
  const at = clock.calendarDate;
  events.push({ type: 'seasonEnded', at, season: 'winter' });
  events.push({ type: 'seasonStarted', at, season: 'summer' });
  events.push({ type: 'summerPeriodStarted', at, period: 1 });
  return withDerived({
    ...clock, completedWinterSeasons: clock.completedWinterSeasons + 1,
    season: 'summer', seasonStartedAt: at, summerPeriod: 1, winterWeek: null,
    weekSecond: 0, runState: 'paused', transitionPending: null,
  }, config);
}

function transitionToWinter(clock: SimulationClock, config: TimeScaleConfig, events: TimeBoundaryEvent[]): SimulationClock {
  const oldDate = asDate(clock.calendarDate);
  const winterStart = firstWinterStartAfter(oldDate, config);
  const advanced = wholeMinutesBetween(oldDate, winterStart);
  const resortYear = clock.completedWinterSeasons + 1;
  const at = iso(winterStart);
  events.push({ type: 'seasonEnded', at, season: 'summer' });
  if (resortYear !== clock.resortYear) events.push({ type: 'yearStarted', at, resortYear });
  events.push({ type: 'seasonStarted', at, season: 'winter' });
  events.push({ type: 'weekStarted', at, week: 1 });
  const base = clock.absoluteGameMinute + advanced;
  const winterStartOperating = operatingDayStart(at, 0, config.timezone);
  return withDerived({
    ...clock, resortYear, season: 'winter', seasonStartedAt: winterStartOperating,
    summerPeriod: null, winterWeek: 1, weekSecond: 0, elapsedSimSecond: 0,
    calendarDate: winterStartOperating, absoluteGameMinute: base,
    winterStartAbsoluteGameMinute: base, runState: 'paused', transitionPending: null,
  }, config);
}

export function createClock(config: TimeScaleConfig = DEFAULT_TIME_CONFIG, initialState?: SimulationClock): SimulationClock {
  if (initialState) return validateClock(initialState, config);
  const start = asDate(config.initialSummerStart);
  return withDerived({
    schemaVersion: 3, timezone: config.timezone, resortYear: 1, completedWinterSeasons: 0,
    season: 'summer', seasonStartedAt: iso(start), summerPeriod: 1, winterWeek: null,
    elapsedSimSecond: 0, weekSecond: 0, absoluteGameMinute: 0, calendarDate: iso(start),
    minuteOfDay: 0, weekday: localWeekday(start, config.timezone), dailyPhase: 'overnight',
    speed: 'normal', runState: 'paused', transitionPending: null,
  }, config);
}

function secondsToNextPhase(clock: SimulationClock, config: TimeScaleConfig): number {
  const starts = [config.dailyPhases.overnightStart, config.dailyPhases.preOpenStart,
    config.dailyPhases.operatingStart, config.dailyPhases.eveningStart].sort((a, b) => a - b);
  const next = starts.find((start) => start > clock.minuteOfDay);
  if (next == null) return Number.POSITIVE_INFINITY;
  return Math.max(1, (next - clock.minuteOfDay) * 60 - (clock.weekSecond % 60));
}

/** Advance the continuous winter clock by simulation seconds. */
export function advanceClockSeconds(clock: SimulationClock, simulatedSeconds: number,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (!Number.isFinite(simulatedSeconds) || simulatedSeconds < 0) {
    throw new Error('simulatedSeconds must be a non-negative finite number');
  }
  if (clock.season !== 'winter' || clock.runState === 'season-transition' || simulatedSeconds === 0) {
    return result({ ...clock }, [], 0);
  }
  let next = withDerived({ ...clock }, config);
  const events: TimeBoundaryEvent[] = [];
  let advanced = 0;
  const seasonSeconds = winterSeasonSeconds(config);
  while (advanced + EPSILON < simulatedSeconds && next.season === 'winter') {
    const elapsed = next.elapsedSimSecond;
    const index = winterWeekIndex(elapsed);
    const weekOffset = elapsed - index * SIMULATION_SECONDS_PER_WEEK;
    const untilWeek = SIMULATION_SECONDS_PER_WEEK - weekOffset;
    const untilSeason = seasonSeconds - elapsed;
    const untilPhase = secondsToNextPhase(next, config);
    const chunk = Math.min(simulatedSeconds - advanced, untilWeek, untilSeason, untilPhase);
    if (!(chunk > EPSILON)) break;
    const oldPhase = next.dailyPhase;
    const oldWeek = next.winterWeek ?? index + 1;
    next = withDerived({ ...next, elapsedSimSecond: elapsed + chunk }, config);
    advanced += chunk;
    if (next.elapsedSimSecond + EPSILON >= seasonSeconds) {
      events.push({ type: 'weekEnded', at: next.calendarDate, week: config.winterWeeks });
      next = transitionToSummer(next, config, events);
      break;
    }
    if (next.winterWeek !== oldWeek) {
      events.push({ type: 'weekEnded', at: next.calendarDate, week: oldWeek });
      events.push({ type: 'weekStarted', at: next.calendarDate, week: next.winterWeek ?? oldWeek + 1 });
      events.push({ type: 'dayStarted', at: next.calendarDate,
        date: localWeatherDateKey(next.calendarDate, config.timezone), weekday: next.weekday });
    }
    if (oldPhase !== next.dailyPhase) {
      events.push({ type: 'dailyPhaseChanged', at: next.calendarDate, from: oldPhase, to: next.dailyPhase });
    }
  }
  return result(next, events, advanced);
}

/** Compatibility facade for minute-based consumers. */
export function advanceClock(clock: SimulationClock, simulatedMinutes: number,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (!Number.isInteger(simulatedMinutes) || simulatedMinutes < 0) {
    throw new Error('simulatedMinutes must be a non-negative integer');
  }
  return advanceClockSeconds(clock, simulatedMinutes * 60, config);
}

export function getSummerPeriodRange(clock: SimulationClock, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): { start: string; end: string } | null {
  if (clock.season !== 'summer' || clock.summerPeriod == null) return null;
  const summerStart = asDate(clock.seasonStartedAt);
  const winterStart = firstWinterStartAfter(summerStart, config);
  const duration = winterStart.getTime() - summerStart.getTime();
  const index = clock.summerPeriod - 1;
  return {
    start: iso(new Date(summerStart.getTime() + (duration * index) / config.summerPeriods)),
    end: iso(new Date(summerStart.getTime() + (duration * (index + 1)) / config.summerPeriods)),
  };
}

export function advanceSummerPeriod(clock: SimulationClock, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (clock.season !== 'summer' || clock.summerPeriod == null || clock.runState === 'season-transition') {
    return result({ ...clock }, [], 0);
  }
  const range = getSummerPeriodRange(clock, config);
  if (!range) return result({ ...clock }, [], 0);
  const targetDate = asDate(range.end);
  const events: TimeBoundaryEvent[] = [{ type: 'summerPeriodEnded', at: iso(targetDate), period: clock.summerPeriod }];
  const advanced = wholeMinutesBetween(asDate(clock.calendarDate), targetDate);
  let next = withDerived({ ...clock, calendarDate: iso(targetDate), absoluteGameMinute: clock.absoluteGameMinute + advanced }, config);
  if (clock.summerPeriod >= config.summerPeriods) {
    next = { ...next, runState: 'season-transition', transitionPending: 'winter' };
    events.push({ type: 'seasonTransitionPending', at: next.calendarDate, target: 'winter' });
  } else {
    const nextPeriod = clock.summerPeriod + 1;
    next = { ...next, summerPeriod: nextPeriod };
    events.push({ type: 'summerPeriodStarted', at: next.calendarDate, period: nextPeriod });
  }
  return { ...result(next, events, 0), simulatedMinutesAdvanced: advanced };
}

export function confirmSeasonTransition(clock: SimulationClock, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (clock.transitionPending !== 'winter') return result({ ...clock }, [], 0);
  const events: TimeBoundaryEvent[] = [];
  const next = transitionToWinter(clock, config, events);
  return { ...result(next, events, 0), simulatedMinutesAdvanced: 0 };
}

function forceNextWinter(clock: SimulationClock, config: TimeScaleConfig): AdvanceResult {
  if (clock.season === 'winter') {
    const toSummer = advanceToBoundary(clock, 'next-summer', config);
    const toWinter = forceNextWinter(toSummer.clock, config);
    return combineResults(clock, [toSummer, toWinter]);
  }
  const events: TimeBoundaryEvent[] = [];
  const next = transitionToWinter(clock, config, events);
  return result(next, events, 0);
}
function combineResults(start: SimulationClock, results: AdvanceResult[]): AdvanceResult {
  const final = results.at(-1)?.clock ?? start;
  return {
    clock: final, events: results.flatMap((item) => item.events),
    simulatedSecondsAdvanced: results.reduce((sum, item) => sum + item.simulatedSecondsAdvanced, 0),
    simulatedMinutesAdvanced: results.reduce((sum, item) => sum + item.simulatedMinutesAdvanced, 0),
  };
}

export function advanceToBoundary(clock: SimulationClock, boundary: TimeBoundaryTarget,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (boundary === 'next-day' || boundary === 'next-week') {
    if (clock.season !== 'winter') throw new Error('Day and week skipping are available only during winter');
    const remaining = SIMULATION_SECONDS_PER_WEEK - (clock.elapsedSimSecond % SIMULATION_SECONDS_PER_WEEK);
    return advanceClockSeconds(clock, remaining, config);
  }
  if (boundary === 'next-season') {
    return clock.season === 'winter' ? advanceToBoundary(clock, 'next-summer', config) : forceNextWinter(clock, config);
  }
  if (boundary === 'next-summer') {
    if (clock.season === 'winter') return advanceClockSeconds(clock, winterSeasonSeconds(config) - clock.elapsedSimSecond, config);
    const toWinter = forceNextWinter(clock, config);
    const toSummer = advanceToBoundary(toWinter.clock, 'next-summer', config);
    return combineResults(clock, [toWinter, toSummer]);
  }
  if (boundary === 'next-winter') return forceNextWinter(clock, config);
  if (clock.season === 'winter') {
    const offset = clock.elapsedSimSecond;
    const toWinter = forceNextWinter(clock, config);
    return combineResults(clock, [toWinter, advanceClockSeconds(toWinter.clock, offset, config)]);
  }
  const originalPeriod = clock.summerPeriod ?? 1;
  const toSummer = advanceToBoundary(clock, 'next-summer', config);
  const results = [toSummer];
  let next = toSummer.clock;
  for (let period = 1; period < originalPeriod; period += 1) {
    const item = advanceSummerPeriod(next, config);
    results.push(item);
    next = item.clock;
  }
  return combineResults(clock, results);
}

export function createTimeSnapshot(clock: SimulationClock, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): TimeEngineSnapshot {
  const normalized = validateClock(clock, config);
  return { schemaVersion: 3, configVersion: config.configVersion, clock: { ...normalized, runState: 'paused' } };
}
function isIsoDate(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(new Date(value).getTime());
}
function validateClock(raw: SimulationClock, config: TimeScaleConfig): SimulationClock {
  if (!raw || typeof raw !== 'object' || (raw.schemaVersion !== 3 && raw.schemaVersion !== 2)) throw new Error('Unsupported or invalid clock schema');
  if (!Number.isInteger(raw.resortYear) || raw.resortYear < 1) throw new Error('Invalid resort year');
  if (!Number.isInteger(raw.completedWinterSeasons) || raw.completedWinterSeasons < 0) throw new Error('Invalid completed winter season count');
  if (raw.season !== 'summer' && raw.season !== 'winter') throw new Error('Invalid season');
  if (!isIsoDate(raw.calendarDate) || !isIsoDate(raw.seasonStartedAt)) throw new Error('Invalid calendar date');
  const speed = normalizeSimulationSpeed(raw.speed);
  const oldRaw = raw as SimulationClock & { elapsedSimSecond?: number };
  const elapsed = Number.isFinite(oldRaw.elapsedSimSecond) ? Math.max(0, oldRaw.elapsedSimSecond as number) :
    raw.season === 'winter' ? Math.max(0, ((raw.winterWeek ?? 1) - 1) * SIMULATION_SECONDS_PER_WEEK +
      Math.max(0, Math.min(SIMULATION_SECONDS_PER_WEEK - 1, (raw.minuteOfDay ?? OPERATING_DAY_START_MINUTE) * 60 - OPERATING_DAY_START_MINUTE * 60))) : 0;
  if (elapsed > winterSeasonSeconds(config) + EPSILON) throw new Error('Invalid elapsed simulation second');
  if (!Number.isFinite(raw.absoluteGameMinute) || raw.absoluteGameMinute < 0) throw new Error('Invalid absolute game minute');
  if (raw.season === 'summer' && (!Number.isInteger(raw.summerPeriod) || raw.summerPeriod == null || raw.summerPeriod < 1 || raw.summerPeriod > config.summerPeriods)) throw new Error('Invalid summer period');
  if (raw.season === 'winter' && (!Number.isInteger(raw.winterWeek) || raw.winterWeek == null || raw.winterWeek < 1 || raw.winterWeek > config.winterWeeks) && (raw.schemaVersion as number | undefined) === 3) throw new Error('Invalid winter week');
  const transitionPending = raw.transitionPending === 'summer' || raw.transitionPending === 'winter' ? raw.transitionPending : null;
  const providedBase = (raw as SimulationClock & { winterStartAbsoluteGameMinute?: number }).winterStartAbsoluteGameMinute;
  const base = Number.isFinite(providedBase) ? providedBase : raw.absoluteGameMinute - elapsed / 60;
  const validated = withDerived({ ...raw, schemaVersion: 3, speed, timezone: config.timezone, elapsedSimSecond: elapsed,
    runState: 'paused', transitionPending, summerPeriod: raw.season === 'summer' ? raw.summerPeriod : null,
    winterWeek: raw.season === 'winter' ? raw.winterWeek : null, weekSecond: 0,
    winterStartAbsoluteGameMinute: base }, config);
  // A snapshot can be captured exactly on the terminal boundary before the
  // UI observes the transition event. Normalize that state so a restored
  // clock cannot remain in a zero-length 25th winter week.
  return validated.season === 'winter' && elapsed + EPSILON >= winterSeasonSeconds(config)
    ? transitionToSummer(validated, config, [])
    : validated;
}

type SnapshotClock = Omit<Partial<SimulationClock>, 'schemaVersion'> & {
  schemaVersion?: number;
  timezone?: string;
  speed?: SimulationSpeedValue;
};
function migrateLegacyClock(raw: SnapshotClock, config: TimeScaleConfig): SimulationClock {
  const season = raw.season === 'winter' ? 'winter' : 'summer';
  const calendarDate = isIsoDate(raw.calendarDate) ? raw.calendarDate : config.initialSummerStart;
  const seasonStartedAt = isIsoDate(raw.seasonStartedAt) ? raw.seasonStartedAt : calendarDate;
  const oldWeek = Number.isInteger(raw.winterWeek) ? raw.winterWeek as number : 1;
  const oldMinute = Number.isFinite(raw.minuteOfDay) ? raw.minuteOfDay as number : OPERATING_DAY_START_MINUTE;
  const daySecond = Math.max(0, Math.min(SIMULATION_SECONDS_PER_WEEK - 1, (oldMinute - OPERATING_DAY_START_MINUTE) * 60));
  const elapsed = season === 'winter' ? Math.min(winterSeasonSeconds(config) - 1,
    Math.max(0, (oldWeek - 1) * SIMULATION_SECONDS_PER_WEEK + daySecond)) : 0;
  const absolute = Number.isFinite(raw.absoluteGameMinute) ? raw.absoluteGameMinute as number : 0;
  const clock = {
    schemaVersion: 3, timezone: typeof raw.timezone === 'string' ? raw.timezone : config.timezone,
    resortYear: Number.isInteger(raw.resortYear) ? raw.resortYear as number : 1,
    completedWinterSeasons: Number.isInteger(raw.completedWinterSeasons) ? raw.completedWinterSeasons as number : 0,
    season, seasonStartedAt, summerPeriod: season === 'summer' ? (raw.summerPeriod ?? 1) : null,
    winterWeek: season === 'winter' ? oldWeek : null, elapsedSimSecond: elapsed, weekSecond: 0,
    absoluteGameMinute: absolute, calendarDate, minuteOfDay: oldMinute,
    weekday: Number.isInteger(raw.weekday) ? raw.weekday as number : 0,
    dailyPhase: raw.dailyPhase ?? 'overnight', speed: normalizeSimulationSpeed(raw.speed),
    runState: 'paused' as const,
    transitionPending: raw.transitionPending === 'summer' || raw.transitionPending === 'winter' ? raw.transitionPending : null,
    winterStartAbsoluteGameMinute: absolute - elapsed / 60,
  } satisfies SimulationClock & { winterStartAbsoluteGameMinute: number };
  return validateClock(clock, { ...config, timezone: clock.timezone });
}

export function restoreTimeSnapshot(raw: unknown, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): SimulationClock {
  if (!raw || typeof raw !== 'object') throw new Error('Snapshot must be an object');
  const snapshot = raw as { schemaVersion?: number; configVersion?: number; clock?: SnapshotClock };
  if (snapshot.clock == null) throw new Error('Snapshot clock is missing');
  if (snapshot.schemaVersion === 1 || snapshot.schemaVersion === 2 || snapshot.clock.schemaVersion === 2) return migrateLegacyClock(snapshot.clock, config);
  if (snapshot.schemaVersion == null || snapshot.schemaVersion !== 3) throw new Error('Unsupported snapshot schema');
  if (snapshot.configVersion !== config.configVersion) throw new Error(`Snapshot requires time config ${snapshot.configVersion}`);
  return validateClock(snapshot.clock as SimulationClock, config);
}

/** Complete the one-time planning phase at the exact resort-local September 1 boundary. */
export function advanceSummerToSeptember(clock: SimulationClock, config: TimeScaleConfig = DEFAULT_TIME_CONFIG): AdvanceResult {
  if (clock.season !== 'summer' || clock.runState === 'season-transition') return result({ ...clock }, [], 0);
  const local = weatherLocalParts(clock.calendarDate, config.timezone);
  const year = local.month < 9 ? local.year : local.year + 1;
  const target = new Date(weatherInstantForLocal({ year, month: 9, day: 1, hour: 0, minute: 0, second: 0 }, config.timezone));
  const advanced = wholeMinutesBetween(asDate(clock.calendarDate), target);
  const at = iso(target);
  const events: TimeBoundaryEvent[] = [
    { type: 'summerPeriodEnded', at, period: clock.summerPeriod ?? 1 },
    { type: 'seasonEnded', at, season: 'summer' }, { type: 'seasonStarted', at, season: 'winter' },
    { type: 'weekStarted', at, week: 1 },
  ];
  const operating = operatingDayStart(at, 0, config.timezone);
  const next = withDerived({ ...clock, season: 'winter', seasonStartedAt: operating, summerPeriod: null,
    winterWeek: 1, calendarDate: operating, elapsedSimSecond: 0, weekSecond: 0,
    absoluteGameMinute: clock.absoluteGameMinute + advanced,
    winterStartAbsoluteGameMinute: clock.absoluteGameMinute + advanced,
    runState: 'paused', transitionPending: null }, config);
  return { ...result(next, events, 0), simulatedMinutesAdvanced: advanced };
}

export function describeTimeEvent(event: TimeBoundaryEvent): string {
  switch (event.type) {
    case 'dailyPhaseChanged': return `${event.to === 'preOpen' ? 'Pre-opening' : event.to[0].toUpperCase() + event.to.slice(1)} phase started`;
    case 'dayStarted': return `${['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'][event.weekday]} started`;
    case 'weekStarted': return `Winter Week ${event.week} started`;
    case 'weekEnded': return `Winter Week ${event.week} ended`;
    case 'summerPeriodStarted': return `Summer Period ${event.period} started`;
    case 'summerPeriodEnded': return `Summer Period ${event.period} ended`;
    case 'seasonEnded': return `${event.season === 'winter' ? 'Winter' : 'Summer'} ended`;
    case 'seasonStarted': return `${event.season === 'winter' ? 'Winter' : 'Summer'} started`;
    case 'yearStarted': return `Resort Year ${event.resortYear} started`;
    case 'seasonTransitionPending': return `Ready to begin ${event.target}`;
  }
}

/** Notify old consumers using the new elapsed-second context. */
export function notifyTimeConsumer(consumer: TimeEventConsumer, before: SimulationClock,
  after: SimulationClock, events: readonly TimeBoundaryEvent[]): void {
  const seconds = Math.max(0, after.elapsedSimSecond - before.elapsedSimSecond);
  consumer.onTimeAdvanced({ before, after, simulatedSeconds: seconds, simulatedMinutes: seconds / 60 });
  for (const event of events) consumer.onBoundaryEvent(event);
}
