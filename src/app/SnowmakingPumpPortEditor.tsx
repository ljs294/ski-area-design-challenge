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
  const setTwoArmDirection = (suctionIndex: number) => arms.forEach((arm, index) =>
    onSetPumpPort(arm.pipe.id, arm.segment.id, arm.end,
      index === suctionIndex ? 'suction' : 'discharge'));
  return <fieldset className="snowmaking-pump-ports" id={`pump-ports-${pump.id}`}>
    {!compact && <legend>Hydraulic direction</legend>}
    <div className="network-sub">Assign every connected pipe arm to suction or discharge.</div>
    {arms.length === 0 && <div className="lift-warning">This pump is not connected to a pipe.</div>}
    {arms.length === 2 ? <div className="snowmaking-pump-directions">
      {arms.map((arm, index) => <label key={`${arm.segment.id}:${arm.end}`}>
        <input type="radio" name={`pump-direction-${pump.id}`}
          checked={arm.port === 'suction' && arms[1 - index].port === 'discharge'}
          onChange={() => setTwoArmDirection(index)} />
        <span><strong>Suction from {armLabel(arm, nodes)}</strong><small>
          Discharge toward {armLabel(arms[1 - index], nodes)}</small></span>
      </label>)}
    </div> : arms.map((arm) => <label key={`${arm.segment.id}:${arm.end}`}>
      <span><strong>{armLabel(arm, nodes)}</strong></span>
      <select value={arm.port ?? ''} aria-label={`${arm.pipe.name} pump port`}
        onChange={(event) => onSetPumpPort(arm.pipe.id, arm.segment.id, arm.end,
          event.target.value === '' ? null : event.target.value as SnowmakingPumpPort)}>
        <option value="">Unassigned</option><option value="suction">Suction</option>
        <option value="discharge">Discharge</option>
      </select>
    </label>)}
    {arms.length > 0 && (configured.length !== arms.length || !hasSuction || !hasDischarge) &&
      <div className="lift-warning">Configure at least one suction and one discharge arm.</div>}
  </fieldset>;
}
