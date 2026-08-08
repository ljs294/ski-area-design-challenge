import { useEffect, useState } from 'react';
import type { SavedDam, SavedPond } from '../types';
import { damFillTimeSeconds } from '../damAnalysis';
import { formatLakeDepth, formatLakeVolume } from '../lakeAnalysis';
import { fmtDistance } from '../lifts';
import { MAX_POND_EXCAVATION_M, POND_CREST_WIDTH_M, POND_FREEBOARD_M,
  POND_INNER_SLOPE, POND_OUTER_SLOPE } from '../pondEarthwork';
import { EarthworkStats } from './TrailControl';
import { roadLengthM } from '../roads';
import { SNOWMAKING_NODE_LABELS } from '../snowmakingNodes';
import { snowmakingNodeLabel, snowmakingPipeSegments } from '../snowmakingNetwork';
import { SNOWMAKING_PIPE_DIAMETERS_IN } from '../types/snowmaking';
import type { SavedSnowmakingNode, SavedSnowmakingPipe, SnowmakingLakeSource,
  SavedSnowgun, SnowgunVariantId, SnowmakingPipeDiameterIn, SnowmakingPumpPort } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import type { DamTool, DraftDam } from './damControllerModel';
import type { DraftPond, PondTool } from './pondControllerModel';
import type { SnowmakingHydrantRunTool, SnowmakingNodeTool,
  SnowmakingPipeTool } from './snowmakingNetworkControllerModel';
import type { SnowmakingHydrantRunPreview } from './useSnowmakingNetworkController';
import { SnowgunDirectory, SnowgunInspector, SnowgunToolPanel } from './SnowgunControl';
import type { SnowgunTool } from './snowmakingGunControllerModel';
import type { SnowgunPlanPreview } from './useSnowgunController';
import { SnowmakingPumpPortEditor } from './SnowmakingPumpPortEditor';

export type { DamTool, DraftDam } from './damControllerModel';
export type { DraftPond, PondTool } from './pondControllerModel';

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

function HydrantSpacingField({ valueM, units, onChange }: {
  valueM: number; units: Units; onChange: (valueM: number) => void;
}) {
  const factor = units === 'imperial' ? 3.280839895 : 1;
  const [draft, setDraft] = useState((valueM * factor).toFixed(1));
  return <label className="lake-depth-row"><span className="lift-stat-label">Maximum spacing</span>
    <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number"
      min="0.1" step="1" value={draft}
      aria-label={`Maximum hydrant spacing in ${units === 'imperial' ? 'feet' : 'metres'}`}
      onChange={(event) => {
        setDraft(event.target.value);
        const next = Number(event.target.value) / factor;
        if (Number.isFinite(next)) onChange(next);
      }} />
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
function snowmakingSourceName(node: SavedSnowmakingNode, dams: SavedDam[], ponds: SavedPond[],
  lakes: SnowmakingLakeSource[]): string | null {
  const source = node.source;
  if (!source) return null;
  if (source.kind === 'dam') return dams.find((dam) => dam.id === source.damId)?.name ?? 'Unknown';
  if (source.kind === 'lake') return lakes.find((lake) => lake.id === source.lakeId)?.name ?? 'Unknown';
  return ponds.find((pond) => pond.id === source.pondId)?.name ?? 'Unknown';
}

/** Water storage and (eventually) distribution: dams, standalone ponds, and the
 *  pipe network that will carry their water uphill. Pipes are a placeholder —
 *  the button is deliberately inert until there is a network to build. */
export function SnowmakingControl({ damTool, pondTool, dams, ponds, lakes = [], selectedDam, selectedPond,
  nodes, pipes, guns, selectedNode, selectedPipe, selectedGun, pipeTool, nodeTool, hydrantRunTool,
  gunTool, gunPreview,
  hydrantRunPreview, diameterIn, snapping,
  units, onArmDam, onCancelDam, onDamDraftChange, onConfirmDam,
  onSelectDam, onDeleteDam, onCloseDam, onArmPond, onCancelPond, onUndoPond, onFinishPond,
  onPondDraftChange, onPondElevationChange, onPondExcavationChange,
  onConfirmPond, onSelectPond, onDeletePond, onClosePond,
  onPondSnowmakingChange,
  onArmPipe, onCancelPipe, onUndoPipe, onFinishPipe, onConfirmPipe, onRenameDraftPipe,
  onDiameterChange, onSnappingChange, onArmNode, onCancelNode, onConfirmNode,
  onSetPumpSuctionSide, onSetPumpPort,
  onArmHydrantRun, onCancelHydrantRun, onBackHydrantRun, onHydrantRunModeChange,
  onHydrantRunCountChange, onHydrantRunSpacingChange, onConfirmHydrantRun,
  onSelectNode, onRenameNode, onDeleteNode, onCloseNode,
  onSelectPipe, onPatchPipe, onDeletePipe, onClosePipe,
  onArmGuns, onCancelGuns, onSnowgunVariantChange, onRemoveDraftGun, onReviewGuns,
  onBackGuns, onConfirmGuns, onSelectGun, onMoveGun, onConfirmMoveGun, onDeleteGun, onCloseGun,
  onAnalyzeSystem, onClose, building = false }: {
  damTool: DamTool; pondTool: PondTool; dams: SavedDam[];
  ponds: SavedPond[]; selectedDam: SavedDam | null; selectedPond: SavedPond | null;
  lakes?: SnowmakingLakeSource[];
  nodes: SavedSnowmakingNode[]; pipes: SavedSnowmakingPipe[]; guns: SavedSnowgun[];
  selectedNode: SavedSnowmakingNode | null; selectedPipe: SavedSnowmakingPipe | null;
  selectedGun: SavedSnowgun | null;
  pipeTool: SnowmakingPipeTool; nodeTool: SnowmakingNodeTool;
  hydrantRunTool: SnowmakingHydrantRunTool; hydrantRunPreview: SnowmakingHydrantRunPreview | null;
  gunTool: SnowgunTool; gunPreview: SnowgunPlanPreview;
  diameterIn: SnowmakingPipeDiameterIn; snapping: boolean; units: Units;
  onArmDam: () => void;
  onCancelDam: () => void; onDamDraftChange: (patch: Partial<DraftDam>) => void; onConfirmDam: () => void;
  onSelectDam: (id: string) => void; onDeleteDam: (id: string) => void; onCloseDam: () => void;
  onArmPond: () => void; onCancelPond: () => void; onUndoPond: () => void; onFinishPond: () => void;
  onPondDraftChange: (patch: Partial<DraftPond>) => void; onPondElevationChange: (valueM: number) => void;
  onPondExcavationChange: (valueM: number) => void;
  onConfirmPond: () => void; onSelectPond: (id: string) => void; onDeletePond: (id: string) => void;
  onPondSnowmakingChange: (id: string, checked: boolean) => void;
  onClosePond: () => void;
  onArmPipe: () => void; onCancelPipe: () => void; onUndoPipe: () => void;
  onFinishPipe: () => void; onConfirmPipe: () => void; onRenameDraftPipe: (name: string) => void;
  onDiameterChange: (diameter: SnowmakingPipeDiameterIn) => void;
  onSnappingChange: (snapping: boolean) => void;
  onArmNode: (kind: 'pump' | 'hydrant') => void; onCancelNode: () => void; onConfirmNode: () => void;
  onSetPumpSuctionSide: (side: 'route-start' | 'route-end') => void;
  onSetPumpPort: (pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null) => void;
  onArmHydrantRun: () => void; onCancelHydrantRun: () => void; onBackHydrantRun: () => void;
  onHydrantRunModeChange: (mode: 'count' | 'spacing') => void;
  onHydrantRunCountChange: (count: number) => void;
  onHydrantRunSpacingChange: (spacingM: number) => void; onConfirmHydrantRun: () => void;
  onSelectNode: (id: string) => void; onRenameNode: (id: string, name: string) => void;
  onDeleteNode: (id: string) => void; onCloseNode: () => void;
  onSelectPipe: (id: string) => void;
  onPatchPipe: (id: string, patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>) => void;
  onDeletePipe: (id: string) => void; onClosePipe: () => void;
  onArmGuns: () => void; onCancelGuns: () => void;
  onSnowgunVariantChange: (id: SnowgunVariantId) => void;
  onRemoveDraftGun: (id: string) => void; onReviewGuns: () => void; onBackGuns: () => void;
  onConfirmGuns: () => void; onSelectGun: (id: string) => void; onMoveGun: (id: string) => void;
  onConfirmMoveGun: () => void; onDeleteGun: (id: string) => void; onCloseGun: () => void;
  onAnalyzeSystem: () => void;
  onClose: () => void; building?: boolean;
}) {
  const [pendingHydrantDeleteId, setPendingHydrantDeleteId] = useState<string | null>(null);
  if (gunTool.phase !== 'idle') return <SnowgunToolPanel tool={gunTool} preview={gunPreview}
    units={units} setVariant={onSnowgunVariantChange} removeDraft={onRemoveDraftGun}
    review={onReviewGuns} back={onBackGuns} confirm={onConfirmGuns} cancel={onCancelGuns}
    confirmMove={onConfirmMoveGun} />;
  if (pipeTool.phase === 'armed' || pipeTool.phase === 'drawing') {
    const pointCount = pipeTool.phase === 'drawing' ? pipeTool.points.length : 0;
    return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title="Install snowmaking pipe" onClose={onCancelPipe} />
      <div className="site-hint">Click to route the pipe. Snapping and live statistics are in the lower-right map options.</div>
      <div className="site-actions"><button className="site-btn" onClick={onUndoPipe}
        disabled={pointCount === 0}>Undo point</button>
        <button className="site-btn site-btn-primary" onClick={onFinishPipe}
          disabled={pointCount < 2}>Finish route</button></div>
      <button className="site-btn" onClick={onCancelPipe}>Cancel</button>
    </div>;
  }
  if (pipeTool.phase === 'review') return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title="Review snowmaking pipe" onClose={onCancelPipe} />
    <input className="name-entry-input lift-name-input" aria-label="Pipe name" value={pipeTool.name}
      onChange={(event) => onRenameDraftPipe(event.target.value)} />
    <label className="lake-depth-row"><span className="lift-stat-label">Diameter</span>
      <select className="lift-select" aria-label="Pipe diameter" value={diameterIn}
        onChange={(event) => onDiameterChange(Number(event.target.value) as SnowmakingPipeDiameterIn)}>
        {SNOWMAKING_PIPE_DIAMETERS_IN.map((diameter) => <option key={diameter} value={diameter}>{diameter}&quot;</option>)}
      </select></label>
    {pipeTool.error && <div className="lift-warning">{pipeTool.error}</div>}
    <div className="site-actions"><button className="site-btn" onClick={onCancelPipe}>Cancel</button>
      <button className="site-btn site-btn-primary" onClick={onConfirmPipe}>Install pipe</button></div>
  </div>;
  if (hydrantRunTool.phase !== 'idle') {
    const preview = hydrantRunPreview;
    const stepTitle = hydrantRunTool.phase === 'select-pipe' ? 'Select a pipe'
      : hydrantRunTool.phase === 'select-start' ? 'Select run start'
      : hydrantRunTool.phase === 'select-end' ? 'Select run end' : 'Review hydrant run';
    if (hydrantRunTool.phase !== 'review') return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title={stepTitle} onClose={onCancelHydrantRun} />
      <div className="site-hint">{hydrantRunTool.phase === 'select-pipe'
        ? 'Click an installed snowmaking pipe to begin.'
        : hydrantRunTool.phase === 'select-start'
          ? `Click the first hydrant position on ${preview?.pipeName ?? 'the selected pipe'}.`
          : 'Click the final hydrant position on the same pipe. The direction sets label order.'}</div>
      {preview?.lengthM != null && <div className="lift-stats"><div className="readout-line">
        <span className="lift-stat-label">Interval length</span>
        <span className="lift-stat-value">{fmtDistance(preview.lengthM, units)}</span></div></div>}
      {(hydrantRunTool.error || preview?.error) && <div className="lift-warning">
        {preview?.error ?? hydrantRunTool.error}</div>}
      <div className="site-actions">
        {hydrantRunTool.phase !== 'select-pipe' && <button className="site-btn"
          onClick={onBackHydrantRun}>Back</button>}
        <button className="site-btn" onClick={onCancelHydrantRun}>Cancel</button>
      </div>
    </div>;
    return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title={stepTitle} onClose={onCancelHydrantRun} />
      <div className="site-hint">Positions include both endpoints. Occupied positions are marked and skipped.</div>
      <fieldset className="hydrant-run-mode"><legend>Layout method</legend>
        <label><input type="radio" name="hydrant-run-mode" value="count"
          checked={hydrantRunTool.mode === 'count'}
          onChange={() => onHydrantRunModeChange('count')} /> By count</label>
        <label><input type="radio" name="hydrant-run-mode" value="spacing"
          checked={hydrantRunTool.mode === 'spacing'}
          onChange={() => onHydrantRunModeChange('spacing')} /> By maximum spacing</label>
      </fieldset>
      {hydrantRunTool.mode === 'count' ? <label className="lake-depth-row">
        <span className="lift-stat-label">Hydrant positions</span>
        <input className="lake-depth-input" type="number" min="2" max="500" step="1"
          aria-label="Hydrant position count" value={hydrantRunTool.count}
          onChange={(event) => onHydrantRunCountChange(Number(event.target.value))} /></label>
        : <HydrantSpacingField valueM={hydrantRunTool.spacingM} units={units}
          onChange={onHydrantRunSpacingChange} />}
      <div className="lift-stats">
        <div className="readout-line"><span className="lift-stat-label">Pipe</span>
          <span className="lift-stat-value">{preview?.pipeName ?? '—'}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Length</span>
          <span className="lift-stat-value">{preview?.lengthM != null
            ? fmtDistance(preview.lengthM, units) : '—'}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Actual spacing</span>
          <span className="lift-stat-value">{preview?.actualSpacingM != null
            ? fmtDistance(preview.actualSpacingM, units) : '—'}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Calculated positions</span>
          <span className="lift-stat-value">{preview?.positions.length ?? 0}</span></div>
        <div className="readout-line"><span className="lift-stat-label">New hydrants</span>
          <span className="lift-stat-value">{preview?.newCount ?? 0}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Skipped occupied</span>
          <span className="lift-stat-value">{preview?.skippedCount ?? 0}</span></div>
      </div>
      {(hydrantRunTool.error || preview?.error) && <div className="lift-warning">
        {preview?.error ?? hydrantRunTool.error}</div>}
      <div className="site-actions"><button className="site-btn" onClick={onBackHydrantRun}>Back</button>
        <button className="site-btn site-btn-primary"
          disabled={!!preview?.error || (preview?.newCount ?? 0) === 0}
          onClick={onConfirmHydrantRun}>Place {preview?.newCount ?? 0} hydrants</button></div>
      <button className="site-btn" onClick={onCancelHydrantRun}>Cancel</button>
    </div>;
  }
  if (nodeTool.phase === 'placing') {
    const pumpSnap = nodeTool.kind === 'pump' && nodeTool.candidate?.snap?.kind === 'pipe'
      ? nodeTool.candidate.snap : null;
    const pumpPipe = pumpSnap ? pipes.find((pipe) => pipe.id === pumpSnap.pipeId) ?? null : null;
    const pumpSegment = pumpPipe && nodeTool.candidate?.pumpSegmentId
      ? snowmakingPipeSegments(pumpPipe).find((segment) =>
        segment.id === nodeTool.candidate?.pumpSegmentId) ?? null : null;
    const sideName = (nodeId: string | null, fallback: string) => {
      const node = nodes.find((candidate) => candidate.id === nodeId);
      return node ? snowmakingNodeLabel(node) : fallback;
    };
    const routeStartName = sideName(pumpSegment?.fromNodeId ?? null, `${pumpPipe?.name ?? 'pipe'} start`);
    const routeEndName = sideName(pumpSegment?.toNodeId ?? null, `${pumpPipe?.name ?? 'pipe'} end`);
    return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title={`Place ${nodeTool.kind}`} onClose={onCancelNode} />
    <div className="site-hint">{nodeTool.kind === 'pump'
      ? 'Click inside an existing pipe segment, choose its suction side, then place the pump.'
      : 'Click a location, then confirm it. Leave this tool open to place several hydrants.'}</div>
    {nodeTool.kind !== 'pump' && <label className="snowmaking-snap-toggle">
      <input type="checkbox" checked={snapping}
        aria-label={`Snap single ${nodeTool.kind} to snowmaking network`}
        onChange={(event) => onSnappingChange(event.target.checked)} />
      <span><strong>Node snapping</strong><small>Snap within 16 px of a pipe or existing node.</small></span>
    </label>}
    {nodeTool.error && <div className="lift-warning">{nodeTool.error}</div>}
    {nodeTool.candidate && <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Elevation</span>
        <span className="lift-stat-value">{nodeTool.candidate.elevM != null
          ? fmtDistance(nodeTool.candidate.elevM, units) : '—'}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Connection</span>
        <span className="lift-stat-value">{nodeTool.candidate.snap ? 'Snapped to network' : 'Free-standing'}</span></div>
    </div>}
    {nodeTool.kind === 'pump' && pumpSegment && <fieldset className="snowmaking-pump-ports">
      <legend>Hydraulic direction</legend>
      <label><input type="radio" name="new-pump-direction"
        checked={nodeTool.candidate?.pumpSuctionSide === 'route-start'}
        onChange={() => onSetPumpSuctionSide('route-start')} />
        <span><strong>Suction from {routeStartName}</strong><small>
          Discharge toward {routeEndName}</small></span></label>
      <label><input type="radio" name="new-pump-direction"
        checked={nodeTool.candidate?.pumpSuctionSide === 'route-end'}
        onChange={() => onSetPumpSuctionSide('route-end')} />
        <span><strong>Suction from {routeEndName}</strong><small>
          Discharge toward {routeStartName}</small></span></label>
    </fieldset>}
    <div className="site-actions"><button className="site-btn" onClick={onCancelNode}>Done</button>
      <button className="site-btn site-btn-primary" disabled={!nodeTool.candidate ||
        (nodeTool.kind === 'pump' && !nodeTool.candidate.pumpSuctionSide)}
        onClick={onConfirmNode}>Place {nodeTool.kind}</button></div>
  </div>;
  }
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
  if (selectedGun) return <SnowgunInspector gun={selectedGun} nodes={nodes} units={units}
    close={onCloseGun} move={() => onMoveGun(selectedGun.id)}
    remove={() => onDeleteGun(selectedGun.id)} />;
  if (selectedPipe) return <div className="site-control site-control-wide snowmaking-panel">
    <PanelHead title={selectedPipe.name} onClose={onClosePipe} />
    <input className="name-entry-input lift-name-input" aria-label="Pipe name" value={selectedPipe.name}
      onChange={(event) => onPatchPipe(selectedPipe.id, { name: event.target.value })} />
    <label className="lake-depth-row"><span className="lift-stat-label">Diameter</span>
      <select className="lift-select" aria-label="Pipe diameter" value={selectedPipe.diameterIn}
        onChange={(event) => onPatchPipe(selectedPipe.id,
          { diameterIn: Number(event.target.value) as SnowmakingPipeDiameterIn })}>
        {SNOWMAKING_PIPE_DIAMETERS_IN.map((diameter) => <option key={diameter} value={diameter}>{diameter}&quot;</option>)}
      </select></label>
    <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Length</span>
        <span className="lift-stat-value">{fmtDistance(selectedPipe.lengthM, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Vertical</span>
        <span className="lift-stat-value">{selectedPipe.verticalM != null
          ? fmtDistance(selectedPipe.verticalM, units) : '—'}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Connections</span>
        <span className="lift-stat-value">{new Set(selectedPipe.vertices.flatMap((vertex) =>
          vertex.nodeId ? [vertex.nodeId] : [])).size}</span></div>
    </div>
    <button className="lift-delete-btn" onClick={() => onDeletePipe(selectedPipe.id)}>Remove pipe</button>
  </div>;
  if (selectedNode) {
    const sourceName = snowmakingSourceName(selectedNode, dams, ponds, lakes);
    const connections = pipes.filter((pipe) => pipe.vertices.some((vertex) =>
      vertex.nodeId === selectedNode.id)).length;
    const connectedGun = selectedNode.kind === 'hydrant'
      ? guns.find((gun) => gun.hydrantId === selectedNode.id) ?? null : null;
    return <div className="site-control site-control-wide snowmaking-panel">
      <PanelHead title={selectedNode.kind === 'intake' ? selectedNode.name
        : `${snowmakingNodeLabel(selectedNode)} · ${selectedNode.name}`} onClose={onCloseNode} />
      {selectedNode.kind !== 'junction' && <input className="name-entry-input lift-name-input"
        aria-label="Node name" value={selectedNode.name}
        onChange={(event) => onRenameNode(selectedNode.id, event.target.value)} />}
      <div className="lift-stats">
        {selectedNode.kind !== 'intake' && <div className="readout-line">
          <span className="lift-stat-label">Label</span>
          <span className="lift-stat-value">{snowmakingNodeLabel(selectedNode)}</span></div>}
        <div className="readout-line"><span className="lift-stat-label">Kind</span>
          <span className="lift-stat-value">{SNOWMAKING_NODE_LABELS[selectedNode.kind]}</span></div>
        {sourceName != null && <div className="readout-line"><span className="lift-stat-label">Source</span>
          <span className="lift-stat-value">{sourceName}</span></div>}
        <div className="readout-line"><span className="lift-stat-label">Elevation</span>
          <span className="lift-stat-value">{selectedNode.elevM != null ? fmtDistance(selectedNode.elevM, units) : '—'}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Connected pipes</span>
          <span className="lift-stat-value">{connections}</span></div>
      </div>
      {selectedNode.kind === 'pump' && <SnowmakingPumpPortEditor pump={selectedNode}
        nodes={nodes} pipes={pipes} onSetPumpPort={onSetPumpPort} />}
      {selectedNode.kind === 'intake' && <div className="site-hint">Source-owned intake. It is removed only with its water source; attached pipes become open ends.</div>}
      {selectedNode.kind === 'junction' && <div className="site-hint">Junctions are managed automatically where pipe routes join.</div>}
      {(selectedNode.kind === 'pump' || selectedNode.kind === 'hydrant') && <>
        <div className="site-hint">Removing this device detaches connected pipes without changing their geometry.</div>
        {pendingHydrantDeleteId === selectedNode.id && connectedGun ? <div className="lift-warning">
          This hydrant serves a snowgun. Removing it will end that hookup; the gun remains installed
          and reconnects only if another free hydrant is within 50 ft.
          <div className="site-actions"><button className="site-btn"
            onClick={() => setPendingHydrantDeleteId(null)}>Cancel</button>
            <button className="lift-delete-btn" onClick={() => {
              onDeleteNode(selectedNode.id); setPendingHydrantDeleteId(null);
            }}>Remove hydrant</button></div></div>
          : <button className="lift-delete-btn" onClick={() => connectedGun
            ? setPendingHydrantDeleteId(selectedNode.id) : onDeleteNode(selectedNode.id)}>
            Remove {selectedNode.kind}</button>}
      </>}
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
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onAnalyzeSystem}>
      Analyze snowmaking system</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmDam}>＋ Build dam</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmPond}>＋ Build standalone pond</button>
    <div className="site-actions"><button className="site-btn" onClick={() => onArmNode('hydrant')}>Place one hydrant</button>
      <button className="site-btn" disabled={pipes.length === 0} onClick={onArmHydrantRun}>Place hydrants along pipe</button></div>
    <div className="site-actions">
      <button className="site-btn" onClick={() => onArmNode('pump')}>Place pumps</button></div>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmPipe}
      title="Draw a snowmaking pipe route">＋ Install snowmaking pipe</button>
    <button className="lift-add-btn site-btn site-btn-primary" onClick={onArmGuns}>
      ＋ Install snowguns</button>
    <div className="site-hint">Pipe routes and network devices are saved with the resort.</div>
    {dams.length === 0 && ponds.length === 0 && nodes.length === 0 && pipes.length === 0 && guns.length === 0
      ? <div className="lift-overview-empty">No snowmaking infrastructure yet.</div> : <>
      {dams.length > 0 && <div className="lift-list">{dams.map((dam) => <button key={dam.id} className="lift-row lift-row-button" onClick={() => onSelectDam(dam.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{dam.name}</span>
          <span className="lift-row-summary">Snowmaking pond · {fmtArea(dam.areaM2, units)}</span></span></button>)}</div>}
      {ponds.length > 0 && <div className="lift-list">{ponds.map((pond) => <button key={pond.id} className="lift-row lift-row-button" onClick={() => onSelectPond(pond.id)}>
        <span className="infrastructure-dam-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{pond.name}</span>
          <span className="lift-row-summary">{pond.isSnowmaking !== false ? 'Snowmaking pond' : 'Standalone pond'} · {fmtArea(pond.areaM2, units)}</span></span></button>)}</div>}
      {pipes.length > 0 && <><div className="network-section-title">Pipes</div><div className="lift-list">
        {pipes.map((pipe) => <button key={pipe.id} className="lift-row lift-row-button"
          onClick={() => onSelectPipe(pipe.id)}><span className="snowmaking-pipe-swatch" aria-hidden="true" />
          <span className="lift-row-main"><span className="lift-row-name">{pipe.name}</span>
            <span className="lift-row-summary">{pipe.diameterIn}&quot; · {fmtDistance(pipe.lengthM, units)}</span>
          </span></button>)}</div></>}
      <SnowgunDirectory guns={guns} nodes={nodes} select={onSelectGun} />
      {nodes.length > 0 && <div className="lift-list">{nodes.map((node) => {
        const sourceName = snowmakingSourceName(node, dams, ponds, lakes);
        const summary = sourceName != null ? `${SNOWMAKING_NODE_LABELS[node.kind]} · ${sourceName}` : SNOWMAKING_NODE_LABELS[node.kind];
        return <button key={node.id} className="lift-row lift-row-button" onClick={() => onSelectNode(node.id)}>
          <span className="snowmaking-node-swatch" aria-hidden="true" /><span className="lift-row-main"><span className="lift-row-name">{node.kind === 'intake' ? node.name : `${snowmakingNodeLabel(node)} · ${node.name}`}</span>
            <span className="lift-row-summary">{summary}</span></span></button>;
      })}</div>}
    </>}
  </div>;
}
