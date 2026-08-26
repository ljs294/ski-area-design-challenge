import * as readline from 'node:readline';
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DEFAULT_TIME_CONFIG,
  advanceClock,
  advanceToBoundary,
  createClock,
  type SimulationClock,
  type SimulationSpeed,
} from '../../time-engine/src/timeEngine.ts';
import {
  JACKSON_NH_TEST_LOCATION,
  type ResortClimateBaseline,
  type ResortWeatherLocation,
} from './climateBaseline.ts';
import { fetchClimateBaseline } from './climateProviders.ts';
import {
  advanceWeather,
  createWeatherSnapshot,
  createWeatherState,
  nextWeatherEvent,
  restoreWeatherSnapshot,
  selectWeatherBand,
  type ElevationBand,
  type ForecastDay,
  type PrecipitationType,
  type WeatherAdvanceResult,
  type WeatherEvent,
  type WeatherState,
} from './weatherEngine.ts';

const moduleDirectory = dirname(fileURLToPath(import.meta.url));
const jacksonFixture = resolve(moduleDirectory, '../fixtures/jackson-nh-2010-2019.json');
const DATE_FORMAT = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  weekday: 'long',
  month: 'long',
  day: 'numeric',
});
const TIME_FORMAT = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  hour: 'numeric',
  minute: '2-digit',
});
const HELP = `
status                         Redraw the dashboard
play / pause                   Start or stop real-time advancement
speed 1|2|4|8[x]               Change simulation speed
step 1h|1d|1w                  Advance deterministically
skip day|week                  Advance one day or seven days
skip ahead week               Alias for skipping exactly seven days
skip to event                  Jump to the next notable weather event
weather                        Show the current-day forecast
weather on|off|toggle          Show or hide weather on the dashboard
weather hourly                 Show all 24 forecast hours
weather week                   Show the seven-day planning outlook
weather day 0..6               Show a forecast day
weather band base|mid|summit   Select the detailed elevation band
weather events                 List winter weather events
weather seed <value>            Regenerate this winter with a seed
save <path> / load <path>      Save or restore weather state
help / quit                    Show this help or exit
`.trim();

interface CliOptions {
  ansi: boolean;
  json: boolean;
  yes: boolean;
  seed: string;
  location: ResortWeatherLocation;
  customLocation: boolean;
  step?: string;
  skip?: string;
}

function argumentValue(args: string[], name: string): string | undefined {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
}

function numericArgument(args: string[], name: string, fallback: number): number {
  const raw = argumentValue(args, name);
  if (raw == null) return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value)) throw new Error(`${name} must be a number`);
  return value;
}

function parseOptions(args: string[]): CliOptions {
  const customLocation = ['--lat', '--lon', '--base', '--mid', '--summit'].some((flag) => args.includes(flag));
  const location: ResortWeatherLocation = customLocation ? {
    id: 'custom-resort',
    name: argumentValue(args, '--name') ?? 'Custom Resort',
    latitude: numericArgument(args, '--lat', JACKSON_NH_TEST_LOCATION.latitude),
    longitude: numericArgument(args, '--lon', JACKSON_NH_TEST_LOCATION.longitude),
    baseElevationM: numericArgument(args, '--base', JACKSON_NH_TEST_LOCATION.baseElevationM),
    midElevationM: numericArgument(args, '--mid', JACKSON_NH_TEST_LOCATION.midElevationM),
    summitElevationM: numericArgument(args, '--summit', JACKSON_NH_TEST_LOCATION.summitElevationM),
  } : { ...JACKSON_NH_TEST_LOCATION };
  if (location.baseElevationM > location.midElevationM || location.midElevationM > location.summitElevationM) {
    throw new Error('Elevations must be ordered base <= mid <= summit');
  }
  return {
    ansi: !args.includes('--no-ansi'),
    json: args.includes('--json'),
    yes: args.includes('--yes'),
    seed: argumentValue(args, '--seed') ?? 'jackson-demo-1',
    location,
    customLocation,
    step: argumentValue(args, '--step'),
    skip: argumentValue(args, '--skip'),
  };
}

function cToF(value: number): number {
  return Math.round(value * 9 / 5 + 32);
}

function cmToIn(value: number): number {
  return value / 2.54;
}

function progressBar(fraction: number, width = 20): string {
  const safe = Math.max(0, Math.min(1, fraction));
  const filled = Math.round(safe * width);
  return `[${'█'.repeat(filled)}${'░'.repeat(width - filled)}] ${Math.round(safe * 100).toString().padStart(3)}%`;
}

function calendarLines(clock: SimulationClock, running: boolean): string[] {
  const date = new Date(clock.calendarDate);
  const elapsedMinutes = Math.max(
    0,
    Math.round((date.getTime() - new Date(clock.seasonStartedAt).getTime()) / 60_000),
  );
  const minuteOfWeek = elapsedMinutes % 10_080;
  const totalWinterMinutes = DEFAULT_TIME_CONFIG.winterWeeks * 10_080;
  return [
    'CALENDAR',
    '',
    `Resort Year   ${clock.resortYear}`,
    'Season        Winter',
    `Winter Week   ${clock.winterWeek} / ${DEFAULT_TIME_CONFIG.winterWeeks}`,
    `Date          ${DATE_FORMAT.format(date)}`,
    `Time          ${TIME_FORMAT.format(date)}`,
    `Phase         ${clock.dailyPhase.toUpperCase()}`,
    `Speed         ${clock.speed}×`,
    `Status        ${running ? 'RUNNING' : 'PAUSED'}`,
    '',
    `Day      ${progressBar(clock.minuteOfDay / 1_440)}`,
    `Week     ${progressBar(minuteOfWeek / 10_080)}`,
    `Season   ${progressBar(elapsedMinutes / totalWinterMinutes)}`,
  ];
}

function weatherSymbol(type: PrecipitationType | undefined): string {
  if (type === 'snow') return 's';
  if (type === 'mixed') return 'm';
  if (type === 'rain') return 'r';
  return 'd';
}

function forecastSummary(day: ForecastDay): string {
  const hours = day.hours ?? [];
  const precip = hours.filter((hour) => hour.mid.precipitationMm > 0.01);
  if (!precip.length) {
    const max = day.maxTempC.mid;
    return max > 2 ? 'Dry and seasonably mild' : 'Dry and cold';
  }
  const first = new Date(precip[0].at).getUTCHours();
  const types = new Set(precip.map((hour) => hour.mid.precipitationType));
  const timing = first < 6 ? 'overnight' : first < 12 ? 'this morning' : first < 18 ? 'this afternoon' : 'this evening';
  if (types.has('snow') && (types.has('rain') || types.has('mixed'))) return `Mixed precipitation developing ${timing}`;
  if (types.has('snow')) return `Snow developing ${timing}`;
  return `Rain developing ${timing}`;
}

function dayForClock(state: WeatherState, clock: SimulationClock, offset = 0): ForecastDay | undefined {
  const date = new Date(new Date(clock.calendarDate).getTime() + offset * 86_400_000).toISOString().slice(0, 10);
  return state.latestForecast.days.find((day) => day.date === date);
}

function forecastLines(state: WeatherState, clock: SimulationClock, offset = 0): string[] {
  const day = dayForClock(state, clock, offset);
  if (!day) return ['Forecast is outside the generated winter.'];
  const range = (band: ElevationBand): string =>
    `${cToF(day.minTempC[band])}-${cToF(day.maxTempC[band])}°F`;
  const snow = (band: ElevationBand): string => {
    const inches = cmToIn(day.snowfallCm[band]);
    return inches < 0.1 ? '0 in' : `${Math.max(0.1, inches - 0.5).toFixed(1)}-${(inches + 0.5).toFixed(1)} in`;
  };
  return [
    `Base       ${range('base')}`,
    `Mid        ${range('mid')}`,
    `Summit     ${range('summit')}`,
    `Forecast   ${forecastSummary(day)}`,
    `Snow       Base ${snow('base')} · Mid ${snow('mid')} · Summit ${snow('summit')}`,
    `Confidence ${day.confidencePct}%`,
    `Event      ${day.eventSignal ?? 'None'}`,
  ];
}

function shortForecastDescription(day: ForecastDay): string {
  const hours = day.hours ?? [];
  const types = new Set(hours
    .filter((hour) => hour.mid.precipitationMm > 0.01)
    .map((hour) => hour.mid.precipitationType));
  if (types.has('snow') && (types.has('mixed') || types.has('rain'))) return 'snow/mix';
  if (types.has('snow')) return 'snow';
  if (types.has('mixed')) return 'mixed';
  if (types.has('rain')) return 'rain';
  return day.precipitationProbabilityPct >= 40 ? 'precip possible' : 'dry';
}

function weeklyForecastLines(state: WeatherState, clock: SimulationClock): string[] {
  const currentDate = new Date(clock.calendarDate);
  const days = Array.from({ length: 7 }, (_, offset) => dayForClock(state, clock, offset))
    .filter((day): day is ForecastDay => day != null);
  if (!days.length) return ['Seven-day forecast is unavailable.'];
  return days.flatMap((day, offset) => {
    const date = new Date(`${day.date}T12:00:00.000Z`);
    const label = new Intl.DateTimeFormat('en-US', {
      timeZone: 'UTC',
      weekday: 'short',
      month: 'short',
      day: 'numeric',
    }).format(date).padEnd(11);
    const band = state.selectedBand;
    const temperatures = `${cToF(day.minTempC[band])}-${cToF(day.maxTempC[band])}°F`.padEnd(9);
    const condition = shortForecastDescription(day).padEnd(15);
    const snow = `snow B/M/S ${cmToIn(day.snowfallCm.base).toFixed(1)}/${cmToIn(day.snowfallCm.mid).toFixed(1)}/${cmToIn(day.snowfallCm.summit).toFixed(1)} in`;
    const event = day.eventSignal ? `  Event: ${day.eventSignal}` : '';
    return [
      `${offset === 0 && date.toISOString().slice(0, 10) === currentDate.toISOString().slice(0, 10) ? 'Today' : label}`
        .padEnd(12)
        + `${temperatures} ${condition} ${snow}  ${day.confidencePct}%`,
      ...(event ? [`${''.padEnd(12)}${event.trim()}`] : []),
    ];
  });
}

function dashboard(
  state: WeatherState,
  clock: SimulationClock,
  running: boolean,
  weatherVisible: boolean,
): string {
  const day = dayForClock(state, clock);
  const selected = state.selectedBand;
  const sampleHours = [0, 3, 6, 9, 12, 15, 18, 21];
  const headings = sampleHours.map((hour) => `${hour === 0 ? '12a' : hour < 12 ? `${hour}a` : hour === 12 ? '12p' : `${hour - 12}p`}`.padStart(4)).join(' ');
  const temperatures = sampleHours.map((hour) => {
    const predicted = day?.hours?.[hour]?.[selected].temperatureC;
    return `${predicted == null ? '--' : cToF(predicted)}`.padStart(4);
  }).join(' ');
  const symbols = sampleHours.map((hour) =>
    weatherSymbol(day?.hours?.[hour]?.[selected].precipitationType).padStart(4)).join(' ');
  const lines = [
    'MOUNTAIN PLANNER — CALENDAR + WEATHER',
    state.location.name,
    '',
    ...calendarLines(clock, running),
    '',
  ];
  if (!weatherVisible) {
    lines.push('WEATHER   HIDDEN — use "weather on" to show the forecast');
    return lines.join('\n');
  }
  lines.push(
    "TODAY'S FORECAST",
    '',
    `Band ${selected} · Climate ${state.climateBaseline.source.toUpperCase()} 2010-2019`,
    `Forecast issued ${DATE_FORMAT.format(new Date(state.latestForecast.issuedAt))} ${TIME_FORMAT.format(new Date(state.latestForecast.issuedAt))}`,
    '',
    ...forecastLines(state, clock),
    '',
    headings,
    temperatures,
    symbols,
    '',
    'Legend: s snow · m mix · r rain · d dry',
    '',
    'SEVEN-DAY PLANNING OUTLOOK',
    `Temperatures shown for selected ${selected} band`,
    ...weeklyForecastLines(state, clock),
  );
  return lines.join('\n');
}

function parseStep(value: string): number {
  const match = value.match(/^(\d+)(m|h|d|w)$/i);
  if (!match) throw new Error('Step must look like 1m, 1h, 1d, or 1w');
  const amount = Number(match[1]);
  const multiplier = match[2].toLowerCase() === 'm' ? 1
    : match[2].toLowerCase() === 'h' ? 60
      : match[2].toLowerCase() === 'd' ? 1_440 : 10_080;
  return amount * multiplier;
}

async function loadBaseline(options: CliOptions): Promise<ResortClimateBaseline> {
  if (options.customLocation) {
    process.stdout.write(`Loading climate baseline for ${options.location.name}...\n`);
    return fetchClimateBaseline(options.location);
  }
  return JSON.parse(await readFile(jacksonFixture, 'utf8')) as ResortClimateBaseline;
}

function winterClock(): SimulationClock {
  return advanceToBoundary(createClock(DEFAULT_TIME_CONFIG), 'next-winter', DEFAULT_TIME_CONFIG).clock;
}

async function main(): Promise<void> {
  const options = parseOptions(process.argv.slice(2));
  const baseline = await loadBaseline(options);
  let clock = winterClock();
  let weather = createWeatherState(
    options.location,
    baseline,
    clock.seasonStartedAt,
    DEFAULT_TIME_CONFIG.winterWeeks,
    options.seed,
  );
  weather = advanceWeather(weather, clock.calendarDate).state;
  let running = false;
  let lastWallTime = performance.now();
  let minuteAccumulator = 0;
  let interval: NodeJS.Timeout | null = null;
  let rl: readline.Interface | null = null;
  let lastCompactStatusAt = 0;
  let weatherVisible = true;
  const canRedrawInPlace = options.ansi && process.stdout.isTTY;

  const redraw = (): void => {
    if (options.json) return;
    const output = dashboard(weather, clock, running, weatherVisible);
    if (canRedrawInPlace) {
      process.stdout.write(`\x1b[2J\x1b[H${output}\ntime> `);
    } else {
      process.stdout.write(`${output}\n\n`);
    }
  };

  const advanceMinutes = (minutes: number): WeatherAdvanceResult => {
    const before = clock;
    const result = advanceClock(clock, minutes, DEFAULT_TIME_CONFIG);
    clock = result.clock;
    const weatherResult = advanceWeather(weather, clock.calendarDate);
    weather = weatherResult.state;
    if (before.season === 'winter' && clock.season !== 'winter') running = false;
    return weatherResult;
  };

  const setRunning = (value: boolean): void => {
    running = value && clock.season === 'winter';
    lastWallTime = performance.now();
    minuteAccumulator = 0;
  };

  const tick = (): void => {
    const now = performance.now();
    const elapsedMs = Math.min(DEFAULT_TIME_CONFIG.maxWallDeltaMs, now - lastWallTime);
    lastWallTime = now;
    if (running) {
      minuteAccumulator += elapsedMs
        * (10_080 / (DEFAULT_TIME_CONFIG.realSecondsPerWinterWeek * 1_000))
        * clock.speed;
      const wholeMinutes = Math.floor(minuteAccumulator);
      if (wholeMinutes > 0) {
        minuteAccumulator -= wholeMinutes;
        advanceMinutes(wholeMinutes);
      }
    }
    if (running) {
      if (canRedrawInPlace) redraw();
      else if (now - lastCompactStatusAt >= 1_000) {
        lastCompactStatusAt = now;
        const date = new Date(clock.calendarDate);
        const day = dayForClock(weather, clock);
        process.stdout.write(
          `${date.toISOString()} week=${clock.winterWeek} speed=${clock.speed}x `
          + `forecast=${day ? forecastSummary(day) : 'unavailable'}\n`,
        );
      }
    }
  };

  const showHourly = (offset = 0): void => {
    const day = dayForClock(weather, clock, offset);
    if (!day?.hours) {
      process.stdout.write('Hourly detail is available only for forecast days 0-6.\n');
      return;
    }
    process.stdout.write(`\n${day.date} — ${weather.selectedBand}\n`);
    for (const hour of day.hours) {
      const data = hour[weather.selectedBand];
      const at = new Date(hour.at).getUTCHours().toString().padStart(2, '0');
      process.stdout.write(
        `${at}:00  ${cToF(data.temperatureC).toString().padStart(3)}°F  `
        + `${data.precipitationType.padEnd(5)}  ${data.precipitationMm.toFixed(2)} mm  `
        + `${cmToIn(data.snowfallCm).toFixed(1)} in snow\n`,
      );
    }
  };

  const execute = async (input: string): Promise<boolean> => {
    const command = input.trim();
    if (!command || command === 'status') {
      redraw();
      return true;
    }
    if (command === 'weather') {
      process.stdout.write(`\n${forecastLines(weather, clock).join('\n')}\n`);
      return true;
    }
    if (command === 'play') setRunning(true);
    else if (command === 'pause') setRunning(false);
    else if (/^speed (1|2|4|8)(?:x|×)?$/i.test(command)) {
      const speed = Number(command.match(/^speed (1|2|4|8)/i)?.[1]) as SimulationSpeed;
      clock = { ...clock, speed };
    } else if (command.startsWith('step ')) {
      setRunning(false);
      advanceMinutes(parseStep(command.slice(5)));
    } else if (command === 'skip day') {
      setRunning(false);
      advanceMinutes(1_440);
    } else if (
      command === 'skip week'
      || command === 'skip ahead week'
      || command === 'skip ahead a week'
    ) {
      setRunning(false);
      advanceMinutes(10_080);
    } else if (command === 'skip to event') {
      setRunning(false);
      const event = nextWeatherEvent(weather, clock.calendarDate);
      if (!event) process.stdout.write('No later notable event exists in this winter.\n');
      else {
        const minutes = Math.max(0, Math.ceil(
          (new Date(event.startsAt).getTime() - new Date(clock.calendarDate).getTime()) / 60_000,
        ));
        advanceMinutes(minutes);
        process.stdout.write(`Reached ${event.name} at ${event.startsAt}.\n`);
      }
    } else if (/^weather (on|off|toggle)$/.test(command)) {
      const mode = command.split(' ')[1];
      weatherVisible = mode === 'toggle' ? !weatherVisible : mode === 'on';
    } else if (command === 'weather hourly') {
      showHourly();
      return true;
    } else if (command === 'weather week') {
      process.stdout.write(`\nSEVEN-DAY PLANNING OUTLOOK\n${weeklyForecastLines(weather, clock).join('\n')}\n`);
      return true;
    }
    else if (/^weather day [0-6]$/.test(command)) {
      const offset = Number(command.at(-1));
      process.stdout.write(`\n${forecastLines(weather, clock, offset).join('\n')}\n`);
      return true;
    } else if (/^weather band (base|mid|summit)$/.test(command)) {
      weather = selectWeatherBand(weather, command.split(' ')[2] as ElevationBand);
    } else if (command === 'weather events') {
      const future = weather.truth.events.filter((event) => new Date(event.endsAt) >= new Date(clock.calendarDate));
      process.stdout.write(`${future.slice(0, 30).map((event: WeatherEvent) =>
        `${event.startsAt.slice(0, 10)}  ${event.name} [${event.severity}]`).join('\n') || 'No events.'}\n`);
      return true;
    } else if (command.startsWith('weather seed ')) {
      setRunning(false);
      const seed = command.slice('weather seed '.length).trim();
      if (!seed) throw new Error('A non-empty seed is required');
      weather = createWeatherState(
        options.location,
        baseline,
        clock.seasonStartedAt,
        DEFAULT_TIME_CONFIG.winterWeeks,
        seed,
      );
      weather = advanceWeather(weather, clock.calendarDate).state;
    } else if (command.startsWith('save ')) {
      await writeFile(command.slice(5).trim(), `${JSON.stringify({
        schemaVersion: 1,
        clock: { ...clock, runState: 'paused' },
        weather: createWeatherSnapshot(weather),
      }, null, 2)}\n`);
    } else if (command.startsWith('load ')) {
      setRunning(false);
      const parsed = JSON.parse(await readFile(command.slice(5).trim(), 'utf8')) as {
        schemaVersion: number;
        clock: SimulationClock;
        weather: unknown;
      };
      if (parsed.schemaVersion !== 1 || parsed.clock?.schemaVersion !== 1) throw new Error('Invalid save schema');
      const restoredWeather = restoreWeatherSnapshot(parsed.weather);
      clock = createClock(DEFAULT_TIME_CONFIG, { ...parsed.clock, runState: 'paused' });
      weather = restoredWeather;
    } else if (command === 'help') {
      process.stdout.write(`${HELP}\n`);
      return true;
    } else if (command === 'quit' || command === 'exit') return false;
    else throw new Error(`Unknown command: ${command}. Type help.`);
    redraw();
    return true;
  };

  if (options.step || options.skip) {
    const initialClock = structuredClone(clock);
    let diagnosticResult: WeatherAdvanceResult;
    if (options.step) {
      diagnosticResult = advanceMinutes(parseStep(options.step));
    } else if (options.skip === 'day') {
      diagnosticResult = advanceMinutes(1_440);
    } else if (options.skip === 'week') {
      diagnosticResult = advanceMinutes(10_080);
    } else if (options.skip === 'event') {
      const event = nextWeatherEvent(weather, clock.calendarDate);
      if (!event) throw new Error('No later notable event exists in this winter');
      diagnosticResult = advanceMinutes(Math.ceil(
        (new Date(event.startsAt).getTime() - new Date(clock.calendarDate).getTime()) / 60_000,
      ));
    } else {
      throw new Error('--skip accepts day, week, or event');
    }
    if (options.json) {
      process.stdout.write(`${JSON.stringify({
        initialClock,
        finalClock: clock,
        simulatedMinutesAdvanced: clock.absoluteGameMinute - initialClock.absoluteGameMinute,
        processedWeatherHours: diagnosticResult.processedHours.length,
        startedEvents: diagnosticResult.startedEvents,
        currentDayForecast: dayForClock(weather, clock),
        activeConfigurationVersion: DEFAULT_TIME_CONFIG.configVersion,
        location: weather.location,
        seed: weather.seed,
      }, null, 2)}\n`);
    }
    return;
  }

  interval = setInterval(tick, 1_000 / DEFAULT_TIME_CONFIG.uiUpdateHz);
  rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: canRedrawInPlace });
  redraw();
  rl.on('line', (line) => {
    void execute(line).then((keepGoing) => {
      if (!keepGoing) rl?.close();
      else rl?.prompt();
    }).catch((error: unknown) => {
      process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
      rl?.prompt();
    });
  });
  rl.on('close', () => {
    if (interval) clearInterval(interval);
    process.stdout.write('\nWeather engine stopped.\n');
  });
}

main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
