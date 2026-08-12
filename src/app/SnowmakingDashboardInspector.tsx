import { formatLakeVolume } from '../lakeAnalysis';
import { fmtDistance } from '../lifts';
import { SNOWMAKING_NODE_LABELS } from '../snowmakingNodes';
import { snowmakingNodeLabel, snowmakingPipeSegments, snowmakingPipeStats } from '../snowmakingNetwork';
import { SNOWMAKING_PIPE_DIAMETERS_IN } from '../types/snowmaking';
import type { SavedDam, SavedPond } from '../types';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingLakeSource, SnowmakingPipeDiameterIn, SnowmakingPumpPort } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import { SnowgunDashboardInspector } from './SnowgunDashboard';
import { SnowmakingPumpPortEditor } from './SnowmakingPumpPortEditor';
import { SnowmakingSummaryInspector, Stat } from './SnowmakingSummaryInspector';
import { snowmakingSourceInfo } from './snowmakingDashboardModel';

export interface SnowmakingDashboardInspectorProps {
  selectedNode: SavedSnowmakingNode | null;
  selectedPipe: SavedSnowmakingPipe | null;
  selectedPipeSegmentId?: string | null;
  selectedGun: SavedSnowgun | null;
  dams: SavedDam[];
  ponds: SavedPond[];
  lakes?: SnowmakingLakeSource[];
  nodes: SavedSnowmakingNode[];
  pipes: SavedSnowmakingPipe[];
  guns: SavedSnowgun[];
  units: Units;
  onSelectNode: (id: string | null) => void;
  onSelectPipe: (id: string | null) => void;
  onSelectGun: (id: string | null) => void;
  onRenameNode: (id: string, name: string) => void;
  onDeleteNode: (id: string) => void;
  onPatchPipe: (id: string, patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>) => void;
  onSetPumpPort: (pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null) => void;
  onDeletePipe: (id: string) => void;
  onMoveGun: (id: string) => void;
  onDeleteGun: (id: string) => void;
  pendingHydrantDeleteId: string | null;
  onSetPendingHydrantDeleteId: (id: string | null) => void;
}

export function SnowmakingDashboardInspector({
  selectedNode, selectedPipe, selectedPipeSegmentId, selectedGun, dams, ponds, lakes = [], nodes, pipes, guns,
  units, onSelectNode, onSelectPipe, onSelectGun, onRenameNode, onDeleteNode,
  onPatchPipe, onSetPumpPort, onDeletePipe, onMoveGun, onDeleteGun,
  pendingHydrantDeleteId, onSetPendingHydrantDeleteId,
}: SnowmakingDashboardInspectorProps) {
  if (selectedPipe) {
    const selectedSegment = snowmakingPipeSegments(selectedPipe)
      .find((segment) => segment.id === selectedPipeSegmentId) ?? null;
    const stats = selectedSegment ? snowmakingPipeStats(selectedSegment.vertices) : null;
    return <aside className="network-inspector" data-inspector="pipe">
    <div className="dock-head"><span className="dock-head-title">{selectedPipe.name}
      {selectedSegment ? ` · ${selectedSegment.segmentIndex + 1}` : ''}</span></div>
    <input className="name-entry-input" aria-label="Pipe name" value={selectedPipe.name}
      onChange={(event) => onPatchPipe(selectedPipe.id, { name: event.target.value })} />
    <label className="lake-depth-row"><span>Diameter</span><select className="lift-select"
      aria-label="Pipe diameter" value={selectedPipe.diameterIn}
      onChange={(event) => onPatchPipe(selectedPipe.id,
        { diameterIn: Number(event.target.value) as SnowmakingPipeDiameterIn })}>
      {SNOWMAKING_PIPE_DIAMETERS_IN.map((diameter) => <option key={diameter} value={diameter}>
        {diameter}&quot;</option>)}
    </select></label>
    {stats ? <div className="network-stats"><Stat label="Length" value={fmtDistance(stats.lengthM, units)} />
      <Stat label="Vertical" value={stats.verticalM != null
        ? fmtDistance(stats.verticalM, units) : '—'} /></div>
      : <div className="network-sub">Select a pipe segment on the map to see its length and vertical.</div>}
    <button className="lift-delete-btn" onClick={() => onDeletePipe(selectedPipe.id)}>Remove pipe</button>
  </aside>;
  }
  if (selectedGun) return <SnowgunDashboardInspector gun={selectedGun} nodes={nodes} units={units}
    move={() => onMoveGun(selectedGun.id)} remove={() => onDeleteGun(selectedGun.id)} />;
  if (selectedNode) {
    const sourceInfo = snowmakingSourceInfo(selectedNode, dams, ponds, lakes);
    return <aside className="network-inspector" data-inspector="node">
      <div className="dock-head"><span className="dock-head-title">
        {selectedNode.kind === 'intake' ? selectedNode.name
          : `${snowmakingNodeLabel(selectedNode)} · ${selectedNode.name}`}
      </span></div>
      <div className="network-sub">{SNOWMAKING_NODE_LABELS[selectedNode.kind]}</div>
      <div className="network-stats">
        {sourceInfo && <Stat label="Source" value={sourceInfo.name} />}
        {sourceInfo && <Stat label="Capacity" value={formatLakeVolume(sourceInfo.capacityM3, units)} />}
        <Stat label="Elevation" value={selectedNode.elevM != null
          ? fmtDistance(selectedNode.elevM, units) : '—'} />
      </div>
      {selectedNode.kind !== 'junction' && <input className="name-entry-input"
        aria-label="Node name" value={selectedNode.name}
        onChange={(event) => onRenameNode(selectedNode.id, event.target.value)} />}
      {selectedNode.kind === 'pump' && <SnowmakingPumpPortEditor pump={selectedNode}
        nodes={nodes} pipes={pipes} onSetPumpPort={onSetPumpPort} />}
      {(selectedNode.kind === 'pump' || selectedNode.kind === 'hydrant') && (() => {
        const connectedGun = selectedNode.kind === 'hydrant'
          ? guns.find((gun) => gun.hydrantId === selectedNode.id) ?? null : null;
        if (connectedGun && pendingHydrantDeleteId === selectedNode.id) return <div className="snowgun-delete-warning">
          <p>Removing this hydrant disconnects its snowgun. The gun remains installed and may reconnect automatically.</p>
          <div className="dock-actions"><button className="site-btn"
            onClick={() => onSetPendingHydrantDeleteId(null)}>Cancel</button>
            <button className="lift-delete-btn" onClick={() => {
              onSetPendingHydrantDeleteId(null); onDeleteNode(selectedNode.id);
            }}>Remove hydrant</button></div>
        </div>;
        return <button className="lift-delete-btn" onClick={() => connectedGun
          ? onSetPendingHydrantDeleteId(selectedNode.id) : onDeleteNode(selectedNode.id)}>
          Remove {selectedNode.kind}</button>;
      })()}
    </aside>;
  }
  return <SnowmakingSummaryInspector dams={dams} ponds={ponds} lakes={lakes} nodes={nodes}
    pipes={pipes} guns={guns} units={units} onSelectNode={onSelectNode}
    onSelectPipe={onSelectPipe} onSelectGun={onSelectGun} />;
}
