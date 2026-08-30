import * as readline from 'node:readline';
import { readFile, writeFile } from 'node:fs/promises';
import {
  DEFAULT_TIME_CONFIG,
  MINUTES_PER_WEEK,
  advanceClock,
  advanceSummerPeriod,
  advanceToBoundary,
  confirmSeasonTransition,
  createClock,
  createTimeSnapshot,
  describeTimeEvent,
  getSummerPeriodRange,
  restoreTimeSnapshot,
  type AdvanceResult,
  type SimulationClock,
  type SimulationSpeed,
  type TimeBoundaryEvent,
  type TimeBoundaryTarget,
  type TimeScaleConfig,
} from './timeEngine.ts';

const DATE_FORMAT = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  weekday: 'long',
  month: 'long',
  day: 'numeric',
});
const SHORT_DATE_FORMAT = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'short',
  day: 'numeric',
});
const TIME_FORMAT = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  hour: 'numeric',
  minute: '2-digit',
});

const HELP = `
status                    Redraw the complete dashboard
play / pause              Start or stop real-time advancement
speed 1|2|4|8             Change simulation speed
step 1m|1h|1d|1w          Advance a fixed amount (winter only)
skip day|week             Advance one day or one week
skip season               Jump to the next season
skip to summer|winter     Jump to the next named season
skip year                 Jump to the equivalent point next resort year
advance summer            Advance one summer planning period
confirm                   Confirm a pending season skip or transition
events                    Show recent events
events all                Show all events from the last advancement
events on|off             Toggle live event display
save <path> / load <path> Save or restore a clock snapshot
config                    Show timing configuration
help / quit               Show this help or exit
`.trim();

function pad(label: string, value: string | number, width = 14): string {
  return `${label.padEnd(width)}${value}`;
}

function bar(fraction: number, width = 20): string {
  const safe = Math.max(0, Math.min(1, fraction));
  const filled = Math.round(safe * width);
  return `[${'█'.repeat(filled)}${'░'.repeat(width - filled)}] ${(safe * 100).toFixed(0).padStart(3)}%`;
}

function formatDuration(realSeconds: number): string {
  if (!Number.isFinite(realSeconds) || realSeconds < 0) return '—';
  const total = Math.ceil(realSeconds);
  const hours = Math.floor(total / 3_600);
  const minutes = Math.floor((total % 3_600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`
    : `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

function winterElapsedMinutes(clock: SimulationClock): number {
  return Math.max(
    0,
    Math.round(
      (new Date(clock.calendarDate).getTime() - new Date(clock.seasonStartedAt).getTime()) / 60_000,
    ),
  );
}

function dashboard(
  clock: SimulationClock,
  config: TimeScaleConfig,
  recentEvents: readonly TimeBoundaryEvent[],
  running: boolean,
): string {
  const date = new Date(clock.calendarDate);
  const lines = ['MOUNTAIN PLANNER — TIME ENGINE', ''];
  lines.push(pad('Resort Year', clock.resortYear));
  lines.push(pad('Season', clock.season === 'winter' ? 'Winter' : 'Summer'));
  if (clock.season === 'winter') {
    lines.push(pad('Winter Week', `${clock.winterWeek} / ${config.winterWeeks}`));
    lines.push(pad('Date', DATE_FORMAT.format(date)));
    lines.push(pad('Time', TIME_FORMAT.format(date)));
    lines.push(pad('Phase', clock.dailyPhase.toUpperCase()));
  } else {
    const range = getSummerPeriodRange(clock, config);
    lines.push(pad('Summer Period', `${clock.summerPeriod} / ${config.summerPeriods}`));
    if (range) {
      lines.push(pad(
        'Date Range',
        `${SHORT_DATE_FORMAT.format(new Date(range.start))} – ${SHORT_DATE_FORMAT.format(new Date(range.end))}`,
      ));
    }
  }
  lines.push(pad('Speed', `${clock.speed}×`));
  lines.push(pad('Status', running ? 'RUNNING' : clock.runState.replace('-', ' ').toUpperCase()));
  lines.push('');

  if (clock.season === 'winter') {
    const elapsed = winterElapsedMinutes(clock);
    const weekElapsed = elapsed % MINUTES_PER_WEEK;
    const seasonMinutes = config.winterWeeks * MINUTES_PER_WEEK;
    lines.push(pad('Day', bar(clock.minuteOfDay / 1_440), 10));
    lines.push(pad('Week', bar(weekElapsed / MINUTES_PER_WEEK), 10));
    lines.push(pad('Season', bar(elapsed / seasonMinutes), 10));
    lines.push('');
    const simMinutesPerRealSecond =
      (MINUTES_PER_WEEK / config.realSecondsPerWinterWeek) * clock.speed;
    const toDay = 1_440 - clock.minuteOfDay;
    const toWeek = MINUTES_PER_WEEK - weekElapsed;
    const toSeason = seasonMinutes - elapsed;
    lines.push(pad('Next day', `${formatDuration(toDay / simMinutesPerRealSecond)} real time`));
    lines.push(pad('Next week', `${formatDuration(toWeek / simMinutesPerRealSecond)} real time`));
    lines.push(pad('Season end', `${formatDuration(toSeason / simMinutesPerRealSecond)} real time`));
  } else {
    const period = clock.summerPeriod ?? 1;
    lines.push(pad('Summer', bar((period - 1) / config.summerPeriods), 10));
    lines.push('');
    lines.push('Summer advances only when you use "advance summer" or a skip command.');
  }

  lines.push('', 'Recent events');
  const visible = recentEvents.slice(-5);
  if (visible.length === 0) lines.push('• No events yet');
  else for (const event of visible) lines.push(`• ${describeTimeEvent(event)}`);
  return lines.join('\n');
}

function compactStatus(clock: SimulationClock, running: boolean): string {
  if (clock.season === 'summer') {
    return `[${running ? 'RUN' : 'PAUSE'}] Summer ${clock.resortYear}, period ${clock.summerPeriod}`;
  }
  return `[${running ? 'RUN' : 'PAUSE'} ${clock.speed}×] Winter ${clock.resortYear}, week ${clock.winterWeek} — ` +
    `${DATE_FORMAT.format(new Date(clock.calendarDate))} ${TIME_FORMAT.format(new Date(clock.calendarDate))}`;
}

function parseStep(value: string): number {
  const match = /^(\d+)(m|h|d|w)$/i.exec(value);
  if (!match) throw new Error('Step must look like 1m, 1h, 1d, or 1w');
  const amount = Number(match[1]);
  const unit = match[2].toLowerCase();
  const multiplier = unit === 'm' ? 1 : unit === 'h' ? 60 : unit === 'd' ? 1_440 : MINUTES_PER_WEEK;
  return amount * multiplier;
}

function parseBoundary(words: string[]): TimeBoundaryTarget {
  const phrase = words.join(' ').toLowerCase();
  if (phrase === 'day') return 'next-day';
  if (phrase === 'week') return 'next-week';
  if (phrase === 'season') return 'next-season';
  if (phrase === 'to summer' || phrase === 'summer') return 'next-summer';
  if (phrase === 'to winter' || phrase === 'winter') return 'next-winter';
  if (phrase === 'year') return 'next-year';
  throw new Error('Skip target must be day, week, season, summer, winter, or year');
}

function summarizeEvents(events: readonly TimeBoundaryEvent[]) {
  if (events.length <= 1_000) return { events, eventCounts: undefined };
  const counts: Record<string, number> = {};
  for (const event of events) counts[event.type] = (counts[event.type] ?? 0) + 1;
  return { events: events.slice(-100), eventCounts: counts };
}

class TerminalRuntime {
  private clock: SimulationClock;
  private readonly config: TimeScaleConfig;
  private readonly noAnsi: boolean;
  private readonly rl: readline.Interface;
  private timer: NodeJS.Timeout | null = null;
  private running = false;
  private accumulator = 0;
  private lastWallMs = performance.now();
  private lastRenderMs = 0;
  private history: TimeBoundaryEvent[] = [];
  private lastAdvanceEvents: TimeBoundaryEvent[] = [];
  private liveEvents = true;
  private pendingSkip: TimeBoundaryTarget | null = null;
  private message = '';
  private commandQueue: Promise<void> = Promise.resolve();

  constructor(clock: SimulationClock, config: TimeScaleConfig, noAnsi: boolean) {
    this.clock = clock;
    this.config = config;
    this.noAnsi = noAnsi || !process.stdout.isTTY;
    this.rl = readline.createInterface({ input: process.stdin, output: process.stdout });
    this.rl.setPrompt('time> ');
  }

  start(): void {
    // readline can deliver another line while an asynchronous skip or file
    // operation is still yielding. Serialize commands so fast input cannot
    // observe a half-finished season transition.
    this.rl.on('line', (line) => {
      this.commandQueue = this.commandQueue.then(() => this.execute(line));
    });
    this.rl.on('close', () => this.shutdown());
    this.render(true);
  }

  private record(result: AdvanceResult): void {
    this.clock = result.clock;
    this.lastAdvanceEvents = result.events;
    this.history.push(...result.events);
    if (this.history.length > 5_000) this.history = this.history.slice(-5_000);
    if (this.liveEvents && result.events.length > 0) {
      this.message = result.events.slice(-3).map(describeTimeEvent).join(' · ');
    }
  }

  private setRunning(running: boolean): void {
    if (running && this.clock.season !== 'winter') {
      this.message = 'Summer is period-based. Use "advance summer" or "skip to winter".';
      return;
    }
    if (running && this.clock.runState === 'season-transition') {
      this.message = 'Confirm the pending season transition first.';
      return;
    }
    this.running = running;
    this.clock = { ...this.clock, runState: running ? 'running' : 'paused' };
    this.accumulator = 0;
    this.lastWallMs = performance.now();
    if (running && !this.timer) {
      this.timer = setInterval(() => this.tick(), 50);
    } else if (!running && this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private tick(): void {
    if (!this.running) return;
    const now = performance.now();
    const elapsedMs = Math.min(this.config.maxWallDeltaMs, Math.max(0, now - this.lastWallMs));
    this.lastWallMs = now;
    const minutesPerMs =
      MINUTES_PER_WEEK / (this.config.realSecondsPerWinterWeek * 1_000) * this.clock.speed;
    this.accumulator += elapsedMs * minutesPerMs;
    const steps = Math.floor(this.accumulator / this.config.clockStepMinutes) *
      this.config.clockStepMinutes;
    if (steps > 0) {
      const result = advanceClock(this.clock, steps, this.config);
      this.record(result);
      this.accumulator -= result.simulatedMinutesAdvanced;
      if (this.clock.season !== 'winter') {
        this.setRunning(false);
        this.message = 'Winter completed. The clock is paused in summer.';
      } else {
        this.clock = { ...this.clock, runState: 'running' };
      }
    }
    if (now - this.lastRenderMs >= 1_000 / this.config.uiUpdateHz) {
      this.lastRenderMs = now;
      this.render();
    }
  }

  private render(force = false): void {
    if (this.noAnsi) {
      if (force || !this.running || performance.now() - this.lastRenderMs >= 1_000) {
        process.stdout.write(`${compactStatus(this.clock, this.running)}${this.message ? ` — ${this.message}` : ''}\n`);
        this.message = '';
        if (!this.running) this.rl.prompt();
      }
      return;
    }
    process.stdout.write('\x1b[2J\x1b[H');
    process.stdout.write(dashboard(this.clock, this.config, this.history, this.running));
    if (this.message) process.stdout.write(`\n\n${this.message}`);
    process.stdout.write(`\n\ntime> ${this.rl.line}`);
    this.message = '';
  }

  private async skip(boundary: TimeBoundaryTarget): Promise<void> {
    this.setRunning(false);
    this.message = `⠋ Skipping to ${boundary.replace('next-', 'next ')}…`;
    this.render(true);
    await new Promise<void>((resolve) => setImmediate(resolve));
    this.record(advanceToBoundary(this.clock, boundary, this.config));
    this.clock = { ...this.clock, runState: 'paused' };
    this.message = `Reached ${this.clock.season} — ${this.clock.calendarDate}`;
  }

  private async execute(rawLine: string): Promise<void> {
    const line = rawLine.trim();
    const words = line.split(/\s+/);
    const command = words[0]?.toLowerCase();
    try {
      if (!line || command === 'status') {
        // Redraw below.
      } else if (command === 'play') {
        this.setRunning(true);
      } else if (command === 'pause') {
        this.setRunning(false);
      } else if (command === 'speed') {
        const speed = Number(words[1]) as SimulationSpeed;
        if (!this.config.speedMultipliers.includes(speed)) throw new Error('Speed must be 1, 2, 4, or 8');
        this.clock = { ...this.clock, speed };
        this.message = `Speed set to ${speed}×`;
      } else if (command === 'step') {
        this.setRunning(false);
        this.record(advanceClock(this.clock, parseStep(words[1] ?? ''), this.config));
      } else if (command === 'skip') {
        const boundary = parseBoundary(words.slice(1));
        if (boundary === 'next-day' || boundary === 'next-week') {
          await this.skip(boundary);
        } else {
          this.pendingSkip = boundary;
          this.message = `Type "confirm" to skip to ${boundary.replace('next-', 'the next ')}.`;
        }
      } else if (command === 'advance' && words[1]?.toLowerCase() === 'summer') {
        this.setRunning(false);
        this.record(advanceSummerPeriod(this.clock, this.config));
      } else if (command === 'confirm') {
        if (this.pendingSkip) {
          const target = this.pendingSkip;
          this.pendingSkip = null;
          await this.skip(target);
        } else {
          this.record(confirmSeasonTransition(this.clock, this.config));
        }
      } else if (command === 'events') {
        if (words[1] === 'on' || words[1] === 'off') {
          this.liveEvents = words[1] === 'on';
          this.message = `Live events ${this.liveEvents ? 'enabled' : 'disabled'}`;
        } else {
          const source = words[1] === 'all' ? this.lastAdvanceEvents : this.history.slice(-20);
          this.message = source.length
            ? source.map((event) => `• ${event.at} — ${describeTimeEvent(event)}`).join('\n')
            : 'No events recorded.';
        }
      } else if (command === 'save') {
        const file = words.slice(1).join(' ');
        if (!file) throw new Error('Provide a snapshot path');
        await writeFile(file, JSON.stringify(createTimeSnapshot(this.clock, this.config), null, 2), 'utf8');
        this.message = `Saved ${file}`;
      } else if (command === 'load') {
        const file = words.slice(1).join(' ');
        if (!file) throw new Error('Provide a snapshot path');
        this.setRunning(false);
        const raw = JSON.parse(await readFile(file, 'utf8')) as unknown;
        this.clock = restoreTimeSnapshot(raw, this.config);
        this.history = [];
        this.lastAdvanceEvents = [];
        this.message = `Loaded ${file}`;
      } else if (command === 'config') {
        this.message = JSON.stringify(this.config, null, 2);
      } else if (command === 'help') {
        this.message = HELP;
      } else if (command === 'quit' || command === 'exit') {
        this.rl.close();
        return;
      } else {
        throw new Error(`Unknown command. Type "help" for available commands.`);
      }
    } catch (error) {
      this.message = error instanceof Error ? error.message : String(error);
    }
    this.render(true);
  }

  private shutdown(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.running = false;
    if (process.stdin.isTTY) process.stdout.write('\nTime engine stopped.\n');
  }
}

interface CliOptions {
  noAnsi: boolean;
  json: boolean;
  yes: boolean;
  load?: string;
  save?: string;
  step?: string;
  skip?: string;
}

function parseArgs(args: string[]): CliOptions {
  const options: CliOptions = {
    noAnsi: args.includes('--no-ansi'),
    json: args.includes('--json'),
    yes: args.includes('--yes'),
  };
  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg === '--load') options.load = args[++i];
    else if (arg === '--save') options.save = args[++i];
    else if (arg === '--step') options.step = args[++i];
    else if (arg === '--skip') options.skip = args[++i];
  }
  return options;
}

async function runDiagnostic(options: CliOptions): Promise<void> {
  let clock = options.load
    ? restoreTimeSnapshot(JSON.parse(await readFile(options.load, 'utf8')) as unknown)
    : createClock();
  const initialClock = { ...clock };
  const events: TimeBoundaryEvent[] = [];
  let simulatedMinutesAdvanced = 0;
  if (options.skip) {
    if (!options.yes) throw new Error('Non-interactive skips require --yes');
    const result = advanceToBoundary(clock, parseBoundary(options.skip.split(/\s+/)));
    clock = result.clock;
    events.push(...result.events);
    simulatedMinutesAdvanced += result.simulatedMinutesAdvanced;
  }
  if (options.step) {
    const result = advanceClock(clock, parseStep(options.step));
    clock = result.clock;
    events.push(...result.events);
    simulatedMinutesAdvanced += result.simulatedMinutesAdvanced;
  }
  clock = { ...clock, runState: 'paused' };
  if (options.save) {
    await writeFile(options.save, JSON.stringify(createTimeSnapshot(clock), null, 2), 'utf8');
  }
  if (options.json) {
    const eventOutput = summarizeEvents(events);
    process.stdout.write(`${JSON.stringify({
      initialClock,
      finalClock: clock,
      simulatedMinutesAdvanced,
      ...eventOutput,
      configVersion: DEFAULT_TIME_CONFIG.configVersion,
    }, null, 2)}\n`);
  } else {
    process.stdout.write(`${dashboard(clock, DEFAULT_TIME_CONFIG, events, false)}\n`);
  }
}

async function main(): Promise<void> {
  const options = parseArgs(process.argv.slice(2));
  const diagnostic = Boolean(options.load || options.save || options.step || options.skip || options.json);
  if (diagnostic) {
    await runDiagnostic(options);
    return;
  }
  new TerminalRuntime(createClock(), DEFAULT_TIME_CONFIG, options.noAnsi).start();
}

void main().catch((error: unknown) => {
  process.stderr.write(`Time engine error: ${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
