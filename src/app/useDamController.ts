import { useEffect, useLayoutEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedDam } from '../types/snowmaking';
import type { TerrainRecord } from '../types/terrain';
import { damCrestElevationAt, nextDamName, snapDamEndpoint } from '../damAnalysis';
import type { EarthworkTerrainPatch } from '../earthwork';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
import type { TerrainDocument } from './terrainDocument';
import type { DamAnalysisAdapter } from './damAnalysisClient';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addDamLayers, DAM_BUILT_LAYER_IDS, DAM_HIT_LAYERS, setDamData,
  setDamDraftData, setSelectedDam, type DamDraftMapData } from './damLayers';
import { damFromDraft, IDLE_DAM_TOOL, reduceDamTool,
  type DamTool, type DraftDam } from './damControllerModel';

function largestFootprint(polygons: [number, number][][][]): [number, number][][] | undefined {
  if (!polygons.length) return undefined;
  return polygons.reduce((best, polygon) => polygon[0].length > best[0].length ? polygon : best);
}

function damDraftOf(tool: DamTool): DamDraftMapData | null {
  if (tool.phase === 'anchored') return { points: [tool.first], cursor: tool.cursor };
  if (tool.phase === 'analyzing') return { points: tool.points, cursor: null };
  if (tool.phase === 'review') return { points: tool.draft.points, cursor: null,
    pondRings: tool.draft.pondRings, crestElevationM: tool.draft.crestElevationM,
    averageDepthM: tool.draft.averageDepthM, footprintRings: tool.draft.footprintRings,
    crestRing: tool.draft.crestRing };
  return null;
}

export interface DamControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  dams: readonly SavedDam[];
  selectedId: string | null;
  add(dam: SavedDam): void;
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
  streamWidthOverrides(): Record<string, number>;
  analysis: DamAnalysisAdapter;
  gradeChanged(): void;
  clearCover(polygons: [number, number][][][]): Promise<void>;
  createId(): string;
  now(): string;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

export interface DamController {
  readonly state: DamTool;
  readonly contribution: ManagedMapContribution;
  activeGradePreview(): EarthworkTerrainPatch | null;
  arm(): void;
  cancel(): void;
  patchDraft(patch: Partial<DraftDam>): void;
  confirm(): Promise<void>;
  select(id: string): void;
  remove(id: string): void;
}

export function useDamController(options: DamControllerOptions): DamController {
  const [state, dispatch] = useReducer(reduceDamTool, IDLE_DAM_TOOL);
  const stateRef = useRef(state);
  const damsRef = useRef(options.dams);
  const optionsRef = useRef(options);
  const gradeRef = useRef<EarthworkTerrainPatch | null>(null);
  const gradeRevisionRef = useRef<number | null>(null);
  const draftFrameRef = useRef<number | null>(null);
  stateRef.current = state;
  damsRef.current = options.dams;
  optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'dam', zOrder: MAP_Z_ORDER.dam,
    hits: [{ id: 'dam', priority: MAP_HIT_RANK.dam, layerIds: DAM_HIT_LAYERS,
      select: (id) => optionsRef.current.select(id) }],
    install: ({ map }) => addDamLayers(map),
    synchronizeData: ({ map }) => {
      const record = optionsRef.current.terrainRecord();
      setDamData(map, [...damsRef.current], record);
      setDamDraftData(map, damDraftOf(stateRef.current), record);
      setSelectedDam(map, optionsRef.current.selectedId);
    },
    visibility: (): MapVisibilityDescriptor[] => optionsRef.current.structuresVisible()
      ? [{ id: 'dams', label: 'Snowmaking ponds', layerIds: DAM_BUILT_LAYER_IDS,
        visible: true, section: 'Structures' }]
      : [],
    setCaptureTransient: ({ map }, hidden) => setDamDraftData(map,
      hidden ? null : damDraftOf(stateRef.current), optionsRef.current.terrainRecord()),
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [options.dams, options.selectedId, options.terrainRevision]);
  useEffect(() => {
    if (draftFrameRef.current != null) return;
    draftFrameRef.current = requestAnimationFrame(() => {
      draftFrameRef.current = null;
      const map = optionsRef.current.mapRef.current;
      if (map) setDamDraftData(map, damDraftOf(stateRef.current), optionsRef.current.terrainRecord());
    });
  }, [state]);

  useLayoutEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'anchored')) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const current = stateRef.current;
      const record = optionsRef.current.terrainRecord();
      if (current.phase !== 'anchored' || !record) return;
      const cursor: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snapped = snapDamEndpoint(record, current.first, cursor);
      dispatch({ type: 'move', point: snapped,
        error: snapped ? null : 'No matching crest contour near the cursor.' });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const current = stateRef.current;
      const record = optionsRef.current.terrainRecord();
      if (!record) return;
      if (current.phase === 'armed') {
        const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
        const bounds = record.bounds;
        if (!bounds || point[0] < bounds.west || point[0] > bounds.east ||
          point[1] < bounds.south || point[1] > bounds.north) {
          dispatch({ type: 'arm-failed',
            error: 'Choose a bank inside the resort terrain boundary.' });
          return;
        }
        const crestElevationM = damCrestElevationAt(record, point);
        if (crestElevationM == null) {
          dispatch({ type: 'arm-failed', error: 'Choose a point within the available terrain.' });
          return;
        }
        dispatch({ type: 'anchor', point, crestElevationM });
        return;
      }
      if (current.phase !== 'anchored' || !current.cursor || !record.bounds) return;
      const points: [[number, number], [number, number]] = [current.first, current.cursor];
      const revision = optionsRef.current.terrain.snapshot().revision;
      dispatch({ type: 'analyze', points });
      optionsRef.current.analysis.run({
        heights: Float32Array.from(record.sampleHeights), gridSize: record.sampleGridSize,
        bounds: record.bounds, points, crestElevationM: current.crestElevationM,
        streams: (record.vectorFeatures?.waterLines ?? []).map((stream) => ({ ...stream,
          widthM: optionsRef.current.streamWidthOverrides()[stream.id] ?? stream.widthM })),
        contourGridSize: record.contourMetadata?.gridSize ?? Math.min(512, record.sampleGridSize),
        contourIntervalM: record.contourMetadata?.intervalM ?? 6.096,
        baseElevationChecksum: record.packageManifest?.elevationChecksum ?? '',
      }, {
        onResult: (analysis, grade) => {
          if (optionsRef.current.terrain.snapshot().revision !== revision) {
            dispatch({ type: 'analysis-failed', points,
              crestElevationM: current.crestElevationM,
              error: 'The terrain changed during analysis. Choose the opposite bank again.' });
            return;
          }
          gradeRef.current = grade;
          gradeRevisionRef.current = revision;
          dispatch({ type: 'review', draft: {
            name: nextDamName([...damsRef.current]), points,
            crestElevationM: current.crestElevationM, streamId: analysis.crossing.stream.id,
            streamName: analysis.crossing.stream.name ??
              `Unnamed ${analysis.crossing.stream.waterClass}`,
            sourceWidthM: analysis.sourceWidthM, inflowM3s: analysis.inflowM3s,
            pondRings: analysis.pondRings, areaM2: analysis.areaM2,
            averageDepthM: analysis.averageDepthM, capacityM3: analysis.capacityM3,
            averageDamHeightM: analysis.averageDamHeightM,
            maxDamHeightM: analysis.maxDamHeightM,
            damCrestElevationM: analysis.damCrestElevationM, crestRing: analysis.crestRing,
            footprintRings: largestFootprint(grade.disturbancePolygons),
            builtLengthM: analysis.builtLengthM, disturbedAreaM2: analysis.disturbedAreaM2,
            earthwork: analysis.earthwork,
          } });
          optionsRef.current.gradeChanged();
        },
        onError: (error) => dispatch({ type: 'analysis-failed', points,
          crestElevationM: current.crestElevationM, error }),
      });
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') cancel(); };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); };
  }, [state.phase]);

  useEffect(() => () => {
    if (draftFrameRef.current != null) cancelAnimationFrame(draftFrameRef.current);
    optionsRef.current.analysis.cancel();
    optionsRef.current.release();
  }, []);

  function arm(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.terrainRecord() ||
      !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection();
    optionsRef.current.openDock();
    dispatch({ type: 'arm' });
  }

  function cancel(): void {
    optionsRef.current.analysis.cancel();
    gradeRef.current = null;
    gradeRevisionRef.current = null;
    dispatch({ type: 'cancel' });
    optionsRef.current.gradeChanged();
    optionsRef.current.release();
  }

  async function confirm(): Promise<void> {
    const current = stateRef.current;
    const grade = gradeRef.current;
    const gradeRevision = gradeRevisionRef.current;
    if (current.phase !== 'review') return;
    const dam = damFromDraft(current.draft, damsRef.current,
      optionsRef.current.createId(), optionsRef.current.now());
    let committed = false;
    await optionsRef.current.terrain.runConstruction('dam', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = optionsRef.current.terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        if (!grade || gradeRevision !== revision)
          throw new Error('The terrain changed after analysis. Redraw the dam.');
        const result = optionsRef.current.terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record as TerrainRecord, grade), kind: 'elevation' });
        if (!result.ok) throw new Error('The terrain changed while building. Redraw the dam.');
        optionsRef.current.add(dam);
        gradeRef.current = null;
        gradeRevisionRef.current = null;
        dispatch({ type: 'cancel' });
        optionsRef.current.release();
        committed = true;
        optionsRef.current.select(dam.id);
      } catch (error) {
        dispatch({ type: 'build-failed',
          error: error instanceof Error ? error.message : 'Unable to build this dam.' });
      }
    });
    if (!committed) return;
    try { await optionsRef.current.clearCover(grade!.disturbancePolygons); } catch { /* best effort */ }
  }

  return {
    state, contribution: contributionRef.current,
    activeGradePreview: () => gradeRef.current,
    arm, cancel, patchDraft: (patch) => dispatch({ type: 'patch', patch }), confirm,
    select: (id) => optionsRef.current.select(id),
    remove: (id) => { optionsRef.current.remove(id); optionsRef.current.clearSelected(id); },
  };
}
