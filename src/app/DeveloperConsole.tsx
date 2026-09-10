import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { SimulationClock } from '../types/simulation';
import type { SnowAddResult } from '../types/dualClock';
import { DEVELOPER_CONSOLE_HELP, parseDeveloperConsoleCommand,
  isDeveloperConsoleEnabled, type DeveloperClockSkip } from './developerConsoleCommands';
import './developerConsole.css';

const MAX_OUTPUT_LINES = 60;

function displayTimestamp(clock: SimulationClock): string {
  try {
    return new Intl.DateTimeFormat(undefined, { timeZone: clock.timezone, dateStyle: 'medium',
      timeStyle: 'short' }).format(new Date(clock.calendarDate));
  } catch {
    return clock.calendarDate;
  }
}

export interface DeveloperConsoleProps {
  readonly clock: SimulationClock;
  skip(minutes: number): DeveloperClockSkip;
  onSnowAdd?(meters: number): Promise<SnowAddResult>;
  restart?(fullRestart?: boolean): Promise<{ ok: true } | { ok: false; error: string }>;
}

/** Resolves only after the owning simulation has committed and published snow. */
export async function addSnowConsoleLines(meters: number,
  onSnowAdd: ((meters: number) => Promise<SnowAddResult>) | undefined): Promise<readonly string[]> {
  if (!onSnowAdd) throw new Error('Snow controls are not ready.');
  const result = await onSnowAdd(meters);
  const amount = result.requestedMeters >= 1
    ? `${result.requestedMeters.toLocaleString()} m`
    : `${Math.round(result.requestedMeters * 100).toLocaleString()} cm`;
  return [`Added ${amount} of fresh snow across ${result.affectedCells.toLocaleString()} terrain cells.`,
    result.clippedCells > 0
      ? `${result.clippedCells.toLocaleString()} cells reached the snow-depth limit. The simulation is paused; game time did not advance.`
      : 'The simulation is paused; game time did not advance.'];
}

export function DeveloperConsole({ clock, skip, onSnowAdd, restart }: DeveloperConsoleProps) {
  const enabled = isDeveloperConsoleEnabled();
  const [open, setOpen] = useState(false);
  const [input, setInput] = useState('');
  const [output, setOutput] = useState<readonly string[]>(['Developer console ready. Type "help" for commands.']);
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (!enabled) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.code === 'Backquote' || event.key === 'F10') {
        event.preventDefault(); event.stopImmediatePropagation();
        setOpen((value) => !value);
      } else if (event.key === 'Escape' && open) {
        event.preventDefault(); event.stopImmediatePropagation(); setOpen(false);
      }
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [enabled, open]);

  useEffect(() => { if (open) inputRef.current?.focus(); }, [open]);
  if (!enabled) return null;

  const append = (...lines: readonly string[]) => {
    setOutput((current) => [...current, ...lines].slice(-MAX_OUTPUT_LINES));
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    const source = input.trim();
    if (!source) return;
    setInput('');
    try {
      const command = parseDeveloperConsoleCommand(source);
      if (command.kind === 'clear') { setOutput([]); return; }
      if (command.kind === 'help') { append(`> ${source}`, ...DEVELOPER_CONSOLE_HELP); return; }
      if (command.kind === 'time') { append(`> ${source}`, displayTimestamp(clock)); return; }
      if (command.kind === 'snow-add') {
        append(`> ${source}`, ...await addSnowConsoleLines(command.meters, onSnowAdd));
        return;
      }
      if (command.kind === 'restart' || command.kind === 'restart-app') {
        if (!restart) throw new Error('Game restart is available only in the desktop game or a browser window that allows popups.');
        append(`> ${source}`, 'Saving progress and opening a fresh game window…');
        const result = await restart(command.kind === 'restart-app');
        if (!result.ok) throw new Error(result.error);
        return;
      }
      const result = skip(command.minutes);
      append(`> ${source}`, `Skipped ${result.skippedMinutes.toLocaleString()} minutes to ${displayTimestamp(result.after)}.`,
        'Elapsed weather, snow, and guest events were not simulated.');
    } catch (error) {
      append(`> ${source}`, `Error: ${error instanceof Error ? error.message : 'Command failed.'}`);
    }
  };

  if (!open) return <button type="button" className="developer-console-toggle"
    onClick={() => setOpen(true)} title="Open developer console (` or F10)"
    aria-label="Open developer console">&gt;_ DEV</button>;
  return <section className="developer-console" role="dialog" aria-label="Developer console"
    onKeyDown={(event) => event.stopPropagation()}>
    <header><strong>Developer Console</strong><span>` / F10 to close</span></header>
    <div className="developer-console-output" aria-live="polite">
      {output.map((line, index) => <div key={`${index}:${line}`}>{line || '\u00a0'}</div>)}
    </div>
    <form onSubmit={submit}>
      <label htmlFor="developer-console-input">&gt;</label>
      <input ref={inputRef} id="developer-console-input" value={input} autoComplete="off" spellCheck={false}
        onChange={(event) => setInput(event.target.value)} aria-label="Developer command" />
    </form>
  </section>;
}
