import type { SimulationClock, TimeScaleConfig } from '../types/simulation';
import { advanceClock, DEFAULT_TIME_CONFIG } from '../../time-engine/src/timeEngine';

export type DeveloperConsoleCommand =
  | { readonly kind: 'help' }
  | { readonly kind: 'clear' }
  | { readonly kind: 'time' }
  | { readonly kind: 'skip'; readonly minutes: number };

export interface SimulationTimeDiscontinuity {
  readonly revision: number;
  readonly absoluteGameMinute: number;
  readonly localMidnightAbsoluteMinute: number;
}

export interface DeveloperClockSkip {
  readonly before: SimulationClock;
  readonly after: SimulationClock;
  readonly requestedMinutes: number;
  readonly skippedMinutes: number;
}

const UNIT_MINUTES: Readonly<Record<string, number>> = Object.freeze({
  m: 1, min: 1, mins: 1, minute: 1, minutes: 1,
  h: 60, hr: 60, hrs: 60, hour: 60, hours: 60,
  // Composite winter days/weeks are the same twelve-hour operating window.
  d: 720, day: 720, days: 720,
  w: 720, week: 720, weeks: 720,
});

export const DEVELOPER_CONSOLE_HELP = Object.freeze([
  'skip <duration>  Jump forward without simulating elapsed world time (examples: skip 30m, skip 3h, skip 1d).',
  'time             Show the current game timestamp.',
  'clear            Clear console output.',
  'help             Show available commands.',
]);

export interface DeveloperConsoleEnvironment {
  readonly development: boolean;
  readonly desktop: boolean;
}

/** Available in the desktop game and development builds; web releases require an explicit diagnostic flag. */
export function isDeveloperConsoleEnabled(search?: string, environment?: DeveloperConsoleEnvironment): boolean {
  const runtime = environment ?? {
    development: import.meta.env.DEV,
    desktop: typeof window !== 'undefined' && window.desktop?.isDesktop === true,
  };
  if (runtime.development || runtime.desktop) return true;
  const query = search ?? (typeof window === 'undefined' ? '' : window.location.search);
  return new URLSearchParams(query).has('dev-console');
}

export function parseDeveloperConsoleCommand(source: string): DeveloperConsoleCommand {
  const command = source.trim().toLowerCase().replace(/\s+/g, ' ');
  if (command === 'help' || command === '?') return { kind: 'help' };
  if (command === 'clear' || command === 'cls') return { kind: 'clear' };
  if (command === 'time' || command === 'date') return { kind: 'time' };
  const skip = /^(?:skip(?:\s+ahead)?|skip-ahead|skipahead|advance)(?:\s+(.*))?$/.exec(command);
  if (!skip) throw new Error('Unknown command. Type "help" for available commands.');
  const duration = skip[1]?.trim() || '1m';
  const match = /^(?:(\d+(?:\.\d+)?)\s*)?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days|w|week|weeks)$/.exec(duration)
    ?? /^(\d+(?:\.\d+)?)$/.exec(duration);
  if (!match) throw new Error('Invalid duration. Try "skip 30m", "skip 3h", or "skip 1d".');
  const amount = match[1] === undefined ? 1 : Number(match[1]);
  const minutes = amount * UNIT_MINUTES[match[2] ?? 'm']!;
  if (!Number.isSafeInteger(minutes) || minutes <= 0) {
    throw new Error('Skip duration must resolve to a positive whole number of minutes.');
  }
  return { kind: 'skip', minutes };
}

/** Calculate and publish only the target clock; callers deliberately bypass world-step consumers. */
export function skipClockWithoutSimulation(clock: SimulationClock, minutes: number,
  config: TimeScaleConfig = DEFAULT_TIME_CONFIG): DeveloperClockSkip {
  if (!Number.isSafeInteger(minutes) || minutes <= 0) throw new Error('Skip minutes must be a positive safe integer.');
  if (clock.season !== 'winter' || clock.runState === 'season-transition') {
    throw new Error('Time skipping is available only during an active winter season.');
  }
  const result = advanceClock({ ...clock, runState: 'paused' }, minutes, config);
  if (result.simulatedMinutesAdvanced <= 0) throw new Error('The game clock could not advance.');
  return Object.freeze({ before: clock, after: Object.freeze({ ...result.clock, runState: 'paused' }),
    requestedMinutes: minutes, skippedMinutes: result.simulatedMinutesAdvanced });
}
