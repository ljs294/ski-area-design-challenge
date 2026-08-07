import { useState } from 'react';
import type { ChairSize, LiftStatus, SavedLift } from '../types';
import type { Units } from './SettingsContext';
import { haversineMeters } from '../geo';
import {
  CHAIR_LABELS,
  fixedGripCapacityPph,
  fixedGripDerived,
  fmtDistance,
  formatLiftLabel,
  liftStats,
} from '../lifts';
import type { DraftLift, LiftTool } from './liftControllerModel';

export type { DraftLift, LiftTool } from './liftControllerModel';

function fmtRideTime(s: number): string {
  const m = Math.floor(s / 60);
  const sec = Math.round(s % 60);
  return `${m}:${sec.toString().padStart(2, '0')} min`;
}

/** Shared roll-up header: a title on the left and a ✕ close in the corner.
 *  `onClose` dismisses whatever panel it heads (cancel a draw, leave edit). */
function PanelHead({ title, onClose }: { title: string; onClose: () => void }) {
  return (
    <div className="dock-head">
      <span className="dock-head-title">{title}</span>
      <button className="settings-close-x" aria-label="Close" onClick={onClose}>
        ✕
      </button>
    </div>
  );
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

/** Chair-size select, shared by the new-lift and edit panels. Capacity follows
 *  from the size (fixed headway) and is shown read-only in the stats block. */
function ChairSizeField({
  chairSize,
  onChange,
}: {
  chairSize: ChairSize;
  onChange: (patch: { chairSize: ChairSize }) => void;
}) {
  return (
    <label className="lift-field">
      <span className="lift-field-label">Chairs</span>
      <select
        className="lift-select"
        value={chairSize}
        onChange={(e) => onChange({ chairSize: Number(e.target.value) as ChairSize })}
      >
        {([2, 3, 4] as ChairSize[]).map((s) => (
          <option key={s} value={s}>
            {CHAIR_LABELS[s]}
          </option>
        ))}
      </select>
    </label>
  );
}

function LiftIdentityFields({
  identifier,
  name,
  onChange,
}: {
  identifier?: string;
  name: string;
  onChange: (patch: { identifier?: string; name?: string }) => void;
}) {
  return (
    <>
      <label className="lift-field lift-identity-field">
        <span className="lift-field-label">Letter / number</span>
        <input
          className="name-entry-input lift-identity-input lift-number-input"
          type="text"
          value={identifier ?? ''}
          placeholder="e.g. A or 12"
          autoCapitalize="characters"
          spellCheck={false}
          onChange={(event) => onChange({ identifier: event.target.value })}
        />
      </label>
      <label className="lift-field lift-identity-field">
        <span className="lift-field-label">Name</span>
        <input
          className="name-entry-input lift-identity-input lift-name-input"
          type="text"
          value={name}
          placeholder="e.g. Summit Express"
          onChange={(event) => onChange({ name: event.target.value })}
        />
      </label>
    </>
  );
}

/** Length / vertical / capacity / ride-time readout shared by both panels.
 *  Exported so the single-lift overview (LiftDetail) shows identical stats. */
export function LiftStatsBlock({
  points,
  elev,
  chairSize,
  units,
  elevSlot,
}: {
  points: [[number, number], [number, number]];
  elev: [number | null, number | null];
  chairSize: ChairSize;
  units: Units;
  elevSlot?: React.ReactNode;
}) {
  const stats = liftStats(points, elev);
  const derived = fixedGripDerived(stats.lengthM);
  const bottomElev = stats.topIndex === null ? null : elev[stats.topIndex === 1 ? 0 : 1];
  const topElev = stats.topIndex === null ? null : elev[stats.topIndex];
  return (
    <>
      <div className="lift-stats">
        <div className="readout-line">
          <span className="lift-stat-label">Length</span>
          <span className="lift-stat-value">{fmtDistance(stats.lengthM, units)}</span>
        </div>
        <div className="readout-line">
          <span className="lift-stat-label">Capacity</span>
          <span className="lift-stat-value">{fixedGripCapacityPph(chairSize).toLocaleString()}/hr</span>
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
  building = false,
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
  /** True while the confirmed lift is felling its cover — spins the build button. */
  building?: boolean;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (tool.phase === 'armed') {
    return (
      <div className="site-control site-control-wide">
        <PanelHead title="New lift" onClose={onCancel} />
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
        <PanelHead title="New lift" onClose={onCancel} />
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
        <PanelHead title="New fixed-grip chairlift" onClose={onCancel} />
        <LiftIdentityFields
          identifier={d.identifier}
          name={d.name}
          onChange={onDraftChange}
        />
        <ChairSizeField chairSize={d.chairSize} onChange={onDraftChange} />
        <StatusToggle value={d.status} onChange={(status) => onDraftChange({ status })} />
        <LiftStatsBlock
          points={d.points}
          elev={d.elev}
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
          <button className="site-btn site-btn-primary" onClick={onConfirm} disabled={building}>
            {building ? (
              <><span className="site-btn-spinner" aria-hidden="true" /> Building…</>
            ) : d.status === 'complete' ? 'Build lift' : 'Add to plan'}
          </button>
          <button className="site-btn" onClick={onCancel} disabled={building}>
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
        <PanelHead title="Edit lift" onClose={onCloseEdit} />
        <LiftIdentityFields
          identifier={editing.identifier}
          name={editing.name}
          onChange={(patch) => onEditPatch(editing.id, patch)}
        />
        <ChairSizeField
          chairSize={editing.chairSize}
          onChange={(patch) => onEditPatch(editing.id, patch)}
        />
        <StatusToggle
          value={editing.status}
          onChange={(status) => onEditPatch(editing.id, { status })}
        />
        <LiftStatsBlock
          points={editing.points}
          elev={editing.endpointElevM}
          chairSize={editing.chairSize}
          units={units}
        />
        {confirmDelete ? (
          <div className="lift-delete-confirm">
            <div className="lift-delete-warn">
              Delete “{formatLiftLabel(editing)}”? This can't be undone.
            </div>
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
              title={`Edit ${formatLiftLabel(l)}`}
            >
              <span className={`lift-row-dot lift-row-dot--${l.status}`} aria-hidden="true" />
              <span className="lift-row-main">
                <span className="lift-row-name">{formatLiftLabel(l)}</span>
                <span className="lift-row-summary">
                  {CHAIR_LABELS[l.chairSize]}
                  {` · ${fmtDistance(l.lengthM, units)}`}
                  {` · ${fixedGripCapacityPph(l.chairSize).toLocaleString()}/hr`}
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
