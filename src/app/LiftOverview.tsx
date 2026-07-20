import type { SavedLift } from '../types';
import type { Units } from './SettingsContext';
import { CHAIR_LABELS, fmtDistance, liftStats } from '../lifts';

/**
 * Dedicated overview of every ski lift, shown in the Lifts roll-up when the draw
 * tool is idle and no single lift is selected. An "Add ski lift" button is pinned
 * at the top; each row opens that lift's read-only detail (LiftDetail), from
 * which the user can Edit or Remove. The ✕ closes the whole Lifts roll-up.
 */
export function LiftOverview({
  lifts,
  units,
  onArm,
  onSelect,
  onClose,
}: {
  lifts: SavedLift[];
  units: Units;
  onArm: () => void;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  return (
    <div className="lift-overview">
      <div className="lift-overview-head">
        <span className="lift-overview-title">Ski Lifts ({lifts.length})</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>
          ✕
        </button>
      </div>
      <button className="lift-add-btn site-btn site-btn-primary" onClick={onArm}>
        ＋ Add ski lift
      </button>

      {lifts.length === 0 ? (
        <div className="lift-overview-empty">No lifts yet — add your first one.</div>
      ) : (
        <div className="lift-list">
          {lifts.map((l) => {
            const vertM = liftStats(l.points, l.endpointElevM).verticalM;
            return (
              <button
                key={l.id}
                type="button"
                className="lift-row lift-row-btn"
                onClick={() => onSelect(l.id)}
                title={`View ${l.name}`}
              >
                <span className={`lift-row-dot lift-row-dot--${l.status}`} aria-hidden="true" />
                <span className="lift-row-main">
                  <span className="lift-row-name">{l.name}</span>
                  <span className="lift-row-summary">
                    {CHAIR_LABELS[l.chairSize]}
                    {vertM != null ? ` · ${fmtDistance(vertM, units)} vert` : ''}
                    {` · ${fmtDistance(l.lengthM, units)}`}
                    {l.status === 'planning' ? ' · Planning' : ''}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
