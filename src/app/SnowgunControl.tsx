import { fmtDistance } from '../lifts';
import { HKD_IMPULSE_R5, SNOWGUN_VARIANTS, snowgunHydrantDistanceM,
  snowgunVariant } from '../snowmakingGuns';
import type { SavedSnowgun, SavedSnowmakingNode, SnowgunVariantId } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import type { SnowgunTool } from './snowmakingGunControllerModel';
import type { SnowgunPlanPreview } from './useSnowgunController';

const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD',
  maximumFractionDigits: 0 });

function PlanRows({ preview, remove }: { preview: SnowgunPlanPreview;
  remove?: (draftId: string) => void }) {
  return <div className="snowgun-plan-list" aria-label="Planned snowguns">
    {preview.items.map((item) => { const variant = snowgunVariant(item.variantId); return <div
      className={`snowgun-plan-row${item.hydrantId ? '' : ' is-disconnected'}`} key={item.draftId}>
      <span><strong>{variant.shortLabel}</strong><small>{item.hydrantLabel ?? 'Disconnected'} ·
        {' '}{money.format(variant.priceUsd)}</small></span>
      {!item.hydrantId && <span className="snowgun-warning" aria-label="Disconnected">!</span>}
      {remove && <button className="site-btn" onClick={() => remove(item.draftId)}
        aria-label={`Remove ${variant.label} from plan`}>Remove</button>}
    </div>; })}
  </div>;
}

export function SnowgunToolPanel({ tool, preview, units, setVariant, removeDraft,
  review, back, confirm, cancel, confirmMove }: {
  tool: Exclude<SnowgunTool, { phase: 'idle' }>;
  preview: SnowgunPlanPreview;
  units: Units;
  setVariant(id: SnowgunVariantId): void;
  removeDraft(id: string): void;
  review(): void; back(): void; confirm(): void; cancel(): void; confirmMove(): void;
}) {
  if (tool.phase === 'moving') return <div className="site-control site-control-wide snowmaking-panel">
    <div className="dock-head"><span className="dock-head-title">Move sled snowgun</span>
      <button className="dock-close" onClick={cancel} aria-label="Cancel moving snowgun">×</button></div>
    <div className="site-hint">Click the new location. The sled will use the nearest free hydrant within 50 ft.</div>
    {preview.candidate && <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Connection</span>
        <span className="lift-stat-value">{preview.candidate.hydrantLabel ?? 'Disconnected'}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Hose</span>
        <span className="lift-stat-value">{preview.candidate.hoseDistanceM != null
          ? fmtDistance(preview.candidate.hoseDistanceM, units) : '—'}</span></div></div>}
    {tool.error && <div className="lift-warning">{tool.error}</div>}
    <div className="site-actions"><button className="site-btn" onClick={cancel}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={!tool.candidate}
        onClick={confirmMove}>Move sled</button></div>
  </div>;

  if (tool.phase === 'review') return <div className="site-control site-control-wide snowmaking-panel">
    <div className="dock-head"><span className="dock-head-title">Review snowgun plan</span>
      <button className="dock-close" onClick={cancel} aria-label="Cancel snowgun plan">×</button></div>
    <PlanRows preview={preview} remove={removeDraft} />
    <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Connected</span>
        <span className="lift-stat-value">{preview.connectedCount}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Disconnected</span>
        <span className="lift-stat-value">{preview.disconnectedCount}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Quoted total</span>
        <span className="lift-stat-value">{money.format(preview.totalUsd)}</span></div>
    </div>
    {preview.disconnectedCount > 0 && <div className="lift-warning">Disconnected guns will be built,
      but cannot receive water until a free hydrant is available within 50 ft.</div>}
    {tool.error && <div className="lift-warning">{tool.error}</div>}
    <div className="site-actions"><button className="site-btn" onClick={back}>Back</button>
      <button className="site-btn site-btn-primary" disabled={preview.items.length === 0}
        onClick={confirm}>Build {preview.items.length} snowguns · {money.format(preview.totalUsd)}</button></div>
  </div>;

  const variant = snowgunVariant(tool.variantId);
  return <div className="site-control site-control-wide snowmaking-panel">
    <div className="dock-head"><span className="dock-head-title">Plan snowguns</span>
      <button className="dock-close" onClick={cancel} aria-label="Cancel snowgun plan">×</button></div>
    <div className="site-hint">Choose a type, then click anywhere to add it to the plan. Hookups are assigned automatically.</div>
    <label className="lake-depth-row"><span className="lift-stat-label">Snowgun type</span>
      <select className="lift-select" value={tool.variantId} aria-label="Snowgun type"
        onChange={(event) => setVariant(event.target.value as SnowgunVariantId)}>
        {SNOWGUN_VARIANTS.map((entry) => <option key={entry.id} value={entry.id}>
          {entry.label} · {money.format(entry.priceUsd)}</option>)}</select></label>
    <div className="site-hint">{variant.mount === 'sled' ? 'Movable sled' : 'Permanent tower'} ·
      {' '}{variant.throwFt} ft throw · {HKD_IMPULSE_R5.minimumWaterPressurePsi} PSI water ·
      {' '}{HKD_IMPULSE_R5.minimumAirPressurePsi} PSI air</div>
    {preview.items.length > 0 ? <PlanRows preview={preview} remove={removeDraft} />
      : <div className="lift-overview-empty">No snowguns planned yet.</div>}
    <div className="readout-line"><span className="lift-stat-label">Plan total</span>
      <span className="lift-stat-value">{money.format(preview.totalUsd)}</span></div>
    <div className="site-actions"><button className="site-btn" onClick={cancel}>Cancel</button>
      <button className="site-btn site-btn-primary" disabled={preview.items.length === 0}
        onClick={review}>Review {preview.items.length} snowguns</button></div>
  </div>;
}

export function SnowgunInspector({ gun, nodes, units, close, move, remove }: {
  gun: SavedSnowgun; nodes: SavedSnowmakingNode[]; units: Units;
  close(): void; move(): void; remove(): void;
}) {
  const variant = snowgunVariant(gun.variantId);
  const hydrant = gun.hydrantId ? nodes.find((node) => node.id === gun.hydrantId) ?? null : null;
  const distanceM = hydrant ? snowgunHydrantDistanceM(gun, hydrant) : null;
  return <div className="site-control site-control-wide snowmaking-panel">
    <div className="dock-head"><span className="dock-head-title">{variant.label}</span>
      <button className="dock-close" onClick={close} aria-label="Close snowgun">×</button></div>
    {!hydrant && <div className="lift-warning"><span className="snowgun-warning" aria-hidden="true">!</span>
      Disconnected — no free hydrant is available within 50 ft.</div>}
    <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Mount</span>
        <span className="lift-stat-value">{variant.mount === 'sled' ? 'Sled' : `${variant.towerLengthFt} ft tower`}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Throw</span>
        <span className="lift-stat-value">{variant.throwFt} ft</span></div>
      <div className="readout-line"><span className="lift-stat-label">Catalog price</span>
        <span className="lift-stat-value">{money.format(variant.priceUsd)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Hydrant</span>
        <span className="lift-stat-value">{hydrant?.labelNumber ? `Hydrant ${hydrant.labelNumber}` : 'Disconnected'}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Hose</span>
        <span className="lift-stat-value">{distanceM != null ? fmtDistance(distanceM, units) : '—'}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Elevation</span>
        <span className="lift-stat-value">{gun.elevM != null ? fmtDistance(gun.elevM, units) : '—'}</span></div>
    </div>
    {variant.mount === 'sled' ? <button className="site-btn" onClick={move}>Move sled</button>
      : <div className="site-hint">Tower guns are permanent. Remove and rebuild to change location.</div>}
    <button className="lift-delete-btn" onClick={remove}>Remove snowgun</button>
  </div>;
}

export function SnowgunDirectory({ guns, nodes, select }: { guns: SavedSnowgun[];
  nodes: SavedSnowmakingNode[]; select(id: string): void }) {
  if (guns.length === 0) return null;
  return <><div className="network-section-title">Snowguns</div><div className="lift-list">
    {guns.map((gun) => { const variant = snowgunVariant(gun.variantId);
      const hydrant = gun.hydrantId ? nodes.find((node) => node.id === gun.hydrantId) : null;
      return <button key={gun.id} className="lift-row lift-row-button" onClick={() => select(gun.id)}>
        <span className="snowgun-swatch" aria-hidden="true" />
        <span className="lift-row-main"><span className="lift-row-name">{variant.shortLabel}</span>
          <span className="lift-row-summary">{hydrant?.labelNumber
            ? `Hydrant ${hydrant.labelNumber}` : 'Disconnected'}</span></span>
        {!hydrant && <span className="snowgun-warning" aria-label="Disconnected snowgun">!</span>}
      </button>; })}
  </div></>;
}

