export type Season = 'summer' | 'winter';
export type DailyPhase = 'overnight' | 'preOpen' | 'operating' | 'evening';
export type ClockRunState = 'paused' | 'running' | 'season-transition';
/** Named player-facing speed tiers. */
export type SimulationSpeed = 'slow' | 'normal' | 'fast' | 'ultrafast';

/** Numeric values accepted while hydrating saves written by the v2 engine. */
export type LegacySimulationSpeed = 1 | 2 | 4 | 8;
export type SimulationSpeedValue = SimulationSpeed | LegacySimulationSpeed;

/** The composite winter operating day is twelve hours represented by seconds. */
export const SIMULATION_SECONDS_PER_WEEK = 43_200;
export const SIMULATION_SPEED_RATES: Readonly<Record<SimulationSpeed, number>> = Object.freeze({
  slow: 30,
  normal: 60,
  fast: 240,
  ultrafast: 960,
});

export interface DailyPhaseConfig {
  overnightStart: number;
  preOpenStart: number;
  operatingStart: number;
  eveningStart: number;
}

export interface TimeScaleConfig {
  schemaVersion: 3;
  configVersion: number;
  timezone: string;
  winterWeeks: number;
  summerPeriods: number;
  /** Kept for save/config compatibility. Normal is 720 real seconds. */
  realSecondsPerWinterWeek: number;
  speedMultipliers: readonly SimulationSpeedValue[];
  clockStepMinutes: number;
  uiUpdateHz: number;
  maxWallDeltaMs: number;
  initialSummerStart: string;
  winterStartMonth: number;
  winterStartDay: number;
  winterStartHour: number;
  dailyPhases: DailyPhaseConfig;
}

export interface SimulationClock {
  schemaVersion: 3;
  timezone: string;
  resortYear: number;
  completedWinterSeasons: number;
  season: Season;
  seasonStartedAt: string;
  summerPeriod: number | null;
  winterWeek: number | null;
  /** Authoritative elapsed simulation seconds for the active winter. */
  elapsedSimSecond: number;
  /** Seconds elapsed in the current composite operating day (0..43,199). */
  weekSecond: number;
  absoluteGameMinute: number;
  calendarDate: string;
  minuteOfDay: number;
  weekday: number;
  dailyPhase: DailyPhase;
  /** New saves contain named speeds; numeric values are retained for old callers during rollout. */
  speed: SimulationSpeedValue;
  runState: ClockRunState;
  transitionPending: Season | null;
  /** Compatibility projection base; omitted by old snapshots and reconstructed during migration. */
  winterStartAbsoluteGameMinute?: number;
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
  simulatedSecondsAdvanced: number;
}

export type TimeBoundaryTarget =
  | 'next-day'
  | 'next-week'
  | 'next-season'
  | 'next-summer'
  | 'next-winter'
  | 'next-year';

export interface TimeEngineSnapshot {
  schemaVersion: 3;
  configVersion: number;
  clock: SimulationClock;
}

export interface TimeAdvanceContext {
  before: SimulationClock;
  after: SimulationClock;
  simulatedMinutes: number;
  simulatedSeconds?: number;
}

export interface TimeEventConsumer {
  onTimeAdvanced(context: TimeAdvanceContext): void;
  onBoundaryEvent(event: TimeBoundaryEvent): void;
}
