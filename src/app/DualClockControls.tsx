import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { advanceDestination } from '../dualClock/clock';
import type { AdvanceDestination, OperationalSignal } from '../dualClock/model';
import type { DualSimulationControls } from './useDualClockRuntime';
import './dualClock.css';

const DESTINATIONS: readonly [AdvanceDestination, string][] = [['opening', 'Next Opening'], ['day', 'Day'], ['week', 'Week'], ['month', 'Month'], ['season', 'Season']];
export function DualClockMenu({ controls, children }: { controls: DualSimulationControls; children: React.ReactNode }) {
  const [open, setOpen] = useState(false), [destination, setDestination] = useState<AdvanceDestination>('day');
  const [preparing, setPreparing] = useState(false), [error, setError] = useState<string | null>(null);
  const root = useRef<HTMLDivElement>(null), button = useRef<HTMLButtonElement>(null);
  const popover = useRef<HTMLDivElement>(null);
  const [anchor, setAnchor] = useState({ left: 12, bottom: 48 });
  useEffect(() => {
    if (!open) return;
    const position = () => { const rect = button.current?.getBoundingClientRect(); if (rect) setAnchor({
      left: Math.max(12, Math.min(rect.left, window.innerWidth - 354)), bottom: window.innerHeight - rect.top + 8 }); };
    position(); window.addEventListener('resize', position);
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node) && !popover.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('pointerdown', outside); return () => { document.removeEventListener('pointerdown', outside); window.removeEventListener('resize', position); };
  }, [open]);
  const state = controls.publication, progress = state?.advance;
  const target = progress?.state === 'running' ? progress.request.target
    : state ? advanceDestination(state.clock, state.clock.season === 'summer' ? 'winter' : destination) : null;
  const format = (at: string) => new Intl.DateTimeFormat(undefined, { timeZone: state?.clock.timezone, dateStyle: 'medium', timeStyle: 'short' }).format(new Date(at));
  return <div ref={root} className="dual-clock-control" onKeyDown={event => {
    if (event.key === 'Escape' && open) { event.stopPropagation(); setOpen(false); button.current?.focus(); }
  }}>
    <button ref={button} type="button" className="tb-clock dual-clock-button" aria-label="Date and time: simulation advances"
      aria-expanded={open} onClick={() => setOpen(!open)}>{children}</button>
    {open && createPortal(<div ref={popover} style={anchor} className="dual-clock-popover" role="dialog" aria-label="Advance simulation">
      <strong>Advance simulation</strong>
      {state && <p>Mountain time: {format(state.clock.at)}</p>}
      <label>Advance to <select value={state?.clock.season === 'summer' ? 'winter' : destination} disabled={preparing || progress?.state === 'running'}
        onChange={event => setDestination(event.target.value as AdvanceDestination)}>
        {(state?.clock.season === 'summer' ? [['winter', 'Skip to Winter']] as const : DESTINATIONS).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
      </select></label>
      {target && <p>Destination: {format(target)}</p>}
      {progress?.state === 'running' || preparing ? <>
        <progress max={1} value={preparing ? undefined : progress?.fraction} aria-label="Simulation advance progress" />
        <span>{preparing ? 'Preparing weather…' : `${Math.round((progress?.fraction ?? 0) * 100)}%`}</span>
        <button type="button" className="site-btn" onClick={() => { controls.cancel(); setPreparing(false); }}>Cancel</button>
      </> : <button type="button" className="site-btn" disabled={!controls.ready || controls.weatherReady === false} onClick={async () => {
        setPreparing(true); setError(null);
        try { await controls.advance(state?.clock.season === 'summer' ? 'winter' : destination); }
        catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to advance.'); }
        finally { setPreparing(false); }
      }}>Simulate {state?.clock.season === 'summer' ? 'to Winter' : DESTINATIONS.find(([id]) => id === destination)?.[1]}</button>}
      {progress?.state === 'suspended' && <button type="button" className="site-btn" onClick={controls.resume}>Resume to {format(progress.request.target)}</button>}
      {(progress?.state === 'completed' || progress?.state === 'cancelled') && <p role="status">{progress.state === 'completed' ? 'Advance completed' : 'Advance cancelled'}. Paused at {state && format(state.clock.at)}.</p>}
      {(error || controls.error) && <p role="alert">{error ?? controls.error}</p>}
      {state && <p>{(state.flow.admitted - (progress?.baseline.admitted ?? 0)).toLocaleString()} admissions · {(state.flow.completedRuns - (progress?.baseline.runs ?? 0)).toLocaleString()} runs · ${((state.flow.ticketRevenueCents + state.flow.amenityRevenueCents - (progress?.baseline.revenueCents ?? 0)) / 100).toLocaleString()} revenue{progress ? ' during this advance' : ''}</p>}
    </div>, document.body)}
  </div>;
}
export function SimulationWarnings({ controls, inspect }: { controls: DualSimulationControls; inspect(signal: OperationalSignal): void }) {
  const [open, setOpen] = useState(false), root = useRef<HTMLDivElement>(null), button = useRef<HTMLButtonElement>(null);
  const rank = { critical: 0, opportunity: 1, advisory: 2 };
  const warnings = [...(controls.publication?.signals ?? [])].sort((a, b) => rank[a.severity] - rank[b.severity] || b.at.localeCompare(a.at));
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('pointerdown', outside); return () => document.removeEventListener('pointerdown', outside);
  }, [open]);
  return <div ref={root} className="simulation-warnings" onKeyDown={event => {
    if (event.key === 'Escape' && open) { event.stopPropagation(); setOpen(false); button.current?.focus(); }
  }}>
    <button ref={button} type="button" className="site-btn" aria-expanded={open} onClick={() => setOpen(!open)}>
      Warnings <span aria-live="polite">({warnings.length})</span></button>
    {open && <section className="simulation-warning-popover" aria-label="Mountain warnings">
      <strong>Mountain warnings</strong>{!warnings.length && <p>No active warnings.</p>}
      {warnings.map(warning => <article key={warning.id} className={`simulation-warning ${warning.severity}`}>
        <strong>{warning.title}</strong><p>{warning.message}</p>
        <small>{warning.entityId} · {new Intl.DateTimeFormat(undefined, { timeZone: controls.publication?.clock.timezone, dateStyle: 'short', timeStyle: 'short' }).format(new Date(warning.at))}</small>
        <div><button type="button" className="site-btn" onClick={() => inspect(warning)}>Inspect</button>
          <button type="button" className="site-btn" disabled={warning.acknowledged} onClick={() => controls.acknowledge(warning.id)}>{warning.acknowledged ? 'Acknowledged' : 'Acknowledge'}</button></div>
      </article>)}
    </section>}
  </div>;
}
