import type { SavedTrail } from '../types';
import type { Units } from './SettingsContext';
import { fmtDistance } from '../lifts';
import { DIFFICULTY_LABELS, fmtArea, fmtVertical } from '../trails';

/**
 * Dedicated overview of every ski run, shown in the Trails roll-up when the
 * brush tool is idle and no single run is selected. "Add ski run" is pinned at
 * the top; each row opens that run's read-only detail (TrailDetail). Mirrors
 * LiftOverview.
 */
export function TrailOverview({
  trails,
  units,
  onArm,
  onSelect,
  onClose,
}: {
  trails: SavedTrail[];
  units: Units;
  onArm: () => void;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  return (
    <div className="lift-overview">
      <div className="lift-overview-head">
        <span className="lift-overview-title">Ski Runs ({trails.length})</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>
          ✕
        </button>
      </div>
      <button className="lift-add-btn site-btn site-btn-primary" onClick={onArm}>
        ＋ Add ski run
      </button>

      {trails.length === 0 ? (
        <div className="lift-overview-empty">No runs yet — paint your first one.</div>
      ) : (
        <div className="lift-list">
          {trails.map((t) => (
            <button
              key={t.id}
              type="button"
              className="lift-row lift-row-btn"
              onClick={() => onSelect(t.id)}
              title={`View ${t.name}`}
            >
              <span
                className={`trail-grade-dot trail-grade-dot--${t.difficulty}${
                  t.status === 'planning' ? ' is-planning' : ''
                }`}
                aria-hidden="true"
              />
              <span className="lift-row-main">
                <span className="lift-row-name">{t.name}</span>
                <span className="lift-row-summary">
                  {DIFFICULTY_LABELS[t.difficulty]}
                  {t.verticalM != null ? ` · ${fmtVertical(t.verticalM, units)} vert` : ''}
                  {` · ${fmtDistance(t.lengthM, units)}`}
                  {` · ${fmtArea(t.areaM2, units)}`}
                  {t.status === 'planning' ? ' · Planning' : ''}
                </span>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
