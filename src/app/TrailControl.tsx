import { useState } from 'react';
import type { EarthworkEstimate, SavedTrail, SavedTrailPart, TrailDifficulty, TrailStatus } from '../types';
import type { Units } from './SettingsContext';
import { fmtDistance } from '../lifts';
import { DIFFICULTY_LABELS, fmtArea, fmtSlope, fmtVertical, trailPartsStats,
  MIN_BRUSH_WIDTH_M, MAX_BRUSH_WIDTH_M } from '../trails';
import { TrailProfile } from './TrailProfile';
import type { AnchorRef } from '../types/anchors';
import { describeAnchorDetail, type AnchorWorld } from '../topology';
import type { PaintMode } from './trailPaintEngine';
import type { TrailHeadAnchor, TrailTailAnchor } from './trailHeadAnchor';

export type TrailTool =
  | { phase: 'idle' }
  | { phase: 'place-head'; candidate: TrailHeadAnchor | null; error: string | null }
  | { phase: 'paint'; mode: PaintMode; polygons: [number, number][][][]; areaM2: number; activeAreaM2: number | null; canUndo: boolean; pending: boolean; error: string | null; anchor: TrailHeadAnchor; hasUserStroke: boolean }
  | { phase: 'place-tail'; mode: PaintMode; polygons: [number, number][][][]; areaM2: number; activeAreaM2: number | null; canUndo: boolean; pending: boolean; error: string | null; anchor: TrailHeadAnchor; hasUserStroke: boolean; candidate: TrailTailAnchor | null }
  | { phase: 'analyzing'; polygons: [number, number][][][]; areaM2: number; anchor: TrailHeadAnchor; tailAnchor: TrailTailAnchor }
  | { phase: 'review'; draft: DraftTrail };

export interface DraftTrail {
  parts: SavedTrailPart[];
  /** Terrain-sampled parts retained so an unchecked preview is lossless. */
  ungradedParts: SavedTrailPart[];
  areaM2: number;
  /** Exact painted area restored when grading is unchecked. */
  ungradedAreaM2: number;
  brushWidthM: number;
  name: string;
  status: TrailStatus;
  difficulty: TrailDifficulty;
  elevStatus: 'pending' | 'ok' | 'error';
  /** Why sampling failed, when it did. Absent falls back to a generic line. */
  elevError?: string | null;
  gradingEnabled: boolean;
  gradingStatus: 'idle' | 'pending' | 'ok' | 'error';
  gradingError: string | null;
  earthwork: EarthworkEstimate | null;
  maxGroundCrossSlopePct: number;
  maxFaceSlopePct: number;
  maxDisturbedWidthM: number;
  /** Metres of run too steep to bench, left at natural ground. */
  ungradedLengthM: number;
  infeasibleLines: [number, number][][];
  /**
   * The lift terminal or existing trail centerline chosen before painting. Its
   * exact point is also station 0 of the first centerline part.
   */
  anchor: AnchorRef | null;
  /** Exact required destination selected after brushing. */
  tailAnchor?: AnchorRef | null;
}

/**
 * One end of a run, named. The second line carries the segment and node numbers
 * and is allowed to wrap — clipping it would hide exactly what it exists to say.
 */
export function AnchorValue({ anchor, world }: { anchor: AnchorRef; world: AnchorWorld }) {
  const { label, detail } = describeAnchorDetail(anchor, world);
  return <>{label}{detail && <small className="trail-anchor-detail">{detail}</small>}</>;
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

function fmtVolume(m3: number, units: Units): string {
  const value = units === 'imperial' ? m3 * 1.30795062 : m3;
  return `${Math.round(value).toLocaleString()} ${units === 'imperial' ? 'yd³' : 'm³'}`;
}

export function EarthworkStats({ estimate, units,
  maxGroundCrossSlopePct, maxDisturbedWidthM }: {
  estimate: EarthworkEstimate;
  units: Units;
  maxGroundCrossSlopePct?: number;
  maxDisturbedWidthM?: number;
}) {
  return <div className="lift-stats trail-earthwork">
    <div className="readout-line"><span className="lift-stat-label">Cut</span>
      <span className="lift-stat-value">{fmtVolume(estimate.cutM3, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Fill</span>
      <span className="lift-stat-value">{fmtVolume(estimate.fillM3, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Balance</span>
      <span className="lift-stat-value">{estimate.balanceM3 >= 0 ? '+' : '−'}
        {fmtVolume(Math.abs(estimate.balanceM3), units)}</span></div>
    {maxGroundCrossSlopePct != null &&
      <div className="readout-line"><span className="lift-stat-label">Hillside cross slope</span>
        <span className="lift-stat-value">{maxGroundCrossSlopePct.toFixed(0)}%</span></div>}
    {maxDisturbedWidthM != null &&
      <div className="readout-line"><span className="lift-stat-label">Max disturbed width</span>
        <span className="lift-stat-value">{fmtDistance(maxDisturbedWidthM, units)}</span></div>}
  </div>;
}

export function TrailControl({ tool, trails, world, selectedId, units, brushWidthM, onBrushWidthChange,
  onCancel, onModeChange, onUndo, onClear, onFinish, onDraftChange, onConfirm, onEditPatch,
  onCloseEdit, onDelete, onRetryElevation, onGradingChange, onChangeHead, onBackToPaint,
  building = false }: {
  tool: TrailTool; trails: SavedTrail[];
  /** Lifts, junctions and nodes, so an anchor can be named rather than typed. */
  world: AnchorWorld;
  selectedId: string | null; units: Units; brushWidthM: number;
  onBrushWidthChange: (m: number) => void; onCancel: () => void; onModeChange: (m: PaintMode) => void;
  onUndo: () => void; onClear: () => void; onFinish: () => void; onDraftChange: (p: Partial<DraftTrail>) => void;
  onConfirm: () => void; onEditPatch: (id: string, patch: Partial<SavedTrail>) => void;
  onCloseEdit: () => void; onDelete: (id: string) => void; onRetryElevation: () => void;
  onGradingChange: (enabled: boolean) => void;
  onChangeHead: () => void;
  onBackToPaint?: () => void;
  /** True while the confirmed run is felling its cover — spins the build button. */
  building?: boolean;
}) {
  const [confirmDelete, setConfirmDelete] = useState(false);
  if (tool.phase === 'place-head') return <div className="site-control site-control-wide trail-panel">
    <PanelHead title="Place Trailhead" onClose={onCancel} />
    <div className="site-hint">Click a lift terminal at the top, or anywhere along an existing trail centerline.</div>
    {tool.candidate && <div className="readout-line"><span className="lift-stat-label">Ready to anchor</span>
      <span className="lift-stat-value"><AnchorValue anchor={tool.candidate} world={world} /></span></div>}
    {tool.error && <div className="lift-warning">{tool.error}</div>}
  </div>;

  if (tool.phase === 'place-tail') return <div className="site-control site-control-wide trail-panel">
    <PanelHead title="Place Trail End" onClose={onCancel} />
    <div className="site-hint">Select a lift base or another trail reached by the painted footprint.</div>
    {tool.candidate && <div className="readout-line"><span className="lift-stat-label">Ready to connect</span>
      <span className="lift-stat-value"><AnchorValue anchor={tool.candidate} world={world} /></span></div>}
    {tool.error && <div className="lift-warning">{tool.error}</div>}
    <button className="site-btn" onClick={onBackToPaint}>Back to brush</button>
  </div>;

  if (tool.phase === 'paint') return <div className="site-control site-control-wide trail-panel">
    <PanelHead title="Create Trail" onClose={onCancel} />
    <BrushWidthField widthM={brushWidthM} units={units} disabled={tool.hasUserStroke} onChange={onBrushWidthChange} />
    <div className="trail-paint-modes" role="group" aria-label="Brush mode">
      {(['paint', 'erase'] as PaintMode[]).map((mode) => <button key={mode} className={`site-btn${tool.mode === mode ? ' is-active' : ''}`}
        disabled={mode === 'erase' && !tool.hasUserStroke}
        onClick={() => onModeChange(mode)}>{mode === 'paint' ? 'Paint' : 'Erase'}</button>)}
    </div>
    <div className="readout-line"><span className="lift-stat-label">Painted area</span>
      <span className="lift-stat-value">{tool.activeAreaM2 != null ? '~' : ''}{fmtArea(tool.activeAreaM2 ?? tool.areaM2, units)}</span></div>
    <div className="site-hint">Trailhead anchored. Paint from the seed, or lift the brush and continue with another stroke.</div>
    {tool.error && <div className="lift-warning">{tool.error}</div>}
    <button className="lift-link-btn" disabled={tool.pending} onClick={onChangeHead}>Change trailhead</button>
    <div className="site-actions"><button className="site-btn" disabled={!tool.canUndo || tool.pending} onClick={onUndo}>Undo</button>
      <button className="site-btn" disabled={!tool.hasUserStroke || tool.pending} onClick={onClear}>Clear</button>
      <button className="site-btn site-btn-primary" disabled={!tool.hasUserStroke || tool.pending} onClick={onFinish}>Finish</button></div>
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
      {d.elevStatus === 'error' && <div className="lift-warning">{d.elevError ?? 'Elevation unavailable'} <button className="lift-link-btn" onClick={onRetryElevation}>Retry</button></div>}
      {/* Where the run starts. Required: an unconnected run cannot be skied to,
          and leaves the node map guessing at the topology. */}
      <div className="trail-anchor-row" data-anchor={d.anchor ? 'set' : 'unset'}>
        <span className="lift-stat-label">Starts from</span>
        <span className={`trail-anchor-value${d.anchor ? '' : ' is-missing'}`}>
          {d.anchor ? <AnchorValue anchor={d.anchor} world={world} /> : 'Not connected'}
        </span>
      </div>
      <div className="trail-anchor-row" data-anchor={d.tailAnchor ? 'set' : 'unset'}>
        <span className="lift-stat-label">Ends at</span>
        <span className={`trail-anchor-value${d.tailAnchor ? '' : ' is-missing'}`}>
          {d.tailAnchor ? <AnchorValue anchor={d.tailAnchor} world={world} /> : 'Not connected'}
        </span>
      </div>
      {!d.anchor && (
        <div className="site-hint">
          Restart creation and choose a lift terminal or existing trail centerline.
        </div>
      )}
      <StatusToggle value={d.status} onChange={(status) => onDraftChange({ status })} />
      <label className="trail-grade-terrain">
        <input type="checkbox" checked={d.gradingEnabled}
          disabled={d.elevStatus !== 'ok' || d.gradingStatus === 'pending' || building}
          onChange={(e) => onGradingChange(e.target.checked)} />
        <span><strong>Grade terrain</strong><small>Level the run across its width so contours run square to the centreline, with 45° cut and fill faces. Edited contours preview in yellow.</small></span>
      </label>
      {d.gradingStatus === 'pending' && <div className="site-hint">Calculating terrain grade…</div>}
      {d.gradingStatus === 'error' && <div className="lift-warning">{d.gradingError ?? 'Unable to preview terrain grading.'}</div>}
      {d.gradingStatus === 'ok' && d.ungradedLengthM > 0 &&
        <div className="site-hint">{fmtDistance(d.ungradedLengthM, units)} of this run is steeper
          than 45° ({Math.round(d.maxGroundCrossSlopePct)}% cross slope) and was left at natural
          ground — marked red.</div>}
      {d.gradingEnabled && d.gradingStatus === 'ok' && d.earthwork &&
        <EarthworkStats estimate={d.earthwork} units={units}
          maxGroundCrossSlopePct={d.maxGroundCrossSlopePct}
          maxDisturbedWidthM={d.maxDisturbedWidthM} />}
      {d.gradingEnabled && d.status === 'planning' && d.gradingStatus === 'ok' &&
        <div className="site-hint">Preview only. Choose Complete to commit this terrain edit.</div>}
      <TrailStatsBlock parts={d.parts} areaM2={d.areaM2} difficulty={d.difficulty} units={units} />
      <div className="site-actions"><button className="site-btn site-btn-primary"
        disabled={d.elevStatus !== 'ok' || building || d.anchor == null || d.tailAnchor == null ||
          (d.gradingEnabled && d.gradingStatus !== 'ok')}
        onClick={onConfirm}>
        {building ? <><span className="site-btn-spinner" aria-hidden="true" /> Building…</> : d.status === 'complete' ? 'Build run' : 'Add to plan'}</button>
        <button className="site-btn" onClick={onCancel} disabled={building}>Cancel</button></div>
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
    {editing.earthwork && <EarthworkStats estimate={editing.earthwork} units={units}
      />}
    {confirmDelete ? <div className="lift-delete-confirm"><div className="lift-delete-warn">Delete “{editing.name}”?</div>
      <div className="site-actions"><button className="site-btn site-btn-danger" onClick={() => { onDelete(editing.id); setConfirmDelete(false); }}>Delete</button>
      <button className="site-btn" onClick={() => setConfirmDelete(false)}>Keep</button></div></div>
      : <div className="site-actions"><button className="site-btn site-btn-primary" onClick={onCloseEdit}>Done</button>
        <button className="site-btn site-btn-danger-ghost" onClick={() => setConfirmDelete(true)}>Delete</button></div>}
  </div>;
}
