// Standalone simulation calendar core. This module deliberately has no game,
// browser, Electron, terminal, timer, or filesystem dependencies.
import type {
  AdvanceResult, DailyPhase, SimulationClock, TimeBoundaryEvent,
  TimeBoundaryTarget, TimeEngineSnapshot, TimeScaleConfig,
} from '../../src/types/simulation';
import { localWeatherDateKey, weatherInstantForLocal, weatherLocalParts } from '../../src/weather/localTime';

export type {
  AdvanceResult, ClockRunState, DailyPhase, DailyPhaseConfig, Season, SimulationClock,
  SimulationSpeed, TimeAdvanceContext, TimeBoundaryEvent, TimeBoundaryTarget,
  TimeEngineSnapshot, TimeEventConsumer, TimeScaleConfig,
} from '../../src/types/simulation';

export const DEFAULT_TIME_CONFIG: TimeScaleConfig = {
  schemaVersion: 2,
  configVersion: 2,
  timezone: 'UTC',
  winterWeeks: 24,
  summerPeriods: 16,
  realSecondsPerWinterWeek: 900,
  speedMultipliers: [1, 2, 4, 8],
  clockStepMinutes: 1,
  uiUpdateHz: 4,
  maxWallDeltaMs: 250,
  initialSummerStart: '2026-05-01T00:00:00.000Z',
  winterStartMonth: 10,
  winterStartDay: 1,
  winterStartHour: 5,
  dailyPhases: {
    overnightStart: 0,
    preOpenStart: 6 * 60,
    operatingStart: 8 * 60,
    eveningStart: 16 * 60,
  },
};

const MINUTES_PER_DAY = 1_440;
export const MINUTES_PER_WEEK = 10_080;

export function scaledSimulationMinutes(
  wallMilliseconds: number,
  speed: SimulationClock['speed'],
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): number {
  return Math.max(0, wallMilliseconds) * MINUTES_PER_WEEK /
    (config.realSecondsPerWinterWeek * 1_000) * speed;
}

function asDate(iso: string): Date {
  return new Date(iso);
}

function iso(date: Date): string {
  return date.toISOString();
}

function addMinutes(date: Date, minutes: number): Date {
  return new Date(date.getTime() + minutes * 60_000);
}

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

function minutesToNextLocalDay(date: Date, timezone: string): number {
  const local = weatherLocalParts(date, timezone);
  const nextDate = new Date(Date.UTC(local.year, local.month - 1, local.day + 1));
  const next = new Date(weatherInstantForLocal({
    year: nextDate.getUTCFullYear(), month: nextDate.getUTCMonth() + 1, day: nextDate.getUTCDate(),
    hour: 0, minute: 0, second: 0,
  }, timezone));
  return Math.max(1, wholeMinutesBetween(date, next));
}

export function phaseAt(minute: number, config: TimeScaleConfig): DailyPhase {
  const phases = config.dailyPhases;
  if (minute >= phases.eveningStart) return 'evening';
  if (minute >= phases.operatingStart) return 'operating';
  if (minute >= phases.preOpenStart) return 'preOpen';
  return 'overnight';
}

function withDerived(clock: SimulationClock, config: TimeScaleConfig): SimulationClock {
  const date = asDate(clock.calendarDate);
  const minute = localMinuteOfDay(date, config.timezone);
  let winterWeek = clock.winterWeek;
  if (clock.season === 'winter') {
    const elapsed = wholeMinutesBetween(asDate(clock.seasonStartedAt), date);
    winterWeek = Math.min(config.winterWeeks, Math.floor(elapsed / MINUTES_PER_WEEK) + 1);
  }
  return {
    ...clock,
    minuteOfDay: minute,
    weekday: localWeekday(date, config.timezone),
    dailyPhase: phaseAt(minute, config),
    winterWeek,
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
  // Equality occurs after the final summer period has advanced exactly to the
  // winter boundary. In that case, begin winter now rather than a year later.
  if (candidate < date) {
    year += 1;
    candidate = candidateForYear(year);
  }
  return candidate;
}

function transitionToSummer(
  clock: SimulationClock,
  config: TimeScaleConfig,
  events: TimeBoundaryEvent[],
): SimulationClock {
  const at = clock.calendarDate;
  events.push({ type: 'seasonEnded', at, season: 'winter' });
  events.push({ type: 'seasonStarted', at, season: 'summer' });
  events.push({ type: 'summerPeriodStarted', at, period: 1 });
  return withDerived({
    ...clock,
    completedWinterSeasons: clock.completedWinterSeasons + 1,
    season: 'summer',
    seasonStartedAt: at,
    summerPeriod: 1,
    winterWeek: null,
    runState: 'paused',
    transitionPending: null,
  }, config);
}

function transitionToWinter(
  clock: SimulationClock,
  config: TimeScaleConfig,
  events: TimeBoundaryEvent[],
): SimulationClock {
  const oldDate = asDate(clock.calendarDate);
  const winterStart = firstWinterStartAfter(oldDate, config);
  const advanced = wholeMinutesBetween(oldDate, winterStart);
  const resortYear = clock.completedWinterSeasons + 1;
  const at = iso(winterStart);
  events.push({ type: 'seasonEnded', at, season: 'summer' });
  if (resortYear !== clock.resortYear) {
    events.push({ type: 'yearStarted', at, resortYear });
  }
  events.push({ type: 'seasonStarted', at, season: 'winter' });
  events.push({ type: 'weekStarted', at, week: 1 });
  return withDerived({
    ...clock,
    resortYear,
    season: 'winter',
    seasonStartedAt: at,
    summerPeriod: null,
    winterWeek: 1,
    absoluteGameMinute: clock.absoluteGameMinute + advanced,
    calendarDate: at,
    runState: 'paused',
    transitionPending: null,
  }, config);
}

export function createClock(
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
  initialState?: SimulationClock,
): SimulationClock {
  if (initialState) return validateClock(initialState, config);
  const start = asDate(config.initialSummerStart);
  return withDerived({
    schemaVersion: 2,
    timezone: config.timezone,
    resortYear: 1,
    completedWinterSeasons: 0,
    season: 'summer',
    seasonStartedAt: iso(start),
    summerPeriod: 1,
    winterWeek: null,
    absoluteGameMinute: 0,
    calendarDate: iso(start),
    minuteOfDay: 0,
    weekday: localWeekday(start, config.timezone),
    dailyPhase: 'overnight',
    speed: 1,
    runState: 'paused',
    transitionPending: null,
  }, config);
}

function minutesToNextPhase(clock: SimulationClock, config: TimeScaleConfig): number {
  const starts = [
    config.dailyPhases.overnightStart,
    config.dailyPhases.preOpenStart,
    config.dailyPhases.operatingStart,
    config.dailyPhases.eveningStart,
  ].sort((a, b) => a - b);
  const next = starts.find((start) => start > clock.minuteOfDay);
  return next == null ? MINUTES_PER_DAY - clock.minuteOfDay + starts[0] : next - clock.minuteOfDay;
}

export function advanceClock(
  clock: SimulationClock,
  simulatedMinutes: number,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): AdvanceResult {
  if (!Number.isInteger(simulatedMinutes) || simulatedMinutes < 0) {
    throw new Error('simulatedMinutes must be a non-negative integer');
  }
  if (clock.season !== 'winter' || clock.runState === 'season-transition') {
    return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  }
  let next = { ...clock };
  const events: TimeBoundaryEvent[] = [];
  let advanced = 0;
  while (advanced < simulatedMinutes && next.season === 'winter') {
    const oldDate = asDate(next.calendarDate);
    const oldPhase = next.dailyPhase;
    const oldWeek = next.winterWeek ?? 1;
    const elapsed = wholeMinutesBetween(asDate(next.seasonStartedAt), oldDate);
    const weekElapsed = elapsed % MINUTES_PER_WEEK;
    const untilWeek = MINUTES_PER_WEEK - weekElapsed;
    const untilSeason = config.winterWeeks * MINUTES_PER_WEEK - elapsed;
    const untilDay = minutesToNextLocalDay(oldDate, config.timezone);
    const chunk = Math.min(
      simulatedMinutes - advanced,
      untilWeek,
      untilSeason,
      untilDay,
      minutesToNextPhase(next, config),
    );
    const nextDate = addMinutes(oldDate, chunk);
    next = withDerived({
      ...next,
      calendarDate: iso(nextDate),
      absoluteGameMinute: next.absoluteGameMinute + chunk,
    }, config);
    advanced += chunk;

    const nextElapsed = elapsed + chunk;
    if (nextElapsed >= config.winterWeeks * MINUTES_PER_WEEK) {
      events.push({ type: 'weekEnded', at: next.calendarDate, week: config.winterWeeks });
      next = transitionToSummer(next, config, events);
      break;
    }
    if (localWeatherDateKey(iso(oldDate), config.timezone) !==
      localWeatherDateKey(iso(nextDate), config.timezone)) {
      events.push({
        type: 'dayStarted',
        at: next.calendarDate,
        date: localWeatherDateKey(next.calendarDate, config.timezone),
        weekday: next.weekday,
      });
    }
    if (oldPhase !== next.dailyPhase) {
      events.push({
        type: 'dailyPhaseChanged',
        at: next.calendarDate,
        from: oldPhase,
        to: next.dailyPhase,
      });
    }
    if (next.winterWeek !== oldWeek) {
      events.push({ type: 'weekEnded', at: next.calendarDate, week: oldWeek });
      events.push({ type: 'weekStarted', at: next.calendarDate, week: next.winterWeek ?? oldWeek + 1 });
    }
  }
  return { clock: next, events, simulatedMinutesAdvanced: advanced };
}

export function getSummerPeriodRange(
  clock: SimulationClock,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): { start: string; end: string } | null {
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

export function advanceSummerPeriod(
  clock: SimulationClock,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): AdvanceResult {
  if (clock.season !== 'summer' || clock.summerPeriod == null) {
    return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  }
  if (clock.runState === 'season-transition') {
    return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  }
  const range = getSummerPeriodRange(clock, config);
  if (!range) return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  const targetDate = asDate(range.end);
  const events: TimeBoundaryEvent[] = [{
    type: 'summerPeriodEnded',
    at: iso(targetDate),
    period: clock.summerPeriod,
  }];
  const advanced = wholeMinutesBetween(asDate(clock.calendarDate), targetDate);
  let next = withDerived({
    ...clock,
    calendarDate: iso(targetDate),
    absoluteGameMinute: clock.absoluteGameMinute + advanced,
  }, config);
  if (clock.summerPeriod >= config.summerPeriods) {
    next = {
      ...next,
      runState: 'season-transition',
      transitionPending: 'winter',
    };
    events.push({ type: 'seasonTransitionPending', at: next.calendarDate, target: 'winter' });
  } else {
    const nextPeriod = clock.summerPeriod + 1;
    next = { ...next, summerPeriod: nextPeriod };
    events.push({
      type: 'summerPeriodStarted',
      at: next.calendarDate,
      period: nextPeriod,
    });
  }
  return { clock: next, events, simulatedMinutesAdvanced: advanced };
}

export function confirmSeasonTransition(
  clock: SimulationClock,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): AdvanceResult {
  if (clock.transitionPending !== 'winter') {
    return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  }
  const events: TimeBoundaryEvent[] = [];
  const before = clock.absoluteGameMinute;
  const next = transitionToWinter(clock, config, events);
  return {
    clock: next,
    events,
    simulatedMinutesAdvanced: next.absoluteGameMinute - before,
  };
}

function forceNextWinter(clock: SimulationClock, config: TimeScaleConfig): AdvanceResult {
  if (clock.season === 'winter') {
    const toSummer = advanceToBoundary(clock, 'next-summer', config);
    const toWinter = forceNextWinter(toSummer.clock, config);
    return combineResults(clock, [toSummer, toWinter]);
  }
  const events: TimeBoundaryEvent[] = [];
  const before = clock.absoluteGameMinute;
  const next = transitionToWinter(clock, config, events);
  return { clock: next, events, simulatedMinutesAdvanced: next.absoluteGameMinute - before };
}

function combineResults(start: SimulationClock, results: AdvanceResult[]): AdvanceResult {
  const final = results.at(-1)?.clock ?? start;
  return {
    clock: final,
    events: results.flatMap((result) => result.events),
    simulatedMinutesAdvanced: final.absoluteGameMinute - start.absoluteGameMinute,
  };
}

export function advanceToBoundary(
  clock: SimulationClock,
  boundary: TimeBoundaryTarget,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): AdvanceResult {
  if (boundary === 'next-day') {
    if (clock.season !== 'winter') {
      throw new Error('Day skipping is available only during winter');
    }
    const minutes = minutesToNextLocalDay(asDate(clock.calendarDate), config.timezone);
    return advanceClock(clock, minutes, config);
  }
  if (boundary === 'next-week') {
    if (clock.season !== 'winter') {
      throw new Error('Week skipping is available only during winter');
    }
    return advanceClock(clock, MINUTES_PER_WEEK, config);
  }
  if (boundary === 'next-season') {
    return clock.season === 'winter'
      ? advanceToBoundary(clock, 'next-summer', config)
      : forceNextWinter(clock, config);
  }
  if (boundary === 'next-summer') {
    if (clock.season === 'winter') {
      const elapsed = wholeMinutesBetween(asDate(clock.seasonStartedAt), asDate(clock.calendarDate));
      const remaining = config.winterWeeks * MINUTES_PER_WEEK - elapsed;
      return advanceClock(clock, remaining, config);
    }
    const toWinter = forceNextWinter(clock, config);
    const toSummer = advanceToBoundary(toWinter.clock, 'next-summer', config);
    return combineResults(clock, [toWinter, toSummer]);
  }
  if (boundary === 'next-winter') {
    return forceNextWinter(clock, config);
  }

  if (clock.season === 'winter') {
    const offset = wholeMinutesBetween(asDate(clock.seasonStartedAt), asDate(clock.calendarDate));
    const toWinter = forceNextWinter(clock, config);
    const atEquivalentPoint = advanceClock(toWinter.clock, offset, config);
    return combineResults(clock, [toWinter, atEquivalentPoint]);
  }
  const originalPeriod = clock.summerPeriod ?? 1;
  const toSummer = advanceToBoundary(clock, 'next-summer', config);
  const results = [toSummer];
  let next = toSummer.clock;
  for (let period = 1; period < originalPeriod; period += 1) {
    const result = advanceSummerPeriod(next, config);
    results.push(result);
    next = result.clock;
  }
  return combineResults(clock, results);
}

export function createTimeSnapshot(
  clock: SimulationClock,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): TimeEngineSnapshot {
  return {
    schemaVersion: 2,
    configVersion: config.configVersion,
    clock: { ...clock, runState: 'paused' },
  };
}

function isIsoDate(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(new Date(value).getTime());
}

function validateClock(raw: SimulationClock, config: TimeScaleConfig): SimulationClock {
  if (!raw || typeof raw !== 'object' || raw.schemaVersion !== 2) {
    throw new Error('Unsupported or invalid clock schema');
  }
  if (!Number.isInteger(raw.resortYear) || raw.resortYear < 1) throw new Error('Invalid resort year');
  if (!Number.isInteger(raw.completedWinterSeasons) || raw.completedWinterSeasons < 0) {
    throw new Error('Invalid completed winter season count');
  }
  if (raw.season !== 'summer' && raw.season !== 'winter') throw new Error('Invalid season');
  if (!isIsoDate(raw.calendarDate) || !isIsoDate(raw.seasonStartedAt)) throw new Error('Invalid calendar date');
  if (!Number.isInteger(raw.absoluteGameMinute) || raw.absoluteGameMinute < 0) {
    throw new Error('Invalid absolute game minute');
  }
  if (!config.speedMultipliers.includes(raw.speed)) throw new Error('Invalid simulation speed');
  if (
    raw.season === 'summer' &&
    (!Number.isInteger(raw.summerPeriod) || raw.summerPeriod == null ||
      raw.summerPeriod < 1 || raw.summerPeriod > config.summerPeriods)
  ) {
    throw new Error('Invalid summer period');
  }
  if (
    raw.season === 'winter' &&
    (!Number.isInteger(raw.winterWeek) || raw.winterWeek == null ||
      raw.winterWeek < 1 || raw.winterWeek > config.winterWeeks)
  ) {
    throw new Error('Invalid winter week');
  }
  const transitionPending = raw.transitionPending === 'summer' || raw.transitionPending === 'winter'
    ? raw.transitionPending
    : null;
  return withDerived({
    ...raw,
    timezone: config.timezone,
    runState: 'paused',
    transitionPending,
    summerPeriod: raw.season === 'summer' ? raw.summerPeriod : null,
    winterWeek: raw.season === 'winter' ? raw.winterWeek : null,
  }, config);
}

export function restoreTimeSnapshot(
  raw: unknown,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): SimulationClock {
  if (!raw || typeof raw !== 'object') throw new Error('Snapshot must be an object');
  const snapshot = raw as { schemaVersion?: number; configVersion?: number;
    clock?: Partial<SimulationClock> & { schemaVersion?: number; timezone?: string } };
  if (snapshot.schemaVersion === 1 && snapshot.configVersion === 1 && snapshot.clock) {
    return validateClock({ ...snapshot.clock, schemaVersion: 2, timezone: 'UTC' } as SimulationClock,
      { ...config, timezone: 'UTC' });
  }
  if (snapshot.schemaVersion !== 2) throw new Error('Unsupported snapshot schema');
  if (snapshot.configVersion !== config.configVersion) {
    throw new Error(`Snapshot requires time config ${snapshot.configVersion}`);
  }
  const timezone = snapshot.clock?.timezone;
  return validateClock(snapshot.clock as SimulationClock, {
    ...config,
    ...(typeof timezone === 'string' ? { timezone } : {}),
  });
}

/** Complete the one-time planning phase at the exact resort-local September 1 boundary. */
export function advanceSummerToSeptember(
  clock: SimulationClock,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG,
): AdvanceResult {
  if (clock.season !== 'summer' || clock.runState === 'season-transition') {
    return { clock: { ...clock }, events: [], simulatedMinutesAdvanced: 0 };
  }
  const local = weatherLocalParts(clock.calendarDate, config.timezone);
  const year = local.month < 9 ? local.year : local.year + 1;
  const target = new Date(weatherInstantForLocal(
    { year, month: 9, day: 1, hour: 0, minute: 0, second: 0 }, config.timezone));
  const advanced = wholeMinutesBetween(asDate(clock.calendarDate), target);
  const at = iso(target);
  const events: TimeBoundaryEvent[] = [
    { type: 'summerPeriodEnded', at, period: clock.summerPeriod ?? 1 },
    { type: 'seasonEnded', at, season: 'summer' },
    { type: 'seasonStarted', at, season: 'winter' },
    { type: 'weekStarted', at, week: 1 },
  ];
  return {
    clock: withDerived({
      ...clock,
      season: 'winter',
      seasonStartedAt: at,
      summerPeriod: null,
      winterWeek: 1,
      calendarDate: at,
      absoluteGameMinute: clock.absoluteGameMinute + advanced,
      runState: 'paused',
      transitionPending: null,
    }, config),
    events,
    simulatedMinutesAdvanced: advanced,
  };
}

export function describeTimeEvent(event: TimeBoundaryEvent): string {
  switch (event.type) {
    case 'dailyPhaseChanged':
      return `${event.to === 'preOpen' ? 'Pre-opening' : event.to[0].toUpperCase() + event.to.slice(1)} phase started`;
    case 'dayStarted':
      return `${['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'][event.weekday]} started`;
    case 'weekStarted':
      return `Winter Week ${event.week} started`;
    case 'weekEnded':
      return `Winter Week ${event.week} ended`;
    case 'summerPeriodStarted':
      return `Summer Period ${event.period} started`;
    case 'summerPeriodEnded':
      return `Summer Period ${event.period} ended`;
    case 'seasonEnded':
      return `${event.season === 'winter' ? 'Winter' : 'Summer'} ended`;
    case 'seasonStarted':
      return `${event.season === 'winter' ? 'Winter' : 'Summer'} started`;
    case 'yearStarted':
      return `Resort Year ${event.resortYear} started`;
    case 'seasonTransitionPending':
      return `Ready to begin ${event.target}`;
  }
}
