import { haversineMeters } from '../geo';
import { allocateSnowmakingNode, attachInlinePumpToSnowmakingPipe,
  attachNodeToSnowmakingPipe, closestSnowmakingPipeLocation,
  snowmakingPipeSegments, type SnowmakingNetworkState } from '../snowmakingNetwork';
import { isOwnedSnowmakingPump } from '../snowmakingOwnedPumps';
import type { SavedSnowmakingNode, SavedSnowmakingPipe } from '../types/snowmaking';
import type { SnowmakingNodeCandidate, SnowmakingNodeTool,
  SnowmakingPipeTool, SnowmakingSnapIntent } from './snowmakingNetworkControllerModel';

function pipeElevationAt(pipe: SavedSnowmakingPipe, segmentIndex: number, u: number): number | null {
  const a = pipe.vertices[segmentIndex]?.elevM, b = pipe.vertices[segmentIndex + 1]?.elevM;
  return a != null && b != null ? a + (b - a) * u : null;
}

export function inlinePumpCandidate(input: { pipes: readonly SavedSnowmakingPipe[];
  nodes: readonly SavedSnowmakingNode[];
  snap: Extract<SnowmakingSnapIntent, { kind: 'pipe' }>; revision: number;
  sampleElevation(point: [number, number]): number | null }): SnowmakingNodeCandidate | string {
  const pipe = input.pipes.find((candidate) => candidate.id === input.snap.pipeId);
  const location = pipe ? closestSnowmakingPipeLocation(pipe, input.snap.point) : null;
  const segment = pipe && location ? snowmakingPipeSegments(pipe).find((candidate) =>
    candidate.startVertexIndex <= location.segmentIndex && candidate.endVertexIndex > location.segmentIndex) : null;
  const boundary = segment && pipe ? [segment.startVertexIndex, segment.endVertexIndex]
    .map((index) => pipe.vertices[index]).find((vertex) =>
      haversineMeters(vertex.point, input.snap.point) < 0.05) : null;
  const boundaryNode = boundary?.nodeId
    ? input.nodes.find((node) => node.id === boundary.nodeId) ?? null : null;
  if (boundaryNode?.kind === 'intake')
    return 'Place the pump downstream inside the pipe; a pump cannot occupy the water source.';
  if (!segment || boundary) return 'Place the pump inside the pipe segment, away from its endpoint or another node.';
  return { point: input.snap.point, snap: input.snap, elevM: input.sampleElevation(input.snap.point),
    revision: input.revision, pumpSegmentId: segment.id, pumpSuctionSide: null };
}

export function inlinePumpDirectionPoints(candidate: SnowmakingNodeCandidate | null,
  pipes: readonly SavedSnowmakingPipe[]): [number, number][] | undefined {
  const snap = candidate?.snap?.kind === 'pipe' ? candidate.snap : null;
  const pipe = snap ? pipes.find((entry) => entry.id === snap.pipeId) : null;
  const segment = pipe && candidate?.pumpSegmentId
    ? snowmakingPipeSegments(pipe).find((entry) => entry.id === candidate.pumpSegmentId) : null;
  if (!segment || !candidate?.pumpSuctionSide) return undefined;
  const points = segment.vertices.map((vertex) => vertex.point);
  return candidate.pumpSuctionSide === 'route-start' ? points : points.reverse();
}

export function applySnowmakingNodeCandidate(state: SnowmakingNetworkState,
  tool: Extract<SnowmakingNodeTool, { phase: 'placing' }>, createId: () => string,
  now: () => string): SnowmakingNetworkState | string {
  const candidate = tool.candidate!;
  let point = candidate.point, elevM = candidate.elevM;
  let targetPipe: SavedSnowmakingPipe | null = null;
  let targetLocation: ReturnType<typeof closestSnowmakingPipeLocation> = null;
  const snap = candidate.snap?.kind === 'pipe' ? candidate.snap : null;
  if (snap) {
    targetPipe = state.pipes.find((pipe) => pipe.id === snap.pipeId) ?? null;
    targetLocation = targetPipe ? closestSnowmakingPipeLocation(targetPipe, snap.point) : null;
    if (!targetPipe || !targetLocation || targetLocation.distanceM > 2) return 'That pipe changed. Pick the device location again.';
    point = targetLocation.point;
    elevM = pipeElevationAt(targetPipe, targetLocation.segmentIndex, targetLocation.u);
  }
  if (state.nodes.some((node) => haversineMeters(node.point, point) < 0.05))
    return 'A network node already occupies that location.';
  const allocation = allocateSnowmakingNode(state, { id: createId(), kind: tool.kind,
    point, elevM, createdAt: now() });
  state = allocation.state;
  if (targetPipe && targetLocation) {
    const nextPipe = tool.kind === 'pump'
      ? attachInlinePumpToSnowmakingPipe(targetPipe, targetLocation, allocation.node.id,
        candidate.pumpSuctionSide!, createId)
      : attachNodeToSnowmakingPipe(targetPipe, targetLocation, allocation.node.id, createId);
    if (!nextPipe) return 'That pipe segment changed. Pick the pump location again.';
    state = { ...state, pipes: state.pipes.map((pipe) => pipe.id === nextPipe.id ? nextPipe : pipe) };
  }
  return state;
}

export function resolveSnowmakingPipeDraft(state: SnowmakingNetworkState,
  tool: Extract<SnowmakingPipeTool, { phase: 'review' }>, createId: () => string,
  now: () => string): { state: SnowmakingNetworkState; points: [number, number][];
    nodeIds: (string | null)[] } | string {
  const points: [number, number][] = [], nodeIds: (string | null)[] = [];
  for (const draft of tool.points) {
    const snap = draft.snap;
    if (!snap) { points.push(draft.point); nodeIds.push(null); continue; }
    if (snap.kind === 'node') {
      const node = state.nodes.find((candidate) => candidate.id === snap.nodeId);
      if (!node) return 'A snapped node changed before this pipe was installed. Pick the connection again.';
      if (node.kind === 'pump' && !isOwnedSnowmakingPump(node))
        return 'Connect new pipes at a junction, not directly to a pump.';
      points.push(node.point); nodeIds.push(node.id); continue;
    }
    const pipe = state.pipes.find((candidate) => candidate.id === snap.pipeId);
    const location = pipe ? closestSnowmakingPipeLocation(pipe, snap.point) : null;
    if (!pipe || !location || location.distanceM > 2) return 'A snapped connection is no longer available.';
    const existing = state.nodes.find((node) => haversineMeters(node.point, location.point) < 0.05);
    if (existing) {
      if (existing.kind === 'pump' && !isOwnedSnowmakingPump(existing))
        return 'Connect new pipes at a junction, not directly to a pump.';
      points.push(existing.point); nodeIds.push(existing.id); continue;
    }
    const allocation = allocateSnowmakingNode(state, { id: createId(), kind: 'junction',
      point: location.point, elevM: pipeElevationAt(pipe, location.segmentIndex, location.u), createdAt: now() });
    state = allocation.state;
    const attached = attachNodeToSnowmakingPipe(pipe, location, allocation.node.id, createId);
    state = { ...state, pipes: state.pipes.map((entry) => entry.id === attached.id ? attached : entry) };
    points.push(location.point); nodeIds.push(allocation.node.id);
  }
  return { state, points, nodeIds };
}
