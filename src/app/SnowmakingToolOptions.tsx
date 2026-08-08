import { fmtDistance } from '../lifts';
import { SNOWMAKING_PIPE_DIAMETERS_IN } from '../types/snowmaking';
import type { SnowmakingPipeDiameterIn } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import type { SnowmakingNetworkController } from './useSnowmakingNetworkController';
import type { SnowgunController } from './useSnowgunController';

const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD',
  maximumFractionDigits: 0 });

export function SnowmakingToolOptions({ controller, gunController, units }: {
  controller: SnowmakingNetworkController;
  gunController: SnowgunController;
  units: Units;
}) {
  const pipeActive = controller.pipeTool.phase === 'armed' || controller.pipeTool.phase === 'drawing';
  const nodeActive = controller.nodeTool.phase === 'placing';
  const runActive = controller.hydrantRunTool.phase !== 'idle';
  const gunActive = gunController.tool.phase !== 'idle';
  if (!pipeActive && !nodeActive && !runActive && !gunActive) return null;
  return <div className="snowmaking-tool-options" role="group"
    aria-label={gunActive ? 'Snowgun construction options' : pipeActive ? 'Snowmaking pipe options'
      : runActive ? 'Hydrant run options' : 'Snowmaking node options'}>
    <div className="snowmaking-tool-options-title">{gunActive ? 'Snowgun plan' : pipeActive ? 'Pipe options'
      : runActive ? 'Hydrant run'
      : `Place ${controller.nodeTool.phase === 'placing' ? controller.nodeTool.kind : 'node'}`}</div>
    {pipeActive && <label className="snowmaking-tool-option-row"><span>Diameter</span>
      <select className="lift-select" aria-label="Pipe diameter" value={controller.diameterIn}
        onChange={(event) => controller.setDiameter(
          Number(event.target.value) as SnowmakingPipeDiameterIn)}>
        {SNOWMAKING_PIPE_DIAMETERS_IN.map((diameter) => <option key={diameter} value={diameter}>
          {diameter}&quot;</option>)}
      </select></label>}
    {!runActive && !gunActive && <label className="snowmaking-snap-toggle">
      <input type="checkbox" checked={controller.snapping}
        onChange={(event) => controller.setSnapping(event.target.checked)} />
      <span><strong>Node snapping</strong><small>Snap within 16 px of the existing network.</small></span>
    </label>}
    {pipeActive && <div className="snowmaking-tool-stats">
      <div><span>Length</span><output>{fmtDistance(controller.previewStats?.lengthM ?? 0, units)}</output></div>
      <div><span>Vertical</span><output>{controller.previewStats?.verticalM != null
        ? fmtDistance(controller.previewStats.verticalM, units) : '—'}</output></div>
    </div>}
    {nodeActive && <div className="snowmaking-tool-stats">
      <div><span>Elevation</span><output>{controller.nodeTool.candidate?.elevM != null
        ? fmtDistance(controller.nodeTool.candidate.elevM, units) : '—'}</output></div>
      <div><span>Target</span><output>{controller.nodeCandidateTarget ??
        (controller.nodeTool.candidate ? 'Free-standing' : 'Click the map')}</output></div>
    </div>}
    {runActive && <div className="snowmaking-tool-stats">
      <div><span>Step</span><output>{controller.hydrantRunTool.phase === 'select-pipe'
        ? 'Select pipe' : controller.hydrantRunTool.phase === 'select-start'
          ? 'Select start' : controller.hydrantRunTool.phase === 'select-end'
            ? 'Select end' : 'Review'}</output></div>
      <div><span>Pipe</span><output>{controller.hydrantRunPreview?.pipeName ?? '—'}</output></div>
      <div><span>Length</span><output>{controller.hydrantRunPreview?.lengthM != null
        ? fmtDistance(controller.hydrantRunPreview.lengthM, units) : '—'}</output></div>
      {controller.hydrantRunTool.phase === 'review' && <>
        <div><span>Positions</span><output>{controller.hydrantRunPreview?.positions.length ?? 0}</output></div>
        <div><span>Spacing</span><output>{controller.hydrantRunPreview?.actualSpacingM != null
          ? fmtDistance(controller.hydrantRunPreview.actualSpacingM, units) : '—'}</output></div>
      </>}
    </div>}
    {gunActive && <div className="snowmaking-tool-stats">
      <div><span>Step</span><output>{gunController.tool.phase === 'placing' ? 'Place guns'
        : gunController.tool.phase === 'review' ? 'Review' : 'Move sled'}</output></div>
      <div><span>Target</span><output>{gunController.preview.candidate?.hydrantLabel ??
        (gunController.preview.candidate ? 'Disconnected' : 'Move over map')}</output></div>
      <div><span>Hose</span><output>{gunController.preview.candidate?.hoseDistanceM != null
        ? fmtDistance(gunController.preview.candidate.hoseDistanceM, units) : '—'}</output></div>
      <div><span>Planned</span><output>{gunController.preview.items.length}</output></div>
      <div><span>Total</span><output>{money.format(gunController.preview.totalUsd)}</output></div>
    </div>}
  </div>;
}
