import { useState } from 'react';
import type { SavedLift } from '../types';
import type { Units } from './SettingsContext';
import { CHAIR_LABELS } from '../lifts';
import { LiftStatsBlock } from './LiftControl';

/**
 * Read-only overview of a single lift, shown when a lift is clicked (on the map
 * or in the list). It presents the lift's stats and offers Edit / Remove at the
 * bottom — editing hands off to LiftControl's edit panel; Remove confirms in
 * place before deleting. The ✕ closes back to the full lift list.
 */
export function LiftDetail({
  lift,
  units,
  onEdit,
  onRemove,
  onClose,
}: {
  lift: SavedLift;
  units: Units;
  onEdit: () => void;
  onRemove: () => void;
  onClose: () => void;
}) {
  const [confirmRemove, setConfirmRemove] = useState(false);

  return (
    <div className="lift-detail">
      <div className="dock-head">
        <span className="dock-head-title lift-detail-name">{lift.name}</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>
          ✕
        </button>
      </div>
      <div className="lift-detail-sub">
        <span className={`lift-row-dot lift-row-dot--${lift.status}`} aria-hidden="true" />
        {CHAIR_LABELS[lift.chairSize]} · {lift.status === 'planning' ? 'Planning' : 'Complete'}
      </div>

      <LiftStatsBlock
        points={lift.points}
        elev={lift.endpointElevM}
        chairSize={lift.chairSize}
        units={units}
      />

      {confirmRemove ? (
        <div className="lift-delete-confirm">
          <div className="lift-delete-warn">Remove “{lift.name}”? This can't be undone.</div>
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
          <button
            className="site-btn site-btn-danger-ghost"
            onClick={() => setConfirmRemove(true)}
          >
            Remove
          </button>
        </div>
      )}
    </div>
  );
}
