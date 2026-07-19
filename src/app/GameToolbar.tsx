import { useState } from 'react';
import type { Readout } from './CursorReadout';
import type { Units } from './SettingsContext';

// Simulation controls are visual placeholders for now — there is no clock,
// economy, or tick loop behind them yet. Play/pause and speed hold local state
// so the buttons feel live; the date and money read fixed placeholder values.

const SPEEDS = [1, 2, 3, 4] as const;
const M_TO_FT = 3.28084;

/** Cursor readout for the toolbar's right edge: elevation is always shown; the
 *  second cell follows the active overlay layer (slope / aspect / ground cover)
 *  and otherwise stays blank. The cell keeps a fixed width either way so the
 *  toolbar never resizes as overlays toggle. */
function ToolbarReadout({ readout, units }: { readout: Readout | null; units: Units }) {
  const elev = !readout
    ? '—'
    : units === 'imperial'
      ? `${Math.round(readout.elevationM * M_TO_FT).toLocaleString()} ft`
      : `${Math.round(readout.elevationM).toLocaleString()} m`;

  let ctx: { label: string; value: string } | null = null;
  if (readout) {
    if (readout.overlay === 'slope') ctx = { label: 'Slope', value: `${Math.round(readout.slopeDeg)}°` };
    else if (readout.overlay === 'aspect') ctx = { label: 'Aspect', value: readout.aspectCompass };
    else if (readout.overlay === 'groundcover')
      ctx = { label: 'Cover', value: readout.coverLabel ?? '—' };
  }

  return (
    <div className="tb-readout" role="group" aria-label="Cursor terrain readout">
      <div className="tb-readout-cell">
        <span className="tb-readout-label">Elev</span>
        <span className="tb-readout-value">{elev}</span>
      </div>
      <div className="tb-readout-cell tb-readout-ctx">
        <span className="tb-readout-label">{ctx?.label ?? ''}</span>
        <span className="tb-readout-value">{ctx?.value ?? ''}</span>
      </div>
    </div>
  );
}

export function GameToolbar({
  resortName,
  onOpenStats,
  readout,
  units,
}: {
  resortName: string;
  onOpenStats: () => void;
  readout: Readout | null;
  units: Units;
}) {
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState<number>(1);

  return (
    <div className="game-toolbar">
      <div className="tb-group">
        <button
          className="tb-play"
          onClick={() => setPlaying((p) => !p)}
          aria-pressed={playing}
          title={playing ? 'Pause simulation' : 'Play simulation'}
        >
          {playing ? '❚❚' : '▶'}
        </button>
      </div>

      <div className="tb-group">
        <div className="tb-clock">
          <span className="tb-day">Day 1</span>
          <span className="tb-time">9:00 AM</span>
        </div>
      </div>

      <div className="tb-group">
        <div className="tb-speeds" role="group" aria-label="Simulation speed">
          {SPEEDS.map((s) => (
            <button
              key={s}
              className={`tb-speed${speed === s ? ' is-active' : ''}`}
              onClick={() => setSpeed(s)}
              title={`${s}× speed`}
            >
              {s}×
            </button>
          ))}
        </div>
      </div>

      <div className="tb-group">
        <div className="tb-money">
          <span className="tb-balance">$0</span>
          <span className="tb-income">+$0 / day</span>
        </div>
      </div>

      <div className="tb-group">
        <button className="tb-resort" onClick={onOpenStats} title="Ski area details">
          <span className="hud-resort tb-resort-name">{resortName}</span>
          <span className="tb-caret" aria-hidden="true">▸</span>
        </button>
      </div>

      <div className="tb-group tb-group-right">
        <ToolbarReadout readout={readout} units={units} />
      </div>
    </div>
  );
}
