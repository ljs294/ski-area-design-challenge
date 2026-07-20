import { useState } from 'react';
import type { SavedTrail } from '../types';
import type { Units } from './SettingsContext';
import { DIFFICULTY_LABELS } from '../trails';
import { TrailProfile } from './TrailProfile';
import { TrailStatsBlock } from './TrailControl';

/**
 * Read-only overview of a single run, shown when a run is clicked (on the map or
 * in the list). Presents its elevation profile + stats and offers Edit / Remove.
 * Editing hands off to TrailControl's edit panel. Mirrors LiftDetail.
 */
export function TrailDetail({
  trail,
  units,
  onEdit,
  onRemove,
  onClose,
}: {
  trail: SavedTrail;
  units: Units;
  onEdit: () => void;
  onRemove: () => void;
  onClose: () => void;
}) {
  const [confirmRemove, setConfirmRemove] = useState(false);

  return (
    <div className="lift-detail trail-detail">
      <div className="dock-head">
        <span className="dock-head-title lift-detail-name">{trail.name}</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>
          ✕
        </button>
      </div>
      <div className="lift-detail-sub">
        <span
          className={`trail-grade-dot trail-grade-dot--${trail.difficulty}${
            trail.status === 'planning' ? ' is-planning' : ''
          }`}
          aria-hidden="true"
        />
        {DIFFICULTY_LABELS[trail.difficulty]} · {trail.status === 'planning' ? 'Planning' : 'Complete'}
      </div>

      <TrailProfile
        spine={trail.spine}
        spineElevM={trail.spineElevM}
        units={units}
        difficulty={trail.difficulty}
      />
      <TrailStatsBlock
        spine={trail.spine}
        spineElevM={trail.spineElevM}
        difficulty={trail.difficulty}
        units={units}
      />

      {confirmRemove ? (
        <div className="lift-delete-confirm">
          <div className="lift-delete-warn">Remove “{trail.name}”? This can't be undone.</div>
          <div className="site-actions">
            <button className="site-btn site-btn-danger" onClick={onRemove}>
              Remove
            </button>
            <button className="site-btn" onClick={() => setConfirmRemove(false)}>
              Keep
            </button>
          </div>
        </div>
      ) : (
        <div className="site-actions lift-detail-actions">
          <button className="site-btn site-btn-primary" onClick={onEdit}>
            Edit
          </button>
          <button className="site-btn site-btn-danger-ghost" onClick={() => setConfirmRemove(true)}>
            Remove
          </button>
        </div>
      )}
    </div>
  );
}
