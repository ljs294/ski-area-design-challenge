import { useEffect, useLayoutEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { TerrainRecord } from '../types/terrain';
import type { SavedBuilding } from '../types/buildings';
import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';
import { hasBuildingCollision, isBuildingFootprintInsideBounds } from '../buildings';
import {
  applyTerrainGradeToRecord,
} from './terrainGradeCommit';
import {
  canConfirmBuilding,
  buildingFromDraft,
  buildingDraftMapData,
  buildingBearingBetween,
  IDLE_BUILDING_TOOL,
  reduceBuildingTool,
  type BuildingCommitDraft,
  type BuildingReviewDraft,
  type BuildingTool,
  type BuildingPoint,
} from './buildingControllerModel';
import { BuildingSiteAdapter } from './buildingSiteClient';
import type { BuildingSiteIdentity } from './buildingControllerModel';
import { buildingSiteGeometryKey, type BuildingSiteRequest } from './buildingSiteProtocol';
import { createBuildingContribution } from './buildingRenderer';
import type { ManagedMapContribution } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import type { TerrainDocument } from './terrainDocument';
import type { ConstructionOutcome } from './constructionLock';

const MIN_BEARING_DISTANCE_M = 1;
const DEFAULT_BUILDING_ERROR = 'Unable to place this building.';

export interface BuildingCollectionCommands {
  /** Add the fully resolved building. A composite owner should add its pump here. */
  add?(building: SavedBuilding): void;
  /** Composite rename keeps the reciprocal pump name synchronized. */
  rename?(id: string, name: string): void | boolean;
  /** Composite removal should detach connected pipe ends before removing the pump. */
  remove?(id: string): void | boolean;
  /** Optional atomic terrain/building/network operation. */
  commit?(building: SavedBuilding, analysis: BuildingSiteAnalysisResult): Promise<void> | void;
}

export interface BuildingCommitRequest {
  building: SavedBuilding;
  analysis: BuildingSiteAnalysisResult;
  terrainRevision: number;
  buildingRevision: number | undefined;
  snowmakingRevision: number | undefined;
}

export interface BuildingTerrainPort {
  snapshot(): { record: TerrainRecord | null; revision: number };
  commit?(request: {
    expectedRevision: number;
    record: TerrainRecord;
    kind: 'elevation';
  }): { ok: true; revision: number } | { ok: false; reason: 'stale' };
  runConstruction?<T>(activity: 'building', operation: () => Promise<T>): Promise<ConstructionOutcome<T>>;
  preview?: { invalidate(): void };
}

export interface BuildingControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  buildings: readonly SavedBuilding[];
  selectedBuildingId?: string | null;
  /** `selectedId` is retained as a small compatibility alias for controllers. */
  selectedId?: string | null;
  selectBuilding?(id: string): void;
  select?(id: string): void;
  clearSelected?(id: string): void;
  clearSelection?(): void;
  addBuilding?(building: SavedBuilding): void;
  renameBuilding?(id: string, name: string): void | boolean;
  removeBuilding?(id: string): void | boolean;
  commands?: BuildingCollectionCommands;
  /** Called by the integration owner for one composite building/node commit. */
  commitBuilding?(request: BuildingCommitRequest): Promise<void> | void;
  canArm?(): boolean;
  activate?(): boolean;
  release?(): void;
  openDock?(): void;
  acquireInteractions?(map: maplibregl.Map): MapInteractionLeaseHandle;
  terrain?: BuildingTerrainPort | TerrainDocument;
  /** Optional wrapper for integrations that own the TerrainDocument elsewhere. */
  runConstruction?<T>(operation: () => Promise<T>): Promise<ConstructionOutcome<T>>;
  terrainRecord?(): TerrainRecord | null;
  terrainRevision?: number;
  /** Relevant document revisions captured when review begins. */
  buildingRevision?: number | (() => number);
  snowmakingRevision?: number | (() => number);
  siteAnalysis?: BuildingSiteAdapter;
  analysis?: BuildingSiteAdapter;
  clearCover?(polygons: [number, number][][][]): Promise<void>;
  createId(): string;
  /** Node IDs may be allocated by the snowmaking document/composite owner. */
  createNodeId?(): string;
  now(): string;
  structuresVisible?(): boolean;
  synchronizeMap?(): void;
}

export interface BuildingController {
  readonly state: BuildingTool;
  readonly contribution: ManagedMapContribution;
  arm(): void;
  /** Alias used by Snowmaking overview buttons. */
  startPlacement(): void;
  cancel(): void;
  dispose(): void;
  patchDraft(patch: Partial<BuildingReviewDraft>): void;
  confirm(): Promise<void>;
  select(id: string): void;
  rename(id: string, name: string): void;
  remove(id: string): void;
  /** Current resolved site analysis, if any. */
  activeSiteAnalysis(): BuildingSiteAnalysisResult | null;
}

function selectedId(options: BuildingControllerOptions): string | null {
  return options.selectedBuildingId ?? options.selectedId ?? null;
}

function terrainSnapshot(options: BuildingControllerOptions): { record: TerrainRecord | null; revision: number } {
  if (options.terrain) return options.terrain.snapshot() as { record: TerrainRecord | null; revision: number };
  return { record: options.terrainRecord?.() ?? null, revision: options.terrainRevision ?? 0 };
}

function buildingRevisionOf(options: BuildingControllerOptions): number | undefined {
  return typeof options.buildingRevision === 'function'
    ? options.buildingRevision() : options.buildingRevision;
}

function snowmakingRevisionOf(options: BuildingControllerOptions): number | undefined {
  return typeof options.snowmakingRevision === 'function'
    ? options.snowmakingRevision() : options.snowmakingRevision;
}

function noLease(): MapInteractionLeaseHandle {
  return { release: () => {} };
}

function frame(callback: () => void): number | ReturnType<typeof setTimeout> {
  if (typeof requestAnimationFrame === 'function') return requestAnimationFrame(callback);
  return setTimeout(callback, 0);
}

function cancelFrame(id: number | ReturnType<typeof setTimeout>): void {
  if (typeof cancelAnimationFrame === 'function' && typeof id === 'number') cancelAnimationFrame(id);
  else clearTimeout(id as ReturnType<typeof setTimeout>);
}

function asPoint(value: readonly [number, number]): BuildingPoint {
  return [value[0], value[1]];
}

function siteIdentityFor(
  draft: Pick<BuildingReviewDraft, 'center' | 'bearingDeg' | 'dimensions' | 'foundationMode'>,
  terrainRevision: number,
  elevationChecksum: string,
): BuildingSiteIdentity {
  return {
    geometryKey: buildingSiteGeometryKey(draft.center, draft.bearingDeg, draft.dimensions, draft.foundationMode),
    terrainRevision,
    elevationChecksum,
  };
}

function sameIdentity(a: BuildingSiteIdentity | null, b: BuildingSiteIdentity | null): boolean {
  return !!a && !!b && a.geometryKey === b.geometryKey &&
    a.terrainRevision === b.terrainRevision && a.elevationChecksum === b.elevationChecksum;
}

function draftFromCentered(
  state: Extract<BuildingTool, { phase: 'centered' }>,
  bearingDeg: number,
): BuildingReviewDraft {
  return {
    buildingTypeId: state.buildingTypeId,
    name: state.name,
    center: asPoint(state.center),
    bearingDeg,
    dimensions: { ...state.dimensions },
    foundationMode: state.foundationMode,
    siteStatus: 'pending', siteError: null, siteAnalysis: null,
    siteIdentity: null, hasCollision: false, confirmationError: null,
  };
}

/**
 * Owns placement-only map listeners and the identity of one site-analysis
 * request. Committed building/network state remains behind the injected
 * command or composite commit port.
 */
export function useBuildingController(options: BuildingControllerOptions): BuildingController {
  const [state, dispatch] = useReducer(reduceBuildingTool, IDLE_BUILDING_TOOL);
  const stateRef = useRef<BuildingTool>(state);
  const optionsRef = useRef(options);
  const buildingsRef = useRef<readonly SavedBuilding[]>(options.buildings);
  const analysisRef = useRef<BuildingSiteAdapter | null>(options.analysis ?? options.siteAnalysis ?? null);
  const analysisTokenRef = useRef(0);
  const currentIdentityRef = useRef<BuildingSiteIdentity | null>(null);
  const reviewBuildingRevisionRef = useRef<number | undefined>(undefined);
  const reviewSnowmakingRevisionRef = useRef<number | undefined>(undefined);
  const confirmInFlightRef = useRef(false);
  const draftFrameRef = useRef<number | ReturnType<typeof setTimeout> | null>(null);
  const cancelRef = useRef<() => void>(() => {});

  if (!analysisRef.current) analysisRef.current = new BuildingSiteAdapter();
  stateRef.current = state;
  optionsRef.current = options;
  buildingsRef.current = options.buildings;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) {
    contributionRef.current = createBuildingContribution({
      getBuildings: () => buildingsRef.current,
      getSelectedId: () => selectedId(optionsRef.current),
      getDraft: () => buildingDraftMapData(stateRef.current),
      setSelected: (id) => selectBuilt(id),
      structuresVisible: () => optionsRef.current.structuresVisible?.() !== false,
      synchronizeMap: () => optionsRef.current.synchronizeMap?.(),
    });
  }

  useEffect(() => {
    optionsRef.current.synchronizeMap?.();
  }, [options.buildings, options.selectedBuildingId, options.selectedId]);

  useEffect(() => {
    if (draftFrameRef.current != null) return;
    draftFrameRef.current = frame(() => {
      draftFrameRef.current = null;
      optionsRef.current.synchronizeMap?.();
    });
  }, [state]);

  useLayoutEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'centered')) return;
    const interaction = optionsRef.current.acquireInteractions?.(map) ?? noLease();
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const current = stateRef.current;
      if (current.phase !== 'armed' && current.phase !== 'centered') return;
      dispatch({ type: 'move', point: [event.lngLat.lng, event.lngLat.lat] });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: BuildingPoint = [event.lngLat.lng, event.lngLat.lat];
      const current = stateRef.current;
      if (current.phase === 'armed') {
        dispatch({ type: 'center', point });
        return;
      }
      if (current.phase !== 'centered' || !current.center) return;
      const bearingDeg = buildingBearingBetween(current.center, point);
      const latitudeM = (point[1] - current.center[1]) * 111_320;
      const longitudeM = (point[0] - current.center[0]) * 111_320 *
        Math.cos(current.center[1] * Math.PI / 180);
      if (Math.hypot(latitudeM, longitudeM) < MIN_BEARING_DISTANCE_M) return;
      const review = draftFromCentered(current, bearingDeg);
      dispatch({ type: 'lock', point, bearingDeg });
      reviewBuildingRevisionRef.current = buildingRevisionOf(optionsRef.current);
      reviewSnowmakingRevisionRef.current = snowmakingRevisionOf(optionsRef.current);
      startSiteAnalysis(review);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelRef.current();
    };
    map.on('mousemove', onMove);
    map.on('click', onClick);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('mousemove', onMove);
      map.off('click', onClick);
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
    // All event callbacks read mutable refs. Rebind only when ownership phase changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.phase]);

  useEffect(() => () => {
    if (draftFrameRef.current != null) cancelFrame(draftFrameRef.current);
    cancelSiteWork();
    optionsRef.current.release?.();
  }, []);

  function terrain(): { record: TerrainRecord | null; revision: number } {
    return terrainSnapshot(optionsRef.current);
  }

  function cancelSiteWork(): void {
    analysisTokenRef.current++;
    currentIdentityRef.current = null;
    analysisRef.current?.cancel();
    optionsRef.current.terrain?.preview?.invalidate();
  }

  function startSiteAnalysis(draft: BuildingReviewDraft): void {
    cancelSiteWork();
    const token = analysisTokenRef.current;
    const snapshot = terrain();
    const record = snapshot.record;
    const bounds = record?.bounds;
    if (!record || !bounds) {
      dispatch({ type: 'site-failed', error: 'The prepared terrain package is unavailable.' });
      return;
    }
    const elevationChecksum = record.packageManifest?.elevationChecksum ?? '';
    const identity = siteIdentityFor(draft, snapshot.revision, elevationChecksum);
    currentIdentityRef.current = identity;
    dispatch({ type: 'site-started', identity });
    const adapter = analysisRef.current;
    if (!adapter) {
      dispatch({ type: 'site-failed', error: 'Building-site analysis is unavailable.' });
      return;
    }
    const request: Omit<BuildingSiteRequest, 'id'> = {
      type: 'analyze-building-site',
      center: asPoint(draft.center), bearingDeg: draft.bearingDeg,
      dimensions: { ...draft.dimensions }, foundationMode: draft.foundationMode,
      heights: Float32Array.from(record.sampleHeights), gridSize: record.sampleGridSize,
      bounds, terrainRevision: snapshot.revision, elevationChecksum,
      geometryKey: identity.geometryKey,
      contourGridSize: record.contourMetadata?.gridSize,
      contourIntervalM: record.contourMetadata?.intervalM,
    };
    adapter.run(request, {
      isCurrent: () => token === analysisTokenRef.current,
      live: () => {
        const current = stateRef.current;
        const liveTerrain = terrain();
        if (current.phase !== 'review') return identity;
        return siteIdentityFor(current.draft, liveTerrain.revision,
          liveTerrain.record?.packageManifest?.elevationChecksum ?? '');
      },
      onResult: (result, responseIdentity) => {
        if (token !== analysisTokenRef.current || stateRef.current.phase !== 'review') return;
        const normalizedIdentity: BuildingSiteIdentity = {
          geometryKey: responseIdentity.geometryKey,
          terrainRevision: responseIdentity.terrainRevision,
          elevationChecksum: responseIdentity.elevationChecksum ?? responseIdentity.baseElevationChecksum ?? '',
        };
        if (!sameIdentity(identity, normalizedIdentity)) {
          dispatch({ type: 'site-failed', error: 'The building or terrain changed during site analysis. Try again.' });
          return;
        }
        const draftState = stateRef.current;
        if (draftState.phase !== 'review') return;
        const hasCollision = hasBuildingCollision(draftState.draft, buildingsRef.current);
        dispatch({ type: 'site-succeeded', result, identity: normalizedIdentity, hasCollision });
      },
      onSuperseded: () => {
        if (token === analysisTokenRef.current)
          dispatch({ type: 'site-failed', error: 'The building or terrain changed during site analysis. Try again.' });
      },
      onError: (error) => {
        if (token === analysisTokenRef.current) dispatch({ type: 'site-failed', error });
      },
      onCrash: () => {
        if (token === analysisTokenRef.current)
          dispatch({ type: 'site-failed', error: 'Building-site analysis stopped unexpectedly.' });
      },
    });
  }

  function arm(): void {
    if (optionsRef.current.canArm && !optionsRef.current.canArm()) return;
    if (optionsRef.current.activate && !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection?.();
    optionsRef.current.openDock?.();
    dispatch({ type: 'arm' });
  }

  function cancel(): void {
    cancelSiteWork();
    reviewBuildingRevisionRef.current = undefined;
    reviewSnowmakingRevisionRef.current = undefined;
    dispatch({ type: 'cancel' });
    optionsRef.current.release?.();
  }

  function startPlacement(): void { arm(); }

  function patchDraft(patch: Partial<BuildingReviewDraft>): void {
    const current = stateRef.current;
    if (current.phase !== 'review') return;
    const structural = 'center' in patch || 'bearingDeg' in patch ||
      'dimensions' in patch || 'foundationMode' in patch;
    if (!structural) {
      dispatch({ type: 'patch', patch, invalidateSite: false });
      return;
    }
    const next: BuildingReviewDraft = {
      ...current.draft,
      ...patch,
      ...(patch.center ? { center: asPoint(patch.center) } : {}),
      ...(patch.dimensions ? { dimensions: { ...current.draft.dimensions, ...patch.dimensions } } : {}),
      ...(patch.bearingDeg !== undefined ? { bearingDeg: patch.bearingDeg } : {}),
    };
    dispatch({ type: 'patch', patch, invalidateSite: true });
    startSiteAnalysis(next);
  }

  function selectBuilt(id: string): void {
    if (stateRef.current.phase !== 'idle') cancel();
    (optionsRef.current.selectBuilding ?? optionsRef.current.select)?.(id);
  }

  function select(id: string): void { selectBuilt(id); }

  function rename(id: string, name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    const command = optionsRef.current.commands?.rename ?? optionsRef.current.renameBuilding;
    command?.(id, trimmed);
  }

  function remove(id: string): void {
    const command = optionsRef.current.commands?.remove ?? optionsRef.current.removeBuilding;
    command?.(id);
    optionsRef.current.clearSelected?.(id);
  }

  async function commitBuilding(
    building: SavedBuilding,
    analysis: BuildingSiteAnalysisResult,
    terrainState: { record: TerrainRecord | null; revision: number },
  ): Promise<void> {
    const optionsValue = optionsRef.current;
    if (optionsValue.commitBuilding) {
      await optionsValue.commitBuilding({
        building, analysis, terrainRevision: terrainState.revision,
        buildingRevision: reviewBuildingRevisionRef.current,
        snowmakingRevision: reviewSnowmakingRevisionRef.current,
      });
      return;
    }
    const commandCommit = optionsValue.commands?.commit;
    if (commandCommit) {
      await commandCommit(building, analysis);
      return;
    }
    // This fallback is useful for focused controller tests and keeps the
    // terrain/building order coherent. Production integration should inject
    // the composite owner so the reciprocal pump node is added in the same
    // prepared transaction.
    if (analysis.terrainGraded) {
      if (!terrainState.record || !optionsValue.terrain?.commit)
        throw new Error('The terrain commit service is unavailable.');
      const updated = applyTerrainGradeToRecord(terrainState.record, analysis.terrainPatch);
      const result = optionsValue.terrain.commit({ expectedRevision: terrainState.revision,
        record: updated, kind: 'elevation' });
      if (!result.ok) throw new Error('The terrain changed while building. Redraw the pump house.');
    }
    const add = optionsValue.commands?.add ?? optionsValue.addBuilding;
    if (!add) throw new Error('The building commit service is unavailable.');
    add(building);
  }

  async function runConstruction<T>(operation: () => Promise<T>): Promise<ConstructionOutcome<T>> {
    const wrapper = optionsRef.current.runConstruction;
    if (wrapper) return wrapper(operation);
    const wrapped = optionsRef.current.terrain?.runConstruction;
    if (wrapped) return wrapped.call(optionsRef.current.terrain, 'building', operation) as Promise<ConstructionOutcome<T>>;
    return { ok: true, value: await operation() };
  }

  async function confirm(): Promise<void> {
    if (confirmInFlightRef.current) return;
    const current = stateRef.current;
    if (current.phase !== 'review' || !canConfirmBuilding(current.draft) || !current.draft.siteAnalysis) return;
    const draft = current.draft;
    const analysis = draft.siteAnalysis;
    if (!analysis) return;
    const identity = draft.siteIdentity;
    if (!identity || !sameIdentity(identity, currentIdentityRef.current)) return;
    const siteRevision = terrain();
    if (siteRevision.revision !== identity.terrainRevision) {
      dispatch({ type: 'confirmation-failed', error: 'The terrain changed after analysis. Redraw the pump house.' });
      return;
    }
    if (reviewBuildingRevisionRef.current !== undefined &&
        buildingRevisionOf(optionsRef.current) !== reviewBuildingRevisionRef.current) {
      dispatch({ type: 'confirmation-failed', error: 'The building plan changed after analysis. Redraw the pump house.' });
      return;
    }
    if (reviewSnowmakingRevisionRef.current !== undefined &&
        snowmakingRevisionOf(optionsRef.current) !== reviewSnowmakingRevisionRef.current) {
      dispatch({ type: 'confirmation-failed', error: 'The snowmaking network changed after analysis. Redraw the pump house.' });
      return;
    }
    const record = siteRevision.record;
    if (record?.bounds && !isBuildingFootprintInsideBounds(draft, record.bounds)) {
      dispatch({ type: 'confirmation-failed', error: 'The complete building footprint must remain inside prepared terrain.' });
      return;
    }
    if (hasBuildingCollision(draft, buildingsRef.current)) {
      dispatch({ type: 'confirmation-failed', error: 'This pump house overlaps another player building.' });
      return;
    }
    const nodeId = optionsRef.current.createNodeId?.() ?? `${draft.buildingTypeId}-${optionsRef.current.createId()}`;
    let building: SavedBuilding;
    try {
      building = buildingFromDraft(draft as BuildingCommitDraft, buildingsRef.current,
        optionsRef.current.createId(), nodeId, optionsRef.current.now());
    } catch (error) {
      dispatch({ type: 'confirmation-failed', error: error instanceof Error ? error.message : DEFAULT_BUILDING_ERROR });
      return;
    }

    confirmInFlightRef.current = true;
    let committed = false;
    try {
      const outcome = await runConstruction(async () => {
        const live = terrain();
        if (live.revision !== identity.terrainRevision)
          throw new Error('The terrain changed while building. Redraw the pump house.');
        await commitBuilding(building, analysis, live);
      });
      if (!outcome.ok) {
        dispatch({ type: 'confirmation-failed', error: 'Another construction is in progress. Try again when it finishes.' });
        return;
      }
      committed = true;
    } catch (error) {
      dispatch({ type: 'confirmation-failed', error: error instanceof Error ? error.message : DEFAULT_BUILDING_ERROR });
      return;
    } finally {
      confirmInFlightRef.current = false;
    }
    if (!committed) return;
    cancelSiteWork();
    dispatch({ type: 'cancel' });
    optionsRef.current.release?.();
    try {
      await optionsRef.current.clearCover?.(analysis.disturbancePolygons);
    } catch {
      // Cover processing is best effort after the atomic infrastructure commit.
    }
  }

  cancelRef.current = cancel;

  return {
    state,
    contribution: contributionRef.current,
    arm,
    startPlacement,
    cancel,
    dispose: cancel,
    patchDraft,
    confirm,
    select,
    rename,
    remove,
    activeSiteAnalysis: () => stateRef.current.phase === 'review'
      ? stateRef.current.draft.siteAnalysis : null,
  };
}
