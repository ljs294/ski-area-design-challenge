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
  schemaVersion: 2;
  configVersion: number;
  timezone: string;
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

export interface SimulationClock {
  schemaVersion: 2;
  timezone: string;
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
  schemaVersion: 2;
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
