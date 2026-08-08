import { fmtDistance } from '../lifts';
import { snowmakingNodeLabel } from '../snowmakingNetwork';
import { snowgunHydrantDistanceM, snowgunVariant } from '../snowmakingGuns';
import type { SavedSnowgun, SavedSnowmakingNode } from '../types/snowmaking';
import type { XY } from '../network';
import type { Units } from './SettingsContext';

export function SnowgunDashboardConnections({ guns, nodes, place }: {
  guns: SavedSnowgun[]; nodes: SavedSnowmakingNode[]; place(point: [number, number]): XY;
}) {
  return <g className="snowmaking-dashboard-gun-connections"
    aria-label="Snowgun hydrant connections">{guns.map((gun) => {
      const hydrant = gun.hydrantId == null ? null
        : nodes.find((node) => node.id === gun.hydrantId && node.kind === 'hydrant');
      if (!hydrant) return null;
      const from = place(hydrant.point), to = place(gun.point);
      return <line key={gun.id} x1={from.x} y1={from.y} x2={to.x} y2={to.y}
        className="snowmaking-dashboard-gun-connection" data-gun-id={gun.id}
        data-hydrant-id={hydrant.id} vectorEffect="non-scaling-stroke" />;
    })}</g>;
}

export function SnowgunDashboardMarkers({ guns, selectedId, analysisSelectedIds, analysisStatuses,
  width, showTypes, place, select }: {
  guns: SavedSnowgun[]; selectedId: string | null; width: number; showTypes: boolean;
  analysisSelectedIds?: readonly string[];
  analysisStatuses?: Readonly<Record<string, 'ready' | 'failed' | undefined>>;
  place(point: [number, number]): XY; select(id: string): void;
}) {
  const analysisSelected = analysisSelectedIds ? new Set(analysisSelectedIds) : null;
  return <g className="snowmaking-dashboard-guns">{guns.map((gun) => {
    const p = place(gun.point), variant = snowgunVariant(gun.variantId);
    const selected = gun.id === selectedId || !!analysisSelected?.has(gun.id);
    const connected = gun.hydrantId != null, status = analysisStatuses?.[gun.id];
    return <g key={gun.id}
      className={`snowmaking-dashboard-gun${selected ? ' is-selected' : ''}${connected ? '' : ' is-disconnected'}${status ? ` is-analysis-${status}` : ''}`}
      data-gun-id={gun.id} role="button" tabIndex={0}
      {...(analysisSelected ? { 'aria-pressed': analysisSelected.has(gun.id) } : {})}
      aria-label={`${variant.label}, ${connected ? `connected to hydrant ${gun.hydrantId}` : 'disconnected'}`}
      onClick={(event) => { event.stopPropagation(); select(gun.id); }}
      onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault(); event.stopPropagation(); select(gun.id); } }}>
      {selected && <circle cx={p.x} cy={p.y} r={width / 100}
        className="snowmaking-dashboard-gun-halo" vectorEffect="non-scaling-stroke" />}
      <circle cx={p.x} cy={p.y} r={width / 180}
        className="snowmaking-dashboard-gun-dot" vectorEffect="non-scaling-stroke" />
      {showTypes && <text x={p.x} y={p.y - width / 60} textAnchor="middle"
        className="snowmaking-dashboard-gun-label" style={{ fontSize: width / 75 }}>{variant.shortLabel}</text>}
      {!connected && <g aria-label="Warning: disconnected snowgun">
        <circle cx={p.x + width / 75} cy={p.y - width / 75} r={width / 128.6}
          className="snowmaking-dashboard-gun-warning" />
        <text x={p.x + width / 75} y={p.y - width / 75}
          className="snowmaking-dashboard-gun-warning-text" dominantBaseline="central"
          textAnchor="middle" style={{ fontSize: width / 85 }}>!</text>
      </g>}
    </g>;
  })}</g>;
}

function GunStat({ label, value }: { label: string; value: string }) {
  return <div className="network-stat"><span className="network-stat-label">{label}</span>
    <span className="network-stat-value">{value}</span></div>;
}

export function SnowgunDashboardInspector({ gun, nodes, units, move, remove }: {
  gun: SavedSnowgun; nodes: SavedSnowmakingNode[]; units: Units;
  move(): void; remove(): void;
}) {
  const variant = snowgunVariant(gun.variantId);
  const hydrant = gun.hydrantId == null ? null
    : nodes.find((node) => node.id === gun.hydrantId && node.kind === 'hydrant') ?? null;
  const hoseM = hydrant ? snowgunHydrantDistanceM(gun, hydrant) : null;
  return <aside className="network-inspector" data-inspector="gun">
    <div className="dock-head"><span className="dock-head-title">{variant.shortLabel}</span></div>
    <div className="network-sub">{variant.label}</div>
    {!hydrant && <div className="snowgun-disconnected-message" role="status">
      <span className="snowgun-warning" aria-hidden="true">!</span>Disconnected — no free hydrant is within 50 ft.
    </div>}
    <div className="network-stats"><GunStat label="Model" value="HKD Impulse R5" />
      <GunStat label="Mount" value={variant.mount === 'sled' ? 'Sled' : 'Tower'} />
      <GunStat label="Tower length" value={`${variant.towerLengthFt} ft`} />
      <GunStat label="Throw" value={`${variant.throwFt} ft`} />
      <GunStat label="Catalog price" value={`$${variant.priceUsd.toLocaleString('en-US')}`} />
      <GunStat label="Elevation" value={gun.elevM == null ? '—' : fmtDistance(gun.elevM, units)} />
      <GunStat label="Hydrant" value={hydrant ? snowmakingNodeLabel(hydrant) : 'Disconnected'} />
      <GunStat label="Hose distance" value={hoseM == null ? '—' : fmtDistance(hoseM, units)} />
    </div>
    {variant.mount === 'sled' ? <button className="site-btn" onClick={move}>Move sled gun</button>
      : <div className="network-sub">Tower guns are fixed. Remove and rebuild to change their location.</div>}
    <button className="lift-delete-btn" onClick={remove}>Remove snowgun</button>
  </aside>;
}
