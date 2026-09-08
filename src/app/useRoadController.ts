import { useEffect, useLayoutEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { RoadType, SavedRoad } from '../types/roads';
import type { TerrainRecord } from '../types/terrain';
import type { RoadFeature } from '../types/vectorFeatures';
import { haversineMeters } from '../geo';
import { nextRoadName, roadClearingPolygons, TWO_LANE_ROAD_WIDTH_M } from '../roads';
import { strokeToPolygon } from './trailBrush';
import { terrainGradeGeometryKey, type TerrainGradeResponse } from './terrainGradeProtocol';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
import type { TerrainGradeAdapter } from './terrainGradeClient';
import type { TerrainDocument } from './terrainDocument';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addRoadDraftLayers, addRoadLayers, ROAD_HIT_LAYER_IDS, ROAD_LAYER_IDS, setRoadData,
  setRoadDraftData, setSelectedRoad, type RoadDraftLine } from './roadLayers';
import { IDLE_ROAD_TOOL, reduceRoadTool, roadFromDraft,
  type DraftRoad, type RoadTool } from './roadControllerModel';

const ROAD_GRADE_POLICY = { envelope: 'expand', maxWidthMultiplier: 3 } as const;
const EMPTY_IMPORTED_ROADS: readonly RoadFeature[] = [];
type GradeSuccess = Extract<TerrainGradeResponse, { ok: true }>;

export interface RoadControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  roads: readonly SavedRoad[];
  importedRoads?: readonly RoadFeature[];
  selectedRoadKey: string | null;
  selectRoad(key: string): void;
  addRoad(road: SavedRoad): void;
  canArm(): boolean;
  activate(): boolean;
  release(): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  terrain: TerrainDocument;
  terrainRecord(): TerrainRecord | null;
  heightGrid(record: TerrainRecord): Float32Array;
  gradeAdapter: TerrainGradeAdapter;
  showGrade(record: TerrainRecord, result: GradeSuccess): void;
  clearGrade(): void;
  clearCover(clearings: { polygon: [number, number][][] }[]): Promise<void>;
  createId(): string;
  now(): string;
  roadsVisible(): boolean;
  synchronizeMap(): void;
}

export interface RoadController {
  readonly state: RoadTool;
  readonly contribution: ManagedMapContribution;
  activeGradePreview(): GradeSuccess | null;
  arm(type: RoadType): void;
  cancel(): void;
  undo(): void;
  finish(): void;
  patchDraft(patch: Partial<DraftRoad>): void;
  confirm(): Promise<void>;
}

function roadDraftOf(tool: RoadTool): RoadDraftLine | null {
  if (tool.phase === 'drawing') return {
    points: tool.points, cursor: tool.cursor, widthM: TWO_LANE_ROAD_WIDTH_M,
  };
  if (tool.phase === 'review') return { points: tool.draft.points, cursor: null,
    widthM: TWO_LANE_ROAD_WIDTH_M,
    gradingPolygons: tool.draft.gradingPolygons,
    infeasibleLines: tool.draft.gradingInfeasibleLines };
  return null;
}

export function useRoadController(options: RoadControllerOptions): RoadController {
  const [state, dispatch] = useReducer(reduceRoadTool, IDLE_ROAD_TOOL);
  const stateRef = useRef<RoadTool>(state);
  const roadsRef = useRef<readonly SavedRoad[]>(options.roads);
  const importedRoadsRef = useRef<readonly RoadFeature[]>(options.importedRoads ?? EMPTY_IMPORTED_ROADS);
  const optionsRef = useRef(options);
  const gradeResultRef = useRef<GradeSuccess | null>(null);
  const draftFrameRef = useRef<number | null>(null);
  stateRef.current = state;
  roadsRef.current = options.roads;
  importedRoadsRef.current = options.importedRoads ?? EMPTY_IMPORTED_ROADS;
  optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'road',
    zOrder: MAP_Z_ORDER.road,
    hits: [{ id: 'road', priority: MAP_HIT_RANK.road, layerIds: ROAD_HIT_LAYER_IDS,
      select: (id) => optionsRef.current.selectRoad(id) }],
    install: ({ map }) => { addRoadLayers(map); addRoadDraftLayers(map); },
    synchronizeData: ({ map }) => {
      setRoadData(map, importedRoadsRef.current, roadsRef.current);
      setSelectedRoad(map, optionsRef.current.selectedRoadKey);
      setRoadDraftData(map, roadDraftOf(stateRef.current));
    },
    visibility: (): MapVisibilityDescriptor[] => optionsRef.current.roadsVisible()
      ? [{ id: 'bm-roads', label: 'Roads', layerIds: ROAD_LAYER_IDS,
        visible: true, section: 'Master plan' }]
      : [],
    setCaptureTransient: ({ map }, hidden) =>
      setRoadDraftData(map, hidden ? null : roadDraftOf(stateRef.current)),
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); },
    [options.roads, options.importedRoads]);
  useEffect(() => {
    setSelectedRoad(optionsRef.current.mapRef.current, options.selectedRoadKey);
  }, [options.selectedRoadKey]);
  useEffect(() => {
    if (draftFrameRef.current != null) return;
    draftFrameRef.current = requestAnimationFrame(() => {
      draftFrameRef.current = null;
      const map = optionsRef.current.mapRef.current;
      if (map) setRoadDraftData(map, roadDraftOf(stateRef.current));
    });
  }, [state]);

  useLayoutEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'drawing')) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = stateRef.current;
      if (current.phase === 'drawing') {
        const last = current.points.at(-1);
        if (last && haversineMeters(last, point) < 1) return;
      }
      dispatch({ type: 'add-point', point });
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      if (stateRef.current.phase === 'drawing') {
        dispatch({ type: 'move', point: [event.lngLat.lng, event.lngLat.lat] });
      }
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

  useEffect(() => () => {
    if (draftFrameRef.current != null) cancelAnimationFrame(draftFrameRef.current);
    optionsRef.current.gradeAdapter.stop();
    optionsRef.current.release();
  }, []);

  function failGrade(error: string): void {
    dispatch({ type: 'grade-failed', error });
  }

  function startGrade(draft: DraftRoad): void {
    const record = optionsRef.current.terrainRecord();
    const polygon = strokeToPolygon(draft.points, TWO_LANE_ROAD_WIDTH_M);
    const parts = polygon.length ? [{ polygon, centerline: draft.points, centerlineElevM: [] }] : [];
    const requestId = optionsRef.current.terrain.preview.claim();
    const bounds = record?.bounds;
    gradeResultRef.current = null;
    if (!record || !bounds || parts.length === 0) {
      failGrade('The local elevation package or road footprint is unavailable.');
      return;
    }
    requestAnimationFrame(() => {
      if (!optionsRef.current.terrain.preview.isCurrent(requestId)) return;
      const baseElevationChecksum = record.packageManifest?.elevationChecksum ?? '';
      optionsRef.current.gradeAdapter.run({
        id: requestId, kind: 'road', heights: optionsRef.current.heightGrid(record),
        gridSize: record.sampleGridSize, bounds, parts,
        brushWidthM: TWO_LANE_ROAD_WIDTH_M, ...ROAD_GRADE_POLICY,
        baseElevationChecksum,
        trailGeometryKey: terrainGradeGeometryKey(parts, TWO_LANE_ROAD_WIDTH_M,
          [], 'road', ROAD_GRADE_POLICY),
        contourGridSize: record.contourMetadata?.gridSize,
        contourIntervalM: record.contourMetadata?.intervalM,
      }, {
        isCurrent: (id) => optionsRef.current.terrain.preview.isCurrent(id),
        live: () => {
          const active = stateRef.current;
          return {
            baseElevationChecksum:
              optionsRef.current.terrainRecord()?.packageManifest?.elevationChecksum ?? '',
            trailGeometryKey: active.phase === 'review' ? terrainGradeGeometryKey([{
              polygon: strokeToPolygon(active.draft.points, TWO_LANE_ROAD_WIDTH_M),
              centerline: active.draft.points, centerlineElevM: [],
            }], TWO_LANE_ROAD_WIDTH_M, [], 'road', ROAD_GRADE_POLICY) : '',
          };
        },
        onResult: (response) => {
          gradeResultRef.current = response;
          dispatch({ type: 'patch', patch: {
            gradingStatus: 'ok', gradingError: null,
            gradingPolygons: response.expandedPolygons,
            earthwork: { cutM3: response.cutM3, fillM3: response.fillM3,
              balanceM3: response.balanceM3 },
            maxFaceSlopePct: response.maxFaceSlopePct,
            maxGroundCrossSlopePct: response.maxGroundCrossSlopePct,
            maxDisturbedWidthM: response.maxDisturbedWidthM,
            ungradedLengthM: response.ungradedLengthM,
            gradingInfeasibleLines: response.infeasibleLines,
          } });
          optionsRef.current.showGrade(record, response);
        },
        onSuperseded: () => failGrade('The road or terrain changed while grading. Refinish the route.'),
        onError: failGrade,
        onCrash: () => failGrade('Road grading worker stopped unexpectedly.'),
      });
    });
  }

  function arm(roadType: RoadType): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection();
    optionsRef.current.openDock();
    dispatch({ type: 'arm', roadType });
  }

  function cancel(): void {
    optionsRef.current.terrain.preview.invalidate();
    optionsRef.current.gradeAdapter.stop();
    gradeResultRef.current = null;
    optionsRef.current.clearGrade();
    dispatch({ type: 'cancel' });
    optionsRef.current.release();
  }

  function undo(): void { dispatch({ type: 'undo' }); }

  function finish(): void {
    const current = stateRef.current;
    if (current.phase !== 'drawing' || current.points.length < 2) return;
    const draft: DraftRoad = {
      name: nextRoadName([...roadsRef.current]), roadType: current.roadType,
      points: current.points, gradingStatus: 'pending', gradingError: null,
      gradingPolygons: [], earthwork: null, maxFaceSlopePct: 0,
      maxGroundCrossSlopePct: 0, maxDisturbedWidthM: 0, ungradedLengthM: 0,
      gradingInfeasibleLines: [],
    };
    dispatch({ type: 'review', name: draft.name });
    startGrade(draft);
  }

  function patchDraft(patch: Partial<DraftRoad>): void { dispatch({ type: 'patch', patch }); }

  async function confirm(): Promise<void> {
    const current = stateRef.current;
    const result = gradeResultRef.current;
    if (current.phase !== 'review' || current.draft.gradingStatus !== 'ok' || !result) return;
    const road = roadFromDraft(current.draft, roadsRef.current,
      optionsRef.current.createId(), optionsRef.current.now());
    let committed = false;
    await optionsRef.current.terrain.runConstruction('road', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = optionsRef.current.terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        const commit = optionsRef.current.terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record as TerrainRecord, result), kind: 'elevation' });
        if (!commit.ok) throw new Error('The terrain changed while building. Refinish the route.');
        optionsRef.current.addRoad(road);
        gradeResultRef.current = null;
        optionsRef.current.gradeAdapter.stop();
        dispatch({ type: 'cancel' });
        optionsRef.current.release();
        committed = true;
        optionsRef.current.selectRoad(`player:${road.id}`);
      } catch (error) {
        failGrade(error instanceof Error ? error.message : 'Unable to save the road grade.');
      }
    });
    if (!committed) return;
    try {
      await optionsRef.current.clearCover([
        ...roadClearingPolygons(road.points).map((polygon) => ({ polygon })),
        ...result.disturbancePolygons.map((polygon) => ({ polygon })),
      ]);
    } catch {
      // Cover clearing is deliberately best-effort after the coherent road commit.
    }
  }

  return { state, contribution: contributionRef.current,
    activeGradePreview: () => stateRef.current.phase === 'review' ? gradeResultRef.current : null,
    arm, cancel, undo, finish, patchDraft, confirm };
}
