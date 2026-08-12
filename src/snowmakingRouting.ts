import { haversineMeters } from './geo';
import { snowmakingPipeSegments, type SnowmakingPipeSegment } from './snowmakingNetwork';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe } from './types/snowmaking';

const FEET_PER_METER = 3.280839895013123;
const DISTANCE_EPSILON_FT = 1e-8;

export interface SnowmakingRoutingPumpSetting {
  on: boolean;
}

export interface SnowmakingRoutingInput {
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  selectedGunIds: readonly string[];
  selectedIntakeNodeIds: readonly string[];
  pumpSettings: Readonly<Record<string, SnowmakingRoutingPumpSetting | undefined>>;
}

export type SnowmakingRoutingDiagnosticCode =
  | 'unknown-gun'
  | 'disconnected-gun'
  | 'missing-source'
  | 'unconfigured-pump-ports'
  | 'pump-direction-blocks-route'
  | 'unroutable-gun';

export interface SnowmakingRoutingDiagnostic {
  code: SnowmakingRoutingDiagnosticCode;
  message: string;
  entityId?: string;
  componentId?: string;
}

export interface SnowmakingRoutingTree {
  systemId: string;
  componentId: string;
  intakeNodeId: string;
  /** Guns are retained in closest-first attachment order. */
  gunIds: string[];
  segmentIds: string[];
  pumpNodeIds: string[];
}

export interface SnowmakingRoutingFailure {
  systemId: string;
  componentId: string;
  gunIds: string[];
  diagnostics: SnowmakingRoutingDiagnostic[];
}

export interface SnowmakingRoutingForest {
  trees: SnowmakingRoutingTree[];
  failures: SnowmakingRoutingFailure[];
}

interface PhysicalEdge {
  id: string;
  a: string;
  b: string;
  segment: SnowmakingPipeSegment;
  lengthFt: number;
}

interface PhysicalComponent {
  id: string;
  keys: Set<string>;
  segmentIds: Set<string>;
  pumpNodeIds: Set<string>;
}

interface RoutingArc {
  id: string;
  to: string;
  lengthFt: number;
  segmentId: string | null;
  pumpNodeId: string | null;
}

interface SearchResult {
  distanceByKey: Map<string, number>;
  ownerByKey: Map<string, string>;
  previousByKey: Map<string, { from: string; arc: RoutingArc }>;
}

function physicalEndpoint(segment: SnowmakingPipeSegment, side: 'a' | 'b'): string {
  const nodeId = side === 'a' ? segment.fromNodeId : segment.toNodeId;
  return nodeId ?? `pipe:${segment.pipeId}:segment:${segment.id}:${side}`;
}

function segmentLengthFt(segment: SnowmakingPipeSegment): number {
  let lengthM = 0;
  for (let index = 1; index < segment.vertices.length; index += 1) {
    const before = segment.vertices[index - 1], after = segment.vertices[index];
    lengthM += Math.hypot(haversineMeters(before.point, after.point),
      (after.elevM ?? before.elevM ?? 0) - (before.elevM ?? after.elevM ?? 0));
  }
  return Math.max(lengthM * FEET_PER_METER, 1e-9);
}

function physicalComponents(edges: readonly PhysicalEdge[],
  nodeById: ReadonlyMap<string, SavedSnowmakingNode>): PhysicalComponent[] {
  const adjacency = new Map<string, PhysicalEdge[]>();
  for (const edge of edges) {
    adjacency.set(edge.a, [...(adjacency.get(edge.a) ?? []), edge]);
    adjacency.set(edge.b, [...(adjacency.get(edge.b) ?? []), edge]);
  }
  const unseen = new Set(adjacency.keys()), result: PhysicalComponent[] = [];
  while (unseen.size) {
    const start = [...unseen].sort()[0], keys = new Set<string>();
    const segmentIds = new Set<string>(), pumpNodeIds = new Set<string>(), stack = [start];
    while (stack.length) {
      const key = stack.pop()!;
      if (keys.has(key)) continue;
      keys.add(key); unseen.delete(key);
      if (nodeById.get(key)?.kind === 'pump') pumpNodeIds.add(key);
      for (const edge of adjacency.get(key) ?? []) {
        segmentIds.add(edge.id);
        stack.push(edge.a === key ? edge.b : edge.a);
      }
    }
    result.push({ id: [...keys].sort()[0], keys, segmentIds, pumpNodeIds });
  }
  return result.sort((left, right) => left.id.localeCompare(right.id));
}

function routingEndpoint(segment: SnowmakingPipeSegment, side: 'a' | 'b',
  nodeById: ReadonlyMap<string, SavedSnowmakingNode>,
  pumpSettings: SnowmakingRoutingInput['pumpSettings']): string {
  const physical = physicalEndpoint(segment, side), node = nodeById.get(physical);
  if (node?.kind !== 'pump') return physical;
  const port = side === 'a' ? segment.startPumpPort : segment.endPumpPort;
  if (!port) return `pump:${node.id}:unassigned:${segment.id}:${side}`;
  if (!pumpSettings[node.id]?.on) return `pump:${node.id}:passive`;
  return `pump:${node.id}:${port}`;
}

function addArc(adjacency: Map<string, RoutingArc[]>, from: string, arc: RoutingArc): void {
  adjacency.set(from, [...(adjacency.get(from) ?? []), arc]);
}

function buildRoutingAdjacency(edges: readonly PhysicalEdge[],
  nodeById: ReadonlyMap<string, SavedSnowmakingNode>,
  pumpSettings: SnowmakingRoutingInput['pumpSettings']): Map<string, RoutingArc[]> {
  const adjacency = new Map<string, RoutingArc[]>(), activePumpBuses = new Set<string>();
  for (const edge of edges) {
    const a = routingEndpoint(edge.segment, 'a', nodeById, pumpSettings);
    const b = routingEndpoint(edge.segment, 'b', nodeById, pumpSettings);
    addArc(adjacency, a, { id: `${edge.id}:a-b`, to: b, lengthFt: edge.lengthFt,
      segmentId: edge.id, pumpNodeId: null });
    addArc(adjacency, b, { id: `${edge.id}:b-a`, to: a, lengthFt: edge.lengthFt,
      segmentId: edge.id, pumpNodeId: null });
    for (const nodeId of [edge.segment.fromNodeId, edge.segment.toNodeId])
      if (nodeId && nodeById.get(nodeId)?.kind === 'pump' && pumpSettings[nodeId]?.on)
        activePumpBuses.add(nodeId);
  }
  for (const pumpId of [...activePumpBuses].sort()) {
    const suction = `pump:${pumpId}:suction`, discharge = `pump:${pumpId}:discharge`;
    addArc(adjacency, suction, { id: `pump:${pumpId}:forward`, to: discharge,
      lengthFt: 0, segmentId: null, pumpNodeId: pumpId });
  }
  for (const arcs of adjacency.values()) arcs.sort((left, right) => left.id.localeCompare(right.id));
  return adjacency;
}

function compareSearchCandidate(distance: number, owner: string, from: string, arcId: string,
  existingDistance: number | undefined, existingOwner: string | undefined,
  existingPrevious: { from: string; arc: RoutingArc } | undefined): number {
  if (existingDistance == null || distance < existingDistance - DISTANCE_EPSILON_FT) return -1;
  if (distance > existingDistance + DISTANCE_EPSILON_FT) return 1;
  const ownerOrder = owner.localeCompare(existingOwner ?? '');
  if (ownerOrder) return ownerOrder;
  const arcOrder = arcId.localeCompare(existingPrevious?.arc.id ?? '');
  return arcOrder || from.localeCompare(existingPrevious?.from ?? '');
}

function searchFromForest(adjacency: ReadonlyMap<string, RoutingArc[]>,
  activeOwnerByKey: ReadonlyMap<string, string>, allowedSegmentIds: ReadonlySet<string>,
  allowedPumpIds: ReadonlySet<string>): SearchResult {
  const distanceByKey = new Map<string, number>(), ownerByKey = new Map<string, string>();
  const previousByKey = new Map<string, { from: string; arc: RoutingArc }>();
  const unsettled = new Set<string>();
  for (const [key, owner] of [...activeOwnerByKey].sort(([a], [b]) => a.localeCompare(b))) {
    distanceByKey.set(key, 0); ownerByKey.set(key, owner); unsettled.add(key);
  }
  while (unsettled.size) {
    const key = [...unsettled].sort((left, right) =>
      (distanceByKey.get(left)! - distanceByKey.get(right)!) ||
      ownerByKey.get(left)!.localeCompare(ownerByKey.get(right)!) || left.localeCompare(right))[0];
    unsettled.delete(key);
    const distance = distanceByKey.get(key)!, owner = ownerByKey.get(key)!;
    for (const arc of adjacency.get(key) ?? []) {
      if (arc.segmentId && !allowedSegmentIds.has(arc.segmentId)) continue;
      if (arc.pumpNodeId && !allowedPumpIds.has(arc.pumpNodeId)) continue;
      if (activeOwnerByKey.has(arc.to)) continue;
      const nextDistance = distance + arc.lengthFt;
      if (compareSearchCandidate(nextDistance, owner, key, arc.id,
        distanceByKey.get(arc.to), ownerByKey.get(arc.to), previousByKey.get(arc.to)) >= 0) continue;
      distanceByKey.set(arc.to, nextDistance); ownerByKey.set(arc.to, owner);
      previousByKey.set(arc.to, { from: key, arc }); unsettled.add(arc.to);
    }
  }
  return { distanceByKey, ownerByKey, previousByKey };
}

function unconfiguredPumps(component: PhysicalComponent, edges: readonly PhysicalEdge[]): string[] {
  const ids = new Set<string>();
  for (const edge of edges) {
    if (!component.segmentIds.has(edge.id)) continue;
    if (edge.segment.fromNodeId && edge.segment.startPumpPort == null) ids.add(edge.segment.fromNodeId);
    if (edge.segment.toNodeId && edge.segment.endPumpPort == null) ids.add(edge.segment.toNodeId);
  }
  return [...ids].filter((id) => component.pumpNodeIds.has(id)).sort();
}

/**
 * Derive the transient radial network used by both live preview and hydraulics.
 * Every added path touches the existing forest exactly once, so the result is
 * acyclic even when the installed network contains loops or parallel pipes.
 */
export function deriveSnowmakingRoutingForest(input: SnowmakingRoutingInput): SnowmakingRoutingForest {
  const nodeById = new Map(input.nodes.map((node) => [node.id, node]));
  const gunById = new Map(input.guns.map((gun) => [gun.id, gun]));
  const physicalEdges: PhysicalEdge[] = input.pipes.flatMap((pipe) =>
    snowmakingPipeSegments(pipe).map((segment) => ({ id: segment.id,
      a: physicalEndpoint(segment, 'a'), b: physicalEndpoint(segment, 'b'), segment,
      lengthFt: segmentLengthFt(segment) }))).sort((left, right) => left.id.localeCompare(right.id));
  const components = physicalComponents(physicalEdges, nodeById);
  const componentByKey = new Map<string, PhysicalComponent>();
  for (const component of components) for (const key of component.keys) componentByKey.set(key, component);
  const adjacency = buildRoutingAdjacency(physicalEdges, nodeById, input.pumpSettings);
  const failures: SnowmakingRoutingFailure[] = [], gunsByComponent = new Map<string, SavedSnowgun[]>();
  for (const gunId of [...new Set(input.selectedGunIds)]) {
    const gun = gunById.get(gunId);
    if (!gun) {
      failures.push({ systemId: `gun:${gunId}`, componentId: `gun:${gunId}`, gunIds: [gunId],
        diagnostics: [{ code: 'unknown-gun', message: 'A selected snowgun no longer exists.',
          entityId: gunId, componentId: `gun:${gunId}` }] });
      continue;
    }
    const component = gun.hydrantId ? componentByKey.get(gun.hydrantId) : null;
    if (!component) {
      failures.push({ systemId: `gun:${gun.id}`, componentId: `gun:${gun.id}`, gunIds: [gun.id],
        diagnostics: [{ code: 'disconnected-gun',
          message: 'A selected snowgun is not connected to a pipe hydrant.', entityId: gun.id,
          componentId: `gun:${gun.id}` }] });
      continue;
    }
    gunsByComponent.set(component.id, [...(gunsByComponent.get(component.id) ?? []), gun]);
  }

  const selectedIntakes = new Set(input.selectedIntakeNodeIds), trees: SnowmakingRoutingTree[] = [];
  for (const component of components) {
    const guns = gunsByComponent.get(component.id);
    if (!guns?.length) continue;
    const sourceIds = [...component.keys].filter((key) => selectedIntakes.has(key) &&
      nodeById.get(key)?.kind === 'intake').sort();
    if (!sourceIds.length) {
      failures.push({ systemId: `${component.id}:unrouted`, componentId: component.id,
        gunIds: guns.map((gun) => gun.id).sort(), diagnostics: [{ code: 'missing-source',
          message: 'Select at least one water source for this system.', componentId: component.id }] });
      continue;
    }
    const activeOwnerByKey = new Map(sourceIds.map((id) => [id, id]));
    const treeBySource = new Map(sourceIds.map((id) => [id, {
      systemId: `${component.id}:source:${id}`, componentId: component.id, intakeNodeId: id,
      gunIds: [] as string[], segmentIds: [] as string[], pumpNodeIds: [] as string[],
    }]));
    const remaining = new Map(guns.map((gun) => [gun.id, gun]));
    while (remaining.size) {
      const search = searchFromForest(adjacency, activeOwnerByKey,
        component.segmentIds, component.pumpNodeIds);
      const reachable = [...remaining.values()].filter((gun) => gun.hydrantId &&
        search.distanceByKey.has(gun.hydrantId)).sort((left, right) =>
        search.distanceByKey.get(left.hydrantId!)! - search.distanceByKey.get(right.hydrantId!)! ||
        search.ownerByKey.get(left.hydrantId!)!.localeCompare(search.ownerByKey.get(right.hydrantId!)!) ||
        left.id.localeCompare(right.id));
      const gun = reachable[0];
      if (!gun?.hydrantId) break;
      const owner = search.ownerByKey.get(gun.hydrantId)!;
      const tree = treeBySource.get(owner)!;
      const path: { from: string; to: string; arc: RoutingArc }[] = [];
      let cursor = gun.hydrantId;
      while (!activeOwnerByKey.has(cursor)) {
        const previous = search.previousByKey.get(cursor);
        if (!previous) break;
        path.push({ from: previous.from, to: cursor, arc: previous.arc }); cursor = previous.from;
      }
      for (const step of path.reverse()) {
        if (step.arc.segmentId && !tree.segmentIds.includes(step.arc.segmentId))
          tree.segmentIds.push(step.arc.segmentId);
        if (step.arc.pumpNodeId && !tree.pumpNodeIds.includes(step.arc.pumpNodeId))
          tree.pumpNodeIds.push(step.arc.pumpNodeId);
        activeOwnerByKey.set(step.from, owner); activeOwnerByKey.set(step.to, owner);
      }
      if (!tree.gunIds.includes(gun.id)) tree.gunIds.push(gun.id);
      remaining.delete(gun.id);
    }
    if (remaining.size) {
      const pumpIds = unconfiguredPumps(component, physicalEdges);
      const diagnostics: SnowmakingRoutingDiagnostic[] = pumpIds.length
        ? pumpIds.map((pumpId) => ({ code: 'unconfigured-pump-ports' as const,
          message: `${nodeById.get(pumpId)?.name ?? 'A pump'} has a pipe with no water direction. Choose where water enters the pump and where the pump pushes it.`,
          entityId: pumpId, componentId: component.id }))
        : component.pumpNodeIds.size && [...component.pumpNodeIds].some((id) => input.pumpSettings[id]?.on)
          ? [{ code: 'pump-direction-blocks-route',
            message: 'An operating pump faces away from the selected source. Reverse its direction or turn it off.',
            entityId: [...component.pumpNodeIds].filter((id) => input.pumpSettings[id]?.on).sort()[0],
            componentId: component.id }]
          : [{ code: 'unroutable-gun',
            message: 'No radial route from a selected source reaches this snowgun.',
            componentId: component.id }];
      failures.push({ systemId: `${component.id}:unrouted`, componentId: component.id,
        gunIds: [...remaining.keys()].sort(), diagnostics });
    }
    for (const tree of treeBySource.values()) if (tree.gunIds.length) {
      const pumpIds = new Set(tree.pumpNodeIds);
      for (const edge of physicalEdges) if (tree.segmentIds.includes(edge.id)) {
        for (const nodeId of [edge.segment.fromNodeId, edge.segment.toNodeId])
          if (nodeId && nodeById.get(nodeId)?.kind === 'pump') pumpIds.add(nodeId);
      }
      tree.segmentIds.sort(); tree.pumpNodeIds = [...pumpIds].sort(); trees.push(tree);
    }
  }
  return { trees: trees.sort((left, right) => left.systemId.localeCompare(right.systemId)),
    failures: failures.sort((left, right) => left.systemId.localeCompare(right.systemId)) };
}
