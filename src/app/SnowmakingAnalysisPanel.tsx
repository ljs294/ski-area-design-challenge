import { snowgunVariant } from '../snowmakingGuns';
import { snowmakingNodeLabel } from '../snowmakingNetwork';
import type { SnowmakingAnalysisResult } from '../snowmakingHydraulics';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe } from '../types/snowmaking';
import { DEFAULT_PUMP_ANALYSIS_DRAFT, type SnowmakingAnalysisState } from './snowmakingAnalysisModel';

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
  'no-flow-path': 'No flow path',
  'insufficient-pressure': 'Insufficient pressure at demanded flow',
} as const;

export function SnowmakingAnalysisPanel({ state, nodes, pipes, guns, result,
  togglePipe, toggleGun, setWetBulb, setPumpOn, setPumpHp, setPumpEfficiency,
  check, reset }: {
  state: SnowmakingAnalysisState;
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  result: SnowmakingAnalysisResult | null;
  togglePipe(id: string): void;
  toggleGun(id: string): void;
  setWetBulb(value: string): void;
  setPumpOn(id: string, on: boolean): void;
  setPumpHp(id: string, value: string): void;
  setPumpEfficiency(id: string, value: string): void;
  check(): void;
  reset(): void;
}) {
  const selectedPipes = new Set(state.selectedPipeIds);
  const selectedGuns = new Set(state.selectedGunIds);
  const selectedNodeIds = new Set(pipes.filter((pipe) => selectedPipes.has(pipe.id))
    .flatMap((pipe) => pipe.vertices.flatMap((vertex) => vertex.nodeId ? [vertex.nodeId] : [])));
  const pumps = nodes.filter((node) => node.kind === 'pump' && selectedNodeIds.has(node.id));
  const pipeById = new Map(pipes.map((pipe) => [pipe.id, pipe]));
  const gunById = new Map(guns.map((gun) => [gun.id, gun]));
  const nodeById = new Map(nodes.map((node) => [node.id, node]));

  return <aside className="network-inspector snowmaking-analysis-panel"
    data-inspector="analysis" aria-label="Snowmaking system analyzer">
    <div className="dock-head"><span className="dock-head-title">Analyze snowmaking system</span></div>
    <div className="network-sub">Select one intake-rooted tree, configure its pumps, then check capacity.</div>

    <label className="snowmaking-analysis-temperature">
      <span>Wet-bulb temperature</span>
      <span><input type="number" step="1" value={state.wetBulbF} aria-label="Wet-bulb temperature in Fahrenheit"
        onChange={(event) => setWetBulb(event.target.value)} /> °F</span>
    </label>

    <div className="network-section-title">Pipe runs</div>
    {pipes.length ? <div className="snowmaking-analysis-checklist">
      {pipes.map((pipe) => <label key={pipe.id}><input type="checkbox"
        checked={selectedPipes.has(pipe.id)} onChange={() => togglePipe(pipe.id)} />
        <span><strong>{pipe.name}</strong><small>{pipe.diameterIn}&quot; pipe</small></span></label>)}
    </div> : <div className="network-sub">No snowmaking pipes are installed.</div>}

    <div className="network-section-title">Snowguns</div>
    {guns.length ? <div className="snowmaking-analysis-checklist">
      {guns.map((gun) => { const variant = snowgunVariant(gun.variantId); return <label key={gun.id}
        className={gun.hydrantId ? '' : 'is-disabled'}><input type="checkbox"
          checked={selectedGuns.has(gun.id)} disabled={!gun.hydrantId}
          onChange={() => toggleGun(gun.id)} />
        <span><strong>{variant.shortLabel}</strong><small>{gun.hydrantId
          ? `Hydrant ${nodeById.get(gun.hydrantId)?.labelNumber ?? '—'}` : 'Disconnected'}</small></span>
      </label>; })}
    </div> : <div className="network-sub">No snowguns are installed.</div>}

    <div className="network-section-title">Pumps in selected pipes</div>
    {pumps.length ? <div className="snowmaking-analysis-pumps">{pumps.map((pump) => {
      const setting = state.pumpSettings[pump.id] ?? DEFAULT_PUMP_ANALYSIS_DRAFT;
      return <fieldset key={pump.id}><legend>{snowmakingNodeLabel(pump)} · {pump.name}</legend>
        <label className="snowmaking-analysis-pump-toggle"><input type="checkbox" checked={setting.on}
          onChange={(event) => setPumpOn(pump.id, event.target.checked)} /> Pump On</label>
        <label><span>Horsepower</span><input type="number" min="0" step="1"
          disabled={!setting.on} value={setting.horsepowerHp} aria-label={`${pump.name} horsepower`}
          onChange={(event) => setPumpHp(pump.id, event.target.value)} /></label>
        <label><span>Efficiency</span><span><input type="number" min="1" max="100" step="1"
          disabled={!setting.on} value={setting.efficiencyPercent}
          aria-label={`${pump.name} efficiency percent`}
          onChange={(event) => setPumpEfficiency(pump.id, event.target.value)} /> %</span></label>
      </fieldset>;
    })}</div> : <div className="lift-warning">No pump node is attached to the selected pipes.</div>}

    <div className="snowmaking-analysis-actions">
      <button className="site-btn" onClick={reset}>Reset analysis</button>
      <button className="site-btn site-btn-primary" onClick={check}>Check system</button>
    </div>

    <div aria-live="polite" className={`snowmaking-analysis-results${state.stale ? ' is-stale' : ''}`}>
      {state.stale && <div className="lift-warning">Inputs changed. Check the system again to refresh results.</div>}
      {result && !result.ok && <div className="snowmaking-analysis-diagnostics">
        <strong>Cannot analyze this selection</strong>
        <ul>{result.diagnostics.map((entry, index) => <li key={`${entry.code}:${entry.entityId ?? index}`}>
          {entry.message}</li>)}</ul>
      </div>}
      {result?.ok && <>
        <div className={`snowmaking-analysis-verdict${result.summary.overallReady ? ' is-ready' : ' is-failed'}`}>
          <strong>{result.summary.overallReady ? 'System ready' : 'System cannot operate every selected gun'}</strong>
          <span>{result.summary.readyGunCount} of {result.summary.selectedGunCount} guns ready</span>
        </div>
        <div className="network-stats">
          <div className="network-stat"><span className="network-stat-label">Demand</span>
            <span className="network-stat-value">{whole.format(result.summary.totalDemandGpm)} GPM</span></div>
          <div className="network-stat"><span className="network-stat-label">Water use</span>
            <span className="network-stat-value">{whole.format(result.summary.waterUseGalPerHour)} gal/hr</span></div>
          <div className="network-stat"><span className="network-stat-label">Source storage</span>
            <span className="network-stat-value">{result.summary.sourceCapacityGallons == null
              ? 'Unavailable' : `${whole.format(result.summary.sourceCapacityGallons)} gal`}</span></div>
          <div className="network-stat"><span className="network-stat-label">Estimated runtime</span>
            <span className="network-stat-value">{runtime(result.summary.sourceRuntimeHours)}</span></div>
          <div className="network-stat"><span className="network-stat-label">Minimum gun pressure</span>
            <span className="network-stat-value">{result.summary.minimumGunPressurePsi == null
              ? 'Unavailable' : `${number.format(result.summary.minimumGunPressurePsi)} PSI`}</span></div>
        </div>

        <details open><summary>Pumps ({result.pumps.length})</summary>
          <div className="snowmaking-analysis-table">{result.pumps.map((pump) => <div key={pump.nodeId}>
            <strong>{nodeById.get(pump.nodeId)?.name ?? pump.nodeId} — {pump.on ? 'On' : 'Off'}</strong>
            <span>{whole.format(pump.flowGpm)} GPM · {pump.horsepowerHp == null
              ? '—' : whole.format(pump.horsepowerHp)} HP at
              {' '}{whole.format(pump.efficiency * 100)}%</span>
            <small>+{number.format(pump.headAddedFt)} ft · +{number.format(pump.pressureAddedPsi)} PSI</small>
          </div>)}</div>
        </details>
        <details><summary>Pipe spans ({result.spans.length})</summary>
          <div className="snowmaking-analysis-table">{result.spans.map((span) => <div key={span.id}>
            <strong>{pipeById.get(span.pipeId)?.name ?? span.pipeId} · span {span.spanIndex + 1}</strong>
            <span>{whole.format(span.flowGpm)} GPM · {number.format(span.upstreamPressurePsi)} →
              {' '}{number.format(span.downstreamPressurePsi)} PSI</span>
            <small>{number.format(span.staticHeadFt)} ft static · {number.format(span.frictionHeadFt)} ft friction</small>
          </div>)}</div>
        </details>
        <details open><summary>Snowguns ({result.guns.length})</summary>
          <div className="snowmaking-analysis-table">{result.guns.map((gun) => {
            const saved = gunById.get(gun.gunId); return <div key={gun.gunId}
              className={gun.status === 'ready' ? 'is-ready' : 'is-failed'}>
              <strong>{saved ? snowgunVariant(saved.variantId).shortLabel : gun.gunId}</strong>
              <span>{gun.stage ? `Stage ${gun.stage.stage} · ${gun.demandGpm} GPM` : 'No operating stage'}
                {' '}· {number.format(gun.pressurePsi)} PSI</span>
              <small>{GUN_STATUS[gun.status]}</small>
            </div>;
          })}</div>
        </details>
      </>}
    </div>
  </aside>;
}
