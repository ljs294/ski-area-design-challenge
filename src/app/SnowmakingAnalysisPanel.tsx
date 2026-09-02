import { memo, useEffect, useRef } from 'react';
import { snowgunLabel, snowgunVariant } from '../snowmakingGuns';
import { snowmakingNodeLabel, snowmakingPipeSegments,
  snowmakingPipeStats } from '../snowmakingNetwork';
import { isOwnedSnowmakingPump } from '../snowmakingOwnedPumps';
import type { SnowmakingAnalysisGroup, SnowmakingAnalysisResult,
  SnowmakingSourceResource } from '../snowmakingHydraulics';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingPumpPort } from '../types/snowmaking';
import { DEFAULT_PUMP_ANALYSIS_DRAFT, type SnowmakingAnalysisState } from './snowmakingAnalysisModel';
import { snowmakingPumpDirectionSummary, SnowmakingPumpPortEditor } from './SnowmakingPumpPortEditor';
import type { SnowmakingLassoSelection } from './snowmakingLasso';
import type { SnowgunSelectionPhase } from './dashboardMode';

const number = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });
const whole = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

function runtime(hours: number | null): string {
  if (hours == null) return 'Unavailable';
  if (hours < 24) return `${number.format(hours)} hr`;
  return `${number.format(hours / 24)} days`;
}

const GUN_STATUS = {
  ready: 'Ready',
  'too-warm': 'Too warm',
  disconnected: 'Disconnected',
  'not-analyzed': 'Not analyzed',
  'insufficient-pressure': 'Insufficient pressure at demanded flow',
} as const;

function MixedCheckbox({ checked, mixed, label, onChange }: {
  checked: boolean; mixed: boolean; label: string; onChange(): void;
}) {
  const ref = useRef<HTMLInputElement | null>(null);
  useEffect(() => { if (ref.current) ref.current.indeterminate = mixed; }, [mixed]);
  return <label><input ref={ref} type="checkbox" checked={checked} onChange={onChange}
    aria-checked={mixed ? 'mixed' : checked} /><span><strong>{label}</strong></span></label>;
}

function focusPumpPorts(pumpId: string): void {
  const editor = document.getElementById(`pump-ports-${pumpId}`);
  editor?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  editor?.querySelector<HTMLElement>('input, select, button')?.focus();
}

const SnowgunChecklistRow = memo(function SnowgunChecklistRow({ gun, nodes, checked,
  toggleGun, setHoveredGun }: {
  gun: SavedSnowgun;
  nodes: readonly SavedSnowmakingNode[];
  checked: boolean;
  toggleGun(id: string): void;
  setHoveredGun(id: string | null): void;
}) {
  const variant = snowgunVariant(gun.variantId);
  const label = snowgunLabel(gun, nodes);
  const node = nodes.find((candidate) => candidate.id === gun.hydrantId);
  return <label onMouseEnter={() => setHoveredGun(gun.id)} onMouseLeave={() => setHoveredGun(null)}
    onFocus={() => setHoveredGun(gun.id)} onBlur={() => setHoveredGun(null)}>
    <input type="checkbox" checked={checked} onChange={() => toggleGun(gun.id)} />
    <span><strong>{label} · {variant.shortLabel}</strong><small>
      Hydrant {node?.labelNumber ?? '—'}</small></span>
  </label>;
});

export function SnowmakingAnalysisPanel({ state, nodes, pipes, guns, groups, relevantGroups,
  sourceResourcesByIntakeId, result, toggleGun, setGuns, toggleIntake, setWetBulb,
  setPumpOn, setPumpHp, setPumpEfficiency, onSetPumpPort, setHoveredGun,
  hoveredSegmentId, analyze, lasso = null, gunSelectionPhase = 'idle',
  toggleGunSelection, cancelGunSelection }: {
  state: SnowmakingAnalysisState;
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  groups: readonly SnowmakingAnalysisGroup[];
  relevantGroups: readonly SnowmakingAnalysisGroup[];
  sourceResourcesByIntakeId: Readonly<Record<string, SnowmakingSourceResource | undefined>>;
  result: SnowmakingAnalysisResult | null;
  toggleGun(id: string): void;
  setGuns(ids: string[]): void;
  toggleIntake(id: string): void;
  setWetBulb(value: string): void;
  setPumpOn(id: string, on: boolean): void;
  setPumpHp(id: string, value: string): void;
  setPumpEfficiency(id: string, value: string): void;
  onSetPumpPort(pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null): void;
  setHoveredGun(id: string | null): void;
  hoveredSegmentId: string | null;
  analyze(): void;
  lasso?: SnowmakingLassoSelection | null;
  gunSelectionPhase?: SnowgunSelectionPhase;
  toggleGunSelection(): void;
  cancelGunSelection(): void;
}) {
  const selectedGuns = new Set(state.selectedGunIds);
  const selectedIntakes = new Set(state.selectedIntakeNodeIds);
  const connectedGunIds = groups.flatMap((group) => group.gunIds);
  const connectedGunSet = new Set(connectedGunIds);
  const selectedConnectedGunCount = connectedGunIds.filter((id) => selectedGuns.has(id)).length;
  const relevantPumpIds = new Set(relevantGroups.flatMap((group) => group.pumpNodeIds));
  const pumps = nodes.filter((node) => node.kind === 'pump' && relevantPumpIds.has(node.id));
  const pipeById = new Map(pipes.map((pipe) => [pipe.id, pipe]));
  const gunById = new Map(guns.map((gun) => [gun.id, gun]));
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const pumpDirectionText = (id: string | undefined) => {
    const pump = id ? nodeById.get(id) : null;
    return pump?.kind === 'pump'
      ? snowmakingPumpDirectionSummary(pump, nodes, pipes)?.text ?? null : null;
  };
  const hoveredSegment = result?.systems.flatMap((system) => system.segments)
    .find((segment) => segment.id === hoveredSegmentId) ?? null;
  const hoveredPipe = hoveredSegment ? pipeById.get(hoveredSegment.pipeId) : null;
  const hoveredPhysicalStats = hoveredPipe && hoveredSegment
    ? snowmakingPipeSegments(hoveredPipe)
      .find((segment) => segment.id === hoveredSegment.id) : null;
  const hoveredStats = hoveredPhysicalStats
    ? snowmakingPipeStats(hoveredPhysicalStats.vertices) : null;

  return <aside className="network-inspector snowmaking-analysis-panel"
    data-inspector="analysis" aria-label="Snowmaking system analyzer">
    <div className="dock-head"><span className="dock-head-title">Operate snowguns</span></div>
    <div className="network-sub">Select the guns to run. Automatic radial routing uses the shortest
      required tree and excludes alternate loop links.</div>

    <section className={`snowmaking-segment-peek${hoveredSegment ? ' is-visible' : ''}`}
      aria-live="polite" aria-label="Hovered pipe segment details">
      {hoveredSegment ? <>
        <div className="snowmaking-segment-peek-head"><span>Pipe segment</span>
          <strong>{hoveredPipe?.name ?? hoveredSegment.pipeId} · {hoveredSegment.segmentIndex + 1}</strong></div>
        <div className="snowmaking-segment-peek-grid">
          <div><span>Pressure</span><strong>{number.format(hoveredSegment.upstreamPressurePsi)} → {number.format(hoveredSegment.downstreamPressurePsi)} PSI</strong></div>
          <div><span>Flow</span><strong>{number.format(Math.abs(hoveredSegment.flowGpm))} GPM</strong></div>
          <div><span>Friction head</span><strong>{number.format(hoveredSegment.frictionHeadFt)} ft</strong></div>
          <div><span>Diameter</span><strong>{hoveredPipe?.diameterIn ?? '—'} in</strong></div>
          <div><span>Length</span><strong>{whole.format(hoveredSegment.lengthFt)} ft</strong></div>
          <div><span>Vertical</span><strong>{hoveredStats?.verticalM == null
            ? '—' : `${whole.format(hoveredStats.verticalM * 3.28084)} ft`}</strong></div>
        </div>
      </> : <div className="snowmaking-segment-peek-empty">Hover or focus a colored pipe segment to inspect its hydraulic performance.</div>}
    </section>

    <label className="snowmaking-analysis-temperature">
      <span>Wet-bulb temperature</span>
      <span><input type="number" step="1" value={state.wetBulbF}
        aria-label="Wet-bulb temperature in Fahrenheit"
        onChange={(event) => setWetBulb(event.target.value)} /> °F</span>
    </label>

    <div className="snowmaking-analysis-actions">
      <div className="snowmaking-analysis-selection-actions">
        <button className="site-btn" type="button" aria-pressed={gunSelectionPhase !== 'idle'}
          onClick={toggleGunSelection}>Select Guns</button>
        <button className="site-btn" type="button" onClick={() => {
          cancelGunSelection(); setGuns(connectedGunIds);
        }}>Select All Connected</button>
        <button className="site-btn" type="button" onClick={() => {
          cancelGunSelection(); setGuns([]);
        }}>Clear Guns</button>
      </div>
      <button className="site-btn site-btn-primary snowmaking-analysis-run" type="button"
        disabled={state.calculating || selectedConnectedGunCount === 0}
        onClick={() => { cancelGunSelection(); analyze(); }}>Analyze</button>
    </div>

    <div className="network-section-title">Snowguns</div>
    <div className="snowmaking-gun-legend" aria-label="Snowgun status legend">
      {[
        ['unselected', 'Not selected'], ['selected', 'Selected'],
        ['selected-ready', 'Selected, analyzed, OK, not operating'],
        ['operating-ready', 'Operating, OK'],
        ['selected-failed', 'Selected, analyzed, not OK, not operating'],
        ['operating-failed', 'Operating, not OK'],
      ].map(([stateName, label]) => <div key={stateName} className="snowmaking-gun-legend-entry">
        <span className={`snowmaking-gun-swatch is-${stateName}`} aria-hidden="true" />
        <span>{label}</span>
      </div>)}
    </div>
    {groups.length ? <div className="snowmaking-analysis-groups">{groups.map((group, groupIndex) => {
      const selectedCount = group.gunIds.filter((id) => selectedGuns.has(id)).length;
      const groupGuns = group.gunIds.map((id) => gunById.get(id)).filter(Boolean) as SavedSnowgun[];
      return <fieldset key={group.componentId}><legend>System {groupIndex + 1}</legend>
        <div className="snowmaking-analysis-checklist">
          <MixedCheckbox label="Select all in this system" checked={selectedCount === group.gunIds.length}
            mixed={selectedCount > 0 && selectedCount < group.gunIds.length}
            onChange={() => setGuns(selectedCount === group.gunIds.length
              ? state.selectedGunIds.filter((id) => !group.gunIds.includes(id))
              : [...new Set([...state.selectedGunIds, ...group.gunIds])])} />
          {groupGuns.map((gun) => <SnowgunChecklistRow key={gun.id} gun={gun} nodes={nodes}
            checked={selectedGuns.has(gun.id)} toggleGun={toggleGun} setHoveredGun={setHoveredGun} />)}
        </div>
      </fieldset>;
    })}</div> : <div className="network-sub">No connected snowguns are installed.</div>}
    {guns.some((gun) => !connectedGunSet.has(gun.id)) && <div className="snowmaking-analysis-checklist">
      {guns.filter((gun) => !connectedGunSet.has(gun.id)).map((gun) => <label key={gun.id}
        className="is-disabled"><input type="checkbox" disabled />
        <span><strong>Disconnected · {snowgunVariant(gun.variantId).shortLabel}</strong>
          <small>Disconnected</small></span>
      </label>)}
    </div>}

    {relevantGroups.map((group) => <fieldset key={`sources:${group.componentId}`}
      className="snowmaking-analysis-source-group"><legend>Water sources</legend>
      {group.intakeNodeIds.length ? group.intakeNodeIds.map((id) => {
        const node = nodeById.get(id); const resource = sourceResourcesByIntakeId[id];
        return <label key={id}><input type="checkbox" checked={selectedIntakes.has(id)}
          onChange={() => toggleIntake(id)} /><span><strong>{resource?.name ?? node?.name ?? id}</strong>
          <small>{selectedIntakes.has(id) ? 'Supplying this analysis' : 'Not supplying'}</small></span></label>;
      }) : <div className="lift-warning">This system has no connected water source.</div>}
    </fieldset>)}

    {relevantGroups.length > 0 && <><div className="network-section-title">Pumps in selected systems</div>
      {pumps.length ? <div className="snowmaking-analysis-pumps">{pumps.map((pump) => {
        const ownedPump = isOwnedSnowmakingPump(pump);
        const setting = ownedPump ? { ...(state.pumpSettings[pump.id] ?? DEFAULT_PUMP_ANALYSIS_DRAFT),
          horsepowerHp: '1000', efficiencyPercent: '85' } :
          state.pumpSettings[pump.id] ?? DEFAULT_PUMP_ANALYSIS_DRAFT;
        return <fieldset key={pump.id}><legend>{snowmakingNodeLabel(pump)} · {pump.name}</legend>
          <SnowmakingPumpPortEditor pump={pump} nodes={nodes} pipes={pipes}
            onSetPumpPort={onSetPumpPort} compact />
          <label className="snowmaking-analysis-pump-toggle"><input type="checkbox" checked={setting.on}
            onChange={(event) => setPumpOn(pump.id, event.target.checked)} /> Pump On</label>
          <label><span>Horsepower</span><input type="number" min="0" step="1"
            disabled={ownedPump || !setting.on} value={setting.horsepowerHp} aria-label={`${pump.name} horsepower`}
            onChange={(event) => setPumpHp(pump.id, event.target.value)} /></label>
          <label><span>Efficiency</span><span><input type="number" min="1" max="100" step="1"
            disabled={ownedPump || !setting.on} value={setting.efficiencyPercent}
            aria-label={`${pump.name} efficiency percent`}
            onChange={(event) => setPumpEfficiency(pump.id, event.target.value)} /> %</span></label>
        </fieldset>;
      })}</div> : <div className="network-sub">No pumps are attached to the selected systems. Gravity may still supply pressure.</div>}
    </>}

    {lasso && <div className="snowmaking-lasso-popover" style={{ left: lasso.anchor.x, top: lasso.anchor.y }}
      role="dialog" aria-label="Lasso selection">
      <div className="snowmaking-lasso-counts">
        <span>{lasso.gunIds.length} enclosed</span>
        <span>{lasso.selectedGunCount} selected</span>
        <span>{lasso.unselectedGunCount} unselected</span>
      </div>
      <div className="snowmaking-lasso-actions">
        <button type="button" aria-label="Add enclosed guns" onClick={lasso.add}>+</button>
        <button type="button" aria-label="Remove enclosed guns" onClick={lasso.remove}>−</button>
        <button type="button" aria-label="Cancel lasso selection" onClick={lasso.cancel}>×</button>
      </div>
    </div>}

    <div aria-live="polite" className={`snowmaking-analysis-results${state.stale ? ' is-stale' : ''}`}>
      {state.calculating && <div className="network-sub">Calculating flows and pressures…</div>}
      {state.error && <div className="lift-warning">{state.error}</div>}
      {result && <>
        <div className={`snowmaking-analysis-verdict${result.summary.overallReady ? ' is-ready' : ' is-failed'}`}>
          <strong>{result.summary.overallReady ? 'All selected guns ready'
            : `${result.summary.readySystemCount} of ${result.summary.systemCount} systems ready`}</strong>
          <span>{result.summary.readyGunCount} ready · {result.summary.notAnalyzedGunCount} not analyzed</span>
        </div>
        {result.diagnostics.length > 0 && <div className="snowmaking-analysis-diagnostics"><ul>
          {result.diagnostics.map((entry, index) => <li key={`${entry.code}:${entry.entityId ?? index}`}>
            {entry.message}{entry.code === 'pump-direction-blocks-route' &&
              pumpDirectionText(entry.entityId) && <small className="snowmaking-direction-diagnostic">
                Current: {pumpDirectionText(entry.entityId)}</small>}
            {(entry.code === 'unconfigured-pump-ports' ||
              entry.code === 'pump-direction-blocks-route') && entry.entityId &&
              <button className="site-btn" onClick={() => focusPumpPorts(entry.entityId!)}>
                Set pump direction</button>}</li>)}
        </ul></div>}
        <div className="network-stats">
          <div className="network-stat"><span className="network-stat-label">Requested demand</span>
            <span className="network-stat-value">{whole.format(result.summary.requestedDemandGpm)} GPM</span></div>
          <div className="network-stat"><span className="network-stat-label">Water use</span>
            <span className="network-stat-value">{whole.format(result.summary.waterUseGalPerHour)} gal/hr</span></div>
          <div className="network-stat"><span className="network-stat-label">Minimum gun pressure</span>
            <span className="network-stat-value">{result.summary.minimumGunPressurePsi == null ? 'Unavailable'
              : `${number.format(result.summary.minimumGunPressurePsi)} PSI`}</span></div>
          <div className="network-stat"><span className="network-stat-label">First source depletion</span>
            <span className="network-stat-value">{runtime(result.summary.limitingSourceRuntimeHours)}</span></div>
        </div>
        {result.sources.length > 0 && <details open><summary>Water sources ({result.sources.length})</summary>
          <div className="snowmaking-analysis-table">{result.sources.map((source) => <div key={source.sourceKey}>
            <strong>{source.name} — {source.status}</strong>
            <span>{number.format(Math.abs(source.netWithdrawalGpm))} GPM</span>
            <small>{source.capacityGallons == null ? 'Storage unavailable'
              : `${whole.format(source.capacityGallons)} gal · ${runtime(source.runtimeHours)}`}</small>
          </div>)}</div>
        </details>}
        {result.systems.map((system, index) => <details open key={system.systemId}
          className={system.status === 'failed' ? 'is-failed' : ''}>
          <summary>System {index + 1} · {system.summary.readyGunCount}/{system.summary.selectedGunCount} guns ready</summary>
          {system.diagnostics.length > 0 && <ul>{system.diagnostics.map((entry, diagnosticIndex) =>
            <li key={`${entry.code}:${diagnosticIndex}`}>{entry.message}</li>)}</ul>}
          <div className="snowmaking-analysis-table">
            {system.pumps.map((pump) => <div key={pump.nodeId}><strong>
              {nodeById.get(pump.nodeId)?.name ?? pump.nodeId} — {pump.status}</strong>
              <span>{number.format(pump.flowGpm)} GPM · +{number.format(pump.pressureAddedPsi)} PSI</span>
              <small>{pump.suctionPressurePsi == null ? 'Pressure unavailable'
                : `Water enters at ${number.format(pump.suctionPressurePsi)} PSI · pump pushes out at ${number.format(pump.dischargePressurePsi ?? 0)} PSI`}</small>
            </div>)}
            {system.segments.map((segment) => <div key={segment.id}
              className={hoveredSegmentId === segment.id ? 'is-hovered' : undefined}><strong>
              {pipeById.get(segment.pipeId)?.name ?? segment.pipeId} · segment {segment.segmentIndex + 1}</strong>
              <span>{number.format(segment.flowGpm)} GPM · {number.format(segment.upstreamPressurePsi)} →
                {' '}{number.format(segment.downstreamPressurePsi)} PSI</span>
              <small>{number.format(segment.frictionHeadFt)} ft friction</small>
            </div>)}
            {system.guns.map((gun) => <div key={gun.gunId} tabIndex={0}
              onMouseEnter={() => setHoveredGun(gun.gunId)} onMouseLeave={() => setHoveredGun(null)}
              onFocus={() => setHoveredGun(gun.gunId)} onBlur={() => setHoveredGun(null)}
              className={gun.status === 'ready' ? 'is-ready' : 'is-failed'}><strong>
                {gunById.has(gun.gunId) ? `${snowgunLabel(gunById.get(gun.gunId)!, nodes)} · ${snowgunVariant(gunById.get(gun.gunId)!.variantId).shortLabel}` : gun.gunId}</strong>
              <span>{gun.demandGpm} GPM · {gun.pressurePsi == null ? '—' : number.format(gun.pressurePsi)} PSI</span>
              <small>{GUN_STATUS[gun.status]}</small>
            </div>)}
          </div>
        </details>)}
      </>}
    </div>
  </aside>;
}
