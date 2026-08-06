import { useEffect, useState } from 'react';
import type { SavedDam, SavedPond } from '../types';
import { damFillTimeSeconds } from '../damAnalysis';
import { formatLakeDepth, formatLakeVolume } from '../lakeAnalysis';
import { fmtDistance } from '../lifts';
import { MAX_POND_EXCAVATION_M, POND_CREST_WIDTH_M, POND_FREEBOARD_M,
  POND_INNER_SLOPE, POND_OUTER_SLOPE } from '../pondEarthwork';
import { EarthworkStats } from './TrailControl';
import { roadLengthM } from '../roads';
import { SNOWMAKING_NODE_LABELS, type SavedSnowmakingNode } from '../snowmakingNodes';
import type { Units } from './SettingsContext';

export interface DraftDam extends Omit<SavedDam, 'id' | 'createdAt'> {}

export type DamTool =
  | { phase: 'idle' }
  | { phase: 'armed'; error: string | null }
  | { phase: 'anchored'; first: [number, number]; crestElevationM: number;
      cursor: [number, number] | null; error: string | null }
  | { phase: 'analyzing'; points: [[number, number], [number, number]]; crestElevationM: number }
  | { phase: 'review'; draft: DraftDam; error: string | null };

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

function DamStats({ dam, units }: { dam: DraftDam | SavedDam; units: Units }) {
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Source</span><span className="lift-stat-value">{dam.streamName}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Full pool elevation</span><span className="lift-stat-value">{fmtDistance(dam.crestElevationM, units)}</span></div>
    {dam.damCrestElevationM != null &&
      <div className="readout-line"><span className="lift-stat-label">Dam crest elevation</span>
        <span className="lift-stat-value">{fmtDistance(dam.damCrestElevationM, units)}</span></div>}
    <div className="readout-line"><span className="lift-stat-label">Dam length</span><span className="lift-stat-value">{fmtDistance(roadLengthM(dam.points), units)}</span></div>
    {dam.builtLengthM != null &&
      <div className="readout-line"><span className="lift-stat-label">Embankment length</span>
        <span className="lift-stat-value">{fmtDistance(dam.builtLengthM, units)}</span></div>}
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

/** The embankment the game grades into the valley, and its earthwork bill. */
function DamEarthworkStats({ dam, units }: { dam: DraftDam | SavedDam; units: Units }) {
  if (!dam.earthwork) return null;
  return <>
    {dam.disturbedAreaM2 != null && <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Disturbed area</span>
        <span className="lift-stat-value">{fmtArea(dam.disturbedAreaM2, units)}</span></div>
    </div>}
    <EarthworkStats estimate={dam.earthwork} units={units} />
    <div className="site-hint">{dam.earthwork.balanceM3 >= 0
      ? `Surplus cut: ${fmtVolume(dam.earthwork.balanceM3, units)} to haul off site.`
      : `Short of material: ${fmtVolume(-dam.earthwork.balanceM3, units)} of borrow fill must be hauled in.`}
      {' '}Crest is {fmtDistance(POND_FREEBOARD_M, units)} of freeboard over full pool,
      {' '}{fmtDistance(POND_CREST_WIDTH_M, units)} wide, on {POND_INNER_SLOPE}:1 upstream
      {' '}and {POND_OUTER_SLOPE}:1 downstream faces — the same embankment a pond berm uses.</div>
  </>;
}

function PondStats({ pond, units }: { pond: DraftPond | SavedPond; units: Units }) {
  return <div className="lift-stats">
    <div className="readout-line"><span className="lift-stat-label">Top elevation</span>
      <span className="lift-stat-value">{fmtDistance(pond.topElevationM, units)}</span></div>
    {pond.crestElevationM != null &&
      <div className="readout-line"><span className="lift-stat-label">Berm crest elevation</span>
        <span className="lift-stat-value">{fmtDistance(pond.crestElevationM, units)}</span></div>}
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

/** The berm that holds the pool in, and the earthwork bill for building it. */
function PondEarthworkStats({ pond, units }: { pond: DraftPond | SavedPond; units: Units }) {
  if (!pond.earthwork) return null;
  return <>
    <div className="lift-stats">
      {pond.bermLengthM != null &&
        <div className="readout-line"><span className="lift-stat-label">Berm length</span>
          <span className="lift-stat-value">{fmtDistance(pond.bermLengthM, units)}</span></div>}
      {pond.maxBermHeightM != null &&
        <div className="readout-line"><span className="lift-stat-label">Max berm height</span>
          <span className="lift-stat-value">{fmtDistance(pond.maxBermHeightM, units)}</span></div>}
      {pond.maxCutDepthM != null &&
        <div className="readout-line"><span className="lift-stat-label">Max cut depth</span>
          <span className="lift-stat-value">{fmtDistance(pond.maxCutDepthM, units)}</span></div>}
      {pond.disturbedAreaM2 != null &&
        <div className="readout-line"><span className="lift-stat-label">Disturbed area</span>
          <span className="lift-stat-value">{fmtArea(pond.disturbedAreaM2, units)}</span></div>}
    </div>
    <EarthworkStats estimate={pond.earthwork} units={units} />
    <div className="site-hint">{pond.earthwork.balanceM3 >= 0
      ? `Surplus cut: ${fmtVolume(pond.earthwork.balanceM3, units)} to haul off site.`
      : `Short of material: ${fmtVolume(-pond.earthwork.balanceM3, units)} must be imported. Deepen the excavation to balance it.`}
      {' '}Crest is {fmtDistance(POND_FREEBOARD_M, units)} of freeboard over full pool,
      {' '}{fmtDistance(POND_CREST_WIDTH_M, units)} wide, on {POND_INNER_SLOPE}:1 water
      {' '}and {POND_OUTER_SLOPE}:1 outer faces.</div>
  </>;
}

function PondMetersField({ label, valueM, units, onCommit, minM = -1000, maxM = 10000 }: {
  label: string; valueM: number; units: Units; onCommit: (valueM: number) => void;
  minM?: number; maxM?: number;
}) {
  const factor = units === 'imperial' ? 3.280839895 : 1;
  const display = (valueM * factor).toFixed(1);
  const [draft, setDraft] = useState(display);
  useEffect(() => setDraft(display), [display]);
  const commit = () => {
    const value = Number(draft) / factor;
    if (!Number.isFinite(value) || value < minM || value > maxM) setDraft(display);
    else onCommit(value);
  };
  return <label className="lake-depth-row"><span className="lift-stat-label">{label}</span>
    <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number" step="0.5"
      value={draft} aria-label={`${label} in ${units === 'imperial' ? 'feet' : 'metres'}`}
      onChange={(event) => setDraft(event.target.value)} onBlur={commit}
      onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur();
        if (event.key === 'Escape') { event.preventDefault(); setDraft(display); } }} />
      <span>{units === 'imperial' ? 'ft' : 'm'}</span></span></label>;
}

function SnowmakingPondField({ checked, onChange }: {
  checked: boolean; onChange: (checked: boolean) => void;
}) {
  return <label className="trail-grade-terrain">
    <input type="checkbox" checked={checked} aria-label="Snowmaking pond"
      onChange={(event) => onChange(event.target.checked)} />
    <span><strong>Snowmaking pond</strong>
      <small>Include this pond's full volume in the ski area's snowmaking water capacity.</small></span>
  </label>;
}

/**
 * Name of the dam/pond a node was auto-seeded from, or null when the node
 * has no `source` (a future hand-placed node) or its source no longer
 * resolves (should be pruned by reconcileSnowmakingNodes, but stay
 * defensive rather than throwing on stale data).
 */
function snowmakingSourceName(node: SavedSnowmakingNode, dams: SavedDam[], ponds: SavedPond[]): string | null {
  const source = node.source;
  if (!source) return null;
  if (source.kind === 'dam') return dams.find((dam) => dam.id === source.damId)?.name ?? 'Unknown';
  return ponds.find((pond) => pond.id === source.pondId)?.name ?? 'Unknown';
}

/** Water storage and (eventually) distribution: dams, standalone ponds, and the
 *  pipe network that will carry their water uphill. Pipes are a placeholder —
 *  the button is deliberately inert until there is a network to build. */
export function SnowmakingControl({ damTool, pondTool, dams, ponds, selectedDam, selectedPond,
  nodes, selectedNode,
  units, onArmDam, onCancelDam, onDamDraftChange, onConfirmDam,
  onSelectDam, onDeleteDam, onCloseDam, onArmPond, onCancelPond, onUndoPond, onFinishPond,
  onPondDraftChange, onPondElevationChange, onPondExcavationChange,
  onConfirmPond, onSelectPond, onDeletePond, onClosePond,
  onPondSnowmakingChange,
  onSelectNode, onRenameNode, onCloseNode,
  onClose, building = false }: {
  damTool: DamTool; pondTool: PondTool; dams: SavedDam[];
  ponds: SavedPond[]; selectedDam: SavedDam | null; selectedPond: SavedPond | null;
  nodes: SavedSnowmakingNode[]; selectedNode: SavedSnowmakingNode | null; units: Units;
  onArmDam: () => void;
  onCancelDam: () => void; onDamDraftChange: (patch: Partial<DraftDam>) => void; onConfirmDam: () => void;
  onSelectDam: (id: string) => void; onDeleteDam: (id: string) => void; onCloseDam: () => void;
  onArmPond: () => void; onCancelPond: () => void; onUndoPond: () => void; onFinishPond: () => void;
  onPondDraftChange: (patch: Partial<DraftPond>) => void; onPondElevationChange: (valueM: number) => void;
  onPondExcavationChange: (valueM: number) => void;
  onConfirmPond: () => void; onSelectPond: (id: string) => void; onDeletePond: (id: string) => void;
  onPondSnowmakingChange: (id: string, checked: boolean) => void;
  onClosePond: () => void;
  onSelectNode: (id: string) => void; onRenameNode: (id: string, name: string) => void; onCloseNode: () => void;
  onClose: () => void; building?: boolean;
}) {
  if (pondTool.phase === 'armed' || pondTool.phase === 'drawing') {
    const points = pondTool.phase === 'drawing' ? pondTool.points : [];
    return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title="New standalone pond" onClose={onCancelPond} />
      <div className="site-hint">Click around the full-pool boundary. Add at least three points, then finish the boundary.</div>
      {pondTool.error && <div className="lift-warning">{pondTool.error}</div>}
      <div className="site-actions"><button className="site-btn" onClick={onUndoPond} disabled={!points.length}>Undo point</button>
        <button className="site-btn site-btn-primary" onClick={onFinishPond} disabled={points.length < 3}>Finish boundary</button></div>
      <button className="site-btn" onClick={onCancelPond}>Cancel</button>
    </div>;
  }
  if (pondTool.phase === 'review') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="Review standalone pond" onClose={onCancelPond} />
    <input className="name-entry-input lift-name-input" value={pondTool.draft.name}
      onChange={(event) => onPondDraftChange({ name: event.target.value })} />
    <PondMetersField label="Top of pond elevation" valueM={pondTool.draft.topElevationM}
      units={units} onCommit={onPondElevationChange} />
    <PondMetersField label="Excavation below full pool" valueM={pondTool.draft.excavationDepthM ?? 0}
      units={units} minM={0} maxM={MAX_POND_EXCAVATION_M} onCommit={onPondExcavationChange} />
    <SnowmakingPondField checked={pondTool.draft.isSnowmaking !== false}
      onChange={(isSnowmaking) => onPondDraftChange({ isSnowmaking })} />
    {pondTool.error ? <div className="lift-warning">{pondTool.error}</div> : <>
      <PondStats pond={pondTool.draft} units={units} />
      <PondEarthworkStats pond={pondTool.draft} units={units} />
      <div className="site-hint">Highlighted contours show the ground this pond will reshape.</div>
    </>}
    <div className="site-actions"><button className="site-btn" onClick={onCancelPond}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={building || !!pondTool.error} onClick={onConfirmPond}>
        {building ? 'Building…' : 'Build pond'}</button></div>
  </div>;
  if (damTool.phase === 'armed') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="New dam" onClose={onCancelDam} />
    <div className="site-hint">Click one bank to set full pool, then click near the matching contour on the opposite bank.</div>
    {damTool.error && <div className="lift-warning">{damTool.error}</div>}
    <button className="site-btn" onClick={onCancelDam}>Cancel</button>
  </div>;
  if (damTool.phase === 'anchored') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="New dam" onClose={onCancelDam} />
    <div className="readout-line"><span className="lift-stat-label">Full pool elevation</span><span className="lift-stat-value">{fmtDistance(damTool.crestElevationM, units)}</span></div>
    <div className="site-hint">Move to the opposite bank. The endpoint snaps to the same elevation.</div>
    {damTool.error && <div className="lift-warning">{damTool.error}</div>}
    <button className="site-btn" onClick={onCancelDam}>Cancel</button>
  </div>;
  if (damTool.phase === 'analyzing') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="Analyzing pond" onClose={onCancelDam} /><div className="site-hint">Grading the embankment and tracing the full-pool shoreline…</div>
  </div>;
  if (damTool.phase === 'review') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="Review snowmaking pond" onClose={onCancelDam} />
    <input className="name-entry-input lift-name-input" value={damTool.draft.name} onChange={(event) => onDamDraftChange({ name: event.target.value })} />
    <DamStats dam={damTool.draft} units={units} />
    <DamEarthworkStats dam={damTool.draft} units={units} />
    <div className="site-hint">Highlighted contours show the ground this dam will reshape.</div>
    {damTool.error && <div className="lift-warning">{damTool.error}</div>}
    <div className="site-actions"><button className="site-btn" onClick={onCancelDam}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={building} onClick={onConfirmDam}>{building ? 'Building…' : 'Build dam'}</button></div>
  </div>;
  if (selectedNode) {
    const sourceName = snowmakingSourceName(selectedNode, dams, ponds);
    return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title={selectedNode.name} onClose={onCloseNode} />
      <input className="name-entry-input lift-name-input" value={selectedNode.name}
        onChange={(event) => onRenameNode(selectedNode.id, event.target.value)} />
      <div className="lift-stats">
        <div className="readout-line"><span className="lift-stat-label">Kind</span>
          <span className="lift-stat-value">{SNOWMAKING_NODE_LABELS[selectedNode.kind]}</span></div>
        {sourceName != null && <div className="readout-line"><span className="lift-stat-label">Source</span>
          <span className="lift-stat-value">{sourceName}</span></div>}
        <div className="readout-line"><span className="lift-stat-label">Elevation</span>
          <span className="lift-stat-value">{selectedNode.elevM != null ? fmtDistance(selectedNode.elevM, units) : '—'}</span></div>
      </div>
      <div className="site-hint">This node belongs to its water source and is removed automatically if that dam or pond is deleted.</div>
    </div>;
  }
  if (selectedDam) return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title={selectedDam.name} onClose={onCloseDam} /><DamStats dam={selectedDam} units={units} />
    <DamEarthworkStats dam={selectedDam} units={units} />
    <button className="lift-delete-btn" onClick={() => onDeleteDam(selectedDam.id)}>Remove dam</button>
  </div>;
  if (selectedPond) return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title={selectedPond.name} onClose={onClosePond} />
    <SnowmakingPondField checked={selectedPond.isSnowmaking !== false}
      onChange={(checked) => onPondSnowmakingChange(selectedPond.id, checked)} />
    <PondStats pond={selectedPond} units={units} />
    <PondEarthworkStats pond={selectedPond} units={units} />
    <button className="lift-delete-btn" onClick={() => onDeletePond(selectedPond.id)}>Remove pond</button>
  </div>;
  return <div className="lift-overview snowmaking-panel">
    <PanelHead title={`Snowmaking · ${dams.length} dams · ${ponds.length} ponds`} onClose={onClose} />
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmDam}>＋ Build dam</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmPond}>＋ Build standalone pond</button>
    <button className="lift-add-btn site-btn site-btn-primary" disabled
      title="Snowmaking pipe networks are not buildable yet">＋ Install snowmaking pipe</button>
    <div className="site-hint">Pipe networks are not buildable yet — water storage only for now.</div>
    {dams.length === 0 && ponds.length === 0 ? <div className="lift-overview-empty">No water storage yet — build your first dam or pond.</div> : <>
      {dams.length > 0 && <div className="lift-list">{dams.map((dam) => <button key={dam.id} className="lift-row lift-row-button" onClick={() => onSelectDam(dam.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{dam.name}</span>
          <span className="lift-row-summary">Snowmaking pond · {fmtArea(dam.areaM2, units)}</span></span></button>)}</div>}
      {ponds.length > 0 && <div className="lift-list">{ponds.map((pond) => <button key={pond.id} className="lift-row lift-row-button" onClick={() => onSelectPond(pond.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{pond.name}</span>
          <span className="lift-row-summary">{pond.isSnowmaking !== false ? 'Snowmaking pond' : 'Standalone pond'} · {fmtArea(pond.areaM2, units)}</span></span></button>)}</div>}
      {nodes.length > 0 && <div className="lift-list">{nodes.map((node) => {
        const sourceName = snowmakingSourceName(node, dams, ponds);
        const summary = sourceName != null ? `${SNOWMAKING_NODE_LABELS[node.kind]} · ${sourceName}` : SNOWMAKING_NODE_LABELS[node.kind];
        return <button key={node.id} className="lift-row lift-row-button" onClick={() => onSelectNode(node.id)}>
          <span className="snowmaking-node-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{node.name}</span>
            <span className="lift-row-summary">{summary}</span></span></button>;
      })}</div>}
    </>}
  </div>;
}
