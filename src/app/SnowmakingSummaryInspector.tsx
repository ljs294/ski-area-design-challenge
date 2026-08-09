import { fmtDistance } from '../lifts';
import { SNOWMAKING_NODE_LABELS } from '../snowmakingNodes';
import { snowmakingNodeLabel } from '../snowmakingNetwork';
import { snowgunCatalogValue, snowgunLabel, snowgunVariant } from '../snowmakingGuns';
import type { SavedDam, SavedPond } from '../types';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingLakeSource } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import { snowmakingSourceInfo } from './snowmakingDashboardModel';

export function Stat({ label, value }: { label: string; value: string }) {
  return <div className="network-stat"><span className="network-stat-label">{label}</span>
    <span className="network-stat-value">{value}</span></div>;
}

export function SnowmakingSummaryInspector({ dams, ponds, lakes, nodes, pipes, guns, units,
  onSelectNode, onSelectPipe, onSelectGun }: {
  dams: SavedDam[]; ponds: SavedPond[]; lakes: SnowmakingLakeSource[];
  nodes: SavedSnowmakingNode[]; pipes: SavedSnowmakingPipe[]; guns: SavedSnowgun[]; units: Units;
  onSelectNode(id: string | null): void; onSelectPipe(id: string | null): void;
  onSelectGun(id: string | null): void;
}) {
  return <aside className="network-inspector" data-inspector="summary">
    <div className="dock-head"><span className="dock-head-title">Snowmaking network</span></div>
    <div className="network-sub">Click a pipe, node, or snowgun to see its detail.</div>
    <div className="network-stats"><Stat label="Dams" value={`${dams.length}`} />
      <Stat label="Ponds" value={`${ponds.length + lakes.length}`} />
      <Stat label="Nodes" value={`${nodes.length}`} /><Stat label="Pipes" value={`${pipes.length}`} />
      <Stat label="Snowguns" value={`${guns.length}`} />
      <Stat label="Disconnected" value={`${guns.filter((gun) => gun.hydrantId == null).length}`} />
      <Stat label="Catalog value" value={`$${snowgunCatalogValue(guns).toLocaleString('en-US')}`} /></div>
    {nodes.length > 0 && <><div className="network-section-title">Nodes</div>
      <ul className="network-run-list">{nodes.map((node) => {
        const info = snowmakingSourceInfo(node, dams, ponds, lakes);
        return <li key={node.id}><button className="network-run" onClick={() => onSelectNode(node.id)}>
          <span className="network-run-name">{node.kind === 'intake' ? node.name
            : `${snowmakingNodeLabel(node)} · ${node.name}`}</span>
          <span className="network-run-meta">{SNOWMAKING_NODE_LABELS[node.kind]}
            {info ? ` · ${info.name}` : ''}</span></button></li>;
      })}</ul></>}
    {pipes.length > 0 && <><div className="network-section-title">Pipes</div>
      <ul className="network-run-list">{pipes.map((pipe) => <li key={pipe.id}>
        <button className="network-run" onClick={() => onSelectPipe(pipe.id)}>
          <span className="network-run-name">{pipe.name}</span>
          <span className="network-run-meta">{pipe.diameterIn}&quot; · {fmtDistance(pipe.lengthM, units)}</span>
        </button></li>)}</ul></>}
    {guns.length > 0 && <><div className="network-section-title">Snowguns</div>
      <ul className="network-run-list">{guns.map((gun) => {
        const variant = snowgunVariant(gun.variantId);
        return <li key={gun.id}><button className="network-run" onClick={() => onSelectGun(gun.id)}>
          <span className="network-run-name">{snowgunLabel(gun, nodes)} · {variant.shortLabel}</span>
          <span className="network-run-meta">{gun.hydrantId ? snowgunLabel(gun, nodes)
            : <><span className="snowgun-warning" aria-hidden="true">!</span> Disconnected</>}</span>
        </button></li>;
      })}</ul></>}
  </aside>;
}
