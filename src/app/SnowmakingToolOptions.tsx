import { fmtDistance } from '../lifts';
import { SNOWMAKING_PIPE_DIAMETERS_IN } from '../types/snowmaking';
import type { SnowmakingPipeDiameterIn } from '../types/snowmaking';
import type { Units } from './SettingsContext';
import type { SnowmakingNetworkController } from './useSnowmakingNetworkController';

export function SnowmakingToolOptions({ controller, units }: {
  controller: SnowmakingNetworkController;
  units: Units;
}) {
  const pipeActive = controller.pipeTool.phase === 'armed' || controller.pipeTool.phase === 'drawing';
  const nodeActive = controller.nodeTool.phase === 'placing';
  if (!pipeActive && !nodeActive) return null;
  return <div className="snowmaking-tool-options" role="group"
    aria-label={pipeActive ? 'Snowmaking pipe options' : 'Snowmaking node options'}>
    <div className="snowmaking-tool-options-title">{pipeActive ? 'Pipe options'
      : `Place ${controller.nodeTool.phase === 'placing' ? controller.nodeTool.kind : 'node'}`}</div>
    {pipeActive && <label className="snowmaking-tool-option-row"><span>Diameter</span>
      <select className="lift-select" aria-label="Pipe diameter" value={controller.diameterIn}
        onChange={(event) => controller.setDiameter(
          Number(event.target.value) as SnowmakingPipeDiameterIn)}>
        {SNOWMAKING_PIPE_DIAMETERS_IN.map((diameter) => <option key={diameter} value={diameter}>
          {diameter}&quot;</option>)}
      </select></label>}
    <label className="snowmaking-snap-toggle">
      <input type="checkbox" checked={controller.snapping}
        onChange={(event) => controller.setSnapping(event.target.checked)} />
      <span><strong>Node snapping</strong><small>Snap within 16 px of the existing network.</small></span>
    </label>
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
  </div>;
}
