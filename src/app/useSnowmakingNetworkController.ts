import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type MutableRefObject } from 'react';
import maplibregl from 'maplibre-gl';
import { haversineMeters } from '../geo';
import {
  buildSnowmakingPipe,
  DEFAULT_SNOWMAKING_PIPE_DIAMETER_IN,
  densifySnowmakingPipe,
  detachSnowmakingNode,
  hydrateSnowmakingNumbering,
  isSnowmakingPipeDiameter,
  nextSnowmakingPipeName,
  populateSnowmakingHydrantRun,
  pruneAffectedJunctions,
  setSnowmakingPumpPort,
  snowmakingNodeLabel,
  snowmakingHydrantRunLayout,
  snowmakingPipeIntervalPoints,
  snowmakingPipeStationAt,
  snowmakingPipeStats,
  type SnowmakingHydrantRunLayout, type SnowmakingNetworkState, type SnowmakingPipeStation,
  type SnowmakingPipeStats,
} from '../snowmakingNetwork';
import { reconcileSnowmakingNodes } from '../snowmakingNodes';
import { reconcileSnowgunConnections } from '../snowmakingGuns';
import type { SavedDam, SavedPond, SavedSnowmakingNode, SavedSnowmakingPipe,
  SavedSnowgun, SnowmakingLakeSource, SnowmakingPipeDiameterIn,
  SnowmakingPumpPort } from '../types/snowmaking';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution,
  type MapVisibilityDescriptor } from './mapContribution';
import { setSelectedSnowmakingFeature, setSnowmakingCaptureTransient, setSnowmakingData,
  setSnowmakingDraftData, addSnowmakingLayers, SNOWMAKING_BUILT_LAYER_IDS,
  SNOWMAKING_HIT_LAYERS, SNOWMAKING_HOVER_LAYERS } from './snowmakingLayers';
import { reduceSnowmakingHydrantRunTool, reduceSnowmakingNodeTool, reduceSnowmakingPipeTool,
  IDLE_SNOWMAKING_HYDRANT_RUN_TOOL, IDLE_SNOWMAKING_NODE_TOOL, IDLE_SNOWMAKING_PIPE_TOOL,
  snowmakingPipePreview,
  type SnowmakingHydrantRunPreview, type SnowmakingHydrantRunTool,
  type SnowmakingNodeCandidate, type SnowmakingNodeTool,
  type SnowmakingPipeTool } from './snowmakingNetworkControllerModel';
export type { SnowmakingHydrantRunPreview } from './snowmakingNetworkControllerModel';
import { snowmakingNetworkProjection, type SnowmakingNetworkDocument } from './snowmakingNetworkDocument';
import type { ToolId } from './toolCoordinator';
import { pipeSnapAt, snowmakingSnapAt } from './snowmakingNetworkSnap';
import { applySnowmakingNodeCandidate, inlinePumpCandidate,
  inlinePumpDirectionPoints, resolveSnowmakingPipeDraft } from './snowmakingNodePlacement';
const TARGET_REVALIDATE_M = 2;
export type SnowmakingSelection = { kind: 'node' | 'pipe' | 'gun'; id: string } | null;
export interface SnowmakingNetworkControllerOptions {
  mapRef: MutableRefObject<maplibregl.Map | null>;
  dams: readonly SavedDam[];
  ponds: readonly SavedPond[];
  lakes: readonly SnowmakingLakeSource[] | null;
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
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
  selectGun(id: string): void;
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
  readonly hydrantRunTool: SnowmakingHydrantRunTool;
  readonly snapping: boolean;
  readonly diameterIn: SnowmakingPipeDiameterIn;
  readonly previewStats: SnowmakingPipeStats | null;
  readonly nodeCandidateTarget: string | null;
  readonly hydrantRunPreview: SnowmakingHydrantRunPreview | null;
  armPipe(): void;
  cancelPipe(): void;
  undoPipe(): void;
  finishPipe(): void;
  confirmPipe(): void;
  renameDraftPipe(name: string): void;
  setDiameter(value: SnowmakingPipeDiameterIn): void;
  setSnapping(value: boolean): void;
  setPumpSuctionSide(side: 'route-start' | 'route-end'): void;
  armNode(kind: 'pump' | 'hydrant'): void;
  cancelNode(): void;
  confirmNode(): void;
  armHydrantRun(): void;
  cancelHydrantRun(): void;
  backHydrantRun(): void;
  setHydrantRunMode(mode: 'count' | 'spacing'): void;
  setHydrantRunCount(count: number): void;
  setHydrantRunSpacing(spacingM: number): void;
  confirmHydrantRun(): void;
  selectNode(id: string): void;
  selectPipe(id: string): void;
  selectGun(id: string): void;
  renameNode(id: string, name: string): void;
  removeNode(id: string): void;
  patchPipe(id: string, patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>): void;
  setPumpPort(pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null): void;
  removePipe(id: string): void;
}
export function useSnowmakingNetworkController(
  options: SnowmakingNetworkControllerOptions,
): SnowmakingNetworkController {
  const [pipeTool, pipeDispatch] = useReducer(reduceSnowmakingPipeTool, IDLE_SNOWMAKING_PIPE_TOOL);
  const [nodeTool, nodeDispatch] = useReducer(reduceSnowmakingNodeTool, IDLE_SNOWMAKING_NODE_TOOL);
  const [hydrantRunTool, hydrantRunDispatch] = useReducer(reduceSnowmakingHydrantRunTool,
    IDLE_SNOWMAKING_HYDRANT_RUN_TOOL);
  const [snapping, setSnapping] = useState(false);
  const [diameterIn, setDiameter] = useState<SnowmakingPipeDiameterIn>(DEFAULT_SNOWMAKING_PIPE_DIAMETER_IN);
  const [snapHover, setSnapHover] = useState<[number, number] | null>(null);
  const [hydrantRunHover, setHydrantRunHover] = useState<SnowmakingPipeStation | null>(null);
  const snapHoverRef = useRef<[number, number] | null>(snapHover);
  const optionsRef = useRef(options);
  const pipeToolRef = useRef(pipeTool);
  const nodeToolRef = useRef(nodeTool);
  const hydrantRunToolRef = useRef(hydrantRunTool);
  optionsRef.current = options; pipeToolRef.current = pipeTool;
  nodeToolRef.current = nodeTool;
  hydrantRunToolRef.current = hydrantRunTool;
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

  const hydrantRunPreview = useMemo((): SnowmakingHydrantRunPreview | null => {
    if (hydrantRunTool.phase === 'idle') return null;
    const pipeId = hydrantRunTool.phase === 'select-pipe' ? null : hydrantRunTool.pipeId;
    const pipe = pipeId ? options.pipes.find((candidate) => candidate.id === pipeId) ?? null : null;
    const start = hydrantRunTool.phase === 'select-start' ? hydrantRunHover
      : hydrantRunTool.phase === 'select-end' || hydrantRunTool.phase === 'review'
        ? hydrantRunTool.start : null;
    const end = hydrantRunTool.phase === 'review' ? hydrantRunTool.end
      : hydrantRunTool.phase === 'select-end' ? hydrantRunHover : null;
    let layout: SnowmakingHydrantRunLayout | string | null = null;
    if (pipe && start && end) layout = snowmakingHydrantRunLayout(pipe, start, end,
      hydrantRunTool.phase === 'review'
        ? hydrantRunTool.mode === 'count' ? { mode: 'count', count: hydrantRunTool.count }
          : { mode: 'spacing', spacingM: hydrantRunTool.spacingM }
        : { mode: 'count', count: 2 });
    const occupied = options.nodes.map((node) => node.point);
    const positions = typeof layout === 'object' && layout ? layout.positions.map((station) => {
      const conflict = occupied.some((point) => haversineMeters(point, station.point) < 0.05);
      if (!conflict) occupied.push(station.point);
      return { station, conflict };
    }) : [];
    return {
      pipeName: pipe?.name ?? null,
      selectedRoute: pipe?.vertices.map((vertex) => vertex.point) ?? [],
      intervalPoints: pipe && start && end ? snowmakingPipeIntervalPoints(pipe, start, end) : [],
      startPoint: start?.point ?? null,
      endPoint: end?.point ?? null,
      lengthM: typeof layout === 'object' && layout ? layout.lengthM : null,
      actualSpacingM: hydrantRunTool.phase === 'review' && typeof layout === 'object' && layout
        ? layout.actualSpacingM : null,
      positions: hydrantRunTool.phase === 'review' ? positions : [],
      newCount: positions.filter((position) => !position.conflict).length,
      skippedCount: positions.filter((position) => position.conflict).length,
      error: typeof layout === 'string' ? layout : hydrantRunTool.error,
    };
  }, [hydrantRunTool, hydrantRunHover, options.nodes, options.pipes]);
  const hydrantRunPreviewRef = useRef<SnowmakingHydrantRunPreview | null>(hydrantRunPreview);
  hydrantRunPreviewRef.current = hydrantRunPreview;

  const synchronizeDraft = useCallback((map = optionsRef.current.mapRef.current): void => {
    const current = pipeToolRef.current;
    const preview = snowmakingPipePreview(current);
    const run = hydrantRunPreviewRef.current;
    const node = nodeToolRef.current;
    const pumpCandidate = node.phase === 'placing' && node.kind === 'pump' ? node.candidate : null;
    const pumpDirection = inlinePumpDirectionPoints(pumpCandidate, optionsRef.current.pipes);
    setSnowmakingDraftData(map, run ? {
      points: [], cursor: null, snapPoint: null,
      selectedRoute: run.selectedRoute, intervalPoints: run.intervalPoints,
      startPoint: run.startPoint, endPoint: run.endPoint,
      hydrants: run.positions.map((position) => ({ point: position.station.point,
        conflict: position.conflict })),
    } : preview ? {
      ...preview,
      snapPoint: current.phase === 'drawing' ? snapHoverRef.current : null,
    } : { points: [], cursor: null, snapPoint: snapHoverRef.current, pumpDirection });
  }, []);

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'snowmaking', zOrder: MAP_Z_ORDER.snowmaking,
    hits: [{ id: 'snowmaking', priority: MAP_HIT_RANK.snowmaking,
      layerIds: SNOWMAKING_HIT_LAYERS,
      hoverLayerIds: SNOWMAKING_HOVER_LAYERS,
      select: (id) => {
        if (optionsRef.current.guns.some((gun) => gun.id === id)) optionsRef.current.selectGun(id);
        else if (optionsRef.current.pipes.some((pipe) => pipe.id === id)) optionsRef.current.selectPipe(id);
        else if (optionsRef.current.nodes.some((node) => node.id === id)) optionsRef.current.selectNode(id);
      } }],
    install: ({ map }) => addSnowmakingLayers(map),
    synchronizeData: ({ map }) => {
      setSnowmakingData(map, optionsRef.current.nodes, optionsRef.current.pipes,
        optionsRef.current.guns);
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
    for (const id of removed) pipes = detachSnowmakingNode(pipes, id, current.createId);
    const edit = current.network.begin();
    edit.replace({ nodes: numbered.nodes, pipes, guns: before.guns,
      nextNumbers: numbered.nextNumbers });
    if (edit.commit().ok) for (const id of removed) current.clearSelected(id);
  }, [options.dams, options.ponds, options.lakes, options.network]);

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [options.nodes, options.pipes, options.guns, options.selected]);
  useEffect(() => { synchronizeDraft(); },
    [pipeTool, nodeTool, snapHover, hydrantRunPreview, synchronizeDraft]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (pipeTool.phase !== 'armed' && pipeTool.phase !== 'drawing')) return;
    const interaction = optionsRef.current.acquireInteractions('snowmaking-pipe', map);
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snowmakingSnapAt(map, raw,
        optionsRef.current.nodes, optionsRef.current.pipes) : null;
      const point = snap?.point ?? raw;
      setSnapHover(snap?.point ?? null);
      if (pipeToolRef.current.phase === 'drawing') pipeDispatch({ type: 'move', point, snap });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snap = snapping ? snowmakingSnapAt(map, raw,
        optionsRef.current.nodes, optionsRef.current.pipes) : null;
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
      const current = nodeToolRef.current;
      const snap = current.phase === 'placing' && current.kind === 'pump'
        ? snowmakingSnapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes)
        : snapping ? snowmakingSnapAt(map, raw,
          optionsRef.current.nodes, optionsRef.current.pipes) : null;
      setSnapHover(snap?.point ?? null);
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = nodeToolRef.current;
      const snap = current.phase === 'placing' && current.kind === 'pump'
        ? snowmakingSnapAt(map, raw, optionsRef.current.nodes, optionsRef.current.pipes)
        : snapping ? snowmakingSnapAt(map, raw,
          optionsRef.current.nodes, optionsRef.current.pipes) : null;
      if (snap?.kind === 'node') {
        const occupied = optionsRef.current.nodes.find((node) => node.id === snap.nodeId)!;
        nodeDispatch({ type: 'candidate', candidate: null,
          error: current.phase === 'placing' && current.kind === 'pump' && occupied.kind === 'intake'
            ? 'Place the pump downstream inside the pipe; a pump cannot occupy the water source.'
            : `${snowmakingNodeLabel(occupied)} already occupies that location.` });
        return;
      }
      if (current.phase === 'placing' && current.kind === 'pump' && snap?.kind !== 'pipe') {
        nodeDispatch({ type: 'candidate', candidate: null,
          error: 'Pumps must be placed inside an existing pipe segment.' });
        return;
      }
      const point = snap?.point ?? raw;
      if (current.phase === 'placing' && current.kind === 'pump' && snap?.kind === 'pipe') {
        const result = inlinePumpCandidate({ pipes: optionsRef.current.pipes,
          nodes: optionsRef.current.nodes, snap,
          revision: optionsRef.current.network.revision,
          sampleElevation: optionsRef.current.sampleElevation });
        nodeDispatch({ type: 'candidate', candidate: typeof result === 'string' ? null : result,
          error: typeof result === 'string' ? result : null });
        return;
      }
      const candidate: SnowmakingNodeCandidate = { point, snap,
        elevM: optionsRef.current.sampleElevation(point),
        revision: optionsRef.current.network.revision, pumpSegmentId: null, pumpSuctionSide: null };
      nodeDispatch({ type: 'candidate', candidate, error: null });
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') cancelNode(); };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); setSnapHover(null); };
  }, [nodeTool.phase, snapping]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || hydrantRunTool.phase === 'idle') return;
    const interaction = optionsRef.current.acquireInteractions('snowmaking-node', map);
    const stationOnSelectedPipe = (raw: [number, number]): SnowmakingPipeStation | null => {
      const current = hydrantRunToolRef.current;
      if (current.phase === 'idle' || current.phase === 'select-pipe') return null;
      const pipe = optionsRef.current.pipes.find((candidate) => candidate.id === current.pipeId);
      if (!pipe) return null;
      const snap = pipeSnapAt(map, raw, [pipe]);
      return snap ? snowmakingPipeStationAt(pipe, snap.point) : null;
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = hydrantRunToolRef.current;
      setHydrantRunHover(current.phase === 'select-start' || current.phase === 'select-end'
        ? stationOnSelectedPipe(raw) : null);
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = hydrantRunToolRef.current;
      if (current.phase === 'select-pipe') {
        const snap = pipeSnapAt(map, raw, optionsRef.current.pipes);
        if (!snap) { hydrantRunDispatch({ type: 'error',
          error: 'Click directly on an installed snowmaking pipe.' }); return; }
        hydrantRunDispatch({ type: 'pipe', pipeId: snap.pipeId }); return;
      }
      if (current.phase === 'select-start' || current.phase === 'select-end') {
        const station = stationOnSelectedPipe(raw);
        if (!station) { hydrantRunDispatch({ type: 'error',
          error: 'Click directly on the selected pipe.' }); return; }
        if (current.phase === 'select-start') hydrantRunDispatch({ type: 'start', station });
        else {
          const pipe = optionsRef.current.pipes.find((candidate) => candidate.id === current.pipeId);
          const valid = pipe ? snowmakingHydrantRunLayout(pipe, current.start, station,
            { mode: 'count', count: 2 }) : 'The selected pipe is no longer available.';
          if (typeof valid === 'string') { hydrantRunDispatch({ type: 'error', error: valid }); return; }
          hydrantRunDispatch({ type: 'end', station, revision: optionsRef.current.network.revision });
        }
        setHydrantRunHover(null);
      }
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelHydrantRun();
      else if (event.key === 'Backspace' && !(event.target instanceof HTMLInputElement))
        { event.preventDefault(); backHydrantRun(); }
    };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); setHydrantRunHover(null); };
  }, [hydrantRunTool.phase]);

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
    return resolveSnowmakingPipeDraft(snowmakingNetworkProjection(optionsRef.current.network.snapshot()),
      current, optionsRef.current.createId, optionsRef.current.now);
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
    hydrantRunDispatch({ type: 'cancel' });
    optionsRef.current.clearSelection(); optionsRef.current.openDock(); nodeDispatch({ type: 'arm', kind });
  }
  function setPumpSuctionSide(side: 'route-start' | 'route-end'): void {
    nodeDispatch({ type: 'pump-direction', side });
  }
  function cancelNode(): void { nodeDispatch({ type: 'cancel' }); hydrantRunDispatch({ type: 'cancel' });
    setSnapHover(null); setHydrantRunHover(null);
    optionsRef.current.release('snowmaking-node'); }
  function confirmNode(): void {
    const current = nodeToolRef.current;
    if (current.phase !== 'placing' || !current.candidate) return;
    if (current.kind === 'pump' && optionsRef.current.network.revision !== current.candidate.revision) {
      nodeDispatch({ type: 'candidate', candidate: current.candidate,
        error: 'The pipe network changed. Pick the pump location and direction again.' }); return;
    }
    if (current.kind === 'pump' && (!current.candidate.pumpSegmentId ||
      !current.candidate.pumpSuctionSide || current.candidate.snap?.kind !== 'pipe')) {
      nodeDispatch({ type: 'candidate', candidate: current.candidate,
        error: 'Choose which side supplies the pump before placing it.' }); return;
    }
    const state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const applied = applySnowmakingNodeCandidate(state, current,
      optionsRef.current.createId, optionsRef.current.now);
    if (typeof applied === 'string') {
      nodeDispatch({ type: 'candidate', candidate: current.candidate, error: applied }); return;
    }
    const next = current.kind === 'hydrant' ? { ...applied,
      guns: reconcileSnowgunConnections(applied.guns, applied.nodes) } : applied;
    const edit = optionsRef.current.network.begin(); edit.replace(next);
    if (!edit.commit().ok) { nodeDispatch({ type: 'candidate', candidate: current.candidate,
      error: 'The network changed. Pick the location again.' }); return; }
    nodeDispatch({ type: 'committed' });
  }
  function armHydrantRun(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate('snowmaking-node')) return;
    nodeDispatch({ type: 'cancel' });
    optionsRef.current.clearSelection(); optionsRef.current.openDock();
    hydrantRunDispatch({ type: 'arm' });
  }
  function cancelHydrantRun(): void {
    hydrantRunDispatch({ type: 'cancel' }); setHydrantRunHover(null); optionsRef.current.release('snowmaking-node');
  }
  function backHydrantRun(): void { hydrantRunDispatch({ type: 'back' }); setHydrantRunHover(null); }
  function setHydrantRunMode(mode: 'count' | 'spacing'): void { hydrantRunDispatch({ type: 'mode', mode }); }
  function setHydrantRunCount(count: number): void { hydrantRunDispatch({ type: 'count', count }); }
  function setHydrantRunSpacing(spacingM: number): void { hydrantRunDispatch({ type: 'spacing', spacingM }); }
  function confirmHydrantRun(): void {
    const current = hydrantRunToolRef.current;
    if (current.phase !== 'review') return;
    const document = optionsRef.current.network;
    if (document.revision !== current.revision) {
      hydrantRunDispatch({ type: 'error', revision: document.revision,
        error: 'The snowmaking network changed. The preview has been refreshed; confirm it again.' });
      return;
    }
    const edit = document.begin();
    let state = edit.snapshot();
    const target = state.pipes.find((pipe) => pipe.id === current.pipeId);
    if (!target) { hydrantRunDispatch({ type: 'error',
      error: 'The selected pipe is no longer available.' }); return; }
    const start = snowmakingPipeStationAt(target, current.start.point);
    const end = snowmakingPipeStationAt(target, current.end.point);
    if (!start || !end || start.distanceM > TARGET_REVALIDATE_M || end.distanceM > TARGET_REVALIDATE_M) {
      hydrantRunDispatch({ type: 'error', error: 'The selected pipe changed. Choose the interval again.' });
      return;
    }
    const layout = snowmakingHydrantRunLayout(target, start, end,
      current.mode === 'count' ? { mode: 'count', count: current.count }
        : { mode: 'spacing', spacingM: current.spacingM });
    if (typeof layout === 'string') { hydrantRunDispatch({ type: 'error', error: layout }); return; }
    const populated = populateSnowmakingHydrantRun(state, target.id, layout,
      optionsRef.current.createId, optionsRef.current.now);
    if (typeof populated === 'string') { hydrantRunDispatch({ type: 'error',
      error: populated }); return; }
    state = { ...populated.state, guns: reconcileSnowgunConnections(
      populated.state.guns, populated.state.nodes) };
    edit.replace(state);
    if (!edit.commit().ok) { hydrantRunDispatch({ type: 'error', revision: document.revision,
      error: 'The network changed during confirmation. Review the refreshed preview and try again.' });
      return;
    }
    cancelHydrantRun();
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
      pipes: detachSnowmakingNode(state.pipes, id, optionsRef.current.createId) };
    state = { ...state, guns: reconcileSnowgunConnections(state.guns, state.nodes) };
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
  function setPumpPort(pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null): void {
    const state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const next = setSnowmakingPumpPort(state, pipeId, segmentId, end, port);
    if (!next) return;
    const edit = optionsRef.current.network.begin();
    edit.replace(next);
    edit.commit();
  }
  function removePipe(id: string): void {
    let state = snowmakingNetworkProjection(optionsRef.current.network.snapshot());
    const removed = state.pipes.find((pipe) => pipe.id === id);
    if (!removed) return;
    const candidates = new Set(removed.vertices.flatMap((vertex) => vertex.nodeId ? [vertex.nodeId] : []));
    state = pruneAffectedJunctions({ ...state, pipes: state.pipes.filter((pipe) => pipe.id !== id) },
      candidates, optionsRef.current.createId);
    const edit = optionsRef.current.network.begin(); edit.replace(state);
    if (edit.commit().ok) optionsRef.current.clearSelected(id);
  }

  return {
    contribution: contributionRef.current, pipeTool, nodeTool, hydrantRunTool, snapping, diameterIn,
    previewStats, nodeCandidateTarget, hydrantRunPreview, armPipe, cancelPipe,
    undoPipe: () => pipeDispatch({ type: 'undo' }), finishPipe, confirmPipe, renameDraftPipe,
    setDiameter, setSnapping, armNode, cancelNode, confirmNode, setPumpSuctionSide,
    armHydrantRun, cancelHydrantRun, backHydrantRun, setHydrantRunMode,
    setHydrantRunCount, setHydrantRunSpacing, confirmHydrantRun,
    selectNode: options.selectNode, selectPipe: options.selectPipe, selectGun: options.selectGun,
    renameNode, removeNode, patchPipe, setPumpPort, removePipe,
  };
}
