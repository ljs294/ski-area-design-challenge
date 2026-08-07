import { useEffect, useMemo, useReducer, useRef, useState, type MutableRefObject } from 'react';
import maplibregl from 'maplibre-gl';
import { haversineMeters } from '../geo';
import {
  allocateSnowmakingNode,
  attachNodeToSnowmakingPipe,
  buildSnowmakingPipe,
  closestSnowmakingPipeLocation,
  DEFAULT_SNOWMAKING_PIPE_DIAMETER_IN,
  densifySnowmakingPipe,
  detachSnowmakingNode,
  hydrateSnowmakingNumbering,
  isSnowmakingPipeDiameter,
  nextSnowmakingPipeName,
  pruneAffectedJunctions,
  snowmakingNodeLabel,
  snowmakingPipeStats,
  type SnowmakingNetworkState,
  type SnowmakingPipeStats,
} from '../snowmakingNetwork';
import { reconcileSnowmakingNodes } from '../snowmakingNodes';
import type { SavedDam, SavedPond, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingLakeSource, SnowmakingPipeDiameterIn } from '../types/snowmaking';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution,
  type MapVisibilityDescriptor } from './mapContribution';
import { setSelectedSnowmakingFeature, setSnowmakingCaptureTransient, setSnowmakingData,
  setSnowmakingDraftData, addSnowmakingLayers, SNOWMAKING_BUILT_LAYER_IDS,
  SNOWMAKING_HIT_LAYERS } from './snowmakingLayers';
import { reduceSnowmakingNodeTool, reduceSnowmakingPipeTool,
  IDLE_SNOWMAKING_NODE_TOOL, IDLE_SNOWMAKING_PIPE_TOOL,
  snowmakingPipePreview,
  type SnowmakingNodeCandidate, type SnowmakingNodeTool, type SnowmakingPipeTool,
  type SnowmakingSnapIntent } from './snowmakingNetworkControllerModel';
import { snowmakingNetworkProjection, type SnowmakingNetworkDocument } from './snowmakingNetworkDocument';
import type { ToolId } from './toolCoordinator';

const SNAP_TOLERANCE_PX = 16;
const TARGET_REVALIDATE_M = 2;

export type SnowmakingSelection = { kind: 'node' | 'pipe'; id: string } | null;

export interface SnowmakingNetworkControllerOptions {
  mapRef: MutableRefObject<maplibregl.Map | null>;
  dams: readonly SavedDam[];
  ponds: readonly SavedPond[];
  lakes: readonly SnowmakingLakeSource[] | null;
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  selected: SnowmakingSelection;
  network: SnowmakingNetworkDocument;
  canArm(): boolean;
  activate(tool: Extract<ToolId, 'snowmaking-pipe' | 'snowmaking-node'>): boolean;
  release(tool: Extract<ToolId, 'snowmaking-pipe' | 'snowmaking-node'>): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(tool: Extract<ToolId, 'snowmaking-pipe' | 'snowmaking-node'>,
    map: maplibregl.Map): MapInteractionLeaseHandle;
  selectNode(id: string): void;
  selectPipe(id: string): void;
  clearSelected(id: string): void;
  createId(): string;
  now(): string;
  sampleElevation(point: [number, number]): number | null;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

export interface SnowmakingNetworkController {
  readonly contribution: ManagedMapContribution;
  readonly pipeTool: SnowmakingPipeTool;
  readonly nodeTool: SnowmakingNodeTool;
  readonly snapping: boolean;
  readonly diameterIn: SnowmakingPipeDiameterIn;
  readonly previewStats: SnowmakingPipeStats | null;
  readonly nodeCandidateTarget: string | null;
  armPipe(): void;
  cancelPipe(): void;
  undoPipe(): void;
  finishPipe(): void;
  confirmPipe(): void;
  renameDraftPipe(name: string): void;
  setDiameter(value: SnowmakingPipeDiameterIn): void;
  setSnapping(value: boolean): void;
  armNode(kind: 'pump' | 'hydrant'): void;
  cancelNode(): void;
  confirmNode(): void;
  selectNode(id: string): void;
  selectPipe(id: string): void;
  renameNode(id: string, name: string): void;
  removeNode(id: string): void;
  patchPipe(id: string, patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>): void;
  removePipe(id: string): void;
}

function projectedDistance(a: maplibregl.Point, b: maplibregl.Point): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

function snapAt(map: maplibregl.Map, point: [number, number],
  nodes: readonly SavedSnowmakingNode[], pipes: readonly SavedSnowmakingPipe[]): SnowmakingSnapIntent | null {
  const cursor = map.project(point);
  let nodeBest: { node: SavedSnowmakingNode; distance: number } | null = null;
  for (const node of nodes) {
    const distance = projectedDistance(cursor, map.project(node.point));
    if (distance <= SNAP_TOLERANCE_PX && (!nodeBest || distance < nodeBest.distance)) {
      nodeBest = { node, distance };
    }
  }
  if (nodeBest) return { kind: 'node', nodeId: nodeBest.node.id, point: nodeBest.node.point };

  let pipeBest: { pipeId: string; point: [number, number]; distance: number } | null = null;
  for (const pipe of pipes) for (let index = 0; index < pipe.vertices.length - 1; index += 1) {
    const aLngLat = pipe.vertices[index].point;
    const bLngLat = pipe.vertices[index + 1].point;
    const a = map.project(aLngLat), b = map.project(bLngLat);
    const dx = b.x - a.x, dy = b.y - a.y;
    const lengthSquared = dx * dx + dy * dy;
    const u = lengthSquared > 0 ? Math.max(0, Math.min(1,
      ((cursor.x - a.x) * dx + (cursor.y - a.y) * dy) / lengthSquared)) : 0;
    const projected = new maplibregl.Point(a.x + dx * u, a.y + dy * u);
    const distance = projectedDistance(cursor, projected);
    if (distance <= SNAP_TOLERANCE_PX && (!pipeBest || distance < pipeBest.distance)) {
      pipeBest = { pipeId: pipe.id,
        point: [aLngLat[0] + (bLngLat[0] - aLngLat[0]) * u,
          aLngLat[1] + (bLngLat[1] - aLngLat[1]) * u], distance };
    }
  }
  return pipeBest ? { kind: 'pipe', pipeId: pipeBest.pipeId, point: pipeBest.point } : null;
}

function replacePipe(state: SnowmakingNetworkState, pipe: SavedSnowmakingPipe): SnowmakingNetworkState {
  return { ...state, pipes: state.pipes.map((candidate) => candidate.id === pipe.id ? pipe : candidate) };
}

function pipeElevationAt(pipe: SavedSnowmakingPipe, segmentIndex: number, u: number): number | null {
  const a = pipe.vertices[segmentIndex]?.elevM, b = pipe.vertices[segmentIndex + 1]?.elevM;
  return a != null && b != null ? a + (b - a) * u : null;
}

export function useSnowmakingNetworkController(
  options: SnowmakingNetworkControllerOptions,
): SnowmakingNetworkController {
  const [pipeTool, pipeDispatch] = useReducer(reduceSnowmakingPipeTool, IDLE_SNOWMAKING_PIPE_TOOL);
  const [nodeTool, nodeDispatch] = useReducer(reduceSnowmakingNodeTool, IDLE_SNOWMAKING_NODE_TOOL);
  const [snapping, setSnapping] = useState(false);
  const [diameterIn, setDiameter] = useState<SnowmakingPipeDiameterIn>(
    DEFAULT_SNOWMAKING_PIPE_DIAMETER_IN);
  const [snapHover, setSnapHover] = useState<[number, number] | null>(null);
  const snapHoverRef = useRef<[number, number] | null>(snapHover);
  const optionsRef = useRef(options);
  const pipeToolRef = useRef(pipeTool);
  const nodeToolRef = useRef(nodeTool);
  optionsRef.current = options;
  pipeToolRef.current = pipeTool;
  nodeToolRef.current = nodeTool;
  snapHoverRef.current = snapHover;

  const previewStats = useMemo(() => {
    if (pipeTool.phase !== 'drawing') return null;
    const points = pipeTool.points.map((draft) => draft.point);
    if (pipeTool.cursor) points.push(pipeTool.cursor);
    if (points.length < 2) return { lengthM: 0, verticalM: null };
    return snowmakingPipeStats(densifySnowmakingPipe(points, options.sampleElevation));
  }, [pipeTool, options.sampleElevation]);

  const candidateSnap = nodeTool.phase === 'placing' ? nodeTool.candidate?.snap ?? null : null;
  const nodeCandidateTarget = candidateSnap?.kind === 'node'
    ? options.nodes.find((node) => node.id === candidateSnap.nodeId)?.name ?? null
    : candidateSnap?.kind === 'pipe'
      ? options.pipes.find((pipe) => pipe.id === candidateSnap.pipeId)?.name ?? null : null;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'snowmaking', zOrder: MAP_Z_ORDER.snowmaking,
    hits: [{ id: 'snowmaking', priority: MAP_HIT_RANK.snowmaking,
      layerIds: SNOWMAKING_HIT_LAYERS,
      select: (id) => {
        if (optionsRef.current.pipes.some((pipe) => pipe.id === id)) optionsRef.current.selectPipe(id);
        else if (optionsRef.current.nodes.some((node) => node.id === id)) optionsRef.current.selectNode(id);
      } }],
    install: ({ map }) => addSnowmakingLayers(map),
    synchronizeData: ({ map }) => {
      setSnowmakingData(map, optionsRef.current.nodes, optionsRef.current.pipes);
      setSelectedSnowmakingFeature(map, optionsRef.current.selected);
      synchronizeDraft(map);
    },
    visibility: (): MapVisibilityDescriptor[] => optionsRef.current.structuresVisible()
      ? [{ id: 'snowmaking-network', label: 'Snowmaking network',
        layerIds: SNOWMAKING_BUILT_LAYER_IDS, visible: true, section: 'Structures' }]
      : [],
    setCaptureTransient: ({ map }, hidden) => setSnowmakingCaptureTransient(map, hidden),
    cleanup: () => {},
  };

  function synchronizeDraft(map = optionsRef.current.mapRef.current): void {
    const current = pipeToolRef.current;
    const preview = snowmakingPipePreview(current);
    setSnowmakingDraftData(map, preview ? {
      ...preview,
      snapPoint: current.phase === 'drawing' ? snapHoverRef.current : null,
    } : { points: [], cursor: null, snapPoint: snapHoverRef.current });
  }

  useEffect(() => {
    const current = optionsRef.current;
    if (current.lakes === null) return;
    const before = snowmakingNetworkProjection(current.network.snapshot());
    const reconciled = reconcileSnowmakingNodes(before.nodes, [...current.dams], [...current.ponds],
      [...current.lakes]);
    if (reconciled === before.nodes) return;
    const removed = new Set(before.nodes.filter((node) => !reconciled.some((next) => next.id === node.id))
      .map((node) => node.id));
    const numbered = hydrateSnowmakingNumbering(reconciled, before.nextNumbers);
    let pipes = before.pipes;
    for (const id of removed) pipes = detachSnowmakingNode(pipes, id);
    const edit = current.network.begin();
    edit.replace({ nodes: numbered.nodes, pipes, nextNumbers: numbered.nextNumbers });
    if (edit.commit().ok) for (const id of removed) current.clearSelected(id);
  }, [options.dams, options.ponds, options.lakes, options.network]);

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [options.nodes, options.pipes, options.selected]);
  useEffect(() => { synchronizeDraft(); }, [pipeTool, nodeTool, snapHover]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (pipeTool.phase !== 'armed' && pipeTool.phase !== 'drawing')) return;
    const interaction = optionsRef.current.acquireInteractions('snowmaking-pipe', map);
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes) : null;
      const point = snap?.point ?? raw;
      setSnapHover(snap?.point ?? null);
      if (pipeToolRef.current.phase === 'drawing') pipeDispatch({ type: 'move', point, snap });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes) : null;
      const point = snap?.point ?? raw;
      const current = pipeToolRef.current;
      const last = current.phase === 'drawing' ? current.points.at(-1)?.point : null;
      if (!last || haversineMeters(last, point) >= 1) pipeDispatch({ type: 'add', point: { point, snap } });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelPipe();
      else if (event.key === 'Backspace') { event.preventDefault(); pipeDispatch({ type: 'undo' }); }
      else if (event.key === 'Enter') finishPipe();
    };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); setSnapHover(null); };
  }, [pipeTool.phase, snapping]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || nodeTool.phase !== 'placing') return;
    const interaction = optionsRef.current.acquireInteractions('snowmaking-node', map);
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes) : null;
      setSnapHover(snap?.point ?? null);
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes) : null;
      if (snap?.kind === 'node') {
        nodeDispatch({ type: 'candidate', candidate: null,
          error: `${snowmakingNodeLabel(optionsRef.current.nodes.find((node) => node.id === snap.nodeId)!)} already occupies that location.` });
        return;
      }
      const point = snap?.point ?? raw;
      const candidate: SnowmakingNodeCandidate = { point, snap,
        elevM: optionsRef.current.sampleElevation(point) };
      nodeDispatch({ type: 'candidate', candidate, error: null });
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') cancelNode(); };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); setSnapHover(null); };
  }, [nodeTool.phase, snapping]);

  useEffect(() => () => {
    optionsRef.current.release('snowmaking-pipe');
    optionsRef.current.release('snowmaking-node');
  }, []);

  function armPipe(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate('snowmaking-pipe')) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock(); pipeDispatch({ type: 'arm' });
  }
  function cancelPipe(): void { pipeDispatch({ type: 'cancel' }); setSnapHover(null);
    optionsRef.current.release('snowmaking-pipe'); }
  function finishPipe(): void {
    const current = pipeToolRef.current;
    if (current.phase !== 'drawing' || current.points.length < 2) return;
    pipeDispatch({ type: 'review', name: nextSnowmakingPipeName(optionsRef.current.pipes) });
  }
  function renameDraftPipe(name: string): void {
    pipeDispatch({ type: 'rename', name });
  }

  function resolvePipeDraft(current: Extract<SnowmakingPipeTool, { phase: 'review' }>):
  { state: SnowmakingNetworkState; points: [number, number][]; nodeIds: (string | null)[] } | string {
    let state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const points: [number, number][] = [];
    const nodeIds: (string | null)[] = [];
    for (const draft of current.points) {
      const snap = draft.snap;
      if (!snap) { points.push(draft.point); nodeIds.push(null); continue; }
      if (snap.kind === 'node') {
        const resolved = state.nodes.find((candidate) => candidate.id === snap.nodeId);
        if (!resolved) return 'A snapped node changed before this pipe was installed. Pick the connection again.';
        points.push(resolved.point); nodeIds.push(resolved.id); continue;
      }
      const pipeIndex = state.pipes.findIndex((pipe) => pipe.id === snap.pipeId);
      if (pipeIndex < 0) return 'A snapped pipe changed before this pipe was installed. Pick the connection again.';
      const target = state.pipes[pipeIndex];
      const location = closestSnowmakingPipeLocation(target, snap.point);
      if (!location || location.distanceM > TARGET_REVALIDATE_M) return 'A snapped connection is no longer available.';
      const existing = state.nodes.find((node) => haversineMeters(node.point, location.point) < 0.05);
      if (existing) {
        points.push(existing.point); nodeIds.push(existing.id); continue;
      }
      const elevM = pipeElevationAt(target, location.segmentIndex, location.u);
      const allocation = allocateSnowmakingNode(state, { id: optionsRef.current.createId(),
        kind: 'junction', point: location.point, elevM, createdAt: optionsRef.current.now() });
      state = allocation.state;
      state = replacePipe(state, attachNodeToSnowmakingPipe(target, location, allocation.node.id));
      points.push(location.point); nodeIds.push(allocation.node.id);
    }
    return { state, points, nodeIds };
  }

  function confirmPipe(): void {
    const current = pipeToolRef.current;
    if (current.phase !== 'review') return;
    const resolved = resolvePipeDraft(current);
    if (typeof resolved === 'string') { pipeDispatch({ type: 'review-error', error: resolved }); return; }
    const pipe = buildSnowmakingPipe({ id: optionsRef.current.createId(),
      name: current.name.trim() || nextSnowmakingPipeName(resolved.state.pipes), diameterIn,
      points: resolved.points, nodeIds: resolved.nodeIds, createdAt: optionsRef.current.now() },
    optionsRef.current.sampleElevation);
    if (pipe.vertices.length < 2) { pipeDispatch({ type: 'review-error',
      error: 'Add at least two distinct pipe points.' }); return; }
    const edit = optionsRef.current.network.begin();
    edit.replace({ ...resolved.state, pipes: [...resolved.state.pipes, pipe] });
    const result = edit.commit();
    if (!result.ok) { pipeDispatch({ type: 'review-error',
      error: 'The network changed before installation. Review the pipe and try again.' }); return; }
    cancelPipe();
  }

  function armNode(kind: 'pump' | 'hydrant'): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate('snowmaking-node')) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock(); nodeDispatch({ type: 'arm', kind });
  }
  function cancelNode(): void { nodeDispatch({ type: 'cancel' }); setSnapHover(null);
    optionsRef.current.release('snowmaking-node'); }
  function confirmNode(): void {
    const current = nodeToolRef.current;
    if (current.phase !== 'placing' || !current.candidate) return;
    let state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    let point = current.candidate.point;
    let elevM = current.candidate.elevM;
    let targetPipe: SavedSnowmakingPipe | null = null;
    let targetLocation: ReturnType<typeof closestSnowmakingPipeLocation> = null;
    if (current.candidate.snap?.kind === 'pipe') {
      const snap = current.candidate.snap;
      targetPipe = state.pipes.find((pipe) => pipe.id === snap.pipeId) ?? null;
      targetLocation = targetPipe
        ? closestSnowmakingPipeLocation(targetPipe, snap.point) : null;
      if (!targetPipe || !targetLocation || targetLocation.distanceM > TARGET_REVALIDATE_M) {
        nodeDispatch({ type: 'candidate', candidate: current.candidate,
          error: 'That pipe changed. Pick the device location again.' }); return;
      }
      point = targetLocation.point;
      elevM = pipeElevationAt(targetPipe, targetLocation.segmentIndex, targetLocation.u);
    }
    if (state.nodes.some((node) => haversineMeters(node.point, point) < 0.05)) {
      nodeDispatch({ type: 'candidate', candidate: null,
        error: 'A network node already occupies that location.' }); return;
    }
    const allocation = allocateSnowmakingNode(state, { id: optionsRef.current.createId(),
      kind: current.kind, point, elevM, createdAt: optionsRef.current.now() });
    state = allocation.state;
    if (targetPipe && targetLocation) {
      state = replacePipe(state, attachNodeToSnowmakingPipe(targetPipe, targetLocation, allocation.node.id));
    }
    const edit = optionsRef.current.network.begin(); edit.replace(state);
    if (!edit.commit().ok) { nodeDispatch({ type: 'candidate', candidate: current.candidate,
      error: 'The network changed. Pick the location again.' }); return; }
    nodeDispatch({ type: 'committed' });
  }

  function renameNode(id: string, name: string): void {
    const state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const node = state.nodes.find((candidate) => candidate.id === id);
    if (!node || node.kind === 'junction') return;
    const edit = optionsRef.current.network.begin(); edit.replace({ ...state,
      nodes: state.nodes.map((candidate) => candidate.id === id ? { ...candidate, name } : candidate) });
    edit.commit();
  }
  function removeNode(id: string): void {
    let state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const node = state.nodes.find((candidate) => candidate.id === id);
    if (!node || node.kind === 'intake' || node.kind === 'junction') return;
    state = { ...state, nodes: state.nodes.filter((candidate) => candidate.id !== id),
      pipes: detachSnowmakingNode(state.pipes, id) };
    const edit = optionsRef.current.network.begin(); edit.replace(state);
    if (edit.commit().ok) optionsRef.current.clearSelected(id);
  }
  function patchPipe(id: string,
    patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>): void {
    const state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    if (!state.pipes.some((pipe) => pipe.id === id)) return;
    if (patch.diameterIn != null && !isSnowmakingPipeDiameter(patch.diameterIn)) return;
    const edit = optionsRef.current.network.begin(); edit.replace({ ...state,
      pipes: state.pipes.map((pipe) => pipe.id === id ? { ...pipe, ...patch } : pipe) });
    edit.commit();
  }
  function removePipe(id: string): void {
    let state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const removed = state.pipes.find((pipe) => pipe.id === id);
    if (!removed) return;
    const candidates = new Set(removed.vertices.flatMap((vertex) => vertex.nodeId ? [vertex.nodeId] : []));
    state = pruneAffectedJunctions({ ...state, pipes: state.pipes.filter((pipe) => pipe.id !== id) },
      candidates);
    const edit = optionsRef.current.network.begin(); edit.replace(state);
    if (edit.commit().ok) optionsRef.current.clearSelected(id);
  }

  return {
    contribution: contributionRef.current, pipeTool, nodeTool, snapping, diameterIn,
    previewStats, nodeCandidateTarget, armPipe, cancelPipe,
    undoPipe: () => pipeDispatch({ type: 'undo' }), finishPipe, confirmPipe, renameDraftPipe,
    setDiameter, setSnapping, armNode, cancelNode, confirmNode,
    selectNode: options.selectNode, selectPipe: options.selectPipe,
    renameNode, removeNode, patchPipe, removePipe,
  };
}
