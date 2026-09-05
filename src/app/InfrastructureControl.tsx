import type { RoadType, SavedRoad } from '../types';
import { fmtDistance } from '../lifts';
import { ROAD_CLEAR_BUFFER_M, ROAD_TYPE_LABELS, roadLengthM,
  TWO_LANE_CLEAR_HALF_WIDTH_M, TWO_LANE_ROAD_WIDTH_M } from '../roads';
import type { Units } from './SettingsContext';
import type { DraftRoad, RoadTool } from './roadControllerModel';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';
import type { GuestConnectivity } from './guestConnectivity';

export type { DraftRoad, RoadTool } from './roadControllerModel';

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
  onFinish, onDraftChange, onConfirm, onClose, onSelectRoad, building = false,
  guestPortal, guestPortalArmed = false, guestPortalError = null,
  guestRuntime,
  guestConnectivity,
  onArmGuestPortal, onCancelGuestPortal, onRemoveGuestPortal }: {
  tool: RoadTool; roads: SavedRoad[]; units: Units;
  onArm: (roadType: RoadType) => void; onCancel: () => void; onUndo: () => void; onFinish: () => void;
  onDraftChange: (patch: Partial<DraftRoad>) => void; onConfirm: () => void;
  onClose: () => void; onSelectRoad?: (id: string) => void; building?: boolean;
  guestPortal?: { label: string; nodeId: string } | null;
  guestPortalArmed?: boolean;
  guestPortalError?: string | null;
  guestRuntime?: GuestSimulationRuntime;
  guestConnectivity?: GuestConnectivity;
  onArmGuestPortal?: () => void;
  onCancelGuestPortal?: () => void;
  onRemoveGuestPortal?: () => void;
}) {
  if (tool.phase === 'idle') return <div className="lift-overview infrastructure-panel">
    <PanelHead title={`Infrastructure · ${roads.length} roads`} onClose={onClose} />
    <RoadTypeField value="two-lane" onChange={() => undefined} />
    <button className="lift-add-btn site-btn site-btn-primary" onClick={() => onArm('two-lane')}>＋ Build road</button>
    <div className="lift-field"><span className="lift-field-label">Guest simulation</span>
      {guestConnectivity && <div className={guestConnectivity.reachable ? 'lift-stats' : 'lift-warning'}
        role={guestConnectivity.reachable ? 'status' : 'alert'}>
        <strong>{guestConnectivity.reachable ? 'Resort reachable' : 'Resort unreachable'}</strong>
        <div>{guestConnectivity.message}</div>
        {guestConnectivity.connectedLiftName && <div>Lift terminal: {guestConnectivity.connectedLiftName}</div>}
        {guestPortal && <div>Road access: {guestConnectivity.roadAccessLabel}</div>}
      </div>}
      {guestPortalArmed ? <button className="site-btn" onClick={onCancelGuestPortal}>Cancel entrance placement</button>
        : <button className="site-btn" onClick={onArmGuestPortal}>{guestPortal ? 'Move Guest Entrance' : 'Place Guest Entrance'}</button>}
      {guestPortal && <button className="site-btn" onClick={onRemoveGuestPortal}>Remove Guest Entrance</button>}
      {guestPortalError && <div className="lift-warning" role="alert">{guestPortalError}</div>}
      {guestPortal && guestRuntime && <div className="lift-stats">
        <div className="readout-line"><span className="lift-stat-label">Worker</span>
          <span className="lift-stat-value">{guestRuntime.status}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Guests active</span>
          <span className="lift-stat-value">{guestRuntime.snapshot?.metrics.active.toLocaleString() ?? '0'}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Guests on map</span>
          <span className="lift-stat-value">{guestRuntime.points.length.toLocaleString()}</span></div>
        <div className="site-hint">{guestRuntime.message}</div>
      </div>}
    </div>
    {roads.length === 0 ? <div className="lift-overview-empty">No infrastructure yet — build your first road.</div> : <>
      {roads.length > 0 && <div className="lift-list">{roads.map((road) => <button key={road.id} className="lift-row" onClick={() => onSelectRoad?.(road.id)}>
        <span className="infrastructure-road-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{road.name}</span>
          <span className="lift-row-summary">Two-lane · {fmtDistance(road.lengthM, units)}</span></span></button>)}</div>}
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
