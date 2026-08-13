import { useEffect, useState } from 'react';
import type { LiftCategoryId, LiftStatus, LiftTypeId, SavedLift } from '../types';
import type { Units } from './SettingsContext';
import {
  LIFT_CATEGORIES,
  LIFT_TYPE_CATALOG,
  LIFT_TYPE_SPECS,
  fmtDistance,
  formatLiftLabel,
  liftPerformance,
  liftStats,
  liftTypeLabel,
} from '../lifts';
import type {
  CommittedLiftPatch,
  DraftLift,
  LiftTool,
  LiveElevationStatus,
} from './liftControllerModel';

export type { DraftLift, LiftTool } from './liftControllerModel';

function fmtRideTime(totalSeconds: number): string {
  const rounded = Math.max(0, Math.round(totalSeconds));
  const minutes = Math.floor(rounded / 60);
  const seconds = rounded % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')} min`;
}

function PanelHead({ title, onClose }: { title: string; onClose: () => void }) {
  return (
    <div className="dock-head">
      <span className="dock-head-title">{title}</span>
      <button className="settings-close-x" aria-label="Close" onClick={onClose}>×</button>
    </div>
  );
}

function StatusToggle({ value, onChange }: {
  value: LiftStatus;
  onChange: (status: LiftStatus) => void;
}) {
  return (
    <label className="lift-field">
      <span className="lift-field-label">Status</span>
      <div className="lift-status-toggle" role="group" aria-label="Build status">
        {(['planning', 'complete'] as LiftStatus[]).map((status) => (
          <button
            key={status}
            type="button"
            className={`lift-status-btn${value === status ? ' is-active' : ''}`}
            onClick={() => onChange(status)}
          >
            {status === 'planning' ? 'Planning' : 'Complete'}
          </button>
        ))}
      </div>
    </label>
  );
}

function LiftIdentityFields({ identifier, name, onChange }: {
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

function categoryFor(liftTypeId: LiftTypeId): LiftCategoryId {
  return LIFT_TYPE_CATALOG[liftTypeId].categoryId;
}

export function LiftTypeTree({ value, onChange }: {
  value: LiftTypeId;
  onChange: (liftTypeId: LiftTypeId) => void;
}) {
  const [openCategory, setOpenCategory] = useState<LiftCategoryId>(() => categoryFor(value));

  useEffect(() => {
    setOpenCategory(categoryFor(value));
  }, [value]);

  return (
    <div className="lift-type-tree" role="tree" aria-label="Lift type">
      {LIFT_CATEGORIES.map((category) => {
        const open = openCategory === category.id;
        return (
          <div className="lift-type-category" key={category.id}>
            <button
              type="button"
              className="lift-type-category-btn"
              role="treeitem"
              aria-expanded={open}
              onClick={() => setOpenCategory(category.id)}
            >
              <span aria-hidden="true">{open ? '▾' : '▸'}</span>
              {category.label}
            </button>
            {open && (
              <div className="lift-type-options" role="group" aria-label={category.label}>
                {LIFT_TYPE_SPECS.filter((spec) => spec.categoryId === category.id).map((spec) => (
                  <button
                    type="button"
                    key={spec.id}
                    role="treeitem"
                    aria-selected={value === spec.id}
                    className={`lift-type-option${value === spec.id ? ' is-selected' : ''}`}
                    onClick={() => onChange(spec.id)}
                  >
                    {spec.optionLabel}
                  </button>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function LiftTypeField({ value, expanded, onToggle, onChange }: {
  value: LiftTypeId;
  expanded: boolean;
  onToggle: () => void;
  onChange: (liftTypeId: LiftTypeId) => void;
}) {
  return (
    <div className="lift-type-field">
      <div className="lift-field">
        <span className="lift-field-label">Type</span>
        <span className="lift-field-value lift-type-value">{liftTypeLabel(value)}</span>
        <button type="button" className="lift-link-btn" aria-expanded={expanded} onClick={onToggle}>
          Change
        </button>
      </div>
      {expanded && <LiftTypeTree value={value} onChange={onChange} />}
    </div>
  );
}

function verticalText(
  verticalM: number | null,
  units: Units,
  status: LiveElevationStatus | 'resolved',
): string {
  if (verticalM != null) return fmtDistance(verticalM, units);
  if (status === 'pending') return 'Sampling…';
  if (status === 'error') return 'Unavailable';
  return '—';
}

/** Shared performance readout for builder, details, and the network-facing lift UI. */
export function LiftStatsBlock({
  points,
  elev,
  liftTypeId,
  units,
  verticalStatus = 'resolved',
  showCost = false,
  showEndpointElevations = true,
  elevSlot,
}: {
  points: [[number, number], [number, number]];
  elev: [number | null, number | null];
  liftTypeId: LiftTypeId;
  units: Units;
  verticalStatus?: LiveElevationStatus | 'resolved';
  showCost?: boolean;
  showEndpointElevations?: boolean;
  elevSlot?: React.ReactNode;
}) {
  const stats = liftStats(points, elev);
  const performance = liftPerformance(liftTypeId, stats.lengthM);
  const bottomElev = stats.topIndex === null ? null : elev[stats.topIndex === 1 ? 0 : 1];
  const topElev = stats.topIndex === null ? null : elev[stats.topIndex];
  return (
    <div className="lift-stats">
      <div className="readout-line">
        <span className="lift-stat-label">Length</span>
        <span className="lift-stat-value">{fmtDistance(stats.lengthM, units)}</span>
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Capacity</span>
        <span className="lift-stat-value">{Math.round(performance.capacityPph).toLocaleString()}/hr</span>
      </div>
      {elevSlot}
      <div className="readout-line">
        <span className="lift-stat-label">Vertical</span>
        <span className="lift-stat-value">{verticalText(stats.verticalM, units, verticalStatus)}</span>
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Estimated Ride Time</span>
        <span className="lift-stat-value">{fmtRideTime(performance.rideTimeS)}</span>
      </div>
      {showCost && (
        <div className="readout-line">
          <span className="lift-stat-label">Cost</span>
          <span className="lift-stat-value">TBD</span>
        </div>
      )}
      {showEndpointElevations && bottomElev != null && topElev != null && (
        <div className="readout-line">
          <span className="lift-stat-label">Base / Top</span>
          <span className="lift-stat-value">
            {fmtDistance(bottomElev, units)} / {fmtDistance(topElev, units)}
          </span>
        </div>
      )}
    </div>
  );
}

function anchoredVerticalStatus(tool: Extract<LiftTool, { phase: 'anchored' }>): LiveElevationStatus {
  if (tool.anchorElevStatus === 'error' || tool.cursorElevStatus === 'error') return 'error';
  if (tool.anchorElevStatus === 'pending' || tool.cursorElevStatus === 'pending') return 'pending';
  return tool.anchorElevStatus === 'ok' && tool.cursorElevStatus === 'ok' ? 'ok' : 'idle';
}

export function LiftControl({
  tool,
  lifts,
  selectedId,
  units,
  onArm,
  onStartPlacement,
  onTypeChange,
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
  onStartPlacement: () => void;
  onTypeChange: (liftTypeId: LiftTypeId) => void;
  onCancel: () => void;
  onDraftChange: (patch: Partial<DraftLift>) => void;
  onConfirm: () => void;
  onSelect: (id: string) => void;
  onEditPatch: (id: string, patch: CommittedLiftPatch) => void;
  onCloseEdit: () => void;
  onDelete: (id: string) => void;
  onRetryElevation: () => void;
  building?: boolean;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [typePickerOpen, setTypePickerOpen] = useState(false);

  useEffect(() => {
    setTypePickerOpen(false);
  }, [tool.phase]);

  const changeType = (liftTypeId: LiftTypeId) => {
    onTypeChange(liftTypeId);
    if (tool.phase !== 'choosing') setTypePickerOpen(false);
  };

  if (tool.phase === 'choosing') {
    return (
      <div className="site-control site-control-wide lift-panel lift-builder-panel">
        <PanelHead title="New lift" onClose={onCancel} />
        <div className="site-hint">Choose a lift type, then draw its terminal line.</div>
        <LiftTypeTree value={tool.liftTypeId} onChange={changeType} />
        <div className="site-actions lift-builder-actions">
          <button className="site-btn site-btn-primary" onClick={onStartPlacement}>Draw lift</button>
          <button className="site-btn" onClick={onCancel}>Cancel</button>
        </div>
      </div>
    );
  }

  if (tool.phase === 'armed') {
    return (
      <div className="site-control site-control-wide lift-panel lift-builder-panel">
        <PanelHead title="New lift" onClose={onCancel} />
        <LiftTypeField value={tool.liftTypeId} expanded={typePickerOpen}
          onToggle={() => setTypePickerOpen((open) => !open)} onChange={changeType} />
        <div className="site-hint">Click the map to place the first terminal</div>
        <button className="site-btn" onClick={onCancel}>Cancel</button>
      </div>
    );
  }

  if (tool.phase === 'anchored') {
    return (
      <div className="site-control site-control-wide lift-panel lift-builder-panel">
        <PanelHead title="New lift" onClose={onCancel} />
        <LiftTypeField value={tool.liftTypeId} expanded={typePickerOpen}
          onToggle={() => setTypePickerOpen((open) => !open)} onChange={changeType} />
        {tool.cursor ? (
          <LiftStatsBlock
            points={[tool.a, tool.cursor]}
            elev={tool.elev}
            liftTypeId={tool.liftTypeId}
            units={units}
            verticalStatus={anchoredVerticalStatus(tool)}
            showCost
            showEndpointElevations={false}
          />
        ) : (
          <div className="site-hint">Move the pointer, then click to place the other terminal</div>
        )}
        <button className="site-btn" onClick={onCancel}>Cancel</button>
      </div>
    );
  }

  if (tool.phase === 'review') {
    const draft = tool.draft;
    return (
      <div className="site-control site-control-wide lift-panel lift-builder-panel">
        <PanelHead title="Review lift" onClose={onCancel} />
        <LiftTypeField value={draft.liftTypeId} expanded={typePickerOpen}
          onToggle={() => setTypePickerOpen((open) => !open)} onChange={changeType} />
        <LiftIdentityFields identifier={draft.identifier} name={draft.name} onChange={onDraftChange} />
        <StatusToggle value={draft.status} onChange={(status) => onDraftChange({ status })} />
        <LiftStatsBlock
          points={draft.points}
          elev={draft.elev}
          liftTypeId={draft.liftTypeId}
          units={units}
          verticalStatus={draft.elevStatus}
          showCost
          elevSlot={
            <>
              {draft.elevStatus === 'pending' && <div className="site-hint">Sampling elevation…</div>}
              {draft.elevStatus === 'error' && (
                <div className="lift-warning">
                  Elevation unavailable{' '}
                  <button className="lift-link-btn" onClick={onRetryElevation}>Retry</button>
                </div>
              )}
            </>
          }
        />
        <div className="site-actions lift-builder-actions">
          <button className="site-btn site-btn-primary" onClick={onConfirm} disabled={building}>
            {building ? (
              <><span className="site-btn-spinner" aria-hidden="true" /> Building…</>
            ) : draft.status === 'complete' ? 'Build lift' : 'Add to plan'}
          </button>
          <button className="site-btn" onClick={onCancel} disabled={building}>Cancel</button>
        </div>
      </div>
    );
  }

  const editing = selectedId ? lifts.find((lift) => lift.id === selectedId) : null;
  if (editing) {
    return (
      <div className="site-control site-control-wide lift-panel">
        <PanelHead title="Edit lift" onClose={onCloseEdit} />
        <div className="lift-field">
          <span className="lift-field-label">Type</span>
          <span className="lift-field-value">{liftTypeLabel(editing.liftTypeId)}</span>
        </div>
        <LiftIdentityFields identifier={editing.identifier} name={editing.name}
          onChange={(patch) => onEditPatch(editing.id, patch)} />
        <StatusToggle value={editing.status}
          onChange={(status) => onEditPatch(editing.id, { status })} />
        <LiftStatsBlock points={editing.points} elev={editing.endpointElevM}
          liftTypeId={editing.liftTypeId} units={units} />
        {confirmDelete ? (
          <div className="lift-delete-confirm">
            <div className="lift-delete-warn">
              Delete “{formatLiftLabel(editing)}”? This can't be undone.
            </div>
            <div className="site-actions">
              <button className="site-btn site-btn-danger" onClick={() => {
                onDelete(editing.id);
                setConfirmDelete(false);
              }}>Delete</button>
              <button className="site-btn" onClick={() => setConfirmDelete(false)}>Keep</button>
            </div>
          </div>
        ) : (
          <div className="site-actions">
            <button className="site-btn site-btn-primary" onClick={onCloseEdit}>Done</button>
            <button className="site-btn site-btn-danger-ghost"
              onClick={() => setConfirmDelete(true)}>Delete</button>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="site-control site-control-wide">
      <button className="site-btn site-btn-primary" onClick={onArm}>⛷ New lift</button>
      {lifts.length > 0 && (
        <div className="lift-list">
          {lifts.map((lift) => {
            const performance = liftPerformance(lift.liftTypeId, lift.lengthM);
            return (
              <button key={lift.id} type="button" className="lift-row lift-row-btn"
                onClick={() => onSelect(lift.id)} title={`Edit ${formatLiftLabel(lift)}`}>
                <span className={`lift-row-dot lift-row-dot--${lift.status}`} aria-hidden="true" />
                <span className="lift-row-main">
                  <span className="lift-row-name">{formatLiftLabel(lift)}</span>
                  <span className="lift-row-summary">
                    {liftTypeLabel(lift.liftTypeId)} · {fmtDistance(lift.lengthM, units)}
                    {` · ${Math.round(performance.capacityPph).toLocaleString()}/hr`}
                    {lift.status === 'planning' ? ' · Planning' : ''}
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
