import { useEffect, useMemo, useRef, type Dispatch, type MutableRefObject,
  type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedLift } from '../types/lifts';
import type { SavedTrail } from '../types/trails';
import { haversineMeters } from '../geo';
import { trailPartContains } from '../trails';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import type { TrailHeadAnchor } from './trailHeadAnchor';
import { TrailAnchorIndex } from './trailHeadAnchor';
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

function sameAnchorTarget(a: TrailHeadAnchor | null, b: TrailHeadAnchor | null): boolean {
  if (a === b) return true;
  if (!a || !b || a.kind !== b.kind) return false;
  return a.kind === 'lift'
    ? b.kind === 'lift' && a.liftId === b.liftId && a.end === b.end
    : b.kind === 'trail' && a.trailId === b.trailId;
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
  const anchorIndex = useMemo(() => new TrailAnchorIndex(options.lifts, options.trails),
    [options.lifts, options.trails]);
  const anchorIndexRef = useRef(anchorIndex);
  anchorIndexRef.current = anchorIndex;

  useEffect(() => {
    const currentOptions = optionsRef.current;
    const map = currentOptions.mapRef.current;
    if (!map || currentOptions.stateRef.current.phase !== 'place-head') return;
    const canvas = map.getCanvas();
    const interaction = currentOptions.acquireInteractions({ cursor: 'crosshair' });
    let previewRaf = 0, previewCandidate: TrailHeadAnchor | null = null;
    const renderCandidate = () => { previewRaf = 0;
      const state = optionsRef.current.stateRef.current;
      setTrailPaintPreview(map, { path: [], cursor: null,
        brushWidthM: optionsRef.current.brushWidthRef.current,
        ...trailHeadPreview(state), candidate: previewCandidate?.point ?? null }); };
    const scheduleCandidate = (candidate: TrailHeadAnchor | null) => {
      previewCandidate = candidate;
      if (!previewRaf) previewRaf = requestAnimationFrame(renderCandidate);
    };
    const candidateAt = (event: maplibregl.MapMouseEvent) => anchorIndexRef.current.nearestHead(
      [event.lngLat.lng, event.lngLat.lat], ANCHOR_PICK_M);
    renderCandidate();
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      scheduleCandidate(candidate);
      const state = optionsRef.current.stateRef.current;
      if (state.phase === 'place-head' && !sameAnchorTarget(state.candidate, candidate))
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
    const onLeave = () => { scheduleCandidate(null); optionsRef.current.dispatch({
      type: 'head-candidate', candidate: null }); };
    map.on('mousemove', onMove); map.on('click', onClick);
    canvas.addEventListener('mouseleave', onLeave); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      canvas.removeEventListener('mouseleave', onLeave); window.removeEventListener('keydown', onKey);
      if (previewRaf) cancelAnimationFrame(previewRaf);
      interaction.release(); };
  }, [options.state.phase]);

  useEffect(() => {
    const currentOptions = optionsRef.current;
    const map = currentOptions.mapRef.current;
    if (!map || currentOptions.stateRef.current.phase !== 'place-tail') return;
    const interaction = currentOptions.acquireInteractions({ cursor: 'crosshair' });
    let previewRaf = 0, previewCandidate: TrailHeadAnchor | null = null;
    const renderCandidate = () => { previewRaf = 0;
      const state = optionsRef.current.stateRef.current;
      setTrailPaintPreview(map, { path: [], cursor: null,
        brushWidthM: optionsRef.current.brushWidthRef.current,
        ...trailHeadPreview(state), candidate: previewCandidate?.point ?? null }); };
    const scheduleCandidate = (candidate: TrailHeadAnchor | null) => {
      previewCandidate = candidate;
      if (!previewRaf) previewRaf = requestAnimationFrame(renderCandidate);
    };
    const candidateAt = (event: maplibregl.MapMouseEvent) => anchorIndexRef.current.nearestTail(
      [event.lngLat.lng, event.lngLat.lat], ANCHOR_PICK_M);
    renderCandidate();
    const isConnected = (state: Extract<TrailTool, { phase: 'place-tail' }>,
      point: [number, number]) => haversineMeters(state.anchor.point, point) >= 8 &&
      state.polygons.some((polygon) => trailPartContains({ polygon }, state.anchor.point) &&
        trailPartContains({ polygon }, point));
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      const state = optionsRef.current.stateRef.current;
      if (state.phase !== 'place-tail') return;
      const connected = !!candidate && isConnected(state, candidate.point);
      const visible = connected ? candidate : null;
      scheduleCandidate(visible);
      const error = candidate && !connected
        ? 'The painted trail must reach this endpoint in one connected footprint.' : state.error;
      if (!sameAnchorTarget(state.candidate, visible) || state.error !== error)
        optionsRef.current.dispatch({ type: 'tail-candidate', candidate: visible, error });
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
      window.removeEventListener('keydown', onKey); if (previewRaf) cancelAnimationFrame(previewRaf);
      interaction.release(); };
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
    let previewRaf = 0, lastMetricAt = 0, strokeLengthM = 0;
    let lastPreviewPixel: { x: number; y: number } | null = null;
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
      painting = true; path = [point]; previewPath = [point]; strokeLengthM = 0;
      lastPreviewPixel = map.project(point);
      optionsRef.current.previewPathRef.current = previewPath;
      optionsRef.current.brushCursorRef.current = point; schedulePreview();
    };
    const move = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      optionsRef.current.brushCursorRef.current = point;
      if (!painting) { schedulePreview(); return; }
      const width = optionsRef.current.brushWidthRef.current;
      const gap = Math.max(0.5, Math.min(2, width / 16));
      const segmentLengthM = haversineMeters(path[path.length - 1], point);
      if (segmentLengthM < gap) { schedulePreview(); return; }
      path.push(point);
      strokeLengthM += segmentLengthM;
      const pixel = map.project(point);
      if (!lastPreviewPixel || Math.hypot(lastPreviewPixel.x - pixel.x,
        lastPreviewPixel.y - pixel.y) >= 2) {
        previewPath.push(point); lastPreviewPixel = pixel;
        if (previewPath.length > 2048) previewPath = previewPath.filter(
          (_, index) => index % 2 === 0 || index === previewPath.length - 1);
        optionsRef.current.previewPathRef.current = previewPath;
      }
      schedulePreview();
      const now = performance.now();
      if (now - lastMetricAt >= 200) {
        lastMetricAt = now;
        const swept = Math.PI * (width / 2) ** 2 + strokeLengthM * width;
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
