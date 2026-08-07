import { useEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedPond } from '../types/snowmaking';
import type { TerrainRecord } from '../types/terrain';
import { haversineMeters } from '../geo';
import { analyzeStandalonePond, nextPondName, suggestedPondTopElevationM } from '../pondAnalysis';
import { designPondEarthwork, MAX_POND_BERM_HEIGHT_M, pondTerrainPatch } from '../pondEarthwork';
import type { EarthworkTerrainPatch } from '../earthwork';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
import type { TerrainDocument } from './terrainDocument';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addPondLayers, POND_BUILT_LAYER_IDS, POND_HIT_LAYERS, setPondData,
  setPondDraftData, setSelectedPond, type PondDraftMapData } from './pondLayers';
import { IDLE_POND_TOOL, pondFromDraft, reducePondTool,
  type DraftPond, type PondTool } from './pondControllerModel';

function pondDraftOf(tool: PondTool): PondDraftMapData | null {
  if (tool.phase === 'drawing') return {
    points: tool.points, cursor: tool.cursor, closed: false,
  };
  if (tool.phase === 'review') return {
    points: tool.draft.boundary.slice(0, -1), cursor: null, closed: true,
    topElevationM: tool.draft.topElevationM, averageDepthM: tool.draft.averageDepthM,
  };
  return null;
}

export interface PondControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  ponds: readonly SavedPond[];
  selectedId: string | null;
  add(pond: SavedPond): void;
  patch(id: string, value: Partial<SavedPond>): void;
  remove(id: string): void;
  select(id: string): void;
  clearSelected(id: string): void;
  canArm(): boolean;
  activate(): boolean;
  release(): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  terrain: TerrainDocument;
  terrainRevision: number;
  terrainRecord(): TerrainRecord | null;
  gradeChanged(): void;
  clearCover(polygons: [number, number][][][]): Promise<void>;
  createId(): string;
  now(): string;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

export interface PondController {
  readonly state: PondTool;
  readonly contribution: ManagedMapContribution;
  activeGradePreview(): EarthworkTerrainPatch | null;
  arm(): void;
  cancel(): void;
  undo(): void;
  finish(): void;
  patchDraft(patch: Partial<DraftPond>): void;
  changeElevation(value: number): void;
  changeExcavation(value: number): void;
  confirm(): Promise<void>;
  select(id: string): void;
  remove(id: string): void;
  setSnowmaking(id: string, enabled: boolean): void;
}

export function usePondController(options: PondControllerOptions): PondController {
  const [state, dispatch] = useReducer(reducePondTool, IDLE_POND_TOOL);
  const stateRef = useRef(state);
  const pondsRef = useRef(options.ponds);
  const optionsRef = useRef(options);
  const gradeRef = useRef<EarthworkTerrainPatch | null>(null);
  stateRef.current = state;
  pondsRef.current = options.ponds;
  optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'pond', zOrder: MAP_Z_ORDER.pond,
    hits: [{ id: 'pond', priority: MAP_HIT_RANK.pond, layerIds: POND_HIT_LAYERS,
      select: (id) => optionsRef.current.select(id) }],
    install: ({ map }) => addPondLayers(map),
    synchronizeData: ({ map }) => {
      const record = optionsRef.current.terrainRecord();
      setPondData(map, [...pondsRef.current], record);
      setPondDraftData(map, pondDraftOf(stateRef.current), record);
      setSelectedPond(map, optionsRef.current.selectedId);
    },
    visibility: (): MapVisibilityDescriptor[] => optionsRef.current.structuresVisible()
      ? [{ id: 'standalone-ponds', label: 'Standalone ponds', layerIds: POND_BUILT_LAYER_IDS,
        visible: true, section: 'Structures' }]
      : [],
    setCaptureTransient: ({ map }, hidden) => setPondDraftData(map,
      hidden ? null : pondDraftOf(stateRef.current), optionsRef.current.terrainRecord()),
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [state, options.ponds, options.selectedId, options.terrainRevision]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'drawing')) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = stateRef.current;
      const bounds = optionsRef.current.terrainRecord()?.bounds;
      if (!bounds || point[0] < bounds.west || point[0] > bounds.east ||
        point[1] < bounds.south || point[1] > bounds.north) {
        dispatch({ type: 'point-failed', error: current.phase === 'drawing'
          ? 'Keep the pond boundary inside the available terrain.'
          : 'Choose a point inside the available terrain.' });
        return;
      }
      if (current.phase === 'drawing') {
        const last = current.points.at(-1);
        if (last && haversineMeters(last, point) < 1) return;
      }
      dispatch({ type: 'add-point', point });
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      if (stateRef.current.phase === 'drawing')
        dispatch({ type: 'move', point: [event.lngLat.lng, event.lngLat.lat] });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancel();
      else if (event.key === 'Backspace') { event.preventDefault(); undo(); }
      else if (event.key === 'Enter') finish();
    };
    map.on('click', onClick); map.on('mousemove', onMove); window.addEventListener('keydown', onKey);
    return () => { map.off('click', onClick); map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey); interaction.release(); };
    // Tool callbacks intentionally read live refs; resubscribe only when the phase changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.phase]);

  useEffect(() => () => { optionsRef.current.release(); }, []);

  function refreshGrade(topElevationM: number, excavationDepthM: number,
    boundary: [number, number][], areaM2: number): void {
    const record = optionsRef.current.terrainRecord();
    const design = record && designPondEarthwork(record, boundary,
      { topElevationM, excavationDepthM, poolAreaM2: areaM2 });
    gradeRef.current = design && record ? pondTerrainPatch(record, design) : null;
    optionsRef.current.gradeChanged();
  }

  function arm(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.terrainRecord() ||
      !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection();
    optionsRef.current.openDock();
    dispatch({ type: 'arm' });
  }

  function cancel(): void {
    gradeRef.current = null;
    dispatch({ type: 'cancel' });
    optionsRef.current.gradeChanged();
    optionsRef.current.release();
  }

  function undo(): void { dispatch({ type: 'undo' }); }

  function finish(): void {
    const current = stateRef.current;
    const record = optionsRef.current.terrainRecord();
    if (current.phase !== 'drawing' || current.points.length < 3 || !record) return;
    const topElevationM = suggestedPondTopElevationM(record, current.points);
    if (topElevationM == null) {
      dispatch({ type: 'point-failed',
        error: 'The pond boundary does not have valid terrain coverage.' });
      return;
    }
    const outcome = analyzeStandalonePond(record, current.points, topElevationM);
    if (!outcome.ok) {
      dispatch({ type: 'point-failed', error: outcome.error });
      return;
    }
    const draft: DraftPond = {
      name: nextPondName([...pondsRef.current]), isSnowmaking: true, ...outcome.result,
    };
    dispatch({ type: 'review', draft });
    refreshGrade(topElevationM, outcome.result.excavationDepthM,
      outcome.result.boundary, outcome.result.areaM2);
  }

  function redesign(topElevationM: number, excavationDepthM: number): void {
    const current = stateRef.current;
    const record = optionsRef.current.terrainRecord();
    if (current.phase !== 'review' || !record) return;
    const points = current.draft.boundary.slice(0, -1);
    const outcome = analyzeStandalonePond(record, points, topElevationM, excavationDepthM);
    if (!outcome.ok) {
      dispatch({ type: 'design-failed', topElevationM, excavationDepthM, error: outcome.error });
      gradeRef.current = null;
      optionsRef.current.gradeChanged();
      return;
    }
    dispatch({ type: 'patch', patch: outcome.result });
    refreshGrade(topElevationM, outcome.result.excavationDepthM,
      outcome.result.boundary, outcome.result.areaM2);
  }

  async function confirm(): Promise<void> {
    const current = stateRef.current;
    if (current.phase !== 'review' || current.error) return;
    const draft = current.draft;
    const outcome = await optionsRef.current.terrain.runConstruction(
      'pond', async (): Promise<EarthworkTerrainPatch | null> => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = optionsRef.current.terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        const design = designPondEarthwork(record, draft.boundary, {
          topElevationM: draft.topElevationM,
          excavationDepthM: draft.excavationDepthM ?? 0,
          poolAreaM2: draft.areaM2,
        });
        if (!design) throw new Error('The pond could not be graded into this terrain.');
        if (design.truncated || design.maxBermHeightM > MAX_POND_BERM_HEIGHT_M)
          throw new Error('The berm no longer fits this terrain. Adjust the top of pond and try again.');
        const patch = pondTerrainPatch(record, design);
        const result = optionsRef.current.terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record as TerrainRecord, patch), kind: 'elevation' });
        if (!result.ok) throw new Error('The terrain changed while building. Redraw the pond.');
        optionsRef.current.add(pondFromDraft(draft, design, pondsRef.current,
          optionsRef.current.createId(), optionsRef.current.now()));
        gradeRef.current = null;
        dispatch({ type: 'cancel' });
        optionsRef.current.release();
        return patch;
      } catch (error) {
        dispatch({ type: 'design-failed', topElevationM: draft.topElevationM,
          excavationDepthM: draft.excavationDepthM ?? 0,
          error: error instanceof Error ? error.message : 'Unable to build this pond.' });
        return null;
      }
    });
    if (!outcome.ok || !outcome.value) return;
    try { await optionsRef.current.clearCover(outcome.value.disturbancePolygons); }
    catch { /* best effort */ }
  }

  return {
    state, contribution: contributionRef.current,
    activeGradePreview: () => gradeRef.current,
    arm, cancel, undo, finish,
    patchDraft: (patch) => dispatch({ type: 'patch', patch }),
    changeElevation: (value) => {
      const current = stateRef.current;
      if (current.phase === 'review') redesign(value, current.draft.excavationDepthM ?? 0);
    },
    changeExcavation: (value) => {
      const current = stateRef.current;
      if (current.phase === 'review') redesign(current.draft.topElevationM, value);
    },
    confirm,
    select: (id) => optionsRef.current.select(id),
    remove: (id) => { optionsRef.current.remove(id); optionsRef.current.clearSelected(id); },
    setSnowmaking: (id, enabled) => optionsRef.current.patch(id, { isSnowmaking: enabled }),
  };
}
