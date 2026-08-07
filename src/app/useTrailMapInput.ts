import { useEffect, useRef, type Dispatch, type MutableRefObject,
  type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedLift } from '../types/lifts';
import type { SavedTrail } from '../types/trails';
import { haversineMeters } from '../geo';
import { trailPartContains } from '../trails';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import type { TrailHeadAnchor } from './trailHeadAnchor';
import { nearestTrailHeadAnchor, nearestTrailTailAnchor } from './trailHeadAnchor';
import { setTrailPaintPreview } from './trailLayers';
import type { TrailTool, TrailToolAction } from './trailControllerModel';

const ANCHOR_PICK_M = 60;

export interface TrailPaintCommand {
  mode: 'paint' | 'erase';
  path: [number, number][];
  seed?: boolean;
  /** Erase is one user action but two worker operations: erase, then repaint seed. */
  restoreSeed?: [number, number];
}

export function trailHeadPreview(tool: TrailTool): {
  candidate: [number, number] | null;
  head: [number, number] | null;
  tail: [number, number] | null;
} {
  if (tool.phase === 'place-head') return {
    candidate: tool.candidate?.point ?? null, head: null, tail: null };
  if (tool.phase === 'place-tail') return {
    candidate: tool.candidate?.point ?? null, head: tool.anchor.point, tail: null };
  if (tool.phase === 'paint') return { candidate: null, head: tool.anchor.point, tail: null };
  if (tool.phase === 'analyzing') return {
    candidate: null, head: tool.anchor.point, tail: tool.tailAnchor.point };
  if (tool.phase === 'review') return { candidate: null,
    head: tool.draft.anchor?.point ?? null, tail: tool.draft.tailAnchor?.point ?? null };
  return { candidate: null, head: null, tail: null };
}

export function hasUserTrailStroke(
  commands: readonly TrailPaintCommand[],
  head: [number, number],
): boolean {
  return commands.some((command) => !command.seed && command.mode === 'paint' &&
    command.path.some((point) => haversineMeters(point, head) >= 0.5));
}

function sameAnchor(a: TrailHeadAnchor | null, b: TrailHeadAnchor | null): boolean {
  if (a === b) return true;
  if (!a || !b || a.kind !== b.kind) return false;
  const samePoint = a.point[0] === b.point[0] && a.point[1] === b.point[1];
  return samePoint && (a.kind === 'lift'
    ? b.kind === 'lift' && a.liftId === b.liftId && a.end === b.end
    : b.kind === 'trail' && a.trailId === b.trailId);
}

interface TrailMapInputOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  state: TrailTool;
  stateRef: MutableRefObject<TrailTool>;
  lifts: readonly SavedLift[];
  trails: readonly SavedTrail[];
  brushWidthRef: MutableRefObject<number>;
  commandsRef: MutableRefObject<TrailPaintCommand[]>;
  previewPathRef: MutableRefObject<[number, number][]>;
  brushCursorRef: MutableRefObject<[number, number] | null>;
  dispatch: Dispatch<TrailToolAction>;
  acquireInteractions(overrides: {
    cursor: string; dragPanEnabled?: boolean; doubleClickZoomEnabled?: boolean;
  }): MapInteractionLeaseHandle;
  beginPainting(anchor: TrailHeadAnchor): void;
  analyzeTail(): void;
  submit(command: TrailPaintCommand): void;
  cancel(): void;
  backToPaint(): void;
}

export function useTrailMapInput(options: TrailMapInputOptions): void {
  const optionsRef = useRef(options);
  optionsRef.current = options;

  useEffect(() => {
    const currentOptions = optionsRef.current;
    const map = currentOptions.mapRef.current;
    if (!map || currentOptions.stateRef.current.phase !== 'place-head') return;
    const canvas = map.getCanvas();
    const interaction = currentOptions.acquireInteractions({ cursor: 'crosshair' });
    const candidateAt = (event: maplibregl.MapMouseEvent) => nearestTrailHeadAnchor(
      [event.lngLat.lng, event.lngLat.lat], [...optionsRef.current.lifts],
      [...optionsRef.current.trails], ANCHOR_PICK_M);
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      const state = optionsRef.current.stateRef.current;
      if (state.phase === 'place-head' && !sameAnchor(state.candidate, candidate))
        optionsRef.current.dispatch({ type: 'head-candidate', candidate });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const anchor = candidateAt(event);
      if (!anchor) {
        optionsRef.current.dispatch({ type: 'head-candidate', candidate: null,
          error: 'Choose the top terminal of a lift or an existing trail centerline.' });
        return;
      }
      optionsRef.current.beginPainting(anchor);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') optionsRef.current.cancel();
    };
    const onLeave = () => optionsRef.current.dispatch({
      type: 'head-candidate', candidate: null });
    map.on('mousemove', onMove); map.on('click', onClick);
    canvas.addEventListener('mouseleave', onLeave); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      canvas.removeEventListener('mouseleave', onLeave); window.removeEventListener('keydown', onKey);
      interaction.release(); };
  }, [options.state.phase]);

  useEffect(() => {
    const currentOptions = optionsRef.current;
    const map = currentOptions.mapRef.current;
    if (!map || currentOptions.stateRef.current.phase !== 'place-tail') return;
    const interaction = currentOptions.acquireInteractions({ cursor: 'crosshair' });
    const candidateAt = (event: maplibregl.MapMouseEvent) => nearestTrailTailAnchor(
      [event.lngLat.lng, event.lngLat.lat], [...optionsRef.current.lifts],
      [...optionsRef.current.trails], ANCHOR_PICK_M);
    const isConnected = (state: Extract<TrailTool, { phase: 'place-tail' }>,
      point: [number, number]) => haversineMeters(state.anchor.point, point) >= 8 &&
      state.polygons.some((polygon) => trailPartContains({ polygon }, state.anchor.point) &&
        trailPartContains({ polygon }, point));
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      const state = optionsRef.current.stateRef.current;
      if (state.phase !== 'place-tail') return;
      const connected = !!candidate && isConnected(state, candidate.point);
      optionsRef.current.dispatch({ type: 'tail-candidate',
        candidate: connected ? candidate : null,
        error: candidate && !connected
          ? 'The painted trail must reach this endpoint in one connected footprint.' : state.error });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      const state = optionsRef.current.stateRef.current;
      if (!candidate || state.phase !== 'place-tail' || !isConnected(state, candidate.point)) {
        optionsRef.current.dispatch({ type: 'tail-candidate', candidate: null,
          error: 'Choose a lift base or trail centerline reached by the painted footprint.' });
        return;
      }
      optionsRef.current.dispatch({ type: 'analyze', tailAnchor: candidate });
      optionsRef.current.analyzeTail();
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') optionsRef.current.backToPaint();
    };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); };
  }, [options.state.phase]);

  useEffect(() => {
    const currentOptions = optionsRef.current;
    const map = currentOptions.mapRef.current;
    if (!map || currentOptions.stateRef.current.phase !== 'paint') return;
    const canvas = map.getCanvas();
    const interaction = currentOptions.acquireInteractions({ cursor: 'none',
      dragPanEnabled: false, doubleClickZoomEnabled: false });
    const renderPreview = () => setTrailPaintPreview(map, {
      path: optionsRef.current.previewPathRef.current,
      cursor: optionsRef.current.brushCursorRef.current,
      brushWidthM: optionsRef.current.brushWidthRef.current,
      ...trailHeadPreview(optionsRef.current.stateRef.current),
    });
    renderPreview();
    let painting = false, path: [number, number][] = [], previewPath: [number, number][] = [];
    let previewRaf = 0, lastMetricAt = 0;
    const drawPreview = () => { previewRaf = 0; renderPreview(); };
    const schedulePreview = () => { if (!previewRaf) previewRaf = requestAnimationFrame(drawPreview); };
    const finish = () => {
      painting = false;
      if (path.length === 1) path.push(path[0]);
      const state = optionsRef.current.stateRef.current;
      const mode = state.phase === 'paint' ? state.mode : 'paint';
      const command: TrailPaintCommand = { mode, path: path.slice(),
        restoreSeed: mode === 'erase' && state.phase === 'paint' ? state.anchor.point : undefined };
      optionsRef.current.commandsRef.current.push(command);
      optionsRef.current.submit(command);
    };
    const down = (event: maplibregl.MapMouseEvent) => {
      const state = optionsRef.current.stateRef.current;
      if (state.phase !== 'paint' || state.pending) return;
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      painting = true; path = [point]; previewPath = path;
      optionsRef.current.previewPathRef.current = previewPath;
      optionsRef.current.brushCursorRef.current = point; schedulePreview();
    };
    const move = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      optionsRef.current.brushCursorRef.current = point;
      if (!painting) { schedulePreview(); return; }
      const width = optionsRef.current.brushWidthRef.current;
      const gap = Math.max(0.5, Math.min(2, width / 16));
      if (haversineMeters(path[path.length - 1], point) < gap) { schedulePreview(); return; }
      path.push(point);
      const lastPreview = previewPath.at(-1);
      if (!lastPreview || Math.hypot(map.project(lastPreview).x - map.project(point).x,
        map.project(lastPreview).y - map.project(point).y) >= 2) {
        previewPath = [...previewPath, point];
        optionsRef.current.previewPathRef.current = previewPath;
      }
      schedulePreview();
      const now = performance.now();
      if (now - lastMetricAt >= 100) {
        lastMetricAt = now;
        let length = 0;
        for (let i = 1; i < path.length; i++) length += haversineMeters(path[i - 1], path[i]);
        const swept = Math.PI * (width / 2) ** 2 + length * width;
        const state = optionsRef.current.stateRef.current;
        if (state.phase === 'paint') optionsRef.current.dispatch({ type: 'paint-patch', patch: {
          activeAreaM2: state.mode === 'paint' ? state.areaM2 + swept
            : Math.max(0, state.areaM2 - swept) } });
      }
    };
    const up = () => { if (painting) finish(); };
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (painting) { painting = false; path = []; previewPath = [];
        optionsRef.current.previewPathRef.current = []; renderPreview(); }
      else optionsRef.current.cancel();
    };
    const leave = () => { optionsRef.current.brushCursorRef.current = null;
      if (!painting) { previewPath = []; optionsRef.current.previewPathRef.current = []; }
      renderPreview(); };
    map.on('mousedown', down); map.on('mousemove', move); window.addEventListener('mouseup', up);
    canvas.addEventListener('mouseleave', leave); window.addEventListener('keydown', onKey);
    return () => { map.off('mousedown', down); map.off('mousemove', move);
      window.removeEventListener('mouseup', up); canvas.removeEventListener('mouseleave', leave);
      window.removeEventListener('keydown', onKey); if (previewRaf) cancelAnimationFrame(previewRaf);
      optionsRef.current.previewPathRef.current = []; optionsRef.current.brushCursorRef.current = null;
      setTrailPaintPreview(map, { path: [], cursor: null,
        brushWidthM: optionsRef.current.brushWidthRef.current,
        ...trailHeadPreview(optionsRef.current.stateRef.current) });
      interaction.release(); };
  }, [options.state.phase]);
}
