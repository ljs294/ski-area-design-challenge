import { useEffect, useReducer, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { AnchorRef } from '../types/anchors';
import type { SavedJunction, SavedPath } from '../types/topology';
import type { SavedLift } from '../types/lifts';
import type { SavedTrail, SavedTrailPart } from '../types/trails';
import type { TerrainRecord } from '../types/terrain';
import { jitterPolygon, TRAIL_CLEAR_JITTER_M, type CoverClearing } from '../coverEdit';
import { trailAreaM2, trailPartsStats, difficultyForSlopes,
  pinTrailEndpoints, DEFAULT_BRUSH_WIDTH_M } from '../trails';
import { resolveTrailHitId } from '../trailHit';
import { TRAIL_PRESENTATION_VERSION,
  type TrailPresentationResult } from '../types/trailPresentation';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
import type { TerrainGradeAdapter, TerrainGradeSuccess } from './terrainGradeClient';
import { terrainGradeGeometryKey } from './terrainGradeProtocol';
import type { TrailPaintAdapter } from './trailPaintClient';
import type { TrailPresentationAdapter } from './trailPresentationClient';
import type { TerrainDocument, TerrainCommitRequest } from './terrainDocument';
import type { TopologyDocument } from './topologyDocument';
import { commitDocuments } from './committedDocumentTransaction';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addTrailLayers, applyTrailTheme, draftToGeoJSON, setTrailData, setTrailDraftData,
  setTrailHitData, setTrailHover, setTrailPaintMode, setTrailPaintPreview,
  setTrailSelection, trailPresentationToGeoJSON, trailsToHitGeoJSON,
  TRAIL_BUILT_LAYER_IDS } from './trailLayers';
import { buildSavedTrail, createTrailDraft, IDLE_TRAIL_TOOL, reduceTrailTool,
  type DraftTrail, type TrailTool } from './trailControllerModel';
import { hasUserTrailStroke, trailHeadPreview, useTrailMapInput,
  type TrailPaintCommand } from './useTrailMapInput';
import type { TrailHeadAnchor } from './trailHeadAnchor';

const TRAIL_GRADE_POLICY = { envelope: 'footprint' } as const;

export interface TrailControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  lifts: readonly SavedLift[];
  trails: readonly SavedTrail[];
  junctions: readonly SavedJunction[];
  paths: readonly SavedPath[];
  selectedTrailId: string | null;
  theme: 'light' | 'dark';
  topology: TopologyDocument;
  terrain: TerrainDocument;
  gradeAdapter: TerrainGradeAdapter;
  paintAdapter: TrailPaintAdapter;
  presentationAdapter: TrailPresentationAdapter;
  canArm(): boolean;
  activate(): boolean;
  release(): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(map: maplibregl.Map, overrides: {
    cursor: string; dragPanEnabled?: boolean; doubleClickZoomEnabled?: boolean;
  }): MapInteractionLeaseHandle;
  terrainRecord(): TerrainRecord | null;
  heightGrid(record: TerrainRecord): Float32Array;
  sampleProfile(line: [number, number][], zoom: number): Promise<number[] | null>;
  gradeChanged(): void;
  restoreGradePreview(map: maplibregl.Map): void;
  clearCover(clearings: CoverClearing[]): Promise<void>;
  select(id: string): void;
  clearSelected(id: string): void;
  closeEditing(): void;
  reportBlockedDelete(message: string): void;
  createId(): string;
  now(): string;
  structuresVisible(): boolean;
}

export interface TrailController {
  readonly state: TrailTool;
  readonly brushWidthM: number;
  readonly presentationError: string | null;
  readonly contribution: ManagedMapContribution;
  activeGradePreview(): TerrainGradeSuccess | null;
  arm(): void;
  cancel(): void;
  setPaintMode(mode: 'paint' | 'erase'): void;
  undoPaint(): void;
  clearPaint(): void;
  finishPaint(): void;
  changeHead(): void;
  backToPaint(): void;
  changeBrushWidth(widthM: number): void;
  patchDraft(patch: Partial<DraftTrail>): void;
  setGrading(enabled: boolean): void;
  retryElevation(): void;
  retryPresentation(): void;
  confirm(): Promise<void>;
  patch(id: string, patch: Partial<SavedTrail>): void;
  remove(id: string): void;
  select(id: string): void;
}

function draftGeoJSON(tool: TrailTool) {
  if (tool.phase === 'paint' || tool.phase === 'place-tail' || tool.phase === 'analyzing')
    return draftToGeoJSON(tool.polygons);
  if (tool.phase === 'review') return draftToGeoJSON([], { parts: tool.draft.parts,
    difficulty: tool.draft.difficulty, name: tool.draft.name,
    infeasibleLines: tool.draft.infeasibleLines });
  return draftToGeoJSON([]);
}

function presentationPreviewTrail(draft: DraftTrail): SavedTrail {
  const stats = trailPartsStats(draft.parts);
  return {
    id: '__trail-review__', name: draft.name, parts: draft.parts,
    brushWidthM: draft.brushWidthM, areaM2: draft.areaM2,
    lengthM: stats.lengthM, verticalM: stats.verticalM,
    avgSlopeDeg: stats.avgSlopeDeg, maxSlopeDeg: stats.maxSlopeDeg,
    difficulty: draft.difficulty, status: draft.status, createdAt: 'preview',
  };
}

export function useTrailController(options: TrailControllerOptions): TrailController {
  const [state, dispatch] = useReducer(reduceTrailTool, IDLE_TRAIL_TOOL);
  const [brushWidthM, setBrushWidthM] = useState(DEFAULT_BRUSH_WIDTH_M);
  const [presentationError, setPresentationError] = useState<string | null>(null);
  const stateRef = useRef<TrailTool>(state), brushWidthRef = useRef(brushWidthM);
  const optionsRef = useRef(options), commandsRef = useRef<TrailPaintCommand[]>([]);
  const replayRef = useRef<TrailPaintCommand[]>([]), pendingUntilRef = useRef(0);
  const previewPathRef = useRef<[number, number][]>([]);
  const brushCursorRef = useRef<[number, number] | null>(null);
  const sampleTokenRef = useRef(0), gradeResultRef = useRef<TerrainGradeSuccess | null>(null);
  const latestPresentationRef = useRef<TrailPresentationResult>({
    version: TRAIL_PRESENTATION_VERSION, surface: [], routes: [], labels: [], junctions: [],
  });
  const previewPresentationRef = useRef<TrailPresentationResult | null>(null);
  const hoveredTrailRef = useRef<string | null>(null);
  const captureHiddenRef = useRef(false);
  stateRef.current = state; brushWidthRef.current = brushWidthM; optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'trail', zOrder: MAP_Z_ORDER.trail,
    hits: [{ id: 'trail', priority: MAP_HIT_RANK.trail,
      layerIds: ['trail-hit', 'dashboard-trail-hit'],
      resolve: (features, lngLat) => {
        const dashboard = features.find((feature) =>
          typeof feature.properties?.edgeKind === 'string');
        if (dashboard && typeof dashboard.properties?.id === 'string') return {
          featureId: dashboard.properties.id,
          properties: dashboard.properties as Record<string, unknown>,
        };
        const candidates = features.map((feature) => feature.properties?.id)
          .filter((id): id is string => typeof id === 'string');
        const current = optionsRef.current;
        const id = resolveTrailHitId(current.trails, candidates, [lngLat.lng, lngLat.lat],
          current.selectedTrailId);
        if (!id) return null;
        return { featureId: id, properties: { id } };
      },
      select: (id) => select(id),
      hover: (target) => {
        hoveredTrailRef.current = target?.featureId ?? null;
        const map = optionsRef.current.mapRef.current;
        if (map) setTrailHover(map, hoveredTrailRef.current);
      } }],
    install: ({ map }) => addTrailLayers(map),
    synchronizeData: ({ map }) => {
      const current = optionsRef.current;
      setTrailData(map, trailPresentationToGeoJSON(
        previewPresentationRef.current ?? latestPresentationRef.current));
      setTrailHitData(map, trailsToHitGeoJSON([...current.trails]));
      setTrailSelection(map, current.selectedTrailId);
      setTrailHover(map, hoveredTrailRef.current);
      applyTrailTheme(map, current.theme);
      setTrailDraftData(map, draftGeoJSON(stateRef.current));
      current.restoreGradePreview(map);
      const tool = stateRef.current;
      setTrailPaintPreview(map, { path: tool.phase === 'paint' ? previewPathRef.current : [],
        cursor: tool.phase === 'paint' ? brushCursorRef.current : null,
        brushWidthM: brushWidthRef.current, ...trailHeadPreview(tool) });
    },
    visibility: () => optionsRef.current.structuresVisible() ? [{
      id: 'trails', label: 'Ski trails', layerIds: TRAIL_BUILT_LAYER_IDS,
      visible: true, section: 'Structures',
    }] : [],
    setCaptureTransient: ({ map }, hidden) => {
      const tool = stateRef.current;
      captureHiddenRef.current = hidden;
      setTrailData(map, trailPresentationToGeoJSON(hidden ? latestPresentationRef.current :
        previewPresentationRef.current ?? latestPresentationRef.current));
      setTrailDraftData(map, hidden ? draftToGeoJSON([]) : draftGeoJSON(tool));
      setTrailPaintPreview(map, hidden ? { path: [], cursor: null,
        brushWidthM: brushWidthRef.current } : {
        path: previewPathRef.current, cursor: brushCursorRef.current,
        brushWidthM: brushWidthRef.current, ...trailHeadPreview(tool),
      });
    },
    cleanup: () => {},
  };

  const draftPolygons = state.phase === 'paint' || state.phase === 'place-tail' ||
    state.phase === 'analyzing' ? state.polygons : null;
  const review = state.phase === 'review' ? state.draft : null;
  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (map) setTrailHitData(map, trailsToHitGeoJSON([...optionsRef.current.trails]));
    compilePresentation(review);
  }, [options.trails, options.junctions, review]);
  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map) return;
    setTrailSelection(map, options.selectedTrailId);
    applyTrailTheme(map, options.theme);
  }, [options.selectedTrailId, options.theme]);
  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (map) setTrailDraftData(map, draftGeoJSON(stateRef.current));
  }, [state.phase, draftPolygons, review?.parts, review?.difficulty, review?.name,
    review?.infeasibleLines]);
  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map) return;
    const tool = stateRef.current;
    setTrailPaintPreview(map, { path: tool.phase === 'paint' ? previewPathRef.current : [],
      cursor: tool.phase === 'paint' ? brushCursorRef.current : null,
      brushWidthM, ...trailHeadPreview(tool) });
  }, [brushWidthM]);
  useEffect(() => () => { sampleTokenRef.current++; optionsRef.current.paintAdapter.stop();
    optionsRef.current.presentationAdapter.cancel(); optionsRef.current.release(); }, []);

  useTrailMapInput({ mapRef: options.mapRef, state, stateRef,
    lifts: options.lifts, trails: options.trails, brushWidthRef, commandsRef,
    previewPathRef, brushCursorRef, dispatch,
    acquireInteractions: (overrides) => {
      const map = optionsRef.current.mapRef.current;
      if (!map) throw new Error('Map is unavailable.');
      return optionsRef.current.acquireInteractions(map, overrides);
    },
    beginPainting, analyzeTail: () => optionsRef.current.paintAdapter.post({ type: 'finish' }),
    submit: submitCommand, cancel, backToPaint,
  });

  function compilePresentation(preview: DraftTrail | null = null): void {
    const current = optionsRef.current;
    const previewTrail = preview ? presentationPreviewTrail(preview) : null;
    current.presentationAdapter.compile({ trails: previewTrail
      ? [...current.trails, previewTrail] : [...current.trails],
      junctions: [...current.junctions] }, {
      onResult: (result) => {
        if (previewTrail) previewPresentationRef.current = result;
        else {
          previewPresentationRef.current = null;
          latestPresentationRef.current = result;
        }
        setPresentationError(null);
        const map = optionsRef.current.mapRef.current;
        if (map) setTrailData(map, trailPresentationToGeoJSON(
          previewTrail && captureHiddenRef.current ? latestPresentationRef.current : result));
      },
      onError: setPresentationError,
    });
  }

  function retryPresentation(): void {
    const current = stateRef.current;
    compilePresentation(current.phase === 'review' ? current.draft : null);
  }

  function clearGrade(): void { gradeResultRef.current = null;
    optionsRef.current.gradeChanged(); }

  function arm(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock();
    commandsRef.current = []; pendingUntilRef.current = 0;
    optionsRef.current.gradeAdapter.stop(); clearGrade();
    optionsRef.current.paintAdapter.allowRestart(); dispatch({ type: 'arm' });
  }

  function beginPainting(anchor: TrailHeadAnchor): void {
    const seed: TrailPaintCommand = { mode: 'paint', path: [anchor.point, anchor.point], seed: true };
    commandsRef.current = [seed]; optionsRef.current.paintAdapter.allowRestart();
    dispatch({ type: 'begin-paint', anchor }); startPaintWorker(brushWidthRef.current, [seed]);
  }

  function changeHead(): void { optionsRef.current.paintAdapter.stop(); commandsRef.current = [];
    pendingUntilRef.current = 0; previewPathRef.current = []; brushCursorRef.current = null;
    dispatch({ type: 'arm' }); }

  function cancel(): void {
    sampleTokenRef.current++; optionsRef.current.terrain.preview.invalidate();
    optionsRef.current.gradeAdapter.stop(); clearGrade(); optionsRef.current.paintAdapter.stop();
    commandsRef.current = []; pendingUntilRef.current = 0;
    previewPathRef.current = []; brushCursorRef.current = null;
    const map = optionsRef.current.mapRef.current;
    if (map) setTrailPaintPreview(map, { path: [], cursor: null,
      brushWidthM: brushWidthRef.current });
    dispatch({ type: 'cancel' }); optionsRef.current.release();
  }

  function startPaintWorker(widthM: number, replay: TrailPaintCommand[]): void {
    replayRef.current = replay;
    const center = optionsRef.current.mapRef.current?.getCenter();
    const origin: [number, number] = center ? [center.lng, center.lat] : [-121.474, 46.928];
    optionsRef.current.paintAdapter.start({ origin, brushWidthM: widthM }, {
      onReady: () => { const pending = replayRef.current; replayRef.current = [];
        for (const command of pending) submitCommand(command); },
      onFailure: (error) => {
        const current = stateRef.current;
        if (current.phase === 'paint' && current.pending && commandsRef.current.length > 1)
          commandsRef.current.pop();
        const canUndo = commandsRef.current.length > 1;
        const anchor = current.phase === 'paint' || current.phase === 'analyzing'
          ? current.anchor.point : null;
        const hasUserStroke = anchor ? hasUserTrailStroke(commandsRef.current, anchor) : false;
        if (current.phase === 'paint') dispatch({ type: 'paint-patch', patch: {
          pending: false, activeAreaM2: null, error, canUndo, hasUserStroke } });
        else if (current.phase === 'analyzing') dispatch({ type: 'analysis-failed',
          error, canUndo, hasUserStroke });
      },
      onPreview: (message) => { previewPathRef.current = [];
        const map = optionsRef.current.mapRef.current;
        if (map) setTrailPaintPreview(map, { path: [], cursor: brushCursorRef.current,
          brushWidthM: brushWidthRef.current, ...trailHeadPreview(stateRef.current) });
        const current = stateRef.current;
        if (current.phase === 'paint') dispatch({ type: 'paint-patch', patch: {
          polygons: message.polygons, areaM2: message.areaM2, activeAreaM2: null,
          canUndo: commandsRef.current.length > 1,
          hasUserStroke: hasUserTrailStroke(commandsRef.current, current.anchor.point),
          pending: message.id < pendingUntilRef.current, error: null } });
      },
      onAnalysis: (message) => {
        const current = stateRef.current;
        if (current.phase !== 'analyzing') return;
        const canUndo = commandsRef.current.length > 1;
        const hasStroke = hasUserTrailStroke(commandsRef.current, current.anchor.point);
        if (!message.parts.length) { dispatch({ type: 'analysis-failed', canUndo,
          hasUserStroke: hasStroke,
          error: 'Paint a longer connected footprint so a centerline can be found.' }); return; }
        const parts = pinTrailEndpoints(message.parts, current.anchor.point, current.tailAnchor.point);
        if (!parts) { dispatch({ type: 'analysis-failed', canUndo, hasUserStroke: true,
          error: 'The trailhead and trail end must be connected by one painted footprint.' }); return; }
        const draft = createTrailDraft(parts, message.areaM2, brushWidthRef.current,
          optionsRef.current.trails, current.anchor, current.tailAnchor);
        dispatch({ type: 'review', draft });
        sampleElevations(parts, current.anchor, current.tailAnchor);
      },
      onRestart: () => { replayRef.current = commandsRef.current.map((command) =>
        ({ ...command, path: command.path.slice() }));
        dispatch({ type: 'paint-patch', patch: { pending: replayRef.current.length > 0,
          error: 'Restarting trail analysis…' } }); },
      onLost: () => dispatch({ type: 'paint-patch', patch: { pending: false,
        error: 'Trail analysis worker stopped. Cancel and reopen the painter to retry.' } }),
    });
  }

  function postStroke(path: [number, number][], mode: 'paint' | 'erase'): number {
    const coordinates = new Float64Array(path.length * 2);
    path.forEach((point, index) => { coordinates[index * 2] = point[0];
      coordinates[index * 2 + 1] = point[1]; });
    return optionsRef.current.paintAdapter.post({ type: 'stroke', mode, coordinates },
      [coordinates.buffer]);
  }
  function submitCommand(command: TrailPaintCommand): void {
    dispatch({ type: 'paint-patch', patch: { pending: true, activeAreaM2: null } });
    let finalId = postStroke(command.path, command.mode);
    if (command.restoreSeed) finalId = postStroke([command.restoreSeed, command.restoreSeed], 'paint');
    pendingUntilRef.current = finalId;
  }
  function setPaintMode(mode: 'paint' | 'erase'): void { dispatch({ type: 'paint-patch', patch: { mode } });
    const map = optionsRef.current.mapRef.current; if (map) setTrailPaintMode(map, mode); }
  function undoPaint(): void { if (commandsRef.current.length <= 1) return;
    const removed = commandsRef.current.pop()!, current = stateRef.current;
    if (current.phase !== 'paint') return;
    dispatch({ type: 'paint-patch', patch: { pending: true,
      canUndo: commandsRef.current.length > 1,
      hasUserStroke: hasUserTrailStroke(commandsRef.current, current.anchor.point) } });
    let finalId = optionsRef.current.paintAdapter.post({ type: 'undo' });
    if (removed.restoreSeed) finalId = optionsRef.current.paintAdapter.post({ type: 'undo' });
    pendingUntilRef.current = finalId; }
  function clearPaint(): void { const current = stateRef.current;
    if (current.phase !== 'paint') return;
    const seed: TrailPaintCommand = { mode: 'paint',
      path: [current.anchor.point, current.anchor.point], seed: true };
    commandsRef.current = [seed]; dispatch({ type: 'paint-patch', patch: { polygons: [],
      areaM2: 0, activeAreaM2: null, pending: true, mode: 'paint', canUndo: false,
      hasUserStroke: false, error: null } });
    optionsRef.current.paintAdapter.post({ type: 'clear' });
    pendingUntilRef.current = postStroke(seed.path, 'paint'); }
  function finishPaint(): void { const current = stateRef.current;
    if (current.phase === 'paint' && !current.pending && current.hasUserStroke)
      dispatch({ type: 'place-tail' }); }
  function backToPaint(): void { dispatch({ type: 'back-to-paint' }); }
  function changeBrushWidth(widthM: number): void { setBrushWidthM(widthM);
    const current = stateRef.current;
    if (current.phase !== 'paint' || current.hasUserStroke) return;
    const seed: TrailPaintCommand = { mode: 'paint',
      path: [current.anchor.point, current.anchor.point], seed: true };
    commandsRef.current = [seed]; dispatch({ type: 'paint-patch', patch: { polygons: [],
      areaM2: 0, activeAreaM2: null, pending: true, canUndo: false, error: null } });
    startPaintWorker(widthM, [seed]); }
  function patchDraft(patch: Partial<DraftTrail>): void {
    dispatch({ type: 'review-patch', patch }); }

  function sampleElevations(parts: SavedTrailPart[], anchor: AnchorRef, tail: AnchorRef): void {
    const map = optionsRef.current.mapRef.current;
    const zoom = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++sampleTokenRef.current;
    dispatch({ type: 'review-patch', patch: { elevStatus: 'pending', elevError: null } });
    const fail = (error: string) => dispatch({ type: 'review-patch', patch: {
      elevStatus: 'error', elevError: error } });
    void Promise.all(parts.map(async (part) => { const elevations = await optionsRef.current
      .sampleProfile(part.centerline, zoom); return elevations ? { ...part,
        centerlineElevM: elevations } : null; })).then((sampled) => {
      if (token !== sampleTokenRef.current) return;
      const resolved = sampled.filter((part): part is SavedTrailPart => part !== null);
      if (resolved.length !== sampled.length) { fail(
        'No terrain data covers this run. Check that the resort package finished downloading.'); return; }
      const pinned = pinTrailEndpoints(resolved, anchor.point, tail.point);
      if (!pinned) { fail('The trailhead and trail end are not joined by one painted footprint.'); return; }
      const stats = trailPartsStats(pinned);
      dispatch({ type: 'review-patch', patch: { parts: pinned, ungradedParts: pinned,
        elevStatus: 'ok', elevError: null,
        difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
        anchor, tailAnchor: tail } });
    }, (error: unknown) => { if (token === sampleTokenRef.current)
      fail(error instanceof Error ? error.message : 'Elevation unavailable.'); });
  }

  function failGrade(error: string): void { dispatch({ type: 'review-patch', patch: {
    gradingStatus: 'error', gradingError: error } }); }
  function setGrading(enabled: boolean): void {
    const current = stateRef.current, record = optionsRef.current.terrainRecord();
    if (current.phase !== 'review') return;
    const requestId = optionsRef.current.terrain.preview.claim(); clearGrade();
    if (!enabled) { optionsRef.current.gradeAdapter.stop();
      const stats = trailPartsStats(current.draft.ungradedParts);
      dispatch({ type: 'review-patch', patch: { parts: current.draft.ungradedParts,
        areaM2: current.draft.ungradedAreaM2,
        difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
        gradingEnabled: false, gradingStatus: 'idle', gradingError: null,
        earthwork: null, maxGroundCrossSlopePct: 0, maxFaceSlopePct: 0,
        maxDisturbedWidthM: 0, ungradedLengthM: 0, infeasibleLines: [] } }); return; }
    if (!record?.bounds) { dispatch({ type: 'review-patch', patch: { gradingEnabled: true,
      gradingStatus: 'error', gradingError: 'The local elevation package is unavailable.' } }); return; }
    dispatch({ type: 'review-patch', patch: { gradingEnabled: true,
      gradingStatus: 'pending', gradingError: null, earthwork: null } });
    requestAnimationFrame(() => runGrade(requestId, record, current.draft));
  }

  function protectedPolygons(): [number, number][][][] { return optionsRef.current.trails.flatMap(
    (trail) => trail.parts.map((part) => part.polygon)); }
  function gradeKey(draft: DraftTrail): string { return terrainGradeGeometryKey(
    draft.ungradedParts, draft.brushWidthM, protectedPolygons(), 'trail', TRAIL_GRADE_POLICY); }
  function runGrade(requestId: number, record: TerrainRecord, draft: DraftTrail): void {
    if (!optionsRef.current.terrain.preview.isCurrent(requestId) || !record.bounds) return;
    const baseElevationChecksum = record.packageManifest?.elevationChecksum ?? '';
    optionsRef.current.gradeAdapter.run({ id: requestId,
      heights: optionsRef.current.heightGrid(record), gridSize: record.sampleGridSize,
      bounds: record.bounds, parts: draft.ungradedParts, brushWidthM: draft.brushWidthM,
      kind: 'trail', protectedPolygons: protectedPolygons(), ...TRAIL_GRADE_POLICY,
      baseElevationChecksum, trailGeometryKey: gradeKey(draft),
      contourGridSize: record.contourMetadata?.gridSize,
      contourIntervalM: record.contourMetadata?.intervalM }, {
      isCurrent: (id) => optionsRef.current.terrain.preview.isCurrent(id),
      live: () => { const active = stateRef.current; return {
        baseElevationChecksum: optionsRef.current.terrainRecord()?.packageManifest?.elevationChecksum ?? '',
        trailGeometryKey: active.phase === 'review' ? gradeKey(active.draft) : '' }; },
      onSuperseded: () => { clearGrade(); failGrade(
        'The trail or terrain changed while grading. Uncheck and retry the preview.'); },
      onError: failGrade,
      onCrash: () => failGrade('Terrain grading worker stopped unexpectedly.'),
      onResult: (response) => { gradeResultRef.current = response;
        const active = stateRef.current;
        if (active.phase !== 'review' || !active.draft.gradingEnabled) return;
        const parts = active.draft.ungradedParts.map((part, index) => ({ ...part,
          polygon: response.expandedPolygons[index] ?? part.polygon,
          centerlineElevM: response.gradedElevations[index] ?? part.centerlineElevM }));
        const stats = trailPartsStats(parts);
        dispatch({ type: 'review-patch', patch: { parts, areaM2: trailAreaM2(parts),
          difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
          gradingStatus: 'ok', gradingError: null,
          earthwork: { cutM3: response.cutM3, fillM3: response.fillM3,
            balanceM3: response.balanceM3 },
          maxGroundCrossSlopePct: response.maxGroundCrossSlopePct,
          maxFaceSlopePct: response.maxFaceSlopePct,
          maxDisturbedWidthM: response.maxDisturbedWidthM,
          ungradedLengthM: response.ungradedLengthM,
          infeasibleLines: response.infeasibleLines } });
        optionsRef.current.gradeChanged(); },
    });
  }
  function retryElevation(): void { const current = stateRef.current;
    if (current.phase === 'review' && current.draft.anchor && current.draft.tailAnchor)
      sampleElevations(current.draft.parts, current.draft.anchor, current.draft.tailAnchor); }
  function terrainGradeCommit(draft: DraftTrail): TerrainCommitRequest {
    const { record, revision } = optionsRef.current.terrain.snapshot();
    const result = gradeResultRef.current;
    if (!record || !result) throw new Error('The terrain grading preview is not ready.');
    if (result.trailGeometryKey !== gradeKey(draft)) throw new Error(
      'The trail changed after this grading preview. Recalculate the grade and try again.');
    return { expectedRevision: revision,
      record: applyTerrainGradeToRecord(record as TerrainRecord, result), kind: 'elevation' };
  }

  async function confirm(): Promise<void> {
    const current = stateRef.current;
    if (current.phase !== 'review') return;
    const draft = current.draft;
    const commitGrading = draft.status === 'complete' && draft.gradingEnabled;
    if (commitGrading && (draft.gradingStatus !== 'ok' || !gradeResultRef.current)) return;
    const edit = optionsRef.current.topology.begin();
    const materialize = (anchor: AnchorRef): SavedJunction | null => {
      if (anchor.kind === 'trail') return edit.splitTrail(anchor.trailId, anchor.point,
        optionsRef.current.createId);
      if (anchor.kind === 'lift') return edit.liftTerminalJunction([...optionsRef.current.lifts],
        anchor.liftId, anchor.end, anchor.point, optionsRef.current.createId);
      return null;
    };
    if (!draft.anchor || !draft.tailAnchor) { edit.abort(); return; }
    const head = materialize(draft.anchor), tail = materialize(draft.tailAnchor);
    if (!head || !tail) { edit.abort(); return; }
    const built = buildSavedTrail(draft, optionsRef.current.trails,
      optionsRef.current.createId(), optionsRef.current.now(), head, tail);
    if (!built) { edit.abort(); return; }
    const gradePolygons = built.commitGrading ? gradeResultRef.current?.disturbancePolygons : undefined;
    let confirmed = false;
    await optionsRef.current.terrain.runConstruction('trail', async () => {
      try {
        await new Promise(requestAnimationFrame);
        edit.addTrail(built.trail);
        const commit = commitDocuments({ terrain: optionsRef.current.terrain, topology: edit,
          terrainCommit: built.commitGrading ? terrainGradeCommit(draft) : undefined });
        if (!commit.ok) throw new Error(commit.reason === 'terrain-stale'
          ? 'The terrain changed after this grading preview. Recalculate the grade and try again.'
          : 'The trail network changed while building. Repaint the run.');
        sampleTokenRef.current++; optionsRef.current.paintAdapter.stop();
        optionsRef.current.gradeAdapter.stop(); clearGrade(); confirmed = true;
        const source = gradePolygons ?? built.trail.parts.map((part) => part.polygon);
        try { await optionsRef.current.clearCover(source.map((polygon, index) => ({
          polygon: jitterPolygon(polygon, TRAIL_CLEAR_JITTER_M, `${built.trail.id}:${index}`),
        }))); } catch { /* Cover clearing is best-effort after the coherent commit. */ }
      } catch (error) { failGrade(error instanceof Error ? error.message
        : 'Unable to save the terrain grade.'); }
      finally { if (confirmed) { dispatch({ type: 'cancel' }); optionsRef.current.release(); } }
    });
  }

  function patch(id: string, value: Partial<SavedTrail>): void { const edit = optionsRef.current.topology.begin();
    edit.patchTrail(id, value); edit.commit(); }
  function remove(id: string): void {
    const target = optionsRef.current.trails.find((trail) => trail.id === id);
    if (!target) return;
    const owned = new Set(target.parts.flatMap((part) => (part.segments ?? []).flatMap((segment) =>
      [segment.fromJunctionId, segment.toJunctionId])));
    const dependentTrails = optionsRef.current.trails.filter((trail) => trail.id !== id &&
      trail.parts.some((part) => (part.segments ?? []).some((segment) =>
        owned.has(segment.fromJunctionId) || owned.has(segment.toJunctionId))));
    const dependentPaths = optionsRef.current.paths.filter((path) =>
      (path.fromJunctionId && owned.has(path.fromJunctionId)) ||
      (path.toJunctionId && owned.has(path.toJunctionId)));
    if (dependentTrails.length || dependentPaths.length) {
      optionsRef.current.reportBlockedDelete(`Remove connected ${[
        ...dependentTrails.map((trail) => trail.name),
        ...dependentPaths.map((path) => path.name)].join(', ')} before deleting ${target.name}.`); return; }
    const edit = optionsRef.current.topology.begin();
    if (!edit.removeTrail(id)) { edit.abort(); return; }
    edit.commit(); optionsRef.current.clearSelected(id); optionsRef.current.closeEditing();
  }
  function select(id: string): void { sampleTokenRef.current++; optionsRef.current.select(id); }

  return { state, brushWidthM, presentationError, contribution: contributionRef.current,
    activeGradePreview: () => stateRef.current.phase === 'review' ? gradeResultRef.current : null,
    arm, cancel, setPaintMode, undoPaint, clearPaint, finishPaint, changeHead, backToPaint,
    changeBrushWidth, patchDraft, setGrading, retryElevation, retryPresentation,
    confirm, patch, remove, select };
}
