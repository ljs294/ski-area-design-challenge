// Standalone simulation calendar core. This module deliberately has no game,
// browser, Electron, terminal, timer, or filesystem dependencies.
export type Season = 'summer' | 'winter';
export type DailyPhase = 'overnight' | 'preOpen' | 'operating' | 'evening';
export type ClockRunState = 'paused' | 'running' | 'season-transition';
export type SimulationSpeed = 1 | 2 | 4 | 8;

export interface DailyPhaseConfig {
  overnightStart: number;
  preOpenStart: number;
  operatingStart: number;
  eveningStart: number;
}

export interface TimeScaleConfig {
  schemaVersion: 1;
  configVersion: number;
  winterWeeks: number;
  summerPeriods: number;
  realSecondsPerWinterWeek: number;
  speedMultipliers: readonly SimulationSpeed[];
  clockStepMinutes: number;
  uiUpdateHz: number;
  maxWallDeltaMs: number;
  initialSummerStart: string;
  winterStartMonth: number;
  winterStartDay: number;
  winterStartHour: number;
  dailyPhases: DailyPhaseConfig;
}

export const DEFAULT_TIME_CONFIG: TimeScaleConfig = {
  schemaVersion: 1,
  configVersion: 1,
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

export interface SimulationClock {
  schemaVersion: 1;
  resortYear: number;
  completedWinterSeasons: number;
  season: Season;
  seasonStartedAt: string;
  summerPeriod: number | null;
  winterWeek: number | null;
  absoluteGameMinute: number;
  calendarDate: string;
  minuteOfDay: number;
  weekday: number;
  dailyPhase: DailyPhase;
  speed: SimulationSpeed;
  runState: ClockRunState;
  transitionPending: Season | null;
}

export type TimeBoundaryEvent =
  | { type: 'dailyPhaseChanged'; at: string; from: DailyPhase; to: DailyPhase }
  | { type: 'dayStarted'; at: string; date: string; weekday: number }
  | { type: 'weekStarted'; at: string; week: number }
  | { type: 'weekEnded'; at: string; week: number }
  | { type: 'summerPeriodStarted'; at: string; period: number }
  | { type: 'summerPeriodEnded'; at: string; period: number }
  | { type: 'seasonEnded'; at: string; season: Season }
  | { type: 'seasonStarted'; at: string; season: Season }
  | { type: 'yearStarted'; at: string; resortYear: number }
  | { type: 'seasonTransitionPending'; at: string; target: Season };

export interface AdvanceResult {
  clock: SimulationClock;
  events: TimeBoundaryEvent[];
  simulatedMinutesAdvanced: number;
}

export type TimeBoundaryTarget =
  | 'next-day'
  | 'next-week'
  | 'next-season'
  | 'next-summer'
  | 'next-winter'
  | 'next-year';

export interface TimeEngineSnapshot {
  schemaVersion: 1;
  configVersion: number;
  clock: SimulationClock;
}

export interface TimeAdvanceContext {
  before: SimulationClock;
  after: SimulationClock;
  simulatedMinutes: number;
}

export interface TimeEventConsumer {
  onTimeAdvanced(context: TimeAdvanceContext): void;
  onBoundaryEvent(event: TimeBoundaryEvent): void;
}

const MINUTES_PER_DAY = 1_440;
export const MINUTES_PER_WEEK = 10_080;

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

function minuteOfDay(date: Date): number {
  return date.getUTCHours() * 60 + date.getUTCMinutes();
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
  const minute = minuteOfDay(date);
  let winterWeek = clock.winterWeek;
  if (clock.season === 'winter') {
    const elapsed = wholeMinutesBetween(asDate(clock.seasonStartedAt), date);
    winterWeek = Math.min(config.winterWeeks, Math.floor(elapsed / MINUTES_PER_WEEK) + 1);
  }
  return {
    ...clock,
    minuteOfDay: minute,
    weekday: date.getUTCDay(),
    dailyPhase: phaseAt(minute, config),
    winterWeek,
  };
}

function firstWinterStartAfter(date: Date, config: TimeScaleConfig): Date {
  let year = date.getUTCFullYear();
  const candidateForYear = (candidateYear: number): Date => {
    let value = new Date(Date.UTC(
      candidateYear,
      config.winterStartMonth,
      config.winterStartDay,
      config.winterStartHour,
    ));
    while (value.getUTCDay() !== 1) value = addMinutes(value, MINUTES_PER_DAY);
    return value;
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
    schemaVersion: 1,
    resortYear: 1,
    completedWinterSeasons: 0,
    season: 'summer',
    seasonStartedAt: iso(start),
    summerPeriod: 1,
    winterWeek: null,
    absoluteGameMinute: 0,
    calendarDate: iso(start),
    minuteOfDay: 0,
    weekday: start.getUTCDay(),
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
    const untilDay = MINUTES_PER_DAY - next.minuteOfDay;
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
    if (oldDate.getUTCDate() !== nextDate.getUTCDate() ||
      oldDate.getUTCMonth() !== nextDate.getUTCMonth() ||
      oldDate.getUTCFullYear() !== nextDate.getUTCFullYear()) {
      events.push({
        type: 'dayStarted',
        at: next.calendarDate,
        date: next.calendarDate.slice(0, 10),
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
    const minutes = MINUTES_PER_DAY - clock.minuteOfDay;
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
    schemaVersion: 1,
    configVersion: config.configVersion,
    clock: { ...clock, runState: 'paused' },
  };
}

function isIsoDate(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(new Date(value).getTime());
}

function validateClock(raw: SimulationClock, config: TimeScaleConfig): SimulationClock {
  if (!raw || typeof raw !== 'object' || raw.schemaVersion !== 1) {
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
  const snapshot = raw as Partial<TimeEngineSnapshot>;
  if (snapshot.schemaVersion !== 1) throw new Error('Unsupported snapshot schema');
  if (snapshot.configVersion !== config.configVersion) {
    throw new Error(`Snapshot requires time config ${snapshot.configVersion}`);
  }
  return validateClock(snapshot.clock as SimulationClock, config);
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
