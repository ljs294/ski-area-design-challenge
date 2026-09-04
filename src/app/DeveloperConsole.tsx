import { useEffect, useRef, useState, type FormEvent } from 'react';
import type { SimulationClock } from '../types/simulation';
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
}

export function DeveloperConsole({ clock, skip }: DeveloperConsoleProps) {
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
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const source = input.trim();
    if (!source) return;
    setInput('');
    try {
      const command = parseDeveloperConsoleCommand(source);
      if (command.kind === 'clear') { setOutput([]); return; }
      if (command.kind === 'help') { append(`> ${source}`, ...DEVELOPER_CONSOLE_HELP); return; }
      if (command.kind === 'time') { append(`> ${source}`, displayTimestamp(clock)); return; }
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
