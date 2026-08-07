import { haversineMeters } from './geo';
import type {
  NumberedSnowmakingNodeKind,
  SavedSnowmakingNode,
  SavedSnowmakingPipe,
  SavedSnowmakingPipeVertex,
  SnowmakingNodeNextNumbers,
  SnowmakingPipeDiameterIn,
} from './types/snowmaking';
import { SNOWMAKING_PIPE_DIAMETERS_IN } from './types/snowmaking';

export const DEFAULT_SNOWMAKING_PIPE_DIAMETER_IN: SnowmakingPipeDiameterIn = 8;
export const SNOWMAKING_PIPE_PROFILE_SPACING_M = 25;
export const EMPTY_SNOWMAKING_NODE_NEXT_NUMBERS: SnowmakingNodeNextNumbers = Object.freeze({
  hydrant: 1,
  junction: 1,
  pump: 1,
});

const DIAMETERS = new Set<number>(SNOWMAKING_PIPE_DIAMETERS_IN);
const POINT_EPSILON_M = 0.05;

export interface SnowmakingNetworkState {
  nodes: SavedSnowmakingNode[];
  pipes: SavedSnowmakingPipe[];
  nextNumbers: SnowmakingNodeNextNumbers;
}

export interface SnowmakingPipeStats {
  lengthM: number;
  verticalM: number | null;
}

function isPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every((coordinate) =>
    typeof coordinate === 'number' && Number.isFinite(coordinate));
}

export function isSnowmakingPipeDiameter(value: unknown): value is SnowmakingPipeDiameterIn {
  return typeof value === 'number' && DIAMETERS.has(value);
}

export function snowmakingNodeLabel(node: SavedSnowmakingNode): string {
  if (node.kind === 'intake') return node.name;
  if (!node.labelNumber) return node.name;
  if (node.kind === 'hydrant') return String(node.labelNumber);
  return `${node.kind === 'junction' ? 'J' : 'P'}${node.labelNumber}`;
}

export function defaultSnowmakingNodeName(
  kind: NumberedSnowmakingNodeKind,
  labelNumber: number,
): string {
  const label = kind === 'hydrant' ? String(labelNumber)
    : `${kind === 'junction' ? 'J' : 'P'}${labelNumber}`;
  return `${kind === 'hydrant' ? 'Hydrant' : kind === 'junction' ? 'Junction' : 'Pump'} ${label}`;
}

export function nextSnowmakingPipeName(pipes: readonly SavedSnowmakingPipe[]): string {
  const used = new Set(pipes.map((pipe) => pipe.name));
  let number = 1;
  while (used.has(`Pipe ${number}`)) number += 1;
  return `Pipe ${number}`;
}

export function snowmakingPipeStats(
  vertices: readonly SavedSnowmakingPipeVertex[],
): SnowmakingPipeStats {
  if (vertices.length < 2) return { lengthM: 0, verticalM: null };
  const resolved = vertices.every((vertex) => vertex.elevM != null && Number.isFinite(vertex.elevM));
  let lengthM = 0;
  for (let index = 1; index < vertices.length; index += 1) {
    const horizontalM = haversineMeters(vertices[index - 1].point, vertices[index].point);
    const verticalM = resolved
      ? (vertices[index].elevM as number) - (vertices[index - 1].elevM as number)
      : 0;
    lengthM += Math.hypot(horizontalM, verticalM);
  }
  if (!resolved) return { lengthM, verticalM: null };
  const elevations = vertices.map((vertex) => vertex.elevM as number);
  return { lengthM, verticalM: Math.max(...elevations) - Math.min(...elevations) };
}

/** Densify each clicked segment independently so exact corners and snap points survive. */
export function densifySnowmakingPipe(
  points: readonly [number, number][],
  sampleElevation: (point: [number, number]) => number | null,
  nodeIds: readonly (string | null)[] = [],
): SavedSnowmakingPipeVertex[] {
  if (points.length === 0) return [];
  const vertices: SavedSnowmakingPipeVertex[] = [];
  const push = (point: [number, number], nodeId: string | null) => {
    const previous = vertices.at(-1);
    if (previous && haversineMeters(previous.point, point) < POINT_EPSILON_M) {
      if (nodeId) previous.nodeId = nodeId;
      return;
    }
    vertices.push({ point, elevM: sampleElevation(point), nodeId });
  };
  push(points[0], nodeIds[0] ?? null);
  for (let index = 1; index < points.length; index += 1) {
    const start = points[index - 1];
    const end = points[index];
    const distanceM = haversineMeters(start, end);
    const steps = Math.max(1, Math.ceil(distanceM / SNOWMAKING_PIPE_PROFILE_SPACING_M));
    for (let step = 1; step <= steps; step += 1) {
      const t = step / steps;
      const point: [number, number] = [
        start[0] + (end[0] - start[0]) * t,
        start[1] + (end[1] - start[1]) * t,
      ];
      push(point, step === steps ? nodeIds[index] ?? null : null);
    }
  }
  return vertices;
}

export function buildSnowmakingPipe(
  input: Omit<SavedSnowmakingPipe, 'vertices' | 'lengthM' | 'verticalM'> & {
    points: readonly [number, number][];
    nodeIds?: readonly (string | null)[];
  },
  sampleElevation: (point: [number, number]) => number | null,
): SavedSnowmakingPipe {
  const vertices = densifySnowmakingPipe(input.points, sampleElevation, input.nodeIds);
  const stats = snowmakingPipeStats(vertices);
  return {
    id: input.id,
    name: input.name,
    diameterIn: input.diameterIn,
    vertices,
    ...stats,
    createdAt: input.createdAt,
  };
}

export interface SnowmakingPipeLocation {
  point: [number, number];
  segmentIndex: number;
  u: number;
  distanceM: number;
}

export interface SnowmakingPipeStation extends SnowmakingPipeLocation {
  /** Terrain-following distance from the first route vertex. */
  stationM: number;
  elevM: number | null;
}

export const MAX_HYDRANT_RUN_POSITIONS = 500;

export type SnowmakingHydrantRunSpec =
  | { mode: 'count'; count: number }
  | { mode: 'spacing'; spacingM: number };

export interface SnowmakingHydrantRunLayout {
  start: SnowmakingPipeStation;
  end: SnowmakingPipeStation;
  lengthM: number;
  actualSpacingM: number;
  positions: SnowmakingPipeStation[];
}

function pipeStationLengths(pipe: SavedSnowmakingPipe): { cumulative: number[]; resolved: boolean } {
  const resolved = pipe.vertices.every((vertex) => vertex.elevM != null && Number.isFinite(vertex.elevM));
  const cumulative = [0];
  for (let index = 1; index < pipe.vertices.length; index += 1) {
    const previous = pipe.vertices[index - 1], current = pipe.vertices[index];
    const horizontalM = haversineMeters(previous.point, current.point);
    const verticalM = resolved ? (current.elevM as number) - (previous.elevM as number) : 0;
    cumulative.push(cumulative[index - 1] + Math.hypot(horizontalM, verticalM));
  }
  return { cumulative, resolved };
}

/** Project a map point to a stable distance along one saved pipe route. */
export function snowmakingPipeStationAt(
  pipe: SavedSnowmakingPipe,
  point: [number, number],
): SnowmakingPipeStation | null {
  const location = closestSnowmakingPipeLocation(pipe, point);
  if (!location) return null;
  const { cumulative, resolved } = pipeStationLengths(pipe);
  const segmentLengthM = cumulative[location.segmentIndex + 1] - cumulative[location.segmentIndex];
  const before = pipe.vertices[location.segmentIndex], after = pipe.vertices[location.segmentIndex + 1];
  return {
    ...location,
    stationM: cumulative[location.segmentIndex] + segmentLengthM * location.u,
    elevM: resolved
      ? (before.elevM as number) + ((after.elevM as number) - (before.elevM as number)) * location.u
      : null,
  };
}

/** Resolve an exact point at a terrain-following station along a saved pipe route. */
export function snowmakingPipePointAtStation(
  pipe: SavedSnowmakingPipe,
  requestedStationM: number,
): SnowmakingPipeStation | null {
  if (pipe.vertices.length < 2) return null;
  const { cumulative, resolved } = pipeStationLengths(pipe);
  const totalM = cumulative.at(-1) ?? 0;
  const stationM = Math.max(0, Math.min(totalM, requestedStationM));
  let segmentIndex = Math.max(0, pipe.vertices.length - 2);
  for (let index = 0; index < cumulative.length - 1; index += 1) {
    if (stationM <= cumulative[index + 1]) { segmentIndex = index; break; }
  }
  const spanM = cumulative[segmentIndex + 1] - cumulative[segmentIndex];
  const u = spanM > 0 ? (stationM - cumulative[segmentIndex]) / spanM : 0;
  const before = pipe.vertices[segmentIndex], after = pipe.vertices[segmentIndex + 1];
  const point: [number, number] = [
    before.point[0] + (after.point[0] - before.point[0]) * u,
    before.point[1] + (after.point[1] - before.point[1]) * u,
  ];
  return {
    point, segmentIndex, u, stationM, distanceM: 0,
    elevM: resolved
      ? (before.elevM as number) + ((after.elevM as number) - (before.elevM as number)) * u
      : null,
  };
}

/** Build endpoint-inclusive hydrant stations in the player's chosen direction. */
export function snowmakingHydrantRunLayout(
  pipe: SavedSnowmakingPipe,
  start: SnowmakingPipeStation,
  end: SnowmakingPipeStation,
  spec: SnowmakingHydrantRunSpec,
): SnowmakingHydrantRunLayout | string {
  const lengthM = Math.abs(end.stationM - start.stationM);
  if (!Number.isFinite(lengthM) || lengthM < POINT_EPSILON_M) {
    return 'Choose two distinct points on the pipe.';
  }
  let count: number;
  if (spec.mode === 'count') {
    if (!Number.isInteger(spec.count) || spec.count < 2 || spec.count > MAX_HYDRANT_RUN_POSITIONS) {
      return `Enter between 2 and ${MAX_HYDRANT_RUN_POSITIONS} hydrant positions.`;
    }
    count = spec.count;
  } else {
    if (!Number.isFinite(spec.spacingM) || spec.spacingM <= 0) return 'Enter a positive maximum spacing.';
    count = Math.ceil(lengthM / spec.spacingM) + 1;
    if (count > MAX_HYDRANT_RUN_POSITIONS) {
      return `This spacing creates more than ${MAX_HYDRANT_RUN_POSITIONS} positions.`;
    }
  }
  const actualSpacingM = lengthM / (count - 1);
  const direction = end.stationM >= start.stationM ? 1 : -1;
  const positions: SnowmakingPipeStation[] = [];
  for (let index = 0; index < count; index += 1) {
    const station = snowmakingPipePointAtStation(pipe,
      start.stationM + direction * actualSpacingM * index);
    if (station) positions.push(station);
  }
  return { start, end, lengthM, actualSpacingM, positions };
}

/** Geometry for highlighting only the selected subsection of a route. */
export function snowmakingPipeIntervalPoints(
  pipe: SavedSnowmakingPipe,
  start: SnowmakingPipeStation,
  end: SnowmakingPipeStation,
): [number, number][] {
  const forward = start.stationM <= end.stationM;
  const { cumulative } = pipeStationLengths(pipe);
  const low = Math.min(start.stationM, end.stationM), high = Math.max(start.stationM, end.stationM);
  const middle = pipe.vertices.flatMap((vertex, index) =>
    cumulative[index] > low && cumulative[index] < high ? [vertex.point] : []);
  const points = forward ? [start.point, ...middle, end.point] : [end.point, ...middle, start.point];
  return forward ? points : points.reverse();
}

/** Insert several explicit node references while retaining the pipe as one route. */
export function attachNodesToSnowmakingPipe(
  pipe: SavedSnowmakingPipe,
  attachments: readonly { stationM: number; nodeId: string }[],
): SavedSnowmakingPipe {
  if (attachments.length === 0) return pipe;
  const { cumulative } = pipeStationLengths(pipe);
  const items: { stationM: number; vertex: SavedSnowmakingPipeVertex; attachment: boolean }[] =
    pipe.vertices.map((vertex, index) => ({ stationM: cumulative[index], vertex: { ...vertex }, attachment: false }));
  for (const attachment of attachments) {
    const station = snowmakingPipePointAtStation(pipe, attachment.stationM);
    if (station) items.push({ stationM: station.stationM,
      vertex: { point: station.point, elevM: station.elevM, nodeId: attachment.nodeId }, attachment: true });
  }
  items.sort((left, right) => left.stationM - right.stationM || Number(left.attachment) - Number(right.attachment));
  const vertices: SavedSnowmakingPipeVertex[] = [];
  const stations: number[] = [];
  for (const item of items) {
    const previousStation = stations.at(-1);
    if (previousStation != null && Math.abs(previousStation - item.stationM) < POINT_EPSILON_M) {
      if (item.attachment) vertices[vertices.length - 1] = {
        ...vertices[vertices.length - 1], point: item.vertex.point, elevM: item.vertex.elevM,
        nodeId: item.vertex.nodeId,
      };
      continue;
    }
    stations.push(item.stationM); vertices.push(item.vertex);
  }
  return { ...pipe, vertices, ...snowmakingPipeStats(vertices) };
}

export function populateSnowmakingHydrantRun(
  state: SnowmakingNetworkState,
  pipeId: string,
  layout: SnowmakingHydrantRunLayout,
  createId: () => string,
  now: () => string,
): { state: SnowmakingNetworkState; nodes: SavedSnowmakingNode[]; skipped: number } | string {
  const pipe = state.pipes.find((candidate) => candidate.id === pipeId);
  if (!pipe) return 'The selected pipe is no longer available.';
  let next = state;
  const occupied = state.nodes.map((node) => node.point);
  const nodes: SavedSnowmakingNode[] = [];
  const attachments: { stationM: number; nodeId: string }[] = [];
  let skipped = 0;
  for (const position of layout.positions) {
    if (occupied.some((point) => haversineMeters(point, position.point) < POINT_EPSILON_M)) {
      skipped += 1; continue;
    }
    const allocation = allocateSnowmakingNode(next, { id: createId(), kind: 'hydrant',
      point: position.point, elevM: position.elevM, createdAt: now() });
    next = allocation.state; nodes.push(allocation.node); occupied.push(position.point);
    attachments.push({ stationM: position.stationM, nodeId: allocation.node.id });
  }
  if (nodes.length === 0) return 'Every calculated position is already occupied.';
  const updatedPipe = attachNodesToSnowmakingPipe(pipe, attachments);
  return { state: { ...next, pipes: next.pipes.map((candidate) =>
    candidate.id === pipeId ? updatedPipe : candidate) }, nodes, skipped };
}

export function closestSnowmakingPipeLocation(
  pipe: SavedSnowmakingPipe,
  point: [number, number],
): SnowmakingPipeLocation | null {
  if (pipe.vertices.length < 2) return null;
  const latitudeRadians = point[1] * Math.PI / 180;
  const metersX = 111320 * Math.cos(latitudeRadians);
  const metersY = 111320;
  let best: SnowmakingPipeLocation | null = null;
  for (let index = 0; index < pipe.vertices.length - 1; index += 1) {
    const a = pipe.vertices[index].point;
    const b = pipe.vertices[index + 1].point;
    const ax = (a[0] - point[0]) * metersX, ay = (a[1] - point[1]) * metersY;
    const bx = (b[0] - point[0]) * metersX, by = (b[1] - point[1]) * metersY;
    const dx = bx - ax, dy = by - ay;
    const lengthSquared = dx * dx + dy * dy;
    const u = lengthSquared > 0 ? Math.max(0, Math.min(1, -(ax * dx + ay * dy) / lengthSquared)) : 0;
    const x = ax + dx * u, y = ay + dy * u;
    const distanceM = Math.hypot(x, y);
    if (!best || distanceM < best.distanceM) best = {
      point: [a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u],
      segmentIndex: index, u, distanceM,
    };
  }
  return best;
}

export function attachNodeToSnowmakingPipe(
  pipe: SavedSnowmakingPipe,
  location: SnowmakingPipeLocation,
  nodeId: string,
): SavedSnowmakingPipe {
  const vertices = pipe.vertices.slice();
  const before = vertices[location.segmentIndex];
  const after = vertices[location.segmentIndex + 1];
  if (location.u <= 1e-6 || haversineMeters(before.point, location.point) < POINT_EPSILON_M) {
    vertices[location.segmentIndex] = { ...before, point: location.point, nodeId };
  } else if (location.u >= 1 - 1e-6 || haversineMeters(after.point, location.point) < POINT_EPSILON_M) {
    vertices[location.segmentIndex + 1] = { ...after, point: location.point, nodeId };
  } else {
    const elevM = before.elevM != null && after.elevM != null
      ? before.elevM + (after.elevM - before.elevM) * location.u : null;
    vertices.splice(location.segmentIndex + 1, 0, { point: location.point, elevM, nodeId });
  }
  return { ...pipe, vertices, ...snowmakingPipeStats(vertices) };
}

function sanitizeVertex(raw: unknown): SavedSnowmakingPipeVertex | null {
  if (!raw || typeof raw !== 'object') return null;
  const value = raw as Record<string, unknown>;
  if (!isPoint(value.point)) return null;
  return {
    point: value.point,
    elevM: typeof value.elevM === 'number' && Number.isFinite(value.elevM) ? value.elevM : null,
    nodeId: typeof value.nodeId === 'string' && value.nodeId ? value.nodeId : null,
  };
}

export function sanitizeSnowmakingPipes(
  raw: unknown[],
  nodes: readonly SavedSnowmakingNode[],
): SavedSnowmakingPipe[] {
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const usedIds = new Set(nodes.map((node) => node.id));
  const pipes: SavedSnowmakingPipe[] = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const value = item as Record<string, unknown>;
    if (typeof value.id !== 'string' || !value.id || usedIds.has(value.id)) continue;
    if (typeof value.name !== 'string' || typeof value.createdAt !== 'string') continue;
    if (!isSnowmakingPipeDiameter(value.diameterIn) || !Array.isArray(value.vertices)) continue;
    const vertices: SavedSnowmakingPipeVertex[] = [];
    for (const candidate of value.vertices) {
      const vertex = sanitizeVertex(candidate);
      if (!vertex) continue;
      const previous = vertices.at(-1);
      if (previous && haversineMeters(previous.point, vertex.point) < POINT_EPSILON_M) {
        if (!previous.nodeId && vertex.nodeId) previous.nodeId = vertex.nodeId;
        continue;
      }
      if (vertex.nodeId) {
        const node = nodeById.get(vertex.nodeId);
        if (!node || haversineMeters(node.point, vertex.point) >= POINT_EPSILON_M) vertex.nodeId = null;
        else vertex.point = node.point;
      }
      vertices.push(vertex);
    }
    if (vertices.length < 2) continue;
    usedIds.add(value.id);
    pipes.push({ id: value.id, name: value.name, diameterIn: value.diameterIn,
      vertices, ...snowmakingPipeStats(vertices), createdAt: value.createdAt });
  }
  return pipes;
}

function sanitizedNext(value: unknown): number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0 ? value : 1;
}

export function hydrateSnowmakingNumbering(
  nodes: readonly SavedSnowmakingNode[],
  rawNext: unknown,
): { nodes: SavedSnowmakingNode[]; nextNumbers: SnowmakingNodeNextNumbers } {
  const input = rawNext && typeof rawNext === 'object' ? rawNext as Record<string, unknown> : {};
  const nextNumbers: SnowmakingNodeNextNumbers = {
    hydrant: sanitizedNext(input.hydrant),
    junction: sanitizedNext(input.junction),
    pump: sanitizedNext(input.pump),
  };
  const used: Record<NumberedSnowmakingNodeKind, Set<number>> = {
    hydrant: new Set(), junction: new Set(), pump: new Set(),
  };
  const numbered = nodes.map((node) => {
    if (node.kind === 'intake') {
      if (node.labelNumber == null) return node;
      const { labelNumber: _labelNumber, ...intake } = node;
      return intake;
    }
    const kind = node.kind;
    const valid = node.labelNumber != null && Number.isSafeInteger(node.labelNumber) &&
      node.labelNumber > 0 && !used[kind].has(node.labelNumber);
    let labelNumber = valid ? node.labelNumber as number : nextNumbers[kind];
    while (used[kind].has(labelNumber)) labelNumber += 1;
    used[kind].add(labelNumber);
    nextNumbers[kind] = Math.max(nextNumbers[kind], labelNumber + 1);
    return node.labelNumber === labelNumber ? node : { ...node, labelNumber };
  });
  return { nodes: numbered, nextNumbers };
}

export function hydrateSnowmakingNetwork(
  nodes: readonly SavedSnowmakingNode[],
  rawPipes: unknown[],
  rawNext: unknown,
): SnowmakingNetworkState {
  const numbered = hydrateSnowmakingNumbering(nodes, rawNext);
  return {
    nodes: numbered.nodes,
    pipes: sanitizeSnowmakingPipes(rawPipes, numbered.nodes),
    nextNumbers: numbered.nextNumbers,
  };
}

export function allocateSnowmakingNode(
  state: SnowmakingNetworkState,
  input: Omit<SavedSnowmakingNode, 'labelNumber' | 'name'> & { kind: NumberedSnowmakingNodeKind;
    name?: string },
): { state: SnowmakingNetworkState; node: SavedSnowmakingNode } {
  const number = state.nextNumbers[input.kind];
  const node: SavedSnowmakingNode = {
    ...input,
    name: input.name?.trim() || defaultSnowmakingNodeName(input.kind, number),
    labelNumber: number,
  };
  return {
    node,
    state: {
      ...state,
      nodes: [...state.nodes, node],
      nextNumbers: { ...state.nextNumbers, [input.kind]: number + 1 },
    },
  };
}

export function nodeReferenceCounts(pipes: readonly SavedSnowmakingPipe[]): Map<string, Set<string>> {
  const counts = new Map<string, Set<string>>();
  for (const pipe of pipes) for (const vertex of pipe.vertices) if (vertex.nodeId) {
    const pipeIds = counts.get(vertex.nodeId) ?? new Set<string>();
    pipeIds.add(pipe.id);
    counts.set(vertex.nodeId, pipeIds);
  }
  return counts;
}

export function detachSnowmakingNode(
  pipes: readonly SavedSnowmakingPipe[],
  nodeId: string,
): SavedSnowmakingPipe[] {
  let changed = false;
  const next = pipes.map((pipe) => {
    if (!pipe.vertices.some((vertex) => vertex.nodeId === nodeId)) return pipe;
    changed = true;
    return { ...pipe, vertices: pipe.vertices.map((vertex) =>
      vertex.nodeId === nodeId ? { ...vertex, nodeId: null } : vertex) };
  });
  return changed ? next : pipes as SavedSnowmakingPipe[];
}

export function pruneAffectedJunctions(
  state: SnowmakingNetworkState,
  candidates: ReadonlySet<string>,
): SnowmakingNetworkState {
  if (candidates.size === 0) return state;
  const counts = nodeReferenceCounts(state.pipes);
  const removed = new Set(state.nodes.filter((node) => node.kind === 'junction' &&
    candidates.has(node.id) && (counts.get(node.id)?.size ?? 0) < 2).map((node) => node.id));
  if (removed.size === 0) return state;
  return {
    ...state,
    nodes: state.nodes.filter((node) => !removed.has(node.id)),
    pipes: state.pipes.map((pipe) => ({ ...pipe, vertices: pipe.vertices.map((vertex) =>
      vertex.nodeId && removed.has(vertex.nodeId) ? { ...vertex, nodeId: null } : vertex) })),
  };
}

/** Future hydraulic solvers consume spans derived from the editable route. */
export function snowmakingPipeSpans(pipe: SavedSnowmakingPipe): SavedSnowmakingPipeVertex[][] {
  if (pipe.vertices.length < 2) return [];
  const breaks = new Set<number>([0, pipe.vertices.length - 1]);
  pipe.vertices.forEach((vertex, index) => { if (vertex.nodeId) breaks.add(index); });
  const indices = [...breaks].sort((left, right) => left - right);
  const spans: SavedSnowmakingPipeVertex[][] = [];
  for (let index = 1; index < indices.length; index += 1) {
    if (indices[index] > indices[index - 1]) {
      spans.push(pipe.vertices.slice(indices[index - 1], indices[index] + 1));
    }
  }
  return spans;
}
