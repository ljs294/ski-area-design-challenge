import { useEffect, useMemo, useRef, useState, type MutableRefObject } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { applyAnalysisRenderProfile, removeAnalysisLayers, setLocalContextData, setSelectedLake, setSelectedStream, setupAnalysisLayers, type LayerToggle, type OverlayId } from './analysisLayers';
import { applyCoverOpacity, setCoverData } from './coverVectorize';
import { buildSkiNetwork } from '../network';
import { sampleSiteCoverGrid } from './worldcoverProtocol';
import { addSiteBoxLayers, setSiteBox, setBoundaryMode, computeBox, siteBoxFromBounds, type SiteBox } from './sitePicker';
import { basemapFor } from './basemapStyle';
import { tilt3D } from './terrain3d';
import { useSettings } from './SettingsContext';
import { MapInteractionLease, type MapInteractionLeaseHandle, type MapInteractionOverrides } from './mapInteractionLease';
import { ToolCoordinator, TOOL_IDS, type DockId, type ToolCoordinatorSnapshot, type ToolId } from './toolCoordinator';
import type { BootControls, BootEvent, BootProgress } from './resortBoot';
import { captureGamePreview, CURRENT_GAME_SAVE_SCHEMA_VERSION } from '../gameSaveClient';
import { isDesktop } from '../desktopBridge';
import type { GameSave, SavedDam, SavedJunction, SavedLift, SavedNode, SavedPath, SavedPond, SavedRoad, SavedTrail, TerrainPackageProgress, TerrainRecord } from '../types';
import { loadTerrain, saveTerrain, saveTerrainCover } from '../terrainStorageClient';
import { prepareResortPackage } from '../terrainIngest';
import { validateTerrainPackage, withPreparedCoverDisplay } from '../terrainPackage';
import { clearResortCoverCache, RESORT_COVER_PROTOCOL, resortCameraBounds, sampleLocalTerrainAt, setActiveResortTerrain } from './resortProtocols';
import { useLiftController } from './useLiftController';
import { useRoadController } from './useRoadController';
import { useCommittedSnowmakingNetwork, useSnowmakingController, useSnowmakingLakeSources } from './useSnowmakingController';
import { useNodePathController } from './useNodePathController';
import { useTrailController } from './useTrailController';
import { MapViewChrome, SnowmakingToolOptions, snowmakingDashboardProps, useMapContextRecovery } from './MapViewChrome';
import { useMapKeyboardControls } from './useMapKeyboardControls';
import { useElevationBackfill } from './useElevationBackfill';
import { useMapRuntime } from './useMapRuntime';
import { useGameSimulation, useMapSampling, useSnowLayer, useTerrainDisplayAssets } from './useMapSampling';
import { useMapWorkers } from './useMapWorkers';
import { TERRAIN_CLEAN, designHasEdits, designOf, flushTerrainEdits, terrainHasEdits, withTerrainEdit, type DesignSnapshot, type TerrainDirty } from './unsavedChanges';
import { refreshTerrainGradeSources, setGradedContourPreview, setTerrainContourData } from './terrainGradeMap';
import { withResumeCheckpoint } from './resumeCheckpoint';
import { TerrainDocument, type TerrainDocumentPorts, type TerrainPublication, type TerrainRecordView } from './terrainDocument';
import { TopologyDocument, topologyProjection, type TopologyState } from './topologyDocument';
import { MAP_HIT_RANK, MAP_Z_ORDER, MapContributionRegistry, type ManagedMapContribution, type MapVisibilityDescriptor } from './mapContribution';
import { addDashboardMapLayers, setDashboardMapVisibility, useInMapDashboards } from './inMapDashboards';
import { guestVibePresentation, withGuestEconomyControls, has3DBuildingContext, initialResortDesign, saveGameWithGuestCheckpoint, useMapGuestSimulationFeature, usePumpHouseFeature } from './mapViewComposition';

// Crystal Mountain, WA — our canonical test site (used as the New Game start).
const INITIAL_CENTER: [number, number] = [-121.474, 46.928], INITIAL_ZOOM = 12;
export type MapMode = 'picking' | 'playing';

/**
 * Editing the graph nodes along a run. A node is a junction in the trail
 * topology — the thing the review panel numbers — not a free-standing pin, so
 * `add` only accepts a click that lands on a painted run and `remove` only
 * accepts a node the run passes straight through. Both pick first and commit on
 * a button, so a misclick costs nothing.
 */
type SelectionTarget = { kind: 'lift' | 'trail' | 'dam' | 'pond' | 'building' | 'snowmaking-node' | 'snowmaking-pipe' | 'snowgun' | 'ski-node' | 'ski-path'; id: string }
  | { kind: 'road' | 'lake' | 'stream'; id: string } | { kind: 'none' };

function layerTogglesOf(descriptors: readonly MapVisibilityDescriptor[]): LayerToggle[] { return descriptors.map((descriptor) => ({ ...descriptor, layerIds: [...descriptor.layerIds] })); }

/** crypto.randomUUID is gated to secure contexts (fails under packaged file://). */
function genId(): string {
  try { return crypto.randomUUID(); }
  catch { return 'save-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8); } }

/** The visible member of the mutually-exclusive overlay group, if any. */
function activeOverlayOf(layers: LayerToggle[]): OverlayId | null {
  const analysis = layers.find((l) => (l.exclusiveGroup === 'overlay' || l.exclusiveGroup === 'analysis') && l.visible);
  const on = analysis ?? layers.find((l) => l.id === 'groundcover' && l.visible);
  return (on?.id as OverlayId) ?? null; }

interface MapViewProps {
  mode: MapMode;
  initialSave?: GameSave | null;
  onQuit: () => void;
  onOpenSettings: () => void;
  /** Open the Load Game modal (owned by App). Menu → Load. */
  onLoadGame: () => void;
  /** Boot reports for the resort loading screen, which App owns (it has to
   *  exist before this component mounts and outlive its first full render). */
  onBoot?: (e: BootEvent) => void;
  bootControlsRef?: MutableRefObject<BootControls | null>;
  sessionControlsRef?: MutableRefObject<GameSessionControls | null>;
  /** True while a modal owned by App (Settings, Load Game) sits on top of
   *  this component; suspends WASD/QE/RF/N/U/1/2 so the hidden map underneath
   *  doesn't drift and stray keydowns from a keybind-rebind UI don't leak
   *  through. */
  controlsSuspended?: boolean;
}

export interface ExitCheckpointResult {
  ok: boolean;
  error?: string;
}

export interface GameSessionControls {
  checkpointForExit(interactive?: boolean): Promise<ExitCheckpointResult>;
  confirmExit(): Promise<boolean>;
  resortSettings?: { mapContextAvailable: boolean;
    downloadMapContext(signal: AbortSignal): Promise<{ ok: true } | { ok: false; error: string }> };
}

function browserPreviewDataUrl(canvas: HTMLCanvasElement): string | null {
  try {
    const longest = Math.max(canvas.width, canvas.height);
    const scale = longest > 1600 ? 1600 / longest : 1;
    const out = document.createElement('canvas');
    out.width = Math.max(1, Math.round(canvas.width * scale));
    out.height = Math.max(1, Math.round(canvas.height * scale));
    const context = out.getContext('2d');
    if (!context) return null;
    context.drawImage(canvas, 0, 0, out.width, out.height);
    return out.toDataURL('image/jpeg', 0.8);
  } catch {
    return null;
  }
}

/** Wait for one composed MapLibre frame; browser capture must happen in-frame. */
function waitForCaptureFrame(map: maplibregl.Map): Promise<string | null> {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (dataUrl: string | null) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      map.off('render', onRender);
      resolve(dataUrl);
    };
    const onRender = () => finish(isDesktop ? null : browserPreviewDataUrl(map.getCanvas()));
    const timeout = window.setTimeout(() => finish(
      isDesktop ? null : browserPreviewDataUrl(map.getCanvas())
    ), 500);
    map.once('render', onRender);
    map.triggerRepaint();
  });
}

export function MapView({
  mode,
  initialSave = null,
  onQuit,
  onOpenSettings,
  onLoadGame,
  onBoot,
  bootControlsRef,
  sessionControlsRef,
  controlsSuspended = false,
}: MapViewProps) {
  const { settings, resolvedTheme } = useSettings();

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const [layers, setLayers] = useState<LayerToggle[]>([]);
  // Bottom-dock roll-ups: user-chosen open panel (the lift panel also force-opens
  // whenever the lift tool is active or a lift is selected — see liftsOpen below).
  const [toolCoordinatorState, setToolCoordinatorState] = useState<ToolCoordinatorSnapshot>({
    activeTool: null,
    openDock: null,
    layersAlongsideBuild: false,
  });
  const toolCoordinatorRef = useRef<ToolCoordinator | null>(null);
  if (!toolCoordinatorRef.current) {
    toolCoordinatorRef.current = new ToolCoordinator(setToolCoordinatorState);
  }
  const toolCoordinator = toolCoordinatorRef.current;
  const { openDock, layersAlongsideBuild } = toolCoordinatorState;
  const setOpenDock = (next: DockId | null | ((current: DockId | null) => DockId | null)) =>
    toolCoordinator.setOpenDock(next);
  const setLayersAlongsideBuild = (next: boolean | ((current: boolean) => boolean)) =>
    toolCoordinator.setLayersAlongsideBuild(next);
  const [showStats, setShowStats] = useState(false);
  const [showCredits, setShowCredits] = useState(false);
  const [siteMode, setSiteMode] = useState<'locked' | 'explore' | 'selecting'>(
    initialSave?.site ? 'locked' : 'explore');
  const [siteBox, setSiteBoxState] = useState<SiteBox | null>((initialSave?.site as SiteBox) ?? null);
  const [is3D, setIs3D] = useState(initialSave?.is3D ?? false);
  const [isOverhead, setIsOverhead] = useState(true);
  const warmAbortRef = useRef<AbortController | null>(null);
  const onBootRef = useRef(onBoot);
  onBootRef.current = onBoot;
  const repairRef = useRef<() => void>(() => {});
  const bootControls = useRef<BootControls | null>(null);
  const [localBoot, setLocalBoot] = useState<BootProgress | null>(null);
  const localBootRef = useRef<BootProgress | null>(localBoot);
  const showLocalBoot = (p: BootProgress | null) => {
    if (localBootRef.current === p) return;
    localBootRef.current = p;
    setLocalBoot(p);
  };
  const reportBoot = (e: BootEvent) => {
    if (e.type === 'ready' || e.type === 'handoff') showLocalBoot(null);
    // Track locally only while a local screen is actually up, or when nobody
    // upstream is listening — otherwise this is a render per warm-up tick.
    else if (e.type === 'progress' && (localBootRef.current || !onBootRef.current)) showLocalBoot(e.progress);
    onBootRef.current?.(e);
  };
  const reportStage = (progress: BootProgress) => reportBoot({ type: 'progress', progress });
  const reportFailure = (message: string) =>
    reportBoot({ type: 'failed', message, repair: () => repairRef.current() });
  const [saved, setSaved] = useState<GameSave | null>(initialSave);
  const persistedSaveRef = useRef<GameSave | null>(initialSave);
  const [nameDraft, setNameDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const [checkpointError, setCheckpointError] = useState<string | null>(null);
  const [unsavedPrompt, setUnsavedPrompt] = useState(false);
  const unsavedChoiceRef = useRef<((choice: 'save' | 'discard' | 'cancel') => void) | null>(null);
  const [terrainRecord, setTerrainRecord] = useState<TerrainRecord | null>(null);
  const mapMode: MapMode = terrainRecord ? 'playing' : mode;
  const snow = useSnowLayer(mapRef);
  const simulation = useGameSimulation({ terrain: terrainRecord, initialTime: initialSave?.time,
    initialWeatherRun: initialSave?.weatherRun, snow, mapRef, renderQuality: settings.renderQuality,
    reducedMotion: settings.reducedMotion });
  const [packageState, setPackageState] = useState<'ready' | 'loading' | 'missing' | 'preparing' | 'optimizing' | 'error'>(
    mode === 'playing' ? 'loading' : 'ready'
  );
  const [packageProgress, setPackageProgress] = useState<TerrainPackageProgress | null>(null);
  const [packageError, setPackageError] = useState<string | null>(null);
  const packageStateRef = useRef(packageState);
  const [initialDesign] = useState(() => initialResortDesign(initialSave));
  const [lifts, setLifts] = useState<SavedLift[]>(initialDesign.lifts);
  const [selectedLiftId, setSelectedLiftId] = useState<string | null>(null);
  const [liftEditing, setLiftEditing] = useState(false);
  const [trails, setTrails] = useState<SavedTrail[]>(initialDesign.trails);
  const [selectedTrailId, setSelectedTrailId] = useState<string | null>(null);
  const [trailEditing, setTrailEditing] = useState(false);
  const [roads, setRoads] = useState<SavedRoad[]>(initialDesign.roads); const [selectedRoadKey,
    setSelectedRoadKey] = useState<string | null>(null);
  const [dams, setDams] = useState<SavedDam[]>(initialDesign.dams);
  const [selectedDamId, setSelectedDamId] = useState<string | null>(null);
  const [ponds, setPonds] = useState<SavedPond[]>(initialDesign.ponds);
  const [selectedPondId, setSelectedPondId] = useState<string | null>(null);
  const snowmakingState = useCommittedSnowmakingNetwork({ nodes: initialDesign.snowmakingNodes,
    pipes: initialDesign.snowmakingPipes, guns: initialDesign.snowguns,
    nextNumbers: initialDesign.snowmakingNodeNextNumbers });
  const { nodes: snowmakingNodes, pipes: snowmakingPipes, guns: snowguns,
    nextNumbers: snowmakingNodeNextNumbers, selectedNodeId: selectedSnowmakingNodeId,
    selectedPipeId: selectedSnowmakingPipeId, selectedGunId: selectedSnowgunId,
    setSelectedNodeId: setSelectedSnowmakingNodeId, setSelectedPipeId: setSelectedSnowmakingPipeId,
    setSelectedGunId: setSelectedSnowgunId, committedRef: committedSnowmakingRef,
    document: snowmakingNetwork } = snowmakingState;
  const [skiNodes, setSkiNodes] = useState<SavedNode[]>(initialDesign.nodes);
  const [skiPaths, setSkiPaths] = useState<SavedPath[]>(initialDesign.paths);
  const [junctions, setJunctions] = useState<SavedJunction[]>(initialDesign.junctions);
  const committedTopologyRef = useRef<TopologyState>(
    { trails, nodes: skiNodes, paths: skiPaths, junctions }
  );
  const topologyDocumentRef = useRef<TopologyDocument | null>(null);
  if (!topologyDocumentRef.current) {
    topologyDocumentRef.current = new TopologyDocument(
      { trails, nodes: skiNodes, paths: skiPaths, junctions },
      ({ snapshot, changed }) => {
        const projection = topologyProjection(snapshot);
        committedTopologyRef.current = projection;
        if (changed.trails) setTrails(projection.trails);
        if (changed.nodes) setSkiNodes(projection.nodes);
        if (changed.paths) setSkiPaths(projection.paths);
        if (changed.junctions) setJunctions(projection.junctions);
      }
    );
  }
  const topology = topologyDocumentRef.current;
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [selectedPathId, setSelectedPathId] = useState<string | null>(null);
  const [selectedLakeId, setSelectedLakeId] = useState<string | null>(null);
  const [selectedStreamId, setSelectedStreamId] = useState<string | null>(null);
  const [streamWidthOverrides, setStreamWidthOverrides] = useState<Record<string, number>>(
    initialDesign.streamWidthOverrides);
  const [lakeDepthOverrides, setLakeDepthOverrides] = useState<Record<string, number>>(
    initialDesign.lakeDepthOverrides);
  const [lakeNameOverrides, setLakeNameOverrides] = useState<Record<string, string>>(
    initialDesign.lakeNameOverrides);
  const [snowmakingLakeIds, setSnowmakingLakeIds] = useState<string[]>(initialDesign.snowmakingLakeIds);
  const snowmakingLakes = useSnowmakingLakeSources(terrainRecord, snowmakingLakeIds,
    lakeDepthOverrides, lakeNameOverrides);
  const [terrainDirty, setTerrainDirtyState] = useState<TerrainDirty>(TERRAIN_CLEAN);
  const terrainDirtyRef = useRef(terrainDirty);
  const setTerrainDirty = (next: TerrainDirty) => {
    terrainDirtyRef.current = next;
    setTerrainDirtyState(next);
  };
  const markTerrainEdited = (kind: 'elevation' | 'cover') =>
    setTerrainDirty(withTerrainEdit(terrainDirtyRef.current, kind));
  const [savedDesign, setSavedDesign] = useState<DesignSnapshot>(() => ({
    name: initialSave?.name ?? '',
    site: initialSave?.site ?? null,
    lifts, trails, roads, dams, ponds, buildings: initialDesign.buildings,
    nodes: skiNodes, paths: skiPaths, junctions, snowmakingNodes, snowmakingPipes, snowguns,
    snowmakingNodeNextNumbers,
    lakeDepthOverrides, lakeNameOverrides, snowmakingLakeIds, streamWidthOverrides,
  }));
  const [buildingActivity, setBuildingActivity] = useState<Parameters<TerrainDocumentPorts['publishConstruction']>[0]>(null);
  const committedBuildingsRef = useRef(initialDesign.buildings);
  const building = buildingActivity !== null;
  const network = useMemo(
    () => buildSkiNetwork(trails, lifts, { nodes: skiNodes, paths: skiPaths, junctions }),
    [trails, lifts, skiNodes, skiPaths, junctions]
  );
  useEffect(() => {
    (window as unknown as { appNetwork?: typeof network }).appNetwork = network;
  }, [network]);
  useEffect(() => {
    (window as unknown as { appTerrainBounds?: TerrainRecord['bounds'] })
      .appTerrainBounds = terrainRecord?.bounds;
  }, [terrainRecord]);
  useEffect(() => {
    (window as unknown as { appSaveState?: unknown }).appSaveState = {
      terrainKey: terrainRecord?.key ?? null,
      elevationChecksum: terrainRecord?.packageManifest?.elevationChecksum ?? null,
      coverChecksum: terrainRecord?.coverMetadata?.checksum ?? null,
      snowCells: snow.grid ? snow.grid.width * snow.grid.height : 0,
      terrainDirty: { ...terrainDirtyRef.current },
      unsaved: hasUnsavedChanges(),
    };
  });
  const activeOverlay = activeOverlayOf(layers);

  const activeOverlayRef = useRef<OverlayId | null>(null);
  const lastLngLatRef = useRef<{ lng: number; lat: number } | null>(null);
  const sampleTokenRef = useRef(0);
  const rafPendingRef = useRef(false);
  const doSampleRef = useRef<(lngLat: { lng: number; lat: number }) => void>(() => {});
  const layersRef = useRef<LayerToggle[]>([]);
  const analysisTogglesRef = useRef<LayerToggle[]>([]);
  const mapContributionRegistryRef = useRef<MapContributionRegistry | null>(null);
  const reconfigureAnalysisProfileRef = useRef<(map: maplibregl.Map) => void>(() => {});
  const siteBoxRef = useRef<SiteBox | null>(siteBox);
  const siteModeRef = useRef<'locked' | 'explore' | 'selecting'>(siteMode);
  const is3DRef = useRef(is3D);
  const isOverheadRef = useRef(isOverhead);
  // Flips true the first time the resort's terrain is mounted, so the one-time
  // "default into 3D" camera ease fires once — not on every dark/light restyle.
  const resortReadyRef = useRef(false);
  const liftsRef = useRef<SavedLift[]>(lifts);
  const skiNodesRef = useRef<SavedNode[]>(skiNodes);
  const skiPathsRef = useRef<SavedPath[]>(skiPaths);
  const junctionsRef = useRef<SavedJunction[]>(junctions);
  const trailsRef = useRef<SavedTrail[]>(trails);
  const selectTrailRef = useRef<(id: string) => void>(() => {});
  const selectLakeRef = useRef<(id: string) => void>(() => {});
  const selectStreamRef = useRef<(id: string) => void>(() => {});
  const selectedLakeIdRef = useRef<string | null>(selectedLakeId);
  const selectedStreamIdRef = useRef<string | null>(selectedStreamId);
  const lakeDepthOverridesRef = useRef(lakeDepthOverrides);
  const lakeNameOverridesRef = useRef(lakeNameOverrides);
  const snowmakingLakeIdsRef = useRef(snowmakingLakeIds);
  const streamWidthOverridesRef = useRef(streamWidthOverrides);
  const roadsRef = useRef<SavedRoad[]>(roads);
  const damsRef = useRef<SavedDam[]>(dams);
  const pondsRef = useRef<SavedPond[]>(ponds);
  const renderQualityRef = useRef(settings.renderQuality);
  const unitsRef = useRef(settings.units);
  const buildingControllerCancelRef = useRef<() => void>(() => {});
  const guestPortalCancelRef = useRef<() => void>(() => {});
  const toolCancellationRef = useRef<Record<ToolId, () => void>>({
    lift: () => {}, road: () => {}, dam: () => {}, pond: () => {},
    building: () => {},
    'guest-portal': () => {},
    'ski-node': () => {}, 'ski-path': () => {}, trail: () => {},
    'snowmaking-pipe': () => {}, 'snowmaking-node': () => {}, 'snowmaking-gun': () => {},
  });
  const toolRegistrationsReadyRef = useRef(false);
  if (!toolRegistrationsReadyRef.current) {
    for (const toolId of TOOL_IDS) {
      toolCoordinator.register(toolId, () => toolCancellationRef.current[toolId]());
    }
    toolRegistrationsReadyRef.current = true;
  }
  toolCancellationRef.current = {
    lift: cancelLiftTool,
    road: cancelRoadTool,
    dam: cancelDamTool,
    pond: cancelPondTool,
    building: () => buildingControllerCancelRef.current(),
    'guest-portal': () => guestPortalCancelRef.current(),
    'ski-node': cancelNodeTool,
    'ski-path': cancelPathTool,
    trail: cancelTrailTool,
    'snowmaking-pipe': () => snowmakingController.network.cancelPipe(),
    'snowmaking-node': () => snowmakingController.network.cancelNode(),
    'snowmaking-gun': () => snowmakingController.guns.cancel(),
  };
  const mapInteractionLeaseRef = useRef<MapInteractionLease | null>(null);
  if (!mapInteractionLeaseRef.current) {
    mapInteractionLeaseRef.current = new MapInteractionLease((toolId) => toolCoordinator.isActive(toolId));
  }
  function acquireMapInteractions(
    owner: ToolId,
    map: maplibregl.Map,
    overrides: MapInteractionOverrides,
  ): MapInteractionLeaseHandle {
    const lease = mapInteractionLeaseRef.current;
    if (!lease) throw new Error('Map interaction lease is unavailable.');
    return lease.acquire(owner, map, overrides);
  }
  const guests = useMapGuestSimulationFeature({ mapRef, network, roads, clock: simulation.clock, snowGrid: snow.grid,
    saveKey: saved?.key ?? null,
    saveRevision: saved ? `${saved.updatedAt}|${saved.lastPlayedAt}` : null,
    activate: () => toolCoordinator.activate('guest-portal'), release: () => { toolCoordinator.release('guest-portal'); },
    openDock: () => toolCoordinator.setOpenDock('infrastructure'), acquireInteractions: (map) => acquireMapInteractions('guest-portal', map,
      { cursor: 'crosshair', dragPanEnabled: true, doubleClickZoomEnabled: true }),
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('guest') });
  const { portal: guestPortal, selectedGuestId, runtime: guestRuntime, controller: guestPortalController } = guests;
  const guestVibe = useMemo(() => withGuestEconomyControls(guestVibePresentation(guestRuntime.snapshot, selectedGuestId), guests.nextDayTicketPriceCents, guests.setNextDayTicketPriceCents), [guestRuntime.snapshot, guests.nextDayTicketPriceCents, guests.setNextDayTicketPriceCents, selectedGuestId]);
  guestPortalCancelRef.current = guestPortalController.cancel;
  // Loaded local package backing cursor sampling, MapLibre protocols, and
  // style reinitialization. Gameplay never populates it from network data.
  // Written only by the terrain document's publication, so a handler reading it
  // between a build and the next render sees the record that was committed.
  const terrainRecordRef = useRef<TerrainRecord | null>(null);
  const displayAssets = useTerrainDisplayAssets({ qualityRef: renderQualityRef, mapRef,
    reconfigureRef: reconfigureAnalysisProfileRef, reportError: setCheckpointError });
  const terrainHeightCacheRef = displayAssets.heightRef;
  const coverDisplayRef = displayAssets.coverRef;
  const localImageryUrlRef = displayAssets.imageryUrlRef;
  const dashboards = useInMapDashboards({ mapRef, registryRef: mapContributionRegistryRef,
    dark: resolvedTheme === 'dark', units: settings.units, network, dams, ponds, lakes: snowmakingLakes ?? [],
    trails, lifts, nodes: snowmakingNodes, buildings: committedBuildingsRef.current, pipes: snowmakingPipes, guns: snowguns,
    coverDisplay: coverDisplayRef.current, terrainRecord });
  useMapKeyboardControls({ mapRef, suspended: controlsSuspended, keybinds: settings.keybinds,
    activeDashboard: dashboards.active, toggle3D, setActiveDashboard: dashboards.setActive });
  const {
    readoutStore,
    setReadout,
    samplePoint: samplePlanningTerrainOrNull,
    sampleProfile,
  } = useMapSampling({
    mapRef,
    terrainRecordRef,
    activeOverlay,
    activeOverlayRef,
    lastLngLatRef,
    sampleTokenRef,
    doSampleRef,
    snowGridRef: snow.gridRef,
  });

  const terrainPortsRef = useRef<TerrainDocumentPorts>({
    cacheDisplayAssets: () => {},
    activateProtocols: () => {},
    publishState: () => {},
    refreshSources: () => {},
    publishPersisted: () => {},
    publishConstruction: () => {},
  });
  const terrainDocumentRef = useRef<TerrainDocument | null>(null);
  if (!terrainDocumentRef.current) {
    terrainDocumentRef.current = new TerrainDocument({
      cacheDisplayAssets: (record) => terrainPortsRef.current.cacheDisplayAssets(record),
      activateProtocols: (record) => terrainPortsRef.current.activateProtocols(record),
      publishState: (publication) => terrainPortsRef.current.publishState(publication),
      refreshSources: (publication) => terrainPortsRef.current.refreshSources(publication),
      publishPersisted: () => terrainPortsRef.current.publishPersisted(),
      publishConstruction: (activity) => terrainPortsRef.current.publishConstruction(activity),
    });
  }
  const terrain = terrainDocumentRef.current;
  const { damAnalysis, coverEdit, coverClear, terrainGrade, trailPaint, trailPresentation } = useMapWorkers(mapRef, terrain);
  const liftController = useLiftController({
    mapRef,
    lifts,
    commands: {
      add: (lift) => setLifts((existing) => [...existing, lift]),
      patch: (id, patch) => setLifts((existing) =>
        existing.map((lift) => lift.id === id ? { ...lift, ...patch } : lift)),
      remove: (id) => setLifts((existing) => existing.filter((lift) => lift.id !== id)),
    },
    canArm: () => siteModeRef.current !== 'selecting',
    activate: () => toolCoordinator.activate('lift'),
    release: () => { toolCoordinator.release('lift'); },
    clearSelection: clearSelectionState,
    select: (id) => { if (!dashboards.selectLift(id)) transitionSelection({ kind: 'lift', id }); },
    clearSelected: (id) => {
      setSelectedLiftId((selected) => selected === id ? null : selected);
      setLiftEditing(false);
    },
    acquireInteractions: (map) => acquireMapInteractions('lift', map, {
      cursor: 'crosshair',
      doubleClickZoomEnabled: false,
    }),
    sampleTerrain: samplePlanningTerrainOrNull,
    runConstruction: (operation) => terrain.runConstruction('lift', operation),
    clearCover: coverClear.clearLift,
    createId: genId,
    now: () => new Date().toISOString(),
    structuresVisible: () => packageStateRef.current !== 'preparing',
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('lift'),
  });
  const roadController = useRoadController({ mapRef, roads,
    importedRoads: terrainRecord?.vectorFeatures?.roads, selectedRoadKey,
    selectRoad: (key) => transitionSelection({ kind: 'road', id: key }),
    addRoad: (road) => setRoads((existing) => [...existing, road]),
    canArm: () => siteModeRef.current !== 'selecting',
    activate: () => toolCoordinator.activate('road'),
    release: () => { toolCoordinator.release('road'); },
    openDock: () => setOpenDock('infrastructure'),
    clearSelection: clearSelectionState,
    acquireInteractions: (map) => acquireMapInteractions('road', map, { cursor: 'crosshair' }),
    terrain,
    terrainRecord: () => terrainRecordRef.current,
    heightGrid: (record) => {
      const cached = terrainHeightCacheRef.current;
      return cached &&
        cached.checksum === (record.packageManifest?.elevationChecksum ?? record.updatedAt)
        ? cached.heights.slice()
        : Float32Array.from(record.sampleHeights);
    },
    gradeAdapter: terrainGrade,
    showGrade: (record, result) => {
      setVisibleContours({ ...record, contourSegments: Array.from(result.contourSegments) });
      setEditedContours(result.editedContourSegments);
    },
    clearGrade: () => {
      const record = terrainRecordRef.current;
      if (record) setVisibleContours(record);
      setEditedContours(null);
    },
    clearCover: coverClear.clear,
    createId: genId,
    now: () => new Date().toISOString(),
    roadsVisible: () => analysisTogglesRef.current.some((entry) => entry.id === 'bm-roads'),
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('road'),
  });
  const snowmakingController = useSnowmakingController({
    dam: {
      mapRef, dams, selectedId: selectedDamId,
      add: (dam) => setDams((existing) => [...existing, dam]),
      remove: (id) => setDams((existing) => existing.filter((dam) => dam.id !== id)),
      select: (id) => transitionSelection({ kind: 'dam', id }),
      clearSelected: (id) => setSelectedDamId((selected) => selected === id ? null : selected),
      canArm: () => siteModeRef.current !== 'selecting',
      activate: () => toolCoordinator.activate('dam'),
      release: () => { toolCoordinator.release('dam'); },
      openDock: () => setOpenDock('snowmaking'), clearSelection: clearSelectionState,
      acquireInteractions: (map) => acquireMapInteractions('dam', map, { cursor: 'crosshair' }),
      terrain, terrainRevision: terrain.snapshot().revision,
      terrainRecord: () => terrainRecordRef.current,
      streamWidthOverrides: () => streamWidthOverridesRef.current,
      analysis: damAnalysis, gradeChanged: applyGradePreview,
      clearCover: (polygons) => coverClear.clear(polygons.map((polygon) => ({ polygon }))),
      createId: genId, now: () => new Date().toISOString(),
      structuresVisible: () => packageStateRef.current !== 'preparing',
      synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('dam'),
    },
    pond: {
      mapRef, ponds, selectedId: selectedPondId,
      add: (pond) => setPonds((existing) => [...existing, pond]),
      patch: (id, value) => setPonds((existing) =>
        existing.map((pond) => pond.id === id ? { ...pond, ...value } : pond)),
      remove: (id) => setPonds((existing) => existing.filter((pond) => pond.id !== id)),
      select: (id) => transitionSelection({ kind: 'pond', id }),
      clearSelected: (id) => setSelectedPondId((selected) => selected === id ? null : selected),
      canArm: () => siteModeRef.current !== 'selecting',
      activate: () => toolCoordinator.activate('pond'),
      release: () => { toolCoordinator.release('pond'); },
      openDock: () => setOpenDock('snowmaking'), clearSelection: clearSelectionState,
      acquireInteractions: (map) => acquireMapInteractions('pond', map, { cursor: 'crosshair' }),
      terrain, terrainRevision: terrain.snapshot().revision,
      terrainRecord: () => terrainRecordRef.current, gradeChanged: applyGradePreview,
      clearCover: (polygons) => coverClear.clear(polygons.map((polygon) => ({ polygon }))),
      createId: genId, now: () => new Date().toISOString(),
      structuresVisible: () => packageStateRef.current !== 'preparing',
      synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('pond'),
    },
    network: { mapRef, dams, ponds, lakes: snowmakingLakes, nodes: snowmakingNodes,
      pipes: snowmakingPipes, guns: snowguns, network: snowmakingNetwork,
      selected: selectedSnowmakingNodeId ? { kind: 'node', id: selectedSnowmakingNodeId } :
        selectedSnowmakingPipeId ? { kind: 'pipe', id: selectedSnowmakingPipeId } :
          selectedSnowgunId ? { kind: 'gun', id: selectedSnowgunId } : null,
      canArm: () => siteModeRef.current !== 'selecting',
      activate: (tool) => toolCoordinator.activate(tool),
      release: (tool) => { toolCoordinator.release(tool); },
      openDock: () => setOpenDock('snowmaking'), clearSelection: clearSelectionState,
      acquireInteractions: (tool, map) => acquireMapInteractions(tool, map,
        { cursor: 'crosshair', doubleClickZoomEnabled: false }),
      selectNode: (id) => { if (!dashboards.selectSnow('node', id)) transitionSelection({ kind: 'snowmaking-node', id }); },
      selectPipe: (id, segmentId) => { if (!dashboards.selectSnow('pipe', id, segmentId)) transitionSelection({ kind: 'snowmaking-pipe', id }); },
      selectGun: (id) => { if (!dashboards.selectSnow('gun', id)) transitionSelection({ kind: 'snowgun', id }); },
      hoverDashboardPipe: dashboards.hoverSnowPipe,
      clearSelected: (id) => {
        setSelectedSnowmakingNodeId((selected) => selected === id ? null : selected);
        setSelectedSnowmakingPipeId((selected) => selected === id ? null : selected);
        setSelectedSnowgunId((selected) => selected === id ? null : selected);
      },
      createId: genId, now: () => new Date().toISOString(),
      sampleElevation: ([lng, lat]) => sampleLocalTerrainAt(lng, lat)?.elevation ?? null,
      structuresVisible: () => packageStateRef.current !== 'preparing',
      synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('snowmaking'),
    },
    guns: { mapRef, nodes: snowmakingNodes, guns: snowguns, network: snowmakingNetwork,
      canArm: () => siteModeRef.current !== 'selecting',
      activate: (tool) => toolCoordinator.activate(tool),
      release: (tool) => { toolCoordinator.release(tool); },
      openDock: () => setOpenDock('snowmaking'), clearSelection: clearSelectionState,
      acquireInteractions: (tool, map) => acquireMapInteractions(tool, map,
        { cursor: 'crosshair', doubleClickZoomEnabled: false }),
      selectGun: (id) => transitionSelection({ kind: 'snowgun', id }),
      clearSelected: (id) => setSelectedSnowgunId((selected) => selected === id ? null : selected),
      createId: genId, now: () => new Date().toISOString(),
      sampleElevation: ([lng, lat]) => sampleLocalTerrainAt(lng, lat)?.elevation ?? null,
    },
  });

  const pumpHouse = usePumpHouseFeature({
    mapRef, initialBuildings: initialDesign.buildings, committedRef: committedBuildingsRef, terrain, snowmaking: snowmakingNetwork,
    canArm: () => siteModeRef.current !== 'selecting',
    activate: () => toolCoordinator.activate('building'),
    release: () => { toolCoordinator.release('building'); },
    openDock: () => setOpenDock('snowmaking'), clearSelection: clearSelectionState,
    selectBuilding: (id) => transitionSelection({ kind: 'building', id }),
    acquireInteractions: (map) => acquireMapInteractions('building', map,
      { cursor: 'crosshair', doubleClickZoomEnabled: false }),
    clearCover: (polygons) => coverClear.clear(polygons.map((polygon) => ({ polygon }))),
    createId: genId, now: () => new Date().toISOString(),
    structuresVisible: () => packageStateRef.current !== 'preparing',
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('building'),
  });
  const { buildings, selectedBuildingId, setSelectedBuildingId,
    controller: buildingController } = pumpHouse;
  buildingControllerCancelRef.current = buildingController.cancel;

  const nodePathController = useNodePathController({
    mapRef, trails, nodes: skiNodes, paths: skiPaths, junctions, topology,
    canArm: () => siteModeRef.current !== 'selecting',
    activate: (tool) => toolCoordinator.activate(tool),
    release: (tool) => { toolCoordinator.release(tool); },
    openDock: () => setOpenDock('trails'), clearSelection: clearSelectionState,
    acquireInteractions: (tool, map) => acquireMapInteractions(tool, map, { cursor: 'crosshair' }),
    selectNode: (id) => transitionSelection({ kind: 'ski-node', id }),
    selectPath: (id) => transitionSelection({ kind: 'ski-path', id }),
    clearSelectedNode: (id) => setSelectedNodeId((selected) => selected === id ? null : selected),
    clearSelectedPath: (id) => setSelectedPathId((selected) => selected === id ? null : selected),
    createId: genId, now: () => new Date().toISOString(),
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('ski-node-path'),
  });

  const trailController = useTrailController({
    mapRef, lifts, trails, junctions, paths: skiPaths, selectedTrailId,
    theme: resolvedTheme, topology, terrain,
    gradeAdapter: terrainGrade, paintAdapter: trailPaint, presentationAdapter: trailPresentation,
    canArm: () => siteModeRef.current !== 'selecting',
    activate: () => toolCoordinator.activate('trail'),
    release: () => { toolCoordinator.release('trail'); },
    openDock: () => setOpenDock('trails'), clearSelection: clearSelectionState,
    acquireInteractions: (map, overrides) => acquireMapInteractions('trail', map, overrides),
    terrainRecord: () => terrainRecordRef.current,
    heightGrid: (record) => {
      const cached = terrainHeightCacheRef.current;
      return cached && cached.checksum ===
        (record.packageManifest?.elevationChecksum ?? record.updatedAt)
        ? cached.heights.slice() : Float32Array.from(record.sampleHeights);
    },
    sampleProfile, gradeChanged: applyGradePreview,
    restoreGradePreview: (map) => { if (activeGradePreview()) applyGradePreview(map); },
    clearCover: coverClear.clear,
    select: (id) => {
      if (!dashboards.selectEdge(id)) transitionSelection({ kind: 'trail', id });
    },
    clearSelected: (id) => setSelectedTrailId((selected) => selected === id ? null : selected),
    closeEditing: () => setTrailEditing(false),
    reportBlockedDelete: (message) => window.alert(message),
    createId: genId, now: () => new Date().toISOString(),
    structuresVisible: () => packageStateRef.current !== 'preparing',
  });

  /** The one place a committed terrain record reaches React and the dirty flag. */
  function publishTerrainState({ record, edit, preserveDirty }: TerrainPublication): void {
    terrainRecordRef.current = record;
    if (edit) markTerrainEdited(edit);
    else if (!preserveDirty) setTerrainDirty(TERRAIN_CLEAN);
    setTerrainRecord(record);
    // Simulation rebuilds terrain coefficients on the next snow step; grading
    // must not erase the accumulated snow grid.
  }

  /**
   * Refresh only the map sources a change actually invalidated. A load or
   * package replacement refreshes nothing: a restyle follows it, and that
   * re-mounts terrain and every custom tile source anyway.
   */
  function refreshTerrainSources({ record, edit }: TerrainPublication): void {
    if (edit === 'elevation') {
      refreshElevationSources(record);
      snow.refresh();
      return;
    }
    if (edit !== 'cover') return;
    const map = mapRef.current;
    if (!map) return;
    // v5+ packages render vector cover, re-derived whole from the freshly
    // stamped grid. v4 raster-only packages refetch the resort-cover tiles.
    if (coverDisplayRef.current && record.coverDisplayMetadata) {
      setCoverData(map, coverDisplayRef.current);
      return;
    }
    clearResortCoverCache();
    const source = map.getSource('worldcover') as { setTiles?: (tiles: string[]) => void } | undefined;
    source?.setTiles?.([`${RESORT_COVER_PROTOCOL}://${encodeURIComponent(record.key)}/{z}/{x}/{y}`]);
  }

  const cacheTerrainDisplayAssets = displayAssets.cache;

  function setVisibleContours(
    record: TerrainRecordView,
    map: maplibregl.Map | null = mapRef.current,
  ): void {
    setTerrainContourData(map, record, unitsRef.current === 'imperial');
  }

  /** Paint the contours a pending grade would move in yellow. `null` clears. */
  function setEditedContours(
    segments: ArrayLike<number> | null,
    map: maplibregl.Map | null = mapRef.current,
  ): void {
    setGradedContourPreview(map, segments,
      terrainRecordRef.current?.bounds, unitsRef.current === 'imperial');
  }

  /** Whichever tool is holding a grade up for approval owns the contours on
   * screen: the map shows the ground as it *would* be, with the moved lines
   * highlighted, until the player builds or cancels. */
  function activeGradePreview(): {
    contourSegments: ArrayLike<number>; editedContourSegments: ArrayLike<number>;
  } | null {
    const trail = trailController.activeGradePreview();
    if (trail) return trail;
    const road = roadController.activeGradePreview();
    if (road) return road;
    return snowmakingController.dam.activeGradePreview() ??
      snowmakingController.pond.activeGradePreview();
  }

  function applyGradePreview(map: maplibregl.Map | null = mapRef.current): void {
    const record = terrainRecordRef.current;
    if (!record) return;
    const preview = activeGradePreview();
    if (preview) {
      setVisibleContours({ ...record, contourSegments: Array.from(preview.contourSegments) }, map);
      setEditedContours(preview.editedContourSegments, map);
    } else {
      setVisibleContours(record, map);
      setEditedContours(null, map);
    }
  }

  function refreshElevationSources(record: TerrainRecord): void {
    refreshTerrainGradeSources(mapRef.current, record, settings.units === 'imperial');
  }

  // Once-registered map handlers read live state through render-synchronized refs.
  renderQualityRef.current = settings.renderQuality;
  unitsRef.current = settings.units;
  layersRef.current = layers;
  siteBoxRef.current = siteBox;
  siteModeRef.current = siteMode;
  is3DRef.current = is3D;
  isOverheadRef.current = isOverhead;
  liftsRef.current = lifts;
  trailsRef.current = trails;
  roadsRef.current = roads;
  damsRef.current = dams;
  pondsRef.current = ponds;
  skiNodesRef.current = skiNodes;
  skiPathsRef.current = skiPaths;
  junctionsRef.current = junctions;
  selectedLakeIdRef.current = selectedLakeId;
  selectedStreamIdRef.current = selectedStreamId;
  lakeDepthOverridesRef.current = lakeDepthOverrides;
  lakeNameOverridesRef.current = lakeNameOverrides;
  snowmakingLakeIdsRef.current = snowmakingLakeIds;
  streamWidthOverridesRef.current = streamWidthOverrides;
  terrainPortsRef.current = {
    cacheDisplayAssets: cacheTerrainDisplayAssets,
    activateProtocols: setActiveResortTerrain,
    publishState: publishTerrainState,
    refreshSources: refreshTerrainSources,
    publishPersisted: () => setTerrainDirty(TERRAIN_CLEAN),
    publishConstruction: setBuildingActivity,
  };
  packageStateRef.current = packageState;
  // Redefined each render so the boot-failure "Prepare Resort Data" button
  // (rendered by App on the loading screen) runs against current state.
  repairRef.current = () => void repairAndContinue();

  useEffect(() => () => {
    // Drops construction ownership and invalidates queued cover work and any
    // outstanding grade preview. The document stays usable, so a StrictMode
    // remount does not retire it.
    terrain.dispose();
    // Without this a cancelled load leaves warmResortTiles rasterizing against
    // a torn-down map for the rest of the tile set.
    warmAbortRef.current?.abort();
    trailPaint.dispose();
    damAnalysis.dispose();
    coverEdit.dispose();
    terrainGrade.dispose();
    // The document and the adapters are ref-held and never change identity, so
    // this stays a mount/unmount effect.
  }, [terrain, damAnalysis, coverEdit, terrainGrade, trailPaint]);

  // A saved resort does not enter gameplay until its mandatory local package
  // has loaded and passed manifest validation.
  useEffect(() => {
    if (mode !== 'playing') {
      setActiveResortTerrain(null);
      return;
    }
    let cancelled = false;
    const key = initialSave?.terrainKey;
    if (!key) {
      setPackageState('missing');
      reportFailure('The local resort package is missing. Prepare it again to continue.');
      return;
    }
    reportStage({ stage: 'package' });
    void loadTerrain(key).then(async (record) => {
      if (cancelled) return;
      if (!record) {
        const message = 'The local resort package is missing. Prepare it again to continue.';
        setPackageError(message);
        setPackageState('missing');
        reportFailure(message);
        return;
      }
      let readyRecord = record;
      if (record.schemaVersion === 4 && record.coverGrid && record.bounds) {
        packageStateRef.current = 'optimizing';
        setPackageState('optimizing');
        setPackageProgress({ phase: 'vectorizing-cover', message: 'Drawing smooth ground cover', completed: 0, total: 1 });
        reportStage({ stage: 'validate', note: 'Drawing smooth ground cover' });
        // Let React paint the one-time upgrade gate before the CPU-heavy trace.
        await new Promise<void>((resolve) => window.setTimeout(resolve, 0));
        try {
          const upgraded = withPreparedCoverDisplay({ ...record, updatedAt: new Date().toISOString() });
          const upgradeValidation = validateTerrainPackage(upgraded);
          if (!upgradeValidation.ok) throw new Error(upgradeValidation.errors.join(' '));
          const savedUpgrade = await saveTerrain(upgraded);
          if (!savedUpgrade.ok) throw new Error(savedUpgrade.error);
          readyRecord = upgraded;
        } catch (error) {
          // The old package stays playable and uses the raster protocol.
          console.warn('Vector ground-cover upgrade failed; using raster fallback.', error);
          readyRecord = record;
        }
      }
      if (cancelled) return;
      reportStage({ stage: 'validate' });
      const validation = validateTerrainPackage(readyRecord);
      if (!validation.ok) {
        setPackageError(validation.errors.join(' '));
        setPackageState('error');
        reportFailure(validation.errors.join(' '));
        return;
      }
      // The package exactly as it is on disk: a clean replacement, never an edit.
      terrain.replace(readyRecord);
      snow.load(readyRecord, initialSave?.snow);
      // The resort's own aerial becomes the loading screen's backdrop — it is
      // decoded here regardless, so the picture is free.
      reportBoot({ type: 'backdrop', imageryUrl: localImageryUrlRef.current });
      reportStage({ stage: 'build' });
      setPackageState('ready');
    }).catch((error) => {
      if (cancelled) return;
      const message = error instanceof Error ? error.message : 'Unable to load the local resort package.';
      setPackageError(message);
      setPackageState('error');
      reportFailure(message);
    });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /** Selection stays outside the tool coordinator, but every transition uses
   * this one path so it cannot leave a hidden selection or active tool behind. */
  function clearSelectionState() {
    setSelectedLiftId(null);
    setSelectedTrailId(null);
    setSelectedDamId(null);
    setSelectedPondId(null);
    setSelectedBuildingId(null);
    setSelectedSnowmakingNodeId(null);
    setSelectedSnowmakingPipeId(null);
    setSelectedSnowgunId(null);
    setSelectedNodeId(null);
    setSelectedPathId(null); setSelectedRoadKey(null);
    setSelectedLakeId(null);
    setSelectedStreamId(null);
    setLiftEditing(false);
    setTrailEditing(false);
  }

  function transitionSelection(target: SelectionTarget) {
    toolCoordinator.cancelActive();
    clearSelectionState();

    switch (target.kind) {
      case 'lift': setSelectedLiftId(target.id); break;
      case 'trail': setSelectedTrailId(target.id); break;
      case 'dam': setSelectedDamId(target.id); setOpenDock('snowmaking'); break;
      case 'pond': setSelectedPondId(target.id); setOpenDock('snowmaking'); break;
      case 'building': setSelectedBuildingId(target.id); setOpenDock('snowmaking'); break;
      case 'snowmaking-node': setSelectedSnowmakingNodeId(target.id); setOpenDock('snowmaking'); break;
      case 'snowmaking-pipe': setSelectedSnowmakingPipeId(target.id); setOpenDock('snowmaking'); break;
      case 'snowgun': setSelectedSnowgunId(target.id); setOpenDock('snowmaking'); break;
      case 'ski-node': setSelectedNodeId(target.id); break;
      case 'ski-path': setSelectedPathId(target.id); break;
      case 'road': setSelectedRoadKey(target.id); setOpenDock('infrastructure'); break;
      case 'lake': setSelectedLakeId(target.id); setOpenDock(null); break;
      case 'stream': setSelectedStreamId(target.id); setOpenDock(null); break;
      case 'none': break;
    }
  }

  // Clicking a run opens its read-only detail.
  selectTrailRef.current = (id: string) => {
    trailController.select(id);
  };

  selectLakeRef.current = (id: string) => {
    transitionSelection({ kind: 'lake', id });
  };

  selectStreamRef.current = (id: string) => {
    transitionSelection({ kind: 'stream', id });
  };

  /**
   * Basemap context: DEM, contours, ground cover, aerial, and local vectors.
   * Hands back the layer-toggle model reconciled against what the player had
   * hidden, so a light↔dark restyle never switches a hidden layer back on.
   */
  function installAnalysisLayers(map: maplibregl.Map): LayerToggle[] {
    // While preparation is blocking the game, remove preview DEM/contour/
    // WorldCover sources so they cannot contend with mandatory downloads.
    const fresh = packageStateRef.current === 'preparing'
      ? []
      : setupAnalysisLayers(map, terrainRecordRef.current, unitsRef.current, coverDisplayRef.current,
        localImageryUrlRef.current, lakeNameOverridesRef.current,
        streamWidthOverridesRef.current, snow.gridRef.current, snow.modeRef.current,
        renderQualityRef.current);
    return fresh;
  }

  function installSiteBoundaryLayers(map: maplibregl.Map): void {
    addSiteBoxLayers(map);
  }

  function synchronizeSiteBoundary(map: maplibregl.Map): void {
    // Once a package exists, the "box" is its true data extent (record.bounds),
    // not the smaller square first dragged: the elevation service snaps the
    // download taller and cover/contours/vectors all fill that extent. Drawing
    // the outline + exterior mask there keeps every play-box layer inside the
    // outline, leaving only elevation + hillshade in the perimeter ring.
    const rec = terrainRecordRef.current;
    const lockedBox = rec?.bounds ? siteBoxFromBounds(rec.bounds) : siteBoxRef.current;
    if (siteModeRef.current === 'locked' && lockedBox) {
      setSiteBox(map, lockedBox);
      setBoundaryMode(map, 'locked', lockedBox);
    }
  }

  /** Feature controllers supply structure contributions; MapView supplies the
   * cross-cutting analysis and site-boundary families. */
  function createMapContributions(): ManagedMapContribution[] {
    return [
      {
        id: 'analysis', zOrder: MAP_Z_ORDER.analysis,
        hits: [
          { id: 'stream', priority: MAP_HIT_RANK.stream, layerIds: ['local-water-line-hit'],
            select: (id) => selectStreamRef.current(id) },
          { id: 'lake', priority: MAP_HIT_RANK.lake, layerIds: ['local-water-fill'],
            select: (id) => selectLakeRef.current(id) },
        ],
        install: ({ map }) => {
          analysisTogglesRef.current = installAnalysisLayers(map);
          addDashboardMapLayers(map);
        },
        synchronizeData: ({ map }) => {
          const record = terrainRecordRef.current;
          if (record) setLocalContextData(map, record, lakeNameOverridesRef.current,
            streamWidthOverridesRef.current);
          setSelectedLake(map, selectedLakeIdRef.current);
          setSelectedStream(map, selectedStreamIdRef.current);
          dashboards.sync(map);
        },
        visibility: () => analysisTogglesRef.current,
        visibilityChanged: ({ map }, id, visible) => {
          if (id === 'satellite') applyCoverOpacity(map, visible);
          if (id === 'hillshade' || id === 'contours') {
            const descriptors = mapContributionRegistryRef.current?.visibilityDescriptors() ?? [];
            applyAnalysisRenderProfile(map, renderQualityRef.current, isOverheadRef.current, {
              hillshade: descriptors.find((entry) => entry.id === 'hillshade')?.visible,
              contours: descriptors.find((entry) => entry.id === 'contours')?.visible,
            });
          }
        },
        presentationChanged: ({ map }, presentation) => setDashboardMapVisibility(map,
          presentation === 'dashboard-trails' ? 'trails'
            : presentation?.startsWith('dashboard-snowmaking') ? 'snowmaking' : null),
        cleanup: () => {},
      },
      {
        id: 'site-boundary', zOrder: MAP_Z_ORDER['site-boundary'],
        install: ({ map }) => installSiteBoundaryLayers(map),
        synchronizeData: ({ map }) => synchronizeSiteBoundary(map),
        cleanup: () => {},
      },
      roadController.contribution,
      snowmakingController.dam.contribution,
      snowmakingController.pond.contribution,
      nodePathController.contribution,
      trailController.contribution,
      liftController.contribution,
      buildingController.contribution,
      snowmakingController.network.contribution,
      guestPortalController.contribution,
    ];
  }
  if (!mapContributionRegistryRef.current) {
    mapContributionRegistryRef.current = new MapContributionRegistry(createMapContributions());
  }
  const mapContributions = mapContributionRegistryRef.current;
  reconfigureAnalysisProfileRef.current = (map) => {
    if (terrainRecordRef.current) cacheTerrainDisplayAssets(terrainRecordRef.current);
    removeAnalysisLayers(map);
    analysisTogglesRef.current = installAnalysisLayers(map);
    dashboards.sync(map);
    setLayers(layerTogglesOf(mapContributions.refreshVisibility()));
  };
  useEffect(() => { if (toolCoordinatorState.activeTool) mapContributions.clearHitHovers(); },
    [toolCoordinatorState.activeTool, mapContributions]);
  const mapContext = useMapContextRecovery(terrain, mapContributions);

  useMapRuntime({
    canStart: mode !== 'playing' || packageState === 'ready',
    mode: mapMode,
    initialSave,
    initialCenter: INITIAL_CENTER,
    initialZoom: INITIAL_ZOOM,
    resolvedTheme,
    renderQuality: settings.renderQuality,
    units: settings.units,
    mapRef,
    containerRef,
    terrainRecordRef,
    renderQualityRef,
    layersRef,
    siteBoxRef,
    siteModeRef,
    is3DRef,
    resortReadyRef,
    warmAbortRef,
    bootControls,
    bootControlsRef,
    mapInteractionLeaseRef,
    registry: mapContributions,
    reconfigureAnalysisProfileRef,
    canDispatchHit: () => toolCoordinator.snapshot.activeTool === null,
    doSampleRef,
    lastLngLatRef,
    rafPendingRef,
    setLayers,
    setReadout,
    setIsOverhead,
    setIs3D,
    reportBoot,
    reportStage,
    showLocalBoot,
    reportGraphicsFailure: setCheckpointError,
  });

  // Drag-to-draw the site rectangle while in 'selecting' mode.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || siteMode !== 'selecting') return;

    map.dragPan.disable();
    map.getCanvas().style.cursor = 'crosshair';
    let anchor: { lng: number; lat: number } | null = null;

    const down = (e: maplibregl.MapMouseEvent) => {
      anchor = { lng: e.lngLat.lng, lat: e.lngLat.lat };
    };
    const move = (e: maplibregl.MapMouseEvent) => {
      if (!anchor) return;
      const box = computeBox(anchor, e.lngLat);
      setSiteBox(map, box);
      setSiteBoxState(box);
    };
    const up = () => {
      anchor = null;
    };
    map.on('mousedown', down);
    map.on('mousemove', move);
    map.on('mouseup', up);

    return () => {
      map.off('mousedown', down);
      map.off('mousemove', move);
      map.off('mouseup', up);
      map.dragPan.enable();
      map.getCanvas().style.cursor = '';
    };
  }, [siteMode]);

  useEffect(() => {
    mapContributions.synchronizeData('analysis');
  }, [lakeNameOverrides, streamWidthOverrides, mapContributions]);

  useElevationBackfill({
    getLifts: () => liftsRef.current,
    getTrails: () => trailsRef.current,
    setLifts,
    topology,
    samplePoint: samplePlanningTerrainOrNull,
    sampleProfile,
  });

  function cancelDamTool() {
    snowmakingController.dam.cancel();
  }

  function cancelPondTool() {
    snowmakingController.pond.cancel();
  }

  function cancelRoadTool() {
    roadController.cancel();
  }

  function cancelLiftTool() {
    liftController.cancel();
  }

  /** Patch a non-geometric field (name/chairs/capacity/status) of a built lift. */
  function patchLift(id: string, patch: Partial<SavedLift>) {
    liftController.patch(id, patch);
  }

  function cancelNodeTool() {
    nodePathController.cancelNode();
  }

  function cancelPathTool() {
    nodePathController.cancelPath();
  }

  /** Patch a non-geometric field (closed) of a built connector path. */
  function patchSkiPath(id: string, patch: Partial<SavedPath>) {
    nodePathController.patchPath(id, patch);
  }

  function cancelTrailTool() {
    trailController.cancel();
  }

  /** Patch a non-geometric field (name/status) of a built run. */
  function patchTrail(id: string, patch: Partial<SavedTrail>) {
    trailController.patch(id, patch);
  }


  /** Close/open a bottom dock, yielding any active draw tool of the others. */
  function toggleDock(which: DockId) {
    const waterDetailOpen = selectedLakeId !== null || selectedStreamId !== null;
    const activeTool = toolCoordinator.snapshot.activeTool;
    const isOpen = !waterDetailOpen && (which === 'layers'
      ? openDock === 'layers' || layersAlongsideBuild
      : which === 'lifts' ? openDock === 'lifts' || activeTool === 'lift' || selectedLiftId !== null
        : which === 'trails' ? openDock === 'trails' || activeTool === 'trail' ||
          activeTool === 'ski-node' || activeTool === 'ski-path' || selectedTrailId !== null
          : which === 'snowmaking' ? openDock === 'snowmaking' || activeTool === 'dam' ||
            activeTool === 'pond' || activeTool === 'snowmaking-pipe' ||
            activeTool === 'snowmaking-node' || activeTool === 'snowmaking-gun' ||
            selectedDamId !== null || selectedPondId !== null || selectedSnowmakingNodeId !== null ||
            selectedSnowmakingPipeId !== null || selectedSnowgunId !== null || selectedBuildingId !== null
            : openDock === 'infrastructure' || activeTool === 'road' || activeTool === 'guest-portal');
    if (toolCoordinator.toggleDock(which, isOpen) === 'layers-alongside') return;

    setSelectedLakeId(null);
    setSelectedStreamId(null);
    if (which !== 'lifts') {
      setSelectedLiftId(null);
      setLiftEditing(false);
    }
    if (which !== 'trails') {
      setSelectedTrailId(null);
      setTrailEditing(false);
    }
    if (which !== 'snowmaking') {
      setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null);
      setSelectedSnowmakingPipeId(null); }
    if (isOpen) {
      if (which === 'lifts') {
        setSelectedLiftId(null);
        setLiftEditing(false);
      }
      if (which === 'trails') {
        setSelectedTrailId(null);
        setTrailEditing(false);
      }
      if (which === 'snowmaking') {
        setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null);
        setSelectedSnowmakingPipeId(null); }
    }
  }

  function startSelect() {
    const map = mapRef.current;
    if (map) {
      setSiteBox(map, null);
      setBoundaryMode(map, 'selecting');
    }
    setSiteBoxState(null);
    setSiteMode('selecting');
  }

  function cancelSelect() {
    const map = mapRef.current;
    if (map) {
      setSiteBox(map, null);
      setBoundaryMode(map, 'off');
    }
    setSiteBoxState(null);
    setSiteMode('explore');
  }

  function confirmSite() {
    const map = mapRef.current;
    if (!map || !siteBox) return;
    setBoundaryMode(map, 'locked', siteBox);
    // Before a package exists, bound to the drawn square; once one is prepared
    // the restyle rebinds to the perimeter ring (see the style.load handler).
    const rec = terrainRecordRef.current;
    const cam = rec ? resortCameraBounds(rec) : undefined;
    map.setMaxBounds(cam ?? siteBox.bounds);
    map.fitBounds(siteBox.bounds, { padding: 40, duration: 600 });
    setSiteMode('locked');
  }

  function exitSite() {
    const map = mapRef.current;
    if (map) {
      map.setMaxBounds(null);
      setSiteBox(map, null);
      setBoundaryMode(map, 'off');
    }
    setSiteBoxState(null);
    setSiteMode('explore');
  }

  function toggle3D() {
    const map = mapRef.current;
    if (!map || !terrainRecordRef.current) return; // only meaningful in the resort view
    // Snap to whichever view the camera is not already in. Any tilt at all —
    // including one the player dragged into — counts as 3D, so the button
    // always does what its label says.
    const next = isOverhead;
    setIs3D(next);
    tilt3D(map, next); // terrain stays mounted; this is a pure camera ease
  }

  async function prepareLocalPackage(name: string): Promise<TerrainRecord | null> {
    const site = siteBoxRef.current;
    if (!site) {
      setPackageError('A resort boundary is required before terrain can be prepared.');
      setPackageState('error');
      return null;
    }
    setPackageError(null);
    mapContext.decide('cancel');
    packageStateRef.current = 'preparing';
    setPackageState('preparing');
    // Preparation owns the screen from here — its own gate has the step
    // checklist and a Cancel button, so any resort loading screen stands down.
    reportBoot({ type: 'handoff' });
    setPackageProgress({ phase: 'elevation', message: 'Starting resort preparation', completed: 0, total: 10 });
    const controller = mapContext.startPreparation();
    mapRef.current?.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
    try {
      const record = await prepareResortPackage(
        site,
        name,
        { sampleSiteCoverGrid },
        { onProgress: setPackageProgress, signal: controller.signal,
          onMapContextFailure: mapContext.requestDecision }
      );
      const validation = validateTerrainPackage(record);
      if (!validation.ok) throw new Error(validation.errors.join(' '));
      const weatherPreparation = await simulation.prepareWeatherForTerrain(record, controller.signal);
      if (!weatherPreparation.ok) console.warn('Terrain prepared without weather:', weatherPreparation.error);
      // Ingest persisted this package itself, so it starts clean.
      terrain.replace(record);
      snow.regenerate(record);
      packageStateRef.current = 'ready';
      setPackageState('ready');
      // Cover the first resort render; the style.load reveal drops it once the
      // resort is fully drawn. Set unconditionally: repairing a save that
      // failed to load has no map yet (mapCanStart was false), and the one it
      // is about to construct needs covering just as much as a restyle does.
      showLocalBoot({ stage: 'build' });
      // The runtime observes mapMode changing to "playing" and performs the
      // single picker-to-offline style handoff.
      return record;
    } catch (error) {
      if (controller.signal.aborted || (error instanceof DOMException && error.name === 'AbortError')) {
        setPackageError(null);
        setPackageProgress(null);
        packageStateRef.current = mode === 'playing' ? 'missing' : 'ready';
        setPackageState(mode === 'playing' ? 'missing' : 'ready');
        if (mode !== 'playing') mapRef.current?.setStyle(basemapFor(resolvedTheme));
        return null;
      }
      setPackageError(error instanceof Error ? error.message : 'Resort preparation failed.');
      packageStateRef.current = 'error';
      setPackageState('error');
      return null;
    } finally {
      mapContext.finishPreparation(controller);
    }
  }

  /**
   * Hide every in-progress overlay for the resume-preview capture, then restore
   * it. Each family owns its own transient, so a family added later cannot be
   * left out of the capture by forgetting to extend a list here; the grade
   * preview is not a family and stays explicit.
   */
  function setCaptureTransients(hidden: boolean): void {
    const map = mapRef.current;
    if (!map) return;
    mapContributions.setCaptureTransients(hidden);
    if (hidden) {
      const record = terrainRecordRef.current;
      if (record) setVisibleContours(record);
      setEditedContours(null);
      return;
    }
    applyGradePreview();
  }
  // Deterministic capture verification, alongside appMap/appNetwork above.
  (window as unknown as { appSetCaptureTransients: (hidden: boolean) => void })
    .appSetCaptureTransients = setCaptureTransients;

  const checkpointPromiseRef = useRef<Promise<ExitCheckpointResult> | null>(null);
  async function checkpointForExit(interactive = true): Promise<ExitCheckpointResult> {
    if (checkpointPromiseRef.current) return checkpointPromiseRef.current;
    const run = (async (): Promise<ExitCheckpointResult> => {
      const persisted = persistedSaveRef.current;
      const map = mapRef.current;
      if (!persisted) return { ok: true };
      // A package failure/loading cancellation can leave no map to capture. The
      // already-persisted resume pose is still valid, so leaving remains safe.
      if (!map) return { ok: true };

      setCheckpointError(null);
      setSaving(true);
      simulation.pause();
      const runtime = simulation.snapshot(), center = map.getCenter();
      const checkpoint = { ...withResumeCheckpoint(persisted, {
        center: [center.lng, center.lat],
        zoom: map.getZoom(),
        bearing: map.getBearing(),
        pitch: map.getPitch(),
      }, is3DRef.current), ...runtime, snow: snow.snapshot(persisted.snow) };
      const savedCheckpoint = await saveGameWithGuestCheckpoint(checkpoint, guestRuntime).catch((error: unknown) => ({
        ok: false as const,
        error: error instanceof Error ? error.message : 'The save service did not respond.',
      }));
      if (!savedCheckpoint.ok) {
        setSaving(false);
        const error = `Could not save the resume position: ${savedCheckpoint.error}`;
        if (interactive) setCheckpointError(error);
        return { ok: false, error };
      }
      persistedSaveRef.current = checkpoint;

      // The image is best effort. A failed capture keeps the prior JPEG and
      // never blocks a successfully saved camera checkpoint.
      try {
        document.documentElement.classList.add('resume-capture');
        setCaptureTransients(true);
        const browserDataUrl = await waitForCaptureFrame(map);
        const preview = await captureGamePreview(checkpoint.key, browserDataUrl).catch(
          (error: unknown) => ({
            ok: false as const,
            error: error instanceof Error ? error.message : 'The preview service did not respond.',
          })
        );
        if (!preview.ok) console.warn('Unable to refresh the resort loading preview.', preview.error);
      } finally {
        try {
          setCaptureTransients(false);
        } catch (error) {
          console.warn('Unable to restore transient map layers after preview capture.', error);
        }
        document.documentElement.classList.remove('resume-capture');
        setSaving(false);
      }
      return { ok: true };
    })().finally(() => {
      checkpointPromiseRef.current = null;
    });
    checkpointPromiseRef.current = run;
    return run;
  }

  /** Snapshot the current camera + site + 3D into a GameSave shape. */
  function snapshot(base: GameSave | null): GameSave | null {
    const map = mapRef.current;
    if (!map) return base;
    const c = map.getCenter();
    const now = new Date().toISOString();
    const committedTopology = committedTopologyRef.current, committedTerrain = terrain.snapshot().record;
    const runtime = simulation.snapshot();
    return {
      schemaVersion: CURRENT_GAME_SAVE_SCHEMA_VERSION,
      key: base?.key ?? genId(),
      name: base?.name ?? (nameDraft.trim() || 'Untitled Resort'),
      mountainId: base?.mountainId,
      terrainKey: committedTerrain?.key ?? base?.terrainKey,
      center: [c.lng, c.lat],
      zoom: map.getZoom(),
      bearing: map.getBearing(),
      pitch: map.getPitch(),
      is3D: is3DRef.current,
      site: siteBoxRef.current,
      lifts: liftsRef.current,
      trails: committedTopology.trails,
      roads: roadsRef.current,
      dams: damsRef.current,
      ponds: pondsRef.current,
      buildings: committedBuildingsRef.current,
      nodes: committedTopology.nodes,
      paths: committedTopology.paths,
      junctions: committedTopology.junctions,
      snowmakingNodes: committedSnowmakingRef.current.nodes,
      snowmakingPipes: committedSnowmakingRef.current.pipes,
      snowguns: committedSnowmakingRef.current.guns,
      snowmakingNodeNextNumbers: committedSnowmakingRef.current.nextNumbers,
      lakeDepthOverrides: lakeDepthOverridesRef.current,
      lakeNameOverrides: lakeNameOverridesRef.current,
      snowmakingLakeIds: snowmakingLakeIdsRef.current,
      streamWidthOverrides: streamWidthOverridesRef.current,
      snow: snow.snapshot(base?.snow),
      weatherRun: runtime.weatherRun ?? base?.weatherRun,
      time: runtime.time,
      createdAt: base?.createdAt ?? now,
      updatedAt: now,
      lastPlayedAt: base?.lastPlayedAt,
    };
  }

  /** The live design, for comparison against the last one written to disk. */
  function liveDesign(): DesignSnapshot {
    const committedTopology = committedTopologyRef.current;
    return {
      name: saved?.name ?? initialSave?.name ?? '',
      site: siteBoxRef.current,
      lifts: liftsRef.current,
      trails: committedTopology.trails,
      roads: roadsRef.current,
      dams: damsRef.current,
      ponds: pondsRef.current,
      buildings: committedBuildingsRef.current,
      nodes: committedTopology.nodes,
      paths: committedTopology.paths,
      junctions: committedTopology.junctions,
      snowmakingNodes: committedSnowmakingRef.current.nodes,
      snowmakingPipes: committedSnowmakingRef.current.pipes,
      snowguns: committedSnowmakingRef.current.guns,
      snowmakingNodeNextNumbers: committedSnowmakingRef.current.nextNumbers,
      lakeDepthOverrides: lakeDepthOverridesRef.current,
      lakeNameOverrides: lakeNameOverridesRef.current,
      snowmakingLakeIds: snowmakingLakeIdsRef.current,
      streamWidthOverrides: streamWidthOverridesRef.current,
    };
  }

  function hasUnsavedChanges(): boolean {
    return terrainHasEdits(terrainDirtyRef.current) || designHasEdits(savedDesign, liveDesign());
  }

  /**
   * Write whatever terrain edits are pending. Returns an error message, or null
   * when there was nothing to do or the write succeeded.
   */
  async function flushTerrain(): Promise<string | null> {
    const { record, revision } = terrain.snapshot();
    if (!record || !terrainHasEdits(terrainDirtyRef.current)) return null;
    // Persistence serializes the record and never mutates it. Keep the one
    // mutable-type escape at this boundary rather than weakening snapshots.
    const result = await flushTerrainEdits(record as unknown as TerrainRecord, terrainDirtyRef.current,
      { saveTerrain, saveTerrainCover });
    if (!result.ok) return result.error;
    // Anything built while the write was in flight is not covered by it.
    terrain.markPersisted(revision);
    return null;
  }

  async function createSave() {
    setSaving(true);
    const name = nameDraft.trim() || 'Untitled Resort';
    const record = terrainRecordRef.current ?? await prepareLocalPackage(name);
    if (!record) {
      setSaving(false);
      return;
    }
    const next = snapshot(null);
    if (!next) { setSaving(false); return; }
    const terrainError = await flushTerrain();
    if (terrainError) {
      setSaving(false);
      setCheckpointError(`The terrain package could not be saved: ${terrainError}`);
      return;
    }
    const res = await saveGameWithGuestCheckpoint(next, guestRuntime);
    setSaving(false);
    if (res.ok) {
      persistedSaveRef.current = next;
      setSaved(next);
      setSavedDesign(designOf(next));
    } else {
      setCheckpointError(`Could not save the resort: ${res.error}`);
    }
  }

  async function repairAndContinue() {
    const base = saved ?? initialSave;
    if (!base) return;
    const record = await prepareLocalPackage(base.name);
    if (!record) return;
    const next: GameSave = { ...base, terrainKey: record.key, updatedAt: new Date().toISOString() };
    const result = await saveGameWithGuestCheckpoint(next, guestRuntime);
    if (result.ok) {
      persistedSaveRef.current = next;
      setSaved(next);
    }
    else {
      setPackageError(result.error);
      setPackageState('error');
    }
  }

  /** Explicit save: the only path that writes terrain edits to disk. Terrain
   *  goes first — a GameSave whose runs reference ungraded ground is the worse
   *  of the two half-written outcomes. */
  async function saveProgress(): Promise<boolean> {
    const next = snapshot(saved);
    if (!next) return false;
    setSaving(true);
    const terrainError = await flushTerrain();
    if (terrainError) {
      setSaving(false);
      setCheckpointError(`The terrain package could not be saved: ${terrainError}`);
      return false;
    }
    const res = await saveGameWithGuestCheckpoint(next, guestRuntime);
    setSaving(false);
    if (!res.ok) {
      setCheckpointError(`Could not save the resort: ${res.error}`);
      return false;
    }
    persistedSaveRef.current = next;
    setSaved(next);
    setSavedDesign(designOf(next));
    return true;
  }

  /**
   * The unsaved-work gate, run before leaving the resort. Resolves true when it
   * is safe to navigate away — nothing pending, the player discarded, or the
   * save they asked for succeeded.
   */
  async function confirmExit(): Promise<boolean> {
    if (!saved || !hasUnsavedChanges()) return true;
    const choice = await new Promise<'save' | 'discard' | 'cancel'>((resolve) => {
      unsavedChoiceRef.current = resolve;
      setUnsavedPrompt(true);
    });
    unsavedChoiceRef.current = null;
    if (choice !== 'save') {
      setUnsavedPrompt(false);
      return choice === 'discard';
    }
    // The dialog stays up showing its spinner until the write settles.
    const ok = await saveProgress();
    setUnsavedPrompt(false);
    return ok;
  }

  /** Live-rename the resort; persists on the next Save (snapshot reads saved.name). */
  function renameResort(name: string) {
    setSaved((s) => (s ? { ...s, name } : s));
  }

  useEffect(() => {
    if (!sessionControlsRef) return;
    sessionControlsRef.current = { checkpointForExit, confirmExit,
      resortSettings: terrainRecord ? {
        mapContextAvailable: has3DBuildingContext(terrainRecord.vectorFeatures),
        downloadMapContext: (signal) => mapContext.repair(terrainRecord, signal) } : undefined };
    return () => {
      sessionControlsRef.current = null;
    };
  });

  const picking = mode === 'picking';
  const awaitingName = picking && siteMode === 'locked' && !saved;

  // The gate is now the New Game preparation surface only. Resuming a saved
  // resort — including a missing or invalid package — is reported upward and
  // rendered on App's resort loading screen, so a load is one screen throughout.
  const showPackageGate = packageState === 'preparing' || (mode === 'picking' && packageState === 'error');

  /** Coordinate to reverse-geocode for the resort's Location: site center if a
   *  site box is locked, else the saved camera center, else the live map. */
  function resortCenter(): [number, number] {
    const box = siteBox;
    if (box) {
      const [[w, s], [e, n]] = box.bounds;
      return [(w + e) / 2, (s + n) / 2];
    }
    if (saved) return saved.center;
    const c = mapRef.current?.getCenter();
    return c ? [c.lng, c.lat] : INITIAL_CENTER;
  }

  function handleToggle(id: string) {
    setLayers(layerTogglesOf(mapContributions.toggleVisibility(id)));
  }

  return (
    <>
      <div ref={containerRef} className="map-root" />
      <MapViewChrome
        checkpointError={checkpointError}
        dismissCheckpointError={() => setCheckpointError(null)}
        unsaved={unsavedPrompt ? {
          saving,
          onChoice: (choice) => unsavedChoiceRef.current?.(choice),
        } : null}
        packageGate={showPackageGate ? {
          state: packageState === 'error' ? 'error' : 'preparing',
          progress: packageProgress,
          error: packageError,
          mapContextError: mapContext.error,
          cancel: mapContext.cancelPreparation,
          back: onQuit,
          prepare: () => { void createSave(); },
          decideMapContext: mapContext.decide,
        } : null}
        localBoot={localBoot ? {
          progress: localBoot,
          title: saved?.name || nameDraft.trim() || 'Your resort',
          imageryUrl: localImageryUrlRef.current,
          back: onQuit,
          reveal: () => bootControls.current?.reveal(),
        } : null}
        menu={{
          canSave: !!saved,
          saving,
          unsaved: !!saved && hasUnsavedChanges(),
          onSave: () => { void saveProgress(); },
          onLoad: onLoadGame,
          onSettings: onOpenSettings,
          onCredits: () => setShowCredits(true),
          onRebuildCover: terrainRecord && terrainRecord.schemaVersion < 6
            ? () => { void repairAndContinue(); } : undefined,
          onQuit,
        }}
        searchResult={picking && !saved ? (result) => {
          mapRef.current?.flyTo({ center: [result.lng, result.lat], zoom: 12, duration: 1200 });
        } : null}
        siteControl={picking && !saved ? {
          mode: siteMode, box: siteBox, onStart: startSelect, onConfirm: confirmSite,
          onCancel: cancelSelect, onExit: exitSite,
        } : null}
        view3D={terrainRecord ? { is3D: !isOverhead, onToggle: toggle3D } : null}
        buildingActivity={buildingActivity}
        bottomRightToolOptions={saved ? <SnowmakingToolOptions
          controller={snowmakingController.network} gunController={snowmakingController.guns}
          units={settings.units} /> : null}
        dashboardToggle={saved ? { active: dashboards.active, change: dashboards.change } : null}
        dashboardPipeHover={dashboards.active === 'snowmaking' && dashboards.snowHover ?
          { hover: dashboards.snowHover, units: settings.units } : null}
        dashboard={saved && dashboards.active ? {
          dashboard: dashboards.active, snowmakingMode: dashboards.snowMode,
          networkProps: {
            network, units: settings.units,
            selectedLiftId: dashboards.liftId, selectedEdgeId: dashboards.edgeId,
            onSelectLift: dashboards.setLiftId,
            onSelectEdge: (id) => { dashboards.setLiftId(null); dashboards.setEdgeId(id); },
            onToggleTrailClosed: (id, closed) => patchTrail(id, { closed }),
            onToggleLiftClosed: (id, closed) => patchLift(id, { closed }),
            onTogglePathClosed: (id, closed) => patchSkiPath(id, { closed }),
          },
          snowmakingProps: { ...snowmakingDashboardProps({
            dams, ponds, lakes: snowmakingLakes ?? [], trails, lifts, nodes: snowmakingNodes,
            pipes: snowmakingPipes, guns: snowguns,
            coverDisplay: coverDisplayRef.current, terrainRecord, units: settings.units,
            selectedNodeId: dashboards.snowSelection?.kind === 'node' ? dashboards.snowSelection.id : null,
            selectedPipeId: dashboards.snowSelection?.kind === 'pipe' ? dashboards.snowSelection.id : null, selectedPipeSegmentId: dashboards.snowSelection?.kind === 'pipe' ? dashboards.snowSelection.segmentId : null,
            selectedGunId: dashboards.snowSelection?.kind === 'gun' ? dashboards.snowSelection.id : null,
            clearNode: () => dashboards.setSnowSelection(null), clearPipe: () => dashboards.setSnowSelection(null),
            clearGun: () => dashboards.setSnowSelection(null), controller: snowmakingController.network,
            gunController: snowmakingController.guns,
          }), mapHoveredPipe: dashboards.snowHover, snowmakingLasso: dashboards.snowLasso, snowGunSelectionPhase: dashboards.snowGunSelectionPhase,
          onToggleSnowGunSelection: dashboards.toggleSnowGunSelection, onCancelSnowGunSelection: dashboards.cancelSnowGunSelection },
          guestProps: { ...guestVibe, selectedGuestId, onSelectGuest: guests.selectGuest,
            onClearSelectedGuest: guests.clearSelectedGuest },
          onFit: dashboards.fit, onSnowmakingPresentationChange: dashboards.setSnowPresentation, onClose: dashboards.close,
        } : null}
        readout={!saved ? { store: readoutStore, units: settings.units } : null}
        dock={saved ? {
          saved, units: settings.units, readoutStore, building,
          openDock, layersAlongsideBuild,
          coordinator: toolCoordinatorState, layers, activeOverlay,
          lifts, trails, roads, dams, ponds, buildings, snowmakingLakes: snowmakingLakes ?? [],
          snowmakingNodes, snowmakingPipes, snowguns, skiNodes, skiPaths,
          junctions, terrainRecord, network, simulation, guestPortal, guestPortalController, guestRuntime,
          selectedLiftId, selectedTrailId,
          selectedDamId, selectedPondId, selectedBuildingId,
          selectedSnowmakingNodeId, selectedSnowmakingPipeId, selectedSnowgunId, selectedNodeId,
          selectedPathId, selectedRoadKey, selectedLakeId,
          selectedStreamId, liftEditing, trailEditing,
          lakeDepthOverrides, lakeNameOverrides, snowmakingLakeIds,
          streamWidthOverrides,
          snowControl: activeOverlay === 'snow' && !dashboards.active
            ? { mode: snow.mode, change: snow.changeMode, close: () => handleToggle('snow'), escapeEnabled: toolCoordinatorState.activeTool === null && !controlsSuspended } : null,
          liftController,
          roadController, trailController,
          nodePathController, snowmakingController, buildingController,
          toggleDock, openSnowmakingAnalysis: dashboards.openAnalysis,
          closeDock: () => setOpenDock(null),
          closeLayers: () => {
            setLayersAlongsideBuild(false);
            setOpenDock((current) => current === 'layers' ? null : current);
          },
          toggleLayer: handleToggle,
          openStats: () => setShowStats(true),
          setLiftEditing,
          setTrailEditing,
          clearSelectedLift: () => { setSelectedLiftId(null); setOpenDock('lifts'); },
          clearSelectedTrail: () => { setSelectedTrailId(null); setOpenDock('trails'); },
          clearSelectedDam: () => setSelectedDamId(null),
          clearSelectedPond: () => setSelectedPondId(null),
          clearSelectedBuilding: () => {
            setSelectedBuildingId(null);
            setSelectedSnowmakingNodeId(null);
            setOpenDock('snowmaking');
          },
          clearSelectedSnowmakingNode: () => setSelectedSnowmakingNodeId(null),
          clearSelectedSnowmakingPipe: () => setSelectedSnowmakingPipeId(null),
          clearSelectedSnowgun: () => setSelectedSnowgunId(null),
          clearSelectedNode: () => setSelectedNodeId(null),
          clearSelectedPath: () => setSelectedPathId(null), clearSelectedRoad: () => setSelectedRoadKey(null),
          clearSelectedLake: () => setSelectedLakeId(null),
          clearSelectedStream: () => setSelectedStreamId(null),
          setLakeName: (id, name) => setLakeNameOverrides((current) => {
            const next = { ...current };
            if (name == null) delete next[id]; else next[id] = name;
            return next;
          }),
          setLakeDepth: (id, depth) => setLakeDepthOverrides((current) => {
            const next = { ...current };
            if (depth == null) delete next[id]; else next[id] = depth;
            return next;
          }),
          setLakeSnowmaking: (id, enabled) => setSnowmakingLakeIds((current) => enabled
            ? current.includes(id) ? current : [...current, id]
            : current.filter((lakeId) => lakeId !== id)),
          setStreamWidth: (id, width) => setStreamWidthOverrides((current) => {
            const next = { ...current };
            if (width == null) delete next[id]; else next[id] = width;
            return next;
          }),
        } : null}
        nameEntry={awaitingName ? {
          value: nameDraft,
          saving,
          change: setNameDraft,
          redraw: exitSite,
          submit: () => { void createSave(); },
        } : null}
        stats={saved && showStats ? {
          name: saved.name,
          onRename: renameResort,
          lifts,
          trails,
          dams,
          ponds,
          snowmakingLakes: snowmakingLakes ?? [],
          center: resortCenter(),
          units: settings.units, averageAnnualSnowfallCm: simulation.averageAnnualSnowfallCm,
          onClose: () => setShowStats(false),
        } : null}
        closeCredits={showCredits ? () => setShowCredits(false) : null}
      />
    </>
  );
}
