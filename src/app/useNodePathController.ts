import { useEffect, useReducer, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { AnchorRef } from '../types/anchors';
import type { SavedJunction, SavedNode, SavedPath } from '../types/topology';
import type { SavedTrail } from '../types/trails';
import { haversineMeters } from '../geo';
import { makeFrame, toMeters } from '../network';
import { canRemoveJunction } from '../topology';
import { nextPathName } from '../skiNodes';
import { nearestTrailTailAnchor } from './trailHeadAnchor';
import type { TopologyDocument } from './topologyDocument';
import { MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addNodePathDraftLayers, addNodePathLayers, setNodePathData,
  setNodePathDraftData, type NodePathDraft } from './nodePathLayers';
import { IDLE_NODE_TOOL, IDLE_PATH_TOOL, pathFromReview, reduceNodeTool, reducePathTool,
  type NodeTool, type PathTool } from './nodePathControllerModel';

const ANCHOR_PICK_M = 60;

function draftOf(pathTool: PathTool, nodeTool: NodeTool,
  snapHover: [number, number] | null): NodePathDraft | null {
  const highlight = snapHover ? [snapHover] : [];
  const pick = nodeTool.phase === 'add' ? nodeTool.candidate?.point ?? null : null;
  if (pathTool.phase === 'drawing') return { points: pathTool.points,
    cursor: pathTool.cursor, highlight };
  if (pathTool.phase === 'review') return { points: pathTool.points, cursor: null, highlight: [] };
  if (pathTool.phase === 'armed' || nodeTool.phase !== 'idle') return {
    points: [], cursor: null, highlight: pick ? [pick] : highlight,
  };
  return null;
}

export interface NodePathControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  trails: readonly SavedTrail[];
  nodes: readonly SavedNode[];
  paths: readonly SavedPath[];
  junctions: readonly SavedJunction[];
  topology: TopologyDocument;
  canArm(): boolean;
  activate(tool: 'ski-node' | 'ski-path'): boolean;
  release(tool: 'ski-node' | 'ski-path'): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(tool: 'ski-node' | 'ski-path', map: maplibregl.Map): MapInteractionLeaseHandle;
  selectNode(id: string): void;
  selectPath(id: string): void;
  clearSelectedNode(id: string): void;
  clearSelectedPath(id: string): void;
  createId(): string;
  now(): string;
  synchronizeMap(): void;
}

export function useNodePathController(options: NodePathControllerOptions) {
  const [nodeTool, nodeDispatch] = useReducer(reduceNodeTool, IDLE_NODE_TOOL);
  const [pathTool, pathDispatch] = useReducer(reducePathTool, IDLE_PATH_TOOL);
  const [snapHover, setSnapHover] = useState<[number, number] | null>(null);
  const nodeRef = useRef(nodeTool), pathRef = useRef(pathTool);
  const snapHoverRef = useRef(snapHover), optionsRef = useRef(options);
  const draftFrameRef = useRef<number | null>(null);
  nodeRef.current = nodeTool; pathRef.current = pathTool;
  snapHoverRef.current = snapHover; optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'ski-node-path', zOrder: MAP_Z_ORDER['ski-node-path'],
    install: ({ map }) => { addNodePathLayers(map); addNodePathDraftLayers(map); },
    synchronizeData: ({ map }) => {
      const current = optionsRef.current;
      setNodePathData(map, [...current.nodes], [...current.paths], [...current.junctions]);
      setNodePathDraftData(map, draftOf(pathRef.current, nodeRef.current, snapHoverRef.current));
    },
    setCaptureTransient: ({ map }, hidden) => setNodePathDraftData(map,
      hidden ? null : draftOf(pathRef.current, nodeRef.current, snapHoverRef.current)),
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [options.nodes, options.paths, options.junctions]);
  useEffect(() => {
    if (draftFrameRef.current != null) return;
    draftFrameRef.current = requestAnimationFrame(() => {
      draftFrameRef.current = null;
      const map = optionsRef.current.mapRef.current;
      if (map) setNodePathDraftData(map,
        draftOf(pathRef.current, nodeRef.current, snapHoverRef.current));
    });
  }, [nodeTool, pathTool, snapHover]);

  function trailAnchorAt(click: [number, number]): Extract<AnchorRef, { kind: 'trail' }> | null {
    const anchor = nearestTrailTailAnchor(click, [], [...optionsRef.current.trails], ANCHOR_PICK_M);
    return anchor?.kind === 'trail' ? anchor : null;
  }

  function junctionAt(click: [number, number]): SavedJunction | null {
    const frame = makeFrame([click]), point = toMeters(frame, click);
    let best: { junction: SavedJunction; distance: number } | null = null;
    for (const junction of optionsRef.current.junctions) {
      const candidate = toMeters(frame, junction.point);
      const distance = Math.hypot(candidate.x - point.x, candidate.y - point.y);
      if (distance <= ANCHOR_PICK_M && (!best || distance < best.distance))
        best = { junction, distance };
    }
    return best?.junction ?? null;
  }

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || nodeTool.phase === 'idle') return;
    const phase = nodeTool.phase;
    const interaction = optionsRef.current.acquireInteractions('ski-node', map);
    const snapAt = (point: [number, number]) => phase === 'add'
      ? trailAnchorAt(point)?.point ?? null : junctionAt(point)?.point ?? null;
    const onMove = (event: maplibregl.MapMouseEvent) =>
      setSnapHover(snapAt([event.lngLat.lng, event.lngLat.lat]));
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      if (phase === 'add') {
        const candidate = trailAnchorAt(point);
        nodeDispatch({ type: 'add-candidate', candidate,
          error: candidate ? null : 'Nodes sit on a run — click along one you have painted.' });
        return;
      }
      const junction = junctionAt(point);
      if (!junction) {
        nodeDispatch({ type: 'remove-candidate', junctionId: null,
          error: 'No node there — click one of the dots on a run.' });
        return;
      }
      const check = canRemoveJunction([...optionsRef.current.trails],
        [...optionsRef.current.junctions], [...optionsRef.current.paths], junction.id);
      nodeDispatch({ type: 'remove-candidate', junctionId: junction.id,
        error: check.ok ? null : check.reason });
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') cancelNode(); };
    map.on('click', onClick); map.on('mousemove', onMove); window.addEventListener('keydown', onKey);
    return () => { map.off('click', onClick); map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey); interaction.release(); setSnapHover(null); };
  }, [nodeTool.phase]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (pathTool.phase !== 'armed' && pathTool.phase !== 'drawing')) return;
    const interaction = optionsRef.current.acquireInteractions('ski-path', map);
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = pathRef.current, snapped = trailAnchorAt(point);
      if (current.phase === 'armed') { if (snapped) pathDispatch({ type: 'start', anchor: snapped }); return; }
      if (current.phase !== 'drawing') return;
      const next = snapped?.point ?? point, last = current.points.at(-1);
      if (!last || haversineMeters(last, next) >= 1) pathDispatch({ type: 'add-point', point: next });
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      setSnapHover(trailAnchorAt(point)?.point ?? null);
      if (pathRef.current.phase === 'drawing') pathDispatch({ type: 'move', point });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelPath();
      else if (event.key === 'Backspace') { event.preventDefault(); pathDispatch({ type: 'undo' }); }
      else if (event.key === 'Enter') finishPath();
    };
    map.on('click', onClick); map.on('mousemove', onMove); window.addEventListener('keydown', onKey);
    return () => { map.off('click', onClick); map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey); interaction.release(); setSnapHover(null); };
    // Tool callbacks intentionally read live refs; resubscribe only when the phase changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathTool.phase]);

  useEffect(() => () => {
    if (draftFrameRef.current != null) cancelAnimationFrame(draftFrameRef.current);
    optionsRef.current.release('ski-node');
    optionsRef.current.release('ski-path');
  }, []);

  function armNode(phase: 'add' | 'remove'): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate('ski-node')) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock();
    nodeDispatch({ type: 'arm', phase });
  }
  function cancelNode(): void { nodeDispatch({ type: 'cancel' });
    optionsRef.current.release('ski-node'); }
  function confirmAddNode(): void {
    const current = nodeRef.current;
    if (current.phase !== 'add' || !current.candidate) return;
    const edit = optionsRef.current.topology.begin();
    if (!edit.splitTrail(current.candidate.trailId, current.candidate.point,
      optionsRef.current.createId)) {
      edit.abort(); nodeDispatch({ type: 'add-candidate', candidate: null,
        error: 'That run cannot be split there.' }); return;
    }
    if (!edit.changed.junctions) { edit.abort(); nodeDispatch({ type: 'add-candidate',
      candidate: null, error: 'There is already a node there.' }); return; }
    edit.commit(); nodeDispatch({ type: 'committed' });
  }
  function removeNode(id: string): void {
    const edit = optionsRef.current.topology.begin();
    if (!edit.removeJunction(id)) { edit.abort(); return; }
    edit.commit(); optionsRef.current.clearSelectedNode(id);
  }
  function confirmRemoveNode(): void {
    const current = nodeRef.current;
    if (current.phase !== 'remove' || !current.junctionId) return;
    removeNode(current.junctionId); nodeDispatch({ type: 'committed' });
  }
  function deleteLegacyNode(id: string): void {
    const edit = optionsRef.current.topology.begin(); edit.removeNode(id); edit.commit();
  }
  function selectNode(id: string): void { optionsRef.current.selectNode(id);
    const junction = optionsRef.current.junctions.find((value) => value.id === id);
    if (junction) optionsRef.current.mapRef.current?.easeTo({ center: junction.point, duration: 400 }); }

  function armPath(): void { if (!optionsRef.current.canArm() ||
    !optionsRef.current.activate('ski-path')) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock(); pathDispatch({ type: 'arm' }); }
  function cancelPath(): void { pathDispatch({ type: 'cancel' });
    optionsRef.current.release('ski-path'); }
  function finishPath(): void {
    const current = pathRef.current;
    if (current.phase !== 'drawing' || current.points.length < 2 || !current.from) return;
    const to = trailAnchorAt(current.points.at(-1) as [number, number]);
    if (!to || current.from.kind !== 'trail' || current.from.trailId === to.trailId) return;
    pathDispatch({ type: 'review', to, name: nextPathName([...optionsRef.current.paths]) });
  }
  function confirmPath(): void {
    const current = pathRef.current;
    if (current.phase !== 'review' || current.from.kind !== 'trail' ||
      current.to.kind !== 'trail' || current.from.trailId === current.to.trailId) return;
    const edit = optionsRef.current.topology.begin();
    const from = edit.splitTrail(current.from.trailId, current.from.point, optionsRef.current.createId);
    if (!from) { edit.abort(); return; }
    const to = edit.splitTrail(current.to.trailId, current.to.point, optionsRef.current.createId);
    if (!to) { edit.abort(); return; }
    edit.addPath(pathFromReview(current, optionsRef.current.paths, optionsRef.current.createId(),
      optionsRef.current.now(), from.id, to.id));
    edit.commit(); pathDispatch({ type: 'cancel' }); optionsRef.current.release('ski-path');
  }
  function removePath(id: string): void { const edit = optionsRef.current.topology.begin();
    edit.removePath(id); edit.commit(); optionsRef.current.clearSelectedPath(id); }
  function patchPath(id: string, patch: Partial<SavedPath>): void {
    const edit = optionsRef.current.topology.begin(); edit.patchPath(id, patch); edit.commit(); }

  return { nodeTool, pathTool, contribution: contributionRef.current,
    armNode, cancelNode, confirmAddNode, confirmRemoveNode, removeNode, deleteLegacyNode, selectNode,
    armPath, cancelPath, undoPath: () => pathDispatch({ type: 'undo' }), finishPath, confirmPath,
    renamePath: (name: string) => pathDispatch({ type: 'rename', name }), removePath, patchPath,
    selectPath: (id: string) => optionsRef.current.selectPath(id) };
}
