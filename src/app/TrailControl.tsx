import { useState } from 'react';
import type { SavedTrail, TrailDifficulty, TrailStatus } from '../types';
import type { Units } from './SettingsContext';
import { fmtDistance } from '../lifts';
import {
  DIFFICULTY_LABELS,
  DIFFICULTY_SYMBOL,
  TRAIL_DIFFICULTIES,
  difficultyForSlopes,
  fmtSlope,
  fmtVertical,
  trailStats,
  MIN_BRUSH_WIDTH_M,
  MAX_BRUSH_WIDTH_M,
} from '../trails';
import { TrailProfile } from './TrailProfile';

// UI state machine for the trail brush tool. MapView owns the state and all map
// interaction (painting, sampling); this component only renders it — the same
// split LiftControl uses.
export type TrailTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'painting'; path: [number, number][] }
  | { phase: 'review'; draft: DraftTrail };

export interface DraftTrail {
  polygon: [number, number][][];
  spine: [number, number][];
  spineElevM: number[]; // filled async by sampling
  elevStatus: 'pending' | 'ok' | 'error';
  brushWidthM: number;
  name: string;
  status: TrailStatus;
  difficulty: TrailDifficulty; // user-chosen; defaults to the recommendation on sample
}

/** Shared roll-up header: title + ✕ close. Mirrors LiftControl's PanelHead. */
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

function StatusToggle({ value, onChange }: { value: TrailStatus; onChange: (s: TrailStatus) => void }) {
  return (
    <label className="lift-field">
      <span className="lift-field-label">Status</span>
      <div className="lift-status-toggle" role="group" aria-label="Build status">
        {(['planning', 'complete'] as TrailStatus[]).map((s) => (
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

function BrushWidthField({
  widthM,
  units,
  onChange,
}: {
  widthM: number;
  units: Units;
  onChange: (m: number) => void;
}) {
  return (
    <label className="lift-field trail-brush-field">
      <span className="lift-field-label">Brush</span>
      <input
        className="trail-brush-slider"
        type="range"
        min={MIN_BRUSH_WIDTH_M}
        max={MAX_BRUSH_WIDTH_M}
        step={2}
        value={widthM}
        onChange={(e) => onChange(Number(e.target.value))}
      />
      <span className="lift-field-value">{fmtDistance(widthM, units)}</span>
    </label>
  );
}

/** Grade picker; the slope-recommended grade is badged. */
function DifficultyField({
  value,
  recommended,
  onChange,
}: {
  value: TrailDifficulty;
  recommended: TrailDifficulty | null;
  onChange: (d: TrailDifficulty) => void;
}) {
  return (
    <div className="trail-difficulty-field">
      <span className="lift-field-label">Rating</span>
      <div className="trail-grade-row" role="group" aria-label="Run difficulty">
        {TRAIL_DIFFICULTIES.map((d) => (
          <button
            key={d}
            type="button"
            className={`trail-grade-btn trail-grade-btn--${d}${value === d ? ' is-active' : ''}`}
            onClick={() => onChange(d)}
            title={DIFFICULTY_LABELS[d]}
          >
            <span className="trail-grade-symbol" aria-hidden="true">
              {DIFFICULTY_SYMBOL[d]}
            </span>
            <span className="trail-grade-label">{DIFFICULTY_LABELS[d]}</span>
            {recommended === d && <span className="trail-grade-rec">Rec.</span>}
          </button>
        ))}
      </div>
    </div>
  );
}

/** Length / vertical / avg-max slope / grade readout. Exported so TrailDetail
 *  shows identical stats. */
export function TrailStatsBlock({
  spine,
  spineElevM,
  difficulty,
  units,
}: {
  spine: [number, number][];
  spineElevM: number[];
  difficulty: TrailDifficulty;
  units: Units;
}) {
  const stats = trailStats(spine, spineElevM);
  return (
    <div className="lift-stats">
      <div className="readout-line">
        <span className="lift-stat-label">Length</span>
        <span className="lift-stat-value">{fmtDistance(stats.lengthM, units)}</span>
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Vertical</span>
        <span className="lift-stat-value">{fmtVertical(stats.verticalM, units)}</span>
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Avg / max pitch</span>
        <span className="lift-stat-value">
          {stats.verticalM == null ? '—' : `${fmtSlope(stats.avgSlopeDeg)} / ${fmtSlope(stats.maxSlopeDeg)}`}
        </span>
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Rating</span>
        <span className="lift-stat-value trail-grade-inline">
          <span className={`trail-grade-dot trail-grade-dot--${difficulty}`} aria-hidden="true" />
          {DIFFICULTY_LABELS[difficulty]}
        </span>
      </div>
    </div>
  );
}

export function TrailControl({
  tool,
  trails,
  selectedId,
  units,
  brushWidthM,
  onBrushWidthChange,
  onCancel,
  onDraftChange,
  onConfirm,
  onEditPatch,
  onCloseEdit,
  onDelete,
  onRetryElevation,
}: {
  tool: TrailTool;
  trails: SavedTrail[];
  selectedId: string | null;
  units: Units;
  brushWidthM: number;
  onBrushWidthChange: (m: number) => void;
  onCancel: () => void;
  onDraftChange: (patch: Partial<DraftTrail>) => void;
  onConfirm: () => void;
  onEditPatch: (id: string, patch: Partial<SavedTrail>) => void;
  onCloseEdit: () => void;
  onDelete: (id: string) => void;
  onRetryElevation: () => void;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (tool.phase === 'armed' || tool.phase === 'painting') {
    const painting = tool.phase === 'painting';
    return (
      <div className="site-control site-control-wide trail-panel">
        <PanelHead title="New run" onClose={onCancel} />
        <BrushWidthField widthM={brushWidthM} units={units} onChange={onBrushWidthChange} />
        <div className="site-hint">
          {painting ? 'Release to finish the run' : 'Drag down the slope to paint a run'}
        </div>
        <button className="site-btn" onClick={onCancel}>
          Cancel
        </button>
      </div>
    );
  }

  if (tool.phase === 'review') {
    const d = tool.draft;
    const recommended =
      d.elevStatus === 'ok'
        ? (() => {
            const s = trailStats(d.spine, d.spineElevM);
            return difficultyForSlopes(s.avgSlopeDeg, s.maxSlopeDeg);
          })()
        : null;
    return (
      <div className="site-control site-control-wide trail-panel lift-panel">
        <PanelHead title="New run" onClose={onCancel} />
        <input
          className="name-entry-input lift-name-input"
          type="text"
          value={d.name}
          onChange={(e) => onDraftChange({ name: e.target.value })}
        />
        <TrailProfile
          spine={d.spine}
          spineElevM={d.spineElevM}
          units={units}
          difficulty={d.difficulty}
        />
        {d.elevStatus === 'error' && (
          <div className="lift-warning">
            Elevation unavailable{' '}
            <button className="lift-link-btn" onClick={onRetryElevation}>
              Retry
            </button>
          </div>
        )}
        <DifficultyField
          value={d.difficulty}
          recommended={recommended}
          onChange={(difficulty) => onDraftChange({ difficulty })}
        />
        <StatusToggle value={d.status} onChange={(status) => onDraftChange({ status })} />
        <TrailStatsBlock
          spine={d.spine}
          spineElevM={d.spineElevM}
          difficulty={d.difficulty}
          units={units}
        />
        <div className="site-actions">
          <button className="site-btn site-btn-primary" onClick={onConfirm}>
            {d.status === 'complete' ? 'Build run' : 'Add to plan'}
          </button>
          <button className="site-btn" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    );
  }

  // Editing an existing run (no re-paint; name/rating/status only).
  const editing = selectedId ? trails.find((t) => t.id === selectedId) : null;
  if (editing) {
    const recommended = difficultyForSlopes(editing.avgSlopeDeg, editing.maxSlopeDeg);
    return (
      <div className="site-control site-control-wide trail-panel lift-panel">
        <PanelHead title="Edit run" onClose={onCloseEdit} />
        <input
          className="name-entry-input lift-name-input"
          type="text"
          value={editing.name}
          onChange={(e) => onEditPatch(editing.id, { name: e.target.value })}
        />
        <TrailProfile
          spine={editing.spine}
          spineElevM={editing.spineElevM}
          units={units}
          difficulty={editing.difficulty}
        />
        <DifficultyField
          value={editing.difficulty}
          recommended={recommended}
          onChange={(difficulty) => onEditPatch(editing.id, { difficulty })}
        />
        <StatusToggle
          value={editing.status}
          onChange={(status) => onEditPatch(editing.id, { status })}
        />
        <TrailStatsBlock
          spine={editing.spine}
          spineElevM={editing.spineElevM}
          difficulty={editing.difficulty}
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

  return null; // idle is handled by TrailOverview in MapView
}
