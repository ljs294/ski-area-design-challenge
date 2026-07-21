import { useState } from 'react';
import type { SavedTrail, SavedTrailPart, TrailDifficulty, TrailStatus } from '../types';
import type { Units } from './SettingsContext';
import { fmtDistance } from '../lifts';
import { DIFFICULTY_LABELS, fmtArea, fmtSlope, fmtVertical, trailPartsStats,
  MIN_BRUSH_WIDTH_M, MAX_BRUSH_WIDTH_M } from '../trails';
import { TrailProfile } from './TrailProfile';
import type { PaintMode } from './trailPaintEngine';

export type TrailTool =
  | { phase: 'idle' }
  | { phase: 'paint'; mode: PaintMode; polygons: [number, number][][][]; areaM2: number; activeAreaM2: number | null; canUndo: boolean; pending: boolean; error: string | null }
  | { phase: 'analyzing'; polygons: [number, number][][][]; areaM2: number }
  | { phase: 'review'; draft: DraftTrail };

export interface DraftTrail {
  parts: SavedTrailPart[];
  areaM2: number;
  brushWidthM: number;
  name: string;
  status: TrailStatus;
  difficulty: TrailDifficulty;
  elevStatus: 'pending' | 'ok' | 'error';
}

function PanelHead({ title, onClose }: { title: string; onClose: () => void }) {
  return <div className="dock-head"><span className="dock-head-title">{title}</span>
    <button className="settings-close-x" aria-label="Close" onClick={onClose}>×</button></div>;
}

function StatusToggle({ value, onChange }: { value: TrailStatus; onChange: (s: TrailStatus) => void }) {
  return <label className="lift-field"><span className="lift-field-label">Status</span>
    <div className="lift-status-toggle" role="group" aria-label="Build status">
      {(['planning', 'complete'] as TrailStatus[]).map((s) => <button key={s} type="button"
        className={`lift-status-btn${value === s ? ' is-active' : ''}`} onClick={() => onChange(s)}>
        {s === 'planning' ? 'Planning' : 'Complete'}</button>)}
    </div></label>;
}

function BrushWidthField({ widthM, units, disabled, onChange }: { widthM: number; units: Units; disabled: boolean; onChange: (m: number) => void }) {
  return <label className="lift-field trail-brush-field"><span className="lift-field-label">Brush</span>
    <input className="trail-brush-slider" type="range" min={MIN_BRUSH_WIDTH_M} max={MAX_BRUSH_WIDTH_M}
      step={2} value={widthM} disabled={disabled} onChange={(e) => onChange(Number(e.target.value))} />
    <span className="lift-field-value">{fmtDistance(widthM, units)}</span></label>;
}

export function TrailStatsBlock({ parts, areaM2, difficulty, units }: {
  parts: SavedTrailPart[]; areaM2: number; difficulty: TrailDifficulty; units: Units;
}) {
  const stats = trailPartsStats(parts);
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Length</span><span className="lift-stat-value">{fmtDistance(stats.lengthM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Area</span><span className="lift-stat-value">{fmtArea(areaM2, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Vertical</span><span className="lift-stat-value">{fmtVertical(stats.verticalM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Avg / max pitch</span><span className="lift-stat-value">
      {stats.verticalM == null ? '—' : `${fmtSlope(stats.avgSlopeDeg)} / ${fmtSlope(stats.maxSlopeDeg)}`}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Rating</span><span className="lift-stat-value trail-grade-inline">
      <span className={`trail-grade-dot trail-grade-dot--${difficulty}`} />{DIFFICULTY_LABELS[difficulty]}</span></div>
  </div>;
}

export function TrailControl({ tool, trails, selectedId, units, brushWidthM, onBrushWidthChange,
  onCancel, onModeChange, onUndo, onClear, onFinish, onDraftChange, onConfirm, onEditPatch,
  onCloseEdit, onDelete, onRetryElevation }: {
  tool: TrailTool; trails: SavedTrail[]; selectedId: string | null; units: Units; brushWidthM: number;
  onBrushWidthChange: (m: number) => void; onCancel: () => void; onModeChange: (m: PaintMode) => void;
  onUndo: () => void; onClear: () => void; onFinish: () => void; onDraftChange: (p: Partial<DraftTrail>) => void;
  onConfirm: () => void; onEditPatch: (id: string, patch: Partial<SavedTrail>) => void;
  onCloseEdit: () => void; onDelete: (id: string) => void; onRetryElevation: () => void;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);
  if (tool.phase === 'paint') return <div className="site-control site-control-wide trail-panel">
    <PanelHead title="Paint ski run" onClose={onCancel} />
    <BrushWidthField widthM={brushWidthM} units={units} disabled={tool.areaM2 > 0} onChange={onBrushWidthChange} />
    <div className="trail-paint-modes" role="group" aria-label="Brush mode">
      {(['paint', 'erase'] as PaintMode[]).map((mode) => <button key={mode} className={`site-btn${tool.mode === mode ? ' is-active' : ''}`}
        onClick={() => onModeChange(mode)}>{mode === 'paint' ? 'Paint' : 'Erase'}</button>)}
    </div>
    <div className="readout-line"><span className="lift-stat-label">Painted area</span>
      <span className="lift-stat-value">{tool.activeAreaM2 != null ? '~' : ''}{fmtArea(tool.activeAreaM2 ?? tool.areaM2, units)}</span></div>
    <div className="site-hint">Paint the skiable footprint. Lift the brush and continue anywhere.</div>
    {tool.error && <div className="lift-warning">{tool.error}</div>}
    <div className="site-actions"><button className="site-btn" disabled={!tool.canUndo || tool.pending} onClick={onUndo}>Undo</button>
      <button className="site-btn" disabled={tool.areaM2 === 0 || tool.pending} onClick={onClear}>Clear</button>
      <button className="site-btn site-btn-primary" disabled={tool.areaM2 === 0 || tool.pending} onClick={onFinish}>Finish</button></div>
  </div>;

  if (tool.phase === 'analyzing') return <div className="site-control site-control-wide trail-panel">
    <PanelHead title="Analyzing run" onClose={onCancel} /><div className="site-hint">Finding the trail centerline and terrain profile…</div>
  </div>;

  if (tool.phase === 'review') {
    const d = tool.draft;
    return <div className="site-control site-control-wide trail-panel lift-panel">
      <PanelHead title="Review ski run" onClose={onCancel} />
      <input className="name-entry-input lift-name-input" value={d.name} onChange={(e) => onDraftChange({ name: e.target.value })} />
      <TrailProfile parts={d.parts} units={units} difficulty={d.difficulty} />
      {d.elevStatus === 'error' && <div className="lift-warning">Elevation unavailable <button className="lift-link-btn" onClick={onRetryElevation}>Retry</button></div>}
      <StatusToggle value={d.status} onChange={(status) => onDraftChange({ status })} />
      <TrailStatsBlock parts={d.parts} areaM2={d.areaM2} difficulty={d.difficulty} units={units} />
      <div className="site-actions"><button className="site-btn site-btn-primary" disabled={d.elevStatus !== 'ok'} onClick={onConfirm}>
        {d.status === 'complete' ? 'Build run' : 'Add to plan'}</button><button className="site-btn" onClick={onCancel}>Cancel</button></div>
    </div>;
  }

  const editing = selectedId ? trails.find((t) => t.id === selectedId) : null;
  if (!editing) return null;
  return <div className="site-control site-control-wide trail-panel lift-panel">
    <PanelHead title="Edit run" onClose={onCloseEdit} />
    <input className="name-entry-input lift-name-input" value={editing.name} onChange={(e) => onEditPatch(editing.id, { name: e.target.value })} />
    <TrailProfile parts={editing.parts} units={units} difficulty={editing.difficulty} />
    <StatusToggle value={editing.status} onChange={(status) => onEditPatch(editing.id, { status })} />
    <TrailStatsBlock parts={editing.parts} areaM2={editing.areaM2} difficulty={editing.difficulty} units={units} />
    {confirmDelete ? <div className="lift-delete-confirm"><div className="lift-delete-warn">Delete “{editing.name}”?</div>
      <div className="site-actions"><button className="site-btn site-btn-danger" onClick={() => { onDelete(editing.id); setConfirmDelete(false); }}>Delete</button>
      <button className="site-btn" onClick={() => setConfirmDelete(false)}>Keep</button></div></div>
      : <div className="site-actions"><button className="site-btn site-btn-primary" onClick={onCloseEdit}>Done</button>
        <button className="site-btn site-btn-danger-ghost" onClick={() => setConfirmDelete(true)}>Delete</button></div>}
  </div>;
}
