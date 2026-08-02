import { useEffect, useState } from 'react';
import type { EarthworkEstimate, RoadType, SavedDam, SavedPond, SavedRoad } from '../types';
import { damFillTimeSeconds } from '../damAnalysis';
import { formatLakeDepth, formatLakeVolume } from '../lakeAnalysis';
import { fmtDistance } from '../lifts';
import { ROAD_CLEAR_BUFFER_M, ROAD_TYPE_LABELS, roadLengthM,
  TWO_LANE_CLEAR_HALF_WIDTH_M, TWO_LANE_ROAD_WIDTH_M } from '../roads';
import type { Units } from './SettingsContext';

export interface DraftRoad {
  name: string;
  roadType: RoadType;
  points: [number, number][];
  gradingStatus: 'pending' | 'ok' | 'error';
  gradingError: string | null;
  gradingPolygons: [number, number][][][];
  earthwork: EarthworkEstimate | null;
  maxFaceSlopePct: number;
  maxGroundCrossSlopePct: number;
  maxDisturbedWidthM: number;
  ungradedLengthM: number;
  gradingInfeasibleLines: [number, number][][];
}

export type RoadTool =
  | { phase: 'idle' }
  | { phase: 'armed'; roadType: RoadType }
  | { phase: 'drawing'; roadType: RoadType; points: [number, number][]; cursor: [number, number] | null }
  | { phase: 'review'; draft: DraftRoad };

export interface DraftDam extends Omit<SavedDam, 'id' | 'createdAt'> {}

export type DamTool =
  | { phase: 'idle' }
  | { phase: 'armed'; error: string | null }
  | { phase: 'anchored'; first: [number, number]; crestElevationM: number;
      cursor: [number, number] | null; error: string | null }
  | { phase: 'analyzing'; points: [[number, number], [number, number]]; crestElevationM: number }
  | { phase: 'review'; draft: DraftDam };

export interface DraftPond extends Omit<SavedPond, 'id' | 'createdAt'> {}

export type PondTool =
  | { phase: 'idle' }
  | { phase: 'armed'; error: string | null }
  | { phase: 'drawing'; points: [number, number][]; cursor: [number, number] | null; error: string | null }
  | { phase: 'review'; draft: DraftPond; error: string | null };

function PanelHead({ title, onClose }: { title: string; onClose: () => void }) {
  return <div className="dock-head"><span className="dock-head-title">{title}</span>
    <button className="settings-close-x" aria-label="Close" onClick={onClose}>✕</button></div>;
}

function RoadTypeField({ value, onChange }: { value: RoadType; onChange: (type: RoadType) => void }) {
  return <label className="lift-field"><span className="lift-field-label">Road width</span>
    <select className="lift-select" value={value}
      onChange={(event) => onChange(event.target.value as RoadType)}>
      <option value="two-lane">{ROAD_TYPE_LABELS['two-lane']}</option>
    </select>
  </label>;
}

function fmtVolume(m3: number, units: Units): string {
  const value = units === 'imperial' ? m3 * 1.30795062 : m3;
  return `${Math.round(value).toLocaleString()} ${units === 'imperial' ? 'yd³' : 'm³'}`;
}

function fmtArea(m2: number, units: Units): string {
  return units === 'imperial'
    ? `${(m2 / 4046.8564224).toLocaleString(undefined, { maximumFractionDigits: 2 })} acres`
    : `${Math.round(m2).toLocaleString()} m²`;
}

function fmtFlow(m3s: number, units: Units): string {
  return units === 'imperial'
    ? `${Math.round(m3s * 15850.323).toLocaleString()} US gal/min`
    : `${Math.round(m3s * 1000).toLocaleString()} L/s`;
}

function fmtFillTime(seconds: number): string {
  if (!Number.isFinite(seconds)) return 'Unavailable';
  if (seconds < 3600) return `${Math.max(1, Math.ceil(seconds / 60))} min`;
  if (seconds < 86400) return `${(seconds / 3600).toFixed(seconds < 36000 ? 1 : 0)} hr`;
  return `${(seconds / 86400).toFixed(seconds < 864000 ? 1 : 0)} days`;
}

function RoadStats({ points, units, draft }: { points: [number, number][]; units: Units; draft?: DraftRoad }) {
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Length</span>
      <span className="lift-stat-value">{fmtDistance(roadLengthM(points), units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Paved width</span>
      <span className="lift-stat-value">{fmtDistance(TWO_LANE_ROAD_WIDTH_M, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Clearing</span>
      <span className="lift-stat-value">{fmtDistance(TWO_LANE_CLEAR_HALF_WIDTH_M * 2, units)}</span></div>
    <div className="site-hint">Includes {fmtDistance(ROAD_CLEAR_BUFFER_M, units)} beyond each pavement edge.</div>
    {draft?.earthwork && <>
      <div className="readout-line"><span className="lift-stat-label">Cut</span>
        <span className="lift-stat-value">{fmtVolume(draft.earthwork.cutM3, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Fill</span>
        <span className="lift-stat-value">{fmtVolume(draft.earthwork.fillM3, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Balance</span>
        <span className="lift-stat-value">{draft.earthwork.balanceM3 >= 0 ? '+' : '−'}
          {fmtVolume(Math.abs(draft.earthwork.balanceM3), units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Cut / fill face</span>
        <span className="lift-stat-value">{draft.maxFaceSlopePct.toFixed(0)}%</span></div>
      <div className="readout-line"><span className="lift-stat-label">Max disturbed width</span>
        <span className="lift-stat-value">{fmtDistance(draft.maxDisturbedWidthM, units)}</span></div>
    </>}
  </div>;
}

function DamStats({ dam, units }: { dam: DraftDam | SavedDam; units: Units }) {
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Source</span><span className="lift-stat-value">{dam.streamName}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Crest elevation</span><span className="lift-stat-value">{fmtDistance(dam.crestElevationM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Dam length</span><span className="lift-stat-value">{fmtDistance(roadLengthM(dam.points), units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Average height</span><span className="lift-stat-value">{formatLakeDepth(dam.averageDamHeightM ?? null, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Maximum height</span><span className="lift-stat-value">{fmtDistance(dam.maxDamHeightM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Pond area</span><span className="lift-stat-value">{fmtArea(dam.areaM2, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Average depth</span><span className="lift-stat-value">{fmtDistance(dam.averageDepthM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Pond volume</span><span className="lift-stat-value">{formatLakeVolume(dam.capacityM3, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Gameplay inflow</span><span className="lift-stat-value">{fmtFlow(dam.inflowM3s, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Estimated fill time</span><span className="lift-stat-value">{fmtFillTime(damFillTimeSeconds(dam.capacityM3, dam.inflowM3s))}</span></div>
    <div className="site-hint">Assumes all modeled stream flow is captured, with no losses or releases.</div>
  </div>;
}

function PondStats({ pond, units }: { pond: DraftPond | SavedPond; units: Units }) {
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Top elevation</span>
      <span className="lift-stat-value">{fmtDistance(pond.topElevationM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Pond area</span>
      <span className="lift-stat-value">{fmtArea(pond.areaM2, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Average depth</span>
      <span className="lift-stat-value">{formatLakeDepth(pond.averageDepthM, units)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Maximum depth</span>
      <span className="lift-stat-value">{formatLakeDepth(pond.maxDepthM, units)}</span></div>
    <div className="readout-line lake-volume-row"><span className="lift-stat-label">Pond volume</span>
      <span className="lift-stat-value">{formatLakeVolume(pond.capacityM3, units)}</span></div>
    <div className="site-hint">No natural inflow is modeled. This pond will not fill on its own.</div>
  </div>;
}

function PondElevationField({ valueM, units, onCommit }: {
  valueM: number; units: Units; onCommit: (valueM: number) => void;
}) {
  const factor = units === 'imperial' ? 3.280839895 : 1;
  const display = (valueM * factor).toFixed(1);
  const [draft, setDraft] = useState(display);
  useEffect(() => setDraft(display), [display]);
  const commit = () => {
    const value = Number(draft) / factor;
    if (!Number.isFinite(value) || value < -1000 || value > 10000) setDraft(display);
    else onCommit(value);
  };
  return <label className="lake-depth-row"><span className="lift-stat-label">Top of pond elevation</span>
    <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number" step="0.5"
      value={draft} aria-label={`Top of pond elevation in ${units === 'imperial' ? 'feet' : 'metres'}`}
      onChange={(event) => setDraft(event.target.value)} onBlur={commit}
      onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur();
        if (event.key === 'Escape') { event.preventDefault(); setDraft(display); } }} />
      <span>{units === 'imperial' ? 'ft' : 'm'}</span></span></label>;
}

export function InfrastructureControl({ tool, damTool, pondTool, roads, dams, ponds, selectedDam, selectedPond,
  units, onArm, onCancel, onUndo,
  onFinish, onDraftChange, onConfirm, onArmDam, onCancelDam, onDamDraftChange, onConfirmDam,
  onSelectDam, onDeleteDam, onCloseDam, onArmPond, onCancelPond, onUndoPond, onFinishPond,
  onPondDraftChange, onPondElevationChange, onConfirmPond, onSelectPond, onDeletePond, onClosePond,
  onClose, building = false }: {
  tool: RoadTool; damTool: DamTool; pondTool: PondTool; roads: SavedRoad[]; dams: SavedDam[];
  ponds: SavedPond[]; selectedDam: SavedDam | null; selectedPond: SavedPond | null; units: Units;
  onArm: (roadType: RoadType) => void; onCancel: () => void; onUndo: () => void; onFinish: () => void;
  onDraftChange: (patch: Partial<DraftRoad>) => void; onConfirm: () => void; onArmDam: () => void;
  onCancelDam: () => void; onDamDraftChange: (patch: Partial<DraftDam>) => void; onConfirmDam: () => void;
  onSelectDam: (id: string) => void; onDeleteDam: (id: string) => void; onCloseDam: () => void;
  onArmPond: () => void; onCancelPond: () => void; onUndoPond: () => void; onFinishPond: () => void;
  onPondDraftChange: (patch: Partial<DraftPond>) => void; onPondElevationChange: (valueM: number) => void;
  onConfirmPond: () => void; onSelectPond: (id: string) => void; onDeletePond: (id: string) => void;
  onClosePond: () => void;
  onClose: () => void; building?: boolean;
}) {
  if (pondTool.phase === 'armed' || pondTool.phase === 'drawing') {
    const points = pondTool.phase === 'drawing' ? pondTool.points : [];
    return <div className="site-control site-control-wide infrastructure-panel">
      <PanelHead title="New standalone pond" onClose={onCancelPond} />
      <div className="site-hint">Click around the full-pool boundary. Add at least three points, then finish the boundary.</div>
      {pondTool.error && <div className="lift-warning">{pondTool.error}</div>}
      <div className="site-actions"><button className="site-btn" onClick={onUndoPond} disabled={!points.length}>Undo point</button>
        <button className="site-btn site-btn-primary" onClick={onFinishPond} disabled={points.length < 3}>Finish boundary</button></div>
      <button className="site-btn" onClick={onCancelPond}>Cancel</button>
    </div>;
  }
  if (pondTool.phase === 'review') return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title="Review standalone pond" onClose={onCancelPond} />
    <input className="name-entry-input lift-name-input" value={pondTool.draft.name}
      onChange={(event) => onPondDraftChange({ name: event.target.value })} />
    <PondElevationField valueM={pondTool.draft.topElevationM} units={units} onCommit={onPondElevationChange} />
    {pondTool.error ? <div className="lift-warning">{pondTool.error}</div> : <PondStats pond={pondTool.draft} units={units} />}
    <div className="site-actions"><button className="site-btn" onClick={onCancelPond}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={building || !!pondTool.error} onClick={onConfirmPond}>
        {building ? 'Building…' : 'Build pond'}</button></div>
  </div>;
  if (damTool.phase === 'armed') return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title="New dam" onClose={onCancelDam} />
    <div className="site-hint">Click one bank to set the crest elevation, then click near the matching contour on the opposite bank.</div>
    {damTool.error && <div className="lift-warning">{damTool.error}</div>}
    <button className="site-btn" onClick={onCancelDam}>Cancel</button>
  </div>;
  if (damTool.phase === 'anchored') return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title="New dam" onClose={onCancelDam} />
    <div className="readout-line"><span className="lift-stat-label">Crest elevation</span><span className="lift-stat-value">{fmtDistance(damTool.crestElevationM, units)}</span></div>
    <div className="site-hint">Move to the opposite bank. The endpoint snaps to the same elevation.</div>
    {damTool.error && <div className="lift-warning">{damTool.error}</div>}
    <button className="site-btn" onClick={onCancelDam}>Cancel</button>
  </div>;
  if (damTool.phase === 'analyzing') return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title="Analyzing pond" onClose={onCancelDam} /><div className="site-hint">Tracing the full-pool shoreline and calculating capacity…</div>
  </div>;
  if (damTool.phase === 'review') return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title="Review snowmaking pond" onClose={onCancelDam} />
    <input className="name-entry-input lift-name-input" value={damTool.draft.name} onChange={(event) => onDamDraftChange({ name: event.target.value })} />
    <DamStats dam={damTool.draft} units={units} />
    <div className="site-actions"><button className="site-btn" onClick={onCancelDam}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={building} onClick={onConfirmDam}>{building ? 'Building…' : 'Build dam'}</button></div>
  </div>;
  if (tool.phase === 'idle' && selectedDam) return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title={selectedDam.name} onClose={onCloseDam} /><DamStats dam={selectedDam} units={units} />
    <button className="lift-delete-btn" onClick={() => onDeleteDam(selectedDam.id)}>Remove dam</button>
  </div>;
  if (tool.phase === 'idle' && selectedPond) return <div className="site-control site-control-wide infrastructure-panel">
    <PanelHead title={selectedPond.name} onClose={onClosePond} /><PondStats pond={selectedPond} units={units} />
    <button className="lift-delete-btn" onClick={() => onDeletePond(selectedPond.id)}>Remove pond</button>
  </div>;
  if (tool.phase === 'idle') return <div className="lift-overview infrastructure-panel">
    <PanelHead title={`Infrastructure · ${roads.length} roads · ${dams.length} dams · ${ponds.length} ponds`} onClose={onClose} />
    <RoadTypeField value="two-lane" onChange={() => undefined} />
    <button className="lift-add-btn site-btn site-btn-primary" onClick={() => onArm('two-lane')}>＋ Build road</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmDam}>＋ Build dam</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmPond}>＋ Build standalone pond</button>
    {roads.length === 0 && dams.length === 0 && ponds.length === 0 ? <div className="lift-overview-empty">No infrastructure yet — build your first road, dam, or pond.</div> : <>
      {roads.length > 0 && <div className="lift-list">{roads.map((road) => <div key={road.id} className="lift-row">
        <span className="infrastructure-road-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{road.name}</span>
          <span className="lift-row-summary">Two-lane · {fmtDistance(road.lengthM, units)}</span></span></div>)}</div>}
      {dams.length > 0 && <div className="lift-list">{dams.map((dam) => <button key={dam.id} className="lift-row lift-row-button" onClick={() => onSelectDam(dam.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{dam.name}</span>
          <span className="lift-row-summary">Snowmaking pond · {fmtArea(dam.areaM2, units)}</span></span></button>)}</div>}
      {ponds.length > 0 && <div className="lift-list">{ponds.map((pond) => <button key={pond.id} className="lift-row lift-row-button" onClick={() => onSelectPond(pond.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{pond.name}</span>
          <span className="lift-row-summary">Standalone pond · {fmtArea(pond.areaM2, units)}</span></span></button>)}</div>}
    </>}
  </div>;
  if (tool.phase === 'armed' || tool.phase === 'drawing') {
    const points = tool.phase === 'drawing' ? tool.points : [];
    const previewPoints = tool.phase === 'drawing' && tool.cursor ? [...points, tool.cursor] : points;
    return <div className="site-control site-control-wide infrastructure-panel"><PanelHead title="New road" onClose={onCancel} />
      <RoadTypeField value={tool.roadType} onChange={() => undefined} /><div className="site-hint">Click along the road centerline. Pan or zoom between points.</div>
      {previewPoints.length >= 2 && <RoadStats points={previewPoints} units={units} />}
      <div className="site-actions"><button className="site-btn" onClick={onUndo} disabled={points.length === 0}>Undo point</button>
        <button className="site-btn site-btn-primary" onClick={onFinish} disabled={points.length < 2}>Finish route</button></div>
      <button className="site-btn" onClick={onCancel}>Cancel</button></div>;
  }
  return <div className="site-control site-control-wide infrastructure-panel"><PanelHead title="Review road" onClose={onCancel} />
    <input className="name-entry-input lift-name-input" value={tool.draft.name} onChange={(event) => onDraftChange({ name: event.target.value })} />
    <RoadTypeField value={tool.draft.roadType} onChange={(roadType) => onDraftChange({ roadType })} />
    <RoadStats points={tool.draft.points} units={units} draft={tool.draft} />
    {tool.draft.gradingStatus === 'pending' && <div className="site-hint">Calculating level road grade…</div>}
    {tool.draft.gradingStatus === 'error' && <div className="lift-warning">{tool.draft.gradingError ?? 'Unable to grade this road.'}</div>}
    {tool.draft.gradingStatus === 'ok' && tool.draft.ungradedLengthM > 0 && <div className="site-hint">{fmtDistance(tool.draft.ungradedLengthM, units)} of this route is steeper than 45° ({Math.round(tool.draft.maxGroundCrossSlopePct)}% cross slope) and was left at natural ground — marked red. Route around it for a level road.</div>}
    <div className="site-actions"><button className="site-btn" onClick={onCancel}>Cancel</button>
      <button className="site-btn site-btn-primary" onClick={onConfirm} disabled={building || tool.draft.gradingStatus !== 'ok'}>{building ? 'Building…' : 'Build road'}</button></div>
  </div>;
}
