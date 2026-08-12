import { snowmakingNodeLabel, snowmakingPipeSegments } from '../snowmakingNetwork';
import type { SavedSnowmakingNode, SavedSnowmakingPipe, SnowmakingPumpPort } from '../types/snowmaking';

export interface SnowmakingPumpArm {
  pipe: SavedSnowmakingPipe;
  segment: ReturnType<typeof snowmakingPipeSegments>[number];
  end: 'start' | 'end';
  port: SnowmakingPumpPort | null;
  neighborId: string | null;
}

export function snowmakingPumpArms(pumpId: string, pipes: readonly SavedSnowmakingPipe[]): SnowmakingPumpArm[] {
  return pipes.flatMap((pipe) => snowmakingPipeSegments(pipe).flatMap((segment): SnowmakingPumpArm[] => {
    if (segment.fromNodeId === pumpId) return [{ pipe, segment, end: 'start',
      port: segment.startPumpPort, neighborId: segment.toNodeId }];
    if (segment.toNodeId === pumpId) return [{ pipe, segment, end: 'end',
      port: segment.endPumpPort, neighborId: segment.fromNodeId }];
    return [];
  }));
}

function armLabel(arm: SnowmakingPumpArm, nodes: readonly SavedSnowmakingNode[]): string {
  const neighbor = nodes.find((node) => node.id === arm.neighborId);
  return `${arm.pipe.name} · segment ${arm.segment.segmentIndex + 1} · toward ${
    neighbor ? snowmakingNodeLabel(neighbor) : 'pipe end'}`;
}

export function snowmakingPumpDirectionSummary(
  pump: SavedSnowmakingNode,
  nodes: readonly SavedSnowmakingNode[],
  pipes: readonly SavedSnowmakingPipe[],
): { inlets: string[]; outlets: string[]; text: string } | null {
  const arms = snowmakingPumpArms(pump.id, pipes);
  const inlets = arms.filter((arm) => arm.port === 'suction').map((arm) => armLabel(arm, nodes));
  const outlets = arms.filter((arm) => arm.port === 'discharge').map((arm) => armLabel(arm, nodes));
  if (!inlets.length || !outlets.length) return null;
  const pumpLabel = snowmakingNodeLabel(pump);
  return { inlets, outlets,
    text: `${inlets.join(', ')} → ${pumpLabel} → ${outlets.join(', ')}` };
}

export function SnowmakingPumpPortEditor({ pump, nodes, pipes, onSetPumpPort, compact = false }: {
  pump: SavedSnowmakingNode;
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  onSetPumpPort(pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null): void;
  compact?: boolean;
}) {
  const arms = snowmakingPumpArms(pump.id, pipes);
  const configured = arms.filter((arm) => arm.port != null);
  const hasSuction = arms.some((arm) => arm.port === 'suction');
  const hasDischarge = arms.some((arm) => arm.port === 'discharge');
  const summary = snowmakingPumpDirectionSummary(pump, nodes, pipes);
  const setTwoArmDirection = (suctionIndex: number) => arms.forEach((arm, index) =>
    onSetPumpPort(arm.pipe.id, arm.segment.id, arm.end,
      index === suctionIndex ? 'suction' : 'discharge'));
  return <fieldset className="snowmaking-pump-ports" id={`pump-ports-${pump.id}`}>
    {!compact && <legend>Which way does this pump push water?</legend>}
    <div className="network-sub">Choose the pipe where water enters the pump and the pipe where the pump pushes water out.</div>
    {summary && <div className="snowmaking-pump-direction-summary" aria-label="Configured pump direction">
      <span>Configured flow</span><strong>{summary.text}</strong></div>}
    {arms.length === 0 && <div className="lift-warning">This pump is not connected to a pipe.</div>}
    {arms.length === 2 ? <div className="snowmaking-pump-directions">
      {arms.map((arm, index) => <label key={`${arm.segment.id}:${arm.end}`}>
        <input type="radio" name={`pump-direction-${pump.id}`}
          checked={arm.port === 'suction' && arms[1 - index].port === 'discharge'}
          onChange={() => setTwoArmDirection(index)} />
        <span><strong><em className="snowmaking-port-badge is-inlet">IN</em>
          Water enters from {armLabel(arm, nodes)}</strong><small>
          <em className="snowmaking-port-badge is-outlet">OUT</em>
          Pump pushes toward {armLabel(arms[1 - index], nodes)}</small></span>
      </label>)}
    </div> : arms.map((arm) => <label key={`${arm.segment.id}:${arm.end}`}>
      <span><strong>{arm.port && <em className={`snowmaking-port-badge ${
        arm.port === 'suction' ? 'is-inlet' : 'is-outlet'}`}>{arm.port === 'suction' ? 'IN' : 'OUT'}</em>}
        {armLabel(arm, nodes)}</strong></span>
      <select value={arm.port ?? ''} aria-label={`${arm.pipe.name} pump port`}
        onChange={(event) => onSetPumpPort(arm.pipe.id, arm.segment.id, arm.end,
          event.target.value === '' ? null : event.target.value as SnowmakingPumpPort)}>
        <option value="">Choose water direction…</option>
        <option value="suction">Water enters pump (inlet)</option>
        <option value="discharge">Pump pushes water out (outlet)</option>
      </select>
    </label>)}
    {arms.length > 0 && (configured.length !== arms.length || !hasSuction || !hasDischarge) &&
      <div className="lift-warning">Choose at least one inlet where water enters and one outlet where the pump pushes water.</div>}
  </fieldset>;
}
