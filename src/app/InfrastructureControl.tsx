import type { EarthworkEstimate, RoadType, SavedRoad } from '../types';
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

/** Roads only. Dams and ponds live in the Snowmaking dock beside the pipe
 *  network they feed — see SnowmakingControl. */
export function InfrastructureControl({ tool, roads, units, onArm, onCancel, onUndo,
  onFinish, onDraftChange, onConfirm, onClose, building = false }: {
  tool: RoadTool; roads: SavedRoad[]; units: Units;
  onArm: (roadType: RoadType) => void; onCancel: () => void; onUndo: () => void; onFinish: () => void;
  onDraftChange: (patch: Partial<DraftRoad>) => void; onConfirm: () => void;
  onClose: () => void; building?: boolean;
}) {
  if (tool.phase === 'idle') return <div className="lift-overview infrastructure-panel">
    <PanelHead title={`Infrastructure · ${roads.length} roads`} onClose={onClose} />
    <RoadTypeField value="two-lane" onChange={() => undefined} />
    <button className="lift-add-btn site-btn site-btn-primary" onClick={() => onArm('two-lane')}>＋ Build road</button>
    {roads.length === 0 ? <div className="lift-overview-empty">No infrastructure yet — build your first road.</div> : <>
      {roads.length > 0 && <div className="lift-list">{roads.map((road) => <div key={road.id} className="lift-row">
        <span className="infrastructure-road-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{road.name}</span>
          <span className="lift-row-summary">Two-lane · {fmtDistance(road.lengthM, units)}</span></span></div>)}</div>}
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
