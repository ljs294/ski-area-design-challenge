import { useState } from 'react';
import type { ChairSize, LiftStatus, SavedLift } from '../types';
import type { Units } from './SettingsContext';
import { haversineMeters } from '../geo';
import { CHAIR_LABELS, capacityRange, fixedGripDerived, fmtDistance, liftStats } from '../lifts';

// UI state machine for the lift drawing tool. MapView owns the state and all
// map interaction; this component only renders it (SiteControl pattern).
export type LiftTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'anchored'; a: [number, number]; cursor: [number, number] | null }
  | { phase: 'review'; draft: DraftLift };

export interface DraftLift {
  points: [[number, number], [number, number]]; // drawn order
  elev: [number | null, number | null]; // filled async by sampling
  elevStatus: 'pending' | 'ok' | 'error';
  chairSize: ChairSize;
  capacityPph: number;
  status: LiftStatus;
  name: string;
}

function fmtRideTime(s: number): string {
  const m = Math.floor(s / 60);
  const sec = Math.round(s % 60);
  return `${m}:${sec.toString().padStart(2, '0')} min`;
}

/** Clamp capacity into the new chair size's range when the size changes. */
function chairSizePatch(chairSize: ChairSize, capacityPph: number) {
  const r = capacityRange(chairSize);
  return { chairSize, capacityPph: Math.min(r.max, Math.max(r.min, capacityPph)) };
}

function StatusToggle({
  value,
  onChange,
}: {
  value: LiftStatus;
  onChange: (s: LiftStatus) => void;
}) {
  return (
    <label className="lift-field">
      <span className="lift-field-label">Status</span>
      <div className="lift-status-toggle" role="group" aria-label="Build status">
        {(['planning', 'complete'] as LiftStatus[]).map((s) => (
          <button
            key={s}
            type="button"
            className={`lift-status-btn${value === s ? ' is-active' : ''}`}
            onClick={() => onChange(s)}
          >
            {s === 'planning' ? 'Planning' : 'Complete'}
          </button>
        ))}
      </div>
    </label>
  );
}

/** Chair-size select + capacity slider, shared by the new-lift and edit panels. */
function ChairCapacityFields({
  chairSize,
  capacityPph,
  onChange,
}: {
  chairSize: ChairSize;
  capacityPph: number;
  onChange: (patch: { chairSize?: ChairSize; capacityPph?: number }) => void;
}) {
  const range = capacityRange(chairSize);
  return (
    <>
      <label className="lift-field">
        <span className="lift-field-label">Chairs</span>
        <select
          className="lift-select"
          value={chairSize}
          onChange={(e) => onChange(chairSizePatch(Number(e.target.value) as ChairSize, capacityPph))}
        >
          {([1, 2, 3, 4] as ChairSize[]).map((s) => (
            <option key={s} value={s}>
              {CHAIR_LABELS[s]}
            </option>
          ))}
        </select>
      </label>
      <label className="lift-field">
        <span className="lift-field-label">Capacity</span>
        <input
          className="lift-slider"
          type="range"
          min={range.min}
          max={range.max}
          step={range.step}
          value={capacityPph}
          onChange={(e) => onChange({ capacityPph: Number(e.target.value) })}
        />
        <span className="lift-field-value">{capacityPph.toLocaleString()}/hr</span>
      </label>
    </>
  );
}

/** Length / vertical / ride-time readout shared by both panels. */
function LiftStatsBlock({
  points,
  elev,
  capacityPph,
  chairSize,
  units,
  elevSlot,
}: {
  points: [[number, number], [number, number]];
  elev: [number | null, number | null];
  capacityPph: number;
  chairSize: ChairSize;
  units: Units;
  elevSlot?: React.ReactNode;
}) {
  const stats = liftStats(points, elev);
  const derived = fixedGripDerived(capacityPph, chairSize, stats.lengthM);
  const bottomElev = stats.topIndex === null ? null : elev[stats.topIndex === 1 ? 0 : 1];
  const topElev = stats.topIndex === null ? null : elev[stats.topIndex];
  return (
    <>
      {derived.aggressive && (
        <div className="lift-warning">
          ⚠ {Math.round(derived.headwayS * 10) / 10} s between chairs — tight loading for a
          fixed-grip lift
        </div>
      )}
      <div className="lift-stats">
        <div className="readout-line">
          <span className="lift-stat-label">Length</span>
          <span className="lift-stat-value">{fmtDistance(stats.lengthM, units)}</span>
        </div>
        {elevSlot}
        {stats.verticalM != null && (
          <>
            <div className="readout-line">
              <span className="lift-stat-label">Vertical</span>
              <span className="lift-stat-value">{fmtDistance(stats.verticalM, units)}</span>
            </div>
            <div className="readout-line">
              <span className="lift-stat-label">Base / Top</span>
              <span className="lift-stat-value">
                {bottomElev != null && topElev != null
                  ? `${fmtDistance(bottomElev, units)} / ${fmtDistance(topElev, units)}`
                  : '—'}
              </span>
            </div>
          </>
        )}
        <div className="readout-line">
          <span className="lift-stat-label">Ride time</span>
          <span className="lift-stat-value">{fmtRideTime(derived.rideTimeS)}</span>
        </div>
      </div>
    </>
  );
}

export function LiftControl({
  tool,
  lifts,
  selectedId,
  units,
  onArm,
  onCancel,
  onDraftChange,
  onConfirm,
  onSelect,
  onEditPatch,
  onCloseEdit,
  onDelete,
  onRetryElevation,
}: {
  tool: LiftTool;
  lifts: SavedLift[];
  selectedId: string | null;
  units: Units;
  onArm: () => void;
  onCancel: () => void;
  onDraftChange: (patch: Partial<DraftLift>) => void;
  onConfirm: () => void;
  onSelect: (id: string) => void;
  onEditPatch: (id: string, patch: Partial<SavedLift>) => void;
  onCloseEdit: () => void;
  onDelete: (id: string) => void;
  onRetryElevation: () => void;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (tool.phase === 'armed') {
    return (
      <div className="site-control site-control-wide">
        <div className="site-hint">Click the map to place the first terminal</div>
        <button className="site-btn" onClick={onCancel}>
          Cancel
        </button>
      </div>
    );
  }

  if (tool.phase === 'anchored') {
    const dist = tool.cursor ? haversineMeters(tool.a, tool.cursor) : null;
    return (
      <div className="site-control site-control-wide">
        {dist != null && dist > 0 ? (
          <div className="site-dims">{fmtDistance(dist, units)}</div>
        ) : (
          <div className="site-hint">Click again to place the other terminal</div>
        )}
        <button className="site-btn" onClick={onCancel}>
          Cancel
        </button>
      </div>
    );
  }

  if (tool.phase === 'review') {
    const d = tool.draft;
    return (
      <div className="site-control site-control-wide lift-panel">
        <div className="lift-panel-title">New fixed-grip chairlift</div>
        <input
          className="name-entry-input lift-name-input"
          type="text"
          value={d.name}
          onChange={(e) => onDraftChange({ name: e.target.value })}
        />
        <ChairCapacityFields
          chairSize={d.chairSize}
          capacityPph={d.capacityPph}
          onChange={onDraftChange}
        />
        <StatusToggle value={d.status} onChange={(status) => onDraftChange({ status })} />
        <LiftStatsBlock
          points={d.points}
          elev={d.elev}
          capacityPph={d.capacityPph}
          chairSize={d.chairSize}
          units={units}
          elevSlot={
            <>
              {d.elevStatus === 'pending' && <div className="site-hint">Sampling elevation…</div>}
              {d.elevStatus === 'error' && (
                <div className="lift-warning">
                  Elevation unavailable{' '}
                  <button className="lift-link-btn" onClick={onRetryElevation}>
                    Retry
                  </button>
                </div>
              )}
            </>
          }
        />
        <div className="site-actions">
          <button className="site-btn site-btn-primary" onClick={onConfirm}>
            {d.status === 'complete' ? 'Build lift' : 'Add to plan'}
          </button>
          <button className="site-btn" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    );
  }

  // Editing an existing lift.
  const editing = selectedId ? lifts.find((l) => l.id === selectedId) : null;
  if (editing) {
    return (
      <div className="site-control site-control-wide lift-panel">
        <div className="lift-panel-title">Edit lift</div>
        <input
          className="name-entry-input lift-name-input"
          type="text"
          value={editing.name}
          onChange={(e) => onEditPatch(editing.id, { name: e.target.value })}
        />
        <ChairCapacityFields
          chairSize={editing.chairSize}
          capacityPph={editing.capacityPph}
          onChange={(patch) => onEditPatch(editing.id, patch)}
        />
        <StatusToggle
          value={editing.status}
          onChange={(status) => onEditPatch(editing.id, { status })}
        />
        <LiftStatsBlock
          points={editing.points}
          elev={editing.endpointElevM}
          capacityPph={editing.capacityPph}
          chairSize={editing.chairSize}
          units={units}
        />
        {confirmDelete ? (
          <div className="lift-delete-confirm">
            <div className="lift-delete-warn">Delete “{editing.name}”? This can't be undone.</div>
            <div className="site-actions">
              <button
                className="site-btn site-btn-danger"
                onClick={() => {
                  onDelete(editing.id);
                  setConfirmDelete(false);
                }}
              >
                Delete
              </button>
              <button className="site-btn" onClick={() => setConfirmDelete(false)}>
                Keep
              </button>
            </div>
          </div>
        ) : (
          <div className="site-actions">
            <button className="site-btn site-btn-primary" onClick={onCloseEdit}>
              Done
            </button>
            <button className="site-btn site-btn-danger-ghost" onClick={() => setConfirmDelete(true)}>
              Delete
            </button>
          </div>
        )}
      </div>
    );
  }

  // idle
  return (
    <div className="site-control site-control-wide">
      <button className="site-btn site-btn-primary" onClick={onArm}>
        ⛓ New lift
      </button>
      {lifts.length > 0 && (
        <div className="lift-list">
          {lifts.map((l) => (
            <button
              key={l.id}
              type="button"
              className="lift-row lift-row-btn"
              onClick={() => onSelect(l.id)}
              title={`Edit ${l.name}`}
            >
              <span className={`lift-row-dot lift-row-dot--${l.status}`} aria-hidden="true" />
              <span className="lift-row-main">
                <span className="lift-row-name">{l.name}</span>
                <span className="lift-row-summary">
                  {CHAIR_LABELS[l.chairSize]}
                  {` · ${fmtDistance(l.lengthM, units)}`}
                  {` · ${l.capacityPph.toLocaleString()}/hr`}
                  {l.status === 'planning' ? ' · Planning' : ''}
                </span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
