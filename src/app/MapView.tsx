import { useEffect, useMemo, useRef, useState, type CSSProperties, type MutableRefObject } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { setLocalContextData, setSelectedLake, setSelectedStream, setupAnalysisLayers, type LayerToggle } from './analysisLayers';
import { applyCoverOpacity, setCoverData } from './coverVectorize';
import { LayerList } from './LayerPanel';
import { GameToolbar } from './GameToolbar';
import { GameMenu } from './GameMenu';
import { CreditsPanel } from './CreditsPanel';
import { LiftOverview } from './LiftOverview';
import { LiftDetail } from './LiftDetail';
import { LakeDetail } from './LakeDetail';
import { StreamDetail } from './StreamDetail';
import { analyzeStream, sanitizeStreamWidthOverrides } from '../streamAnalysis';
import { MountainDashboards, type DashboardKind } from './MountainDashboards';
import { TrailsPanel, type TrailsTool } from './TrailsPanel';
import { buildSkiNetwork } from '../network';
import {
  describeAnchor,
  pathLengthM,
  sanitizeNodes,
  sanitizePaths,
} from '../skiNodes';
import { ResortStatsPanel } from './ResortStatsPanel';
import { CursorReadout, type Readout } from './CursorReadout';
import { Legend, type OverlayId } from './Legend';
import { sampleTerrainAt, compass8 } from './terrainProtocols';
import { sampleCoverAt, sampleSiteCoverGrid, COVER_LABELS } from './worldcoverProtocol';
import { SiteControl, type SiteMode } from './SiteControl';
import {
  addSiteBoxLayers,
  setSiteBox,
  setBoundaryMode,
  computeBox,
  siteBoxFromBounds,
  type SiteBox,
} from './sitePicker';
import { SearchBox, type GeocodeResult } from './SearchBox';
import { tuneBasemap, basemapFor } from './basemapStyle';
import { View3DControl } from './View3DControl';
import { mountTerrain, unmountTerrain, tilt3D, PITCH_3D } from './terrain3d';
import { useSettings, pixelRatioFor } from './SettingsContext';
import { MapInteractionLease, type MapInteractionLeaseHandle,
  type MapInteractionOverrides } from './mapInteractionLease';
import { ToolCoordinator, TOOL_IDS, type DockId, type ToolCoordinatorSnapshot,
  type ToolId } from './toolCoordinator';
import { isTypingTarget, normalizeKey } from '../keybinds';
import { applyTileLod } from './terrainLod';
import { ResortLoadingScreen } from './ResortLoadingScreen';
import type { BootControls, BootEvent, BootProgress } from './resortBoot';
import { captureGamePreview, CURRENT_GAME_SAVE_SCHEMA_VERSION, saveGame } from '../gameSaveClient';
import { isDesktop } from '../desktopBridge';
import type { GameSave, RoadType, SavedDam, SavedJunction, SavedLift,
  SavedNode, SavedPath, SavedPond, SavedRoad, SavedSnowmakingNode, SavedTrail,
  TerrainPackageProgress, TerrainRecord, CoverGrid } from '../types';
import { analyzeLake, sanitizeLakeDepthOverrides, sanitizeLakeNameOverrides } from '../lakeAnalysis';
import { loadTerrain, saveTerrain, saveTerrainCover } from '../terrainStorageClient';
import { prepareResortPackage } from '../terrainIngest';
import {
  coverDisplayMetadataOf,
  manifestOf,
  manifestWithUpdatedCover,
  validateTerrainCoverEdit,
  validateTerrainPackage,
} from '../terrainPackage';
import { coverDisplayToGeoJSON, deriveCoverDisplayGeometry, type CoverDisplayGeoJSON } from '../coverDisplay';
import { liftClearingRing, type CoverClearing } from '../coverEdit';
import { CoverEditAdapter } from './coverEditClient';
import { DamAnalysisAdapter } from './damAnalysisClient';
import { TerrainGradeAdapter } from './terrainGradeClient';
import { TrailPaintAdapter } from './trailPaintClient';
import { clearResortCoverCache, getResortRenderStats, RESORT_COVER_PROTOCOL,
  resortCameraBounds, sampleLocalCoverAt, sampleLocalTerrainAt,
  setActiveResortTerrain, setRenderConcurrency, warmResortTiles, WORLD_COVER_LABELS } from './resortProtocols';
import { LiftControl } from './LiftControl';
import { useLiftController } from './useLiftController';
import { AnchorValue, TrailControl } from './TrailControl';
import { TrailDetail } from './TrailDetail';
import { InfrastructureControl } from './InfrastructureControl';
import { useRoadController } from './useRoadController';
import { SnowmakingControl } from './SnowmakingControl';
import { useSnowmakingController } from './useSnowmakingController';
import { useNodePathController } from './useNodePathController';
import { useTrailController } from './useTrailController';
import { sanitizeDams } from '../damAnalysis';
import { sanitizePonds } from '../pondAnalysis';
import { reconcileSnowmakingNodes, sanitizeSnowmakingNodes } from '../snowmakingNodes';
import {
  TERRAIN_CLEAN,
  designHasEdits,
  designOf,
  flushTerrainEdits,
  terrainHasEdits,
  withTerrainEdit,
  type DesignSnapshot,
  type TerrainDirty,
} from './unsavedChanges';
import { UnsavedChangesModal, type UnsavedChoice } from './UnsavedChangesModal';
import { refreshTerrainGradeSources, setGradedContourPreview,
  setTerrainContourData } from './terrainGradeMap';
import {
  fmtDistance,
  liftStats,
  orientBottomToTop,
  sanitizeLifts,
} from '../lifts';
import {
  sanitizeTrails,
  orientTopToBottom,
  fillElevationGaps,
  trailPartsStats,
  difficultyForSlopes,
} from '../trails';
import { hydrateTopology, summarizeJunctions } from '../topology';
import { sanitizeRoads } from '../roads';
import { resumeCameraOf, withResumeCheckpoint } from './resumeCheckpoint';
import { ConstructionStatusBug } from './ConstructionStatusBug';
import type { ConstructionActivity } from './constructionLock';
import { TerrainDocument, type TerrainDocumentPorts, type TerrainPublication,
  type TerrainRecordView, type TerrainSnapshot } from './terrainDocument';
import { TopologyDocument, topologyProjection, type TopologyState } from './topologyDocument';
import { MAP_HIT_RANK, MAP_Z_ORDER, MapContributionRegistry,
  type ManagedMapContribution, type MapVisibilityDescriptor } from './mapContribution';

// Crystal Mountain, WA — our canonical test site (used as the New Game start).
const INITIAL_CENTER: [number, number] = [-121.474, 46.928];
const INITIAL_ZOOM = 12;

export type MapMode = 'picking' | 'playing';

/**
 * Editing the graph nodes along a run. A node is a junction in the trail
 * topology — the thing the review panel numbers — not a free-standing pin, so
 * `add` only accepts a click that lands on a painted run and `remove` only
 * accepts a node the run passes straight through. Both pick first and commit on
 * a button, so a misclick costs nothing.
 */
type SelectionTarget =
  | { kind: 'lift' | 'trail' | 'dam' | 'pond' | 'snowmaking-node' | 'ski-node' | 'ski-path'; id: string }
  | { kind: 'lake' | 'stream'; id: string }
  | { kind: 'none' };

function layerTogglesOf(descriptors: readonly MapVisibilityDescriptor[]): LayerToggle[] {
  return descriptors.map((descriptor) => ({ ...descriptor, layerIds: [...descriptor.layerIds] }));
}

/** crypto.randomUUID is gated to secure contexts (fails under packaged file://). */
function genId(): string {
  try {
    return crypto.randomUUID();
  } catch {
    return 'save-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  }
}

/** The visible member of the mutually-exclusive overlay group, if any. */
function activeOverlayOf(layers: LayerToggle[]): OverlayId | null {
  const analysis = layers.find((l) => (l.exclusiveGroup === 'overlay' || l.exclusiveGroup === 'analysis') && l.visible);
  const on = analysis ?? layers.find((l) => l.id === 'groundcover' && l.visible);
  return (on?.id as OverlayId) ?? null;
}

/** Ordered display steps for the resort-preparation gate. Their index lines up
 *  with TerrainPackageProgress.completed (0-based): step i is done when
 *  completed > i, active when completed === i, pending otherwise. */
const PREP_STEPS: { key: string; label: string }[] = [
  { key: 'elevation', label: 'Elevation data' },
  { key: 'ground-cover', label: 'Recovery ground cover' },
  { key: 'imagery', label: 'NAIP imagery & map context' },
  { key: 'decoding', label: 'Four terrain classes' },
  { key: 'vectorizing-cover', label: 'Detailed vector cover' },
  { key: 'deriving', label: 'Local contours' },
  { key: 'saving', label: 'Saving package' },
  { key: 'verifying', label: 'Verifying' },
  { key: 'finalizing', label: 'Final validation' },
];

// Escape hatch for the Playwright verification harness: the 3D terrain mesh
// crashes SwiftShader headless, so `?flat` keeps the resort view terrain-free
// (hillshade still stands in). No effect in the real Electron app.
const TERRAIN_DISABLED =
  typeof window !== 'undefined' && new URLSearchParams(window.location.search).has('flat');

// Keyboard camera control tuning (WASD pan, QE rotate, RF tilt) — screen
// px/sec and degrees/sec, reasonable defaults for a 60fps continuous hold.
const PAN_SPEED_PX_S = 900;
const ROTATE_SPEED_DEG_S = 90;
const PITCH_SPEED_DEG_S = 60;

interface MapViewProps {
  mode: MapMode;
  /** Present when resuming a saved resort (Load / Continue). */
  initialSave?: GameSave | null;
  onQuit: () => void;
  onOpenSettings: () => void;
  /** Open the Load Game modal (owned by App). Menu → Load. */
  onLoadGame: () => void;
  /** Boot reports for the resort loading screen, which App owns (it has to
   *  exist before this component mounts and outlive its first full render). */
  onBoot?: (e: BootEvent) => void;
  /** Filled in here so the loading screen can force or abort the warm-up. */
  bootControlsRef?: MutableRefObject<BootControls | null>;
  /** Lets App checkpoint this mounted game before navigating or closing. */
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
  /** Resolve the unsaved-work gate before navigating away. `false` means the
   *  player cancelled and the session must stay open. */
  confirmExit(): Promise<boolean>;
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

/**
 * Owns the MapLibre instance. The map lives in a ref (never React state); React
 * state holds the layer-toggle UI model, cursor readout, and game/site status.
 */
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
  const [readout, setReadout] = useState<Readout | null>(null);
  const [siteMode, setSiteMode] = useState<SiteMode>(initialSave?.site ? 'locked' : 'explore');
  const [siteBox, setSiteBoxState] = useState<SiteBox | null>((initialSave?.site as SiteBox) ?? null);
  const [is3D, setIs3D] = useState(initialSave?.is3D ?? false);
  // Live camera pitch. The 2D/3D button reads the camera, not the last button
  // press — dragging the map into a tilt is a 3D view no matter what was
  // clicked last, and the button has to offer the way back out of it.
  const [pitchDeg, setPitchDeg] = useState(0);
  // The point a snapping tool would take if you clicked right now, drawn as an
  // amber ring. Every tool that must attach to a run shows it, so "it snaps"
  // is something you can see before committing rather than after.
  // Perfectly overhead means pitch 0. A rotated top-down view is still 2D, and
  // `tilt3D` deliberately leaves bearing alone, so pitch alone decides; the
  // epsilon absorbs ease residue, nothing more.
  const isOverhead = pitchDeg < 0.5;
  const warmAbortRef = useRef<AbortController | null>(null);
  // App's resort loading screen stays up until `ready`, so the map is never
  // shown mid-stream. Kept in a ref so the once-registered style.load handler
  // and the warm-up loop always report through the current callback.
  const onBootRef = useRef(onBoot);
  onBootRef.current = onBoot;
  const repairRef = useRef<() => void>(() => {});
  const bootControls = useRef<BootControls | null>(null);
  // A prepare→play handoff still re-mounts terrain and every custom tile source
  // after App's loading screen has stood down (New Game, or repairing a broken
  // package). We drive the same screen locally for that stretch rather than let
  // the resort draw itself in front of the player.
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
  // Unlike `saved`, this ref is never changed by live UI edits such as rename.
  // Exit checkpoints spread this exact record so manual-save semantics remain.
  const persistedSaveRef = useRef<GameSave | null>(initialSave);
  const [nameDraft, setNameDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const [checkpointError, setCheckpointError] = useState<string | null>(null);
  // The unsaved-work gate on exit. The ref holds the pending prompt's resolver.
  const [unsavedPrompt, setUnsavedPrompt] = useState(false);
  const unsavedChoiceRef = useRef<((choice: UnsavedChoice) => void) | null>(null);
  const [terrainRecord, setTerrainRecord] = useState<TerrainRecord | null>(null);
  const [packageState, setPackageState] = useState<'ready' | 'loading' | 'missing' | 'preparing' | 'optimizing' | 'error'>(
    mode === 'playing' ? 'loading' : 'ready'
  );
  const [packageProgress, setPackageProgress] = useState<TerrainPackageProgress | null>(null);
  const [packageError, setPackageError] = useState<string | null>(null);
  const packageStateRef = useRef(packageState);
  const [lifts, setLifts] = useState<SavedLift[]>(() =>
    sanitizeLifts(initialSave?.lifts ?? [])
  );
  const [initialTopology] = useState(() => hydrateTopology(
    sanitizeTrails(initialSave?.trails ?? []), sanitizePaths(initialSave?.paths ?? []),
    sanitizeLifts(initialSave?.lifts ?? []), initialSave?.junctions ?? [],
    sanitizeNodes(initialSave?.nodes ?? [])));
  const [selectedLiftId, setSelectedLiftId] = useState<string | null>(null);
  // A selected lift opens its read-only detail first; Edit flips this to the
  // LiftControl edit panel. Reset to false whenever a (different) lift is opened.
  const [liftEditing, setLiftEditing] = useState(false);
  const [trails, setTrails] = useState<SavedTrail[]>(initialTopology.trails);
  const [selectedTrailId, setSelectedTrailId] = useState<string | null>(null);
  const [trailEditing, setTrailEditing] = useState(false);
  const [roads, setRoads] = useState<SavedRoad[]>(() => sanitizeRoads(initialSave?.roads ?? []));
  const [dams, setDams] = useState<SavedDam[]>(() => sanitizeDams(initialSave?.dams ?? []));
  const [selectedDamId, setSelectedDamId] = useState<string | null>(null);
  const [ponds, setPonds] = useState<SavedPond[]>(() => sanitizePonds(initialSave?.ponds ?? []));
  const [selectedPondId, setSelectedPondId] = useState<string | null>(null);
  // Seeded already-reconciled against the sanitized dams/ponds above, so a
  // save that needed backfilling (or predates this field) doesn't present as
  // dirty the instant it's opened.
  const [snowmakingNodes, setSnowmakingNodes] = useState<SavedSnowmakingNode[]>(() =>
    reconcileSnowmakingNodes(sanitizeSnowmakingNodes(initialSave?.snowmakingNodes ?? []), dams, ponds));
  const [selectedSnowmakingNodeId, setSelectedSnowmakingNodeId] = useState<string | null>(null);
  // User-declared connectivity: placed nodes and drawn connector paths, both
  // owned by the floating Trails roll-up.
  const [skiNodes, setSkiNodes] = useState<SavedNode[]>(() => sanitizeNodes(initialSave?.nodes ?? []));
  const [skiPaths, setSkiPaths] = useState<SavedPath[]>(initialTopology.paths);
  const [junctions, setJunctions] = useState<SavedJunction[]>(initialTopology.junctions);
  // Synchronous committed projection for save/capture and dirty comparison.
  // React's four collection states may render later; this ref moves in the
  // topology publication callback, so persistence cannot observe a mixed graph.
  const committedTopologyRef = useRef<TopologyState>(
    { trails, nodes: skiNodes, paths: skiPaths, junctions }
  );
  // Runs, ski nodes, connector paths, and junctions describe one graph, so one
  // document owns every change to it. React holds the projection; a transaction
  // lands each collection it touched in a single publication, so nothing ever
  // observes a trail whose segments name a junction that has not arrived yet.
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
  const [streamWidthOverrides, setStreamWidthOverrides] = useState<Record<string, number>>(() =>
    sanitizeStreamWidthOverrides(initialSave?.streamWidthOverrides));
  const [lakeDepthOverrides, setLakeDepthOverrides] = useState<Record<string, number>>(() =>
    sanitizeLakeDepthOverrides(initialSave?.lakeDepthOverrides));
  const [lakeNameOverrides, setLakeNameOverrides] = useState<Record<string, string>>(() =>
    sanitizeLakeNameOverrides(initialSave?.lakeNameOverrides));
  // Terrain edits are held in memory and written on Save, like every other
  // design change. The ref mirror lets async build handlers accumulate flags.
  const [terrainDirty, setTerrainDirtyState] = useState<TerrainDirty>(TERRAIN_CLEAN);
  const terrainDirtyRef = useRef(terrainDirty);
  const setTerrainDirty = (next: TerrainDirty) => {
    terrainDirtyRef.current = next;
    setTerrainDirtyState(next);
  };
  const markTerrainEdited = (kind: 'elevation' | 'cover') =>
    setTerrainDirty(withTerrainEdit(terrainDirtyRef.current, kind));
  // The design as last written to disk. Seeded from the *sanitized* state above
  // rather than from initialSave, whose arrays those sanitizers replaced — the
  // comparison is by reference, so seeding it from initialSave would report a
  // freshly-loaded resort dirty.
  const [savedDesign, setSavedDesign] = useState<DesignSnapshot>(() => ({
    name: initialSave?.name ?? '',
    site: initialSave?.site ?? null,
    lifts, trails, roads, dams, ponds,
    nodes: skiNodes, paths: skiPaths, junctions, snowmakingNodes,
    lakeDepthOverrides, lakeNameOverrides, streamWidthOverrides,
  }));
  // Identifies the active construction operation for disabled controls, button
  // spinners, and the persistent map-level status bug.
  const [buildingActivity, setBuildingActivity] = useState<ConstructionActivity | null>(null);
  const building = buildingActivity !== null;
  // Node map: a simplified 2D topology view, toggled from the top-left.
  const [showNetwork, setShowNetwork] = useState(false);
  const [networkLiftId, setNetworkLiftId] = useState<string | null>(null);
  const [networkEdgeId, setNetworkEdgeId] = useState<string | null>(null);
  // Which dashboard the "Mountain Dashboards" overlay shows — independent of
  // showNetwork, which only opens/closes the overlay itself.
  const [dashboard, setDashboard] = useState<DashboardKind>('trails');

  // The trail/lift graph is derived, never persisted — the same rule that has
  // sanitizeTrails recompute cached stats on load, so it can never drift from
  // the geometry. Keyed on the two state arrays, so it rebuilds when a run or
  // lift changes and never while the camera moves.
  const network = useMemo(
    () => buildSkiNetwork(trails, lifts, { nodes: skiNodes, paths: skiPaths, junctions }),
    [trails, lifts, skiNodes, skiPaths, junctions]
  );

  // Exposed for the Playwright verification harness, alongside window.appMap.
  useEffect(() => {
    (window as unknown as { appNetwork?: typeof network }).appNetwork = network;
  }, [network]);

  // Also for the harness: the play box. Local context is drawn over the wider
  // perimeter ring, so a check that only reads map sources cannot tell which
  // features the build tools will actually accept.
  useEffect(() => {
    (window as unknown as { appTerrainBounds?: TerrainRecord['bounds'] })
      .appTerrainBounds = terrainRecord?.bounds;
  }, [terrainRecord]);

  // Also for the harness: what this session would write, versus what is on
  // disk. Terrain edits are only in memory until Save, so a check that reads
  // storage alone cannot tell whether a discard actually discarded anything.
  useEffect(() => {
    (window as unknown as { appSaveState?: unknown }).appSaveState = {
      terrainKey: terrainRecord?.key ?? null,
      elevationChecksum: terrainRecord?.packageManifest?.elevationChecksum ?? null,
      coverChecksum: terrainRecord?.coverMetadata?.checksum ?? null,
      terrainDirty: { ...terrainDirtyRef.current },
      unsaved: hasUnsavedChanges(),
    };
  });

  // Keyboard camera controls: WASD pan, QE rotate, RF tilt, N snap-north, U
  // toggle 2D/3D, 1/2 open the Mountain Dashboards. Registered once (empty
  // deps beyond mount) and read live values through refs (4c below) — the
  // house idiom for a global window keydown listener that needs current
  // state without re-subscribing on every change. Available in both
  // 'picking' and 'playing' mode: the worldwide picker map gets WASD/QE/RF/N/U
  // navigation too, 1/2 and toggleView3D simply no-op before a resort exists.
  const heldRef = useRef<Set<string>>(new Set());
  const rafIdRef = useRef<number | null>(null);
  const lastFrameRef = useRef<number | null>(null);

  useEffect(() => {
    const heldKeys = heldRef.current;

    function stepFrame(ts: number) {
      rafIdRef.current = null;
      if (heldKeys.size === 0) {
        lastFrameRef.current = null;
        return;
      }
      const last = lastFrameRef.current ?? ts;
      const dt = Math.min(0.1, Math.max(0, (ts - last) / 1000));
      lastFrameRef.current = ts;
      const map = mapRef.current;
      if (map) {
        const kb = keybindsRef.current;
        let dx = 0;
        let dy = 0;
        if (heldKeys.has(kb.panForward)) dy -= 1;
        if (heldKeys.has(kb.panBackward)) dy += 1;
        if (heldKeys.has(kb.panLeft)) dx -= 1;
        if (heldKeys.has(kb.panRight)) dx += 1;
        if (dx !== 0 || dy !== 0) {
          const len = Math.hypot(dx, dy);
          map.panBy([(dx / len) * PAN_SPEED_PX_S * dt, (dy / len) * PAN_SPEED_PX_S * dt], { animate: false });
        }
        let bearingDelta = 0;
        if (heldKeys.has(kb.rotateLeft)) bearingDelta -= ROTATE_SPEED_DEG_S * dt;
        if (heldKeys.has(kb.rotateRight)) bearingDelta += ROTATE_SPEED_DEG_S * dt;
        if (bearingDelta !== 0) map.setBearing(map.getBearing() + bearingDelta);
        let pitchDelta = 0;
        if (heldKeys.has(kb.tiltUp)) pitchDelta += PITCH_SPEED_DEG_S * dt;
        if (heldKeys.has(kb.tiltDown)) pitchDelta -= PITCH_SPEED_DEG_S * dt;
        if (pitchDelta !== 0) {
          const nextPitch = Math.min(map.getMaxPitch(), Math.max(0, map.getPitch() + pitchDelta));
          if (nextPitch !== map.getPitch()) map.setPitch(nextPitch);
        }
      }
      if (heldKeys.size > 0) rafIdRef.current = requestAnimationFrame(stepFrame);
    }

    function startLoopIfNeeded() {
      if (rafIdRef.current === null) {
        lastFrameRef.current = null;
        rafIdRef.current = requestAnimationFrame(stepFrame);
      }
    }

    const continuousKeys = () => {
      const kb = keybindsRef.current;
      return [
        kb.panForward, kb.panBackward, kb.panLeft, kb.panRight,
        kb.rotateLeft, kb.rotateRight, kb.tiltUp, kb.tiltDown,
      ];
    };

    function onKeyDown(e: KeyboardEvent) {
      if (controlsSuspendedRef.current) return;
      if (showNetworkRef.current) return; // dashboard open — the map is hidden behind it
      if (isTypingTarget(document.activeElement)) return;
      const key = normalizeKey(e.key);
      if (!continuousKeys().includes(key)) return;
      const wasEmpty = heldKeys.size === 0;
      heldKeys.add(key);
      if (wasEmpty) startLoopIfNeeded();
    }

    function onKeyUp(e: KeyboardEvent) {
      // No guards — releasing a key must always work, even if focus moved to
      // an input mid-hold, or the key could get stuck "held".
      const key = normalizeKey(e.key);
      heldKeys.delete(key);
    }

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      if (rafIdRef.current !== null) cancelAnimationFrame(rafIdRef.current);
      heldKeys.clear();
    };
  }, []);

  // Discrete single-press keyboard actions: N (snap north), U (toggle 2D/3D),
  // 1/2 (open/switch Mountain Dashboards). Only N and U bail while a
  // dashboard is open — panning/tilting/rotating the hidden map behind a
  // dashboard is meaningless, but opening/switching dashboards (1/2) is
  // exactly what those keys are for even while one is already open.
  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (controlsSuspendedRef.current) return;
      if (isTypingTarget(document.activeElement)) return;
      const key = normalizeKey(e.key);
      const kb = keybindsRef.current;
      if (key === kb.snapNorth) {
        if (showNetworkRef.current) return;
        mapRef.current?.easeTo({ bearing: 0, duration: 300 });
      } else if (key === kb.toggleView3D) {
        if (showNetworkRef.current) return;
        // Calls through a ref (not `toggle3D` directly): this effect has an
        // empty dep array and only runs once, so a direct call would forever
        // close over the mount-time `toggle3D` — and the mount-time
        // `isOverhead` it reads is frozen at its initial value (pitchDeg
        // starts at 0, so `isOverhead` starts `true`), making every U press
        // force 3D regardless of the camera's actual current pitch.
        toggle3DRef.current();
      } else if (key === kb.openTrailsDashboard) {
        if (showNetworkRef.current && dashboardRef.current === 'trails') {
          setShowNetwork(false);
        } else {
          setDashboard('trails');
          setShowNetwork(true);
        }
      } else if (key === kb.openSnowmakingDashboard) {
        if (showNetworkRef.current && dashboardRef.current === 'snowmaking') {
          setShowNetwork(false);
        } else {
          setDashboard('snowmaking');
          setShowNetwork(true);
        }
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const activeOverlay = activeOverlayOf(layers);

  // Refs so once-registered handlers + the style-swap re-init read current values.
  const activeOverlayRef = useRef<OverlayId | null>(null);
  const lastLngLatRef = useRef<{ lng: number; lat: number } | null>(null);
  const sampleTokenRef = useRef(0);
  const rafPendingRef = useRef(false);
  const doSampleRef = useRef<(lngLat: { lng: number; lat: number }) => void>(() => {});
  // Mirrors `toggle3D` (defined below, but hoisted as a function declaration)
  // so the once-registered discrete-keydown effect always calls the current
  // render's closure — see the ref-refresh block below where this is kept
  // fresh every render, exactly like `is3DRef`.
  const toggle3DRef = useRef<() => void>(() => {});
  const layersRef = useRef<LayerToggle[]>([]);
  const analysisTogglesRef = useRef<LayerToggle[]>([]);
  const mapContributionRegistryRef = useRef<MapContributionRegistry | null>(null);
  const siteBoxRef = useRef<SiteBox | null>(siteBox);
  const siteModeRef = useRef<SiteMode>(siteMode);
  const is3DRef = useRef(is3D);
  // Live values for the keyboard camera-control listeners above, which are
  // registered once on mount and so cannot close over fresh props/state.
  const controlsSuspendedRef = useRef(controlsSuspended);
  const keybindsRef = useRef(settings.keybinds);
  const showNetworkRef = useRef(showNetwork);
  const dashboardRef = useRef(dashboard);
  useEffect(() => { controlsSuspendedRef.current = controlsSuspended; }, [controlsSuspended]);
  useEffect(() => { keybindsRef.current = settings.keybinds; }, [settings.keybinds]);
  useEffect(() => { showNetworkRef.current = showNetwork; }, [showNetwork]);
  useEffect(() => { dashboardRef.current = dashboard; }, [dashboard]);
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
  const streamWidthOverridesRef = useRef(streamWidthOverrides);
  const roadsRef = useRef<SavedRoad[]>(roads);
  const damsRef = useRef<SavedDam[]>(dams);
  const pondsRef = useRef<SavedPond[]>(ponds);
  const snowmakingNodesRef = useRef<SavedSnowmakingNode[]>(snowmakingNodes);
  const renderQualityRef = useRef(settings.renderQuality);
  const unitsRef = useRef(settings.units);
  const packageAbortRef = useRef<AbortController | null>(null);
  const toolCancellationRef = useRef<Record<ToolId, () => void>>({
    lift: () => {}, road: () => {}, dam: () => {}, pond: () => {},
    'ski-node': () => {}, 'ski-path': () => {}, trail: () => {},
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
    'ski-node': cancelNodeTool,
    'ski-path': cancelPathTool,
    trail: cancelTrailTool,
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
  // Loaded local package backing cursor sampling, MapLibre protocols, and
  // style reinitialization. Gameplay never populates it from network data.
  // Written only by the terrain document's publication, so a handler reading it
  // between a build and the next render sees the record that was committed.
  const terrainRecordRef = useRef<TerrainRecord | null>(null);
  const terrainHeightCacheRef = useRef<{ checksum: string; heights: Float32Array } | null>(null);
  const coverDisplayRef = useRef<CoverDisplayGeoJSON | null>(null);
  const localImageryUrlRef = useRef<string | null>(null);
  const localImageryCacheKeyRef = useRef<string | null>(null);

  // The terrain document is the authority for the committed package: revisions,
  // construction ownership, cover-edit serialization, and grade-preview
  // ownership. It is constructed once, but its ports are re-bound every render
  // because the contour and source refreshes read `settings.units` — a closure
  // captured at construction would pin those to the first render.
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

  // Every worker this session owns. Each adapter answers the same three
  // questions for its protocol — which response is still the one being waited
  // on, what supersedes it, and what stops it — so no feature has to reinvent
  // them, and teardown is one call per adapter instead of a list of terminates
  // that a new worker can quietly fall off.
  const damAnalysisRef = useRef<DamAnalysisAdapter | null>(null);
  if (!damAnalysisRef.current) damAnalysisRef.current = new DamAnalysisAdapter();
  const damAnalysis = damAnalysisRef.current;
  const coverEditRef = useRef<CoverEditAdapter | null>(null);
  if (!coverEditRef.current) coverEditRef.current = new CoverEditAdapter();
  const coverEdit = coverEditRef.current;
  // One grade preview exists on the map, so the road and trail tools share one
  // adapter rather than racing two workers into the same contour overlay.
  const terrainGradeRef = useRef<TerrainGradeAdapter | null>(null);
  if (!terrainGradeRef.current) terrainGradeRef.current = new TerrainGradeAdapter();
  const terrainGrade = terrainGradeRef.current;
  const trailPaintRef = useRef<TrailPaintAdapter | null>(null);
  if (!trailPaintRef.current) trailPaintRef.current = new TrailPaintAdapter();
  const trailPaint = trailPaintRef.current;

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
    select: (id) => transitionSelection({ kind: 'lift', id }),
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
    clearCover: applyLiftCoverClear,
    createId: genId,
    now: () => new Date().toISOString(),
    structuresVisible: () => packageStateRef.current !== 'preparing',
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('lift'),
  });
  const liftTool = liftController.state;

  const roadController = useRoadController({
    mapRef,
    roads,
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
    clearCover,
    createId: genId,
    now: () => new Date().toISOString(),
    roadsVisible: () => analysisTogglesRef.current.some((entry) => entry.id === 'bm-roads'),
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('road'),
  });
  const roadTool = roadController.state;

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
      clearCover: (polygons) => clearCover(polygons.map((polygon) => ({ polygon }))),
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
      clearCover: (polygons) => clearCover(polygons.map((polygon) => ({ polygon }))),
      createId: genId, now: () => new Date().toISOString(),
      structuresVisible: () => packageStateRef.current !== 'preparing',
      synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('pond'),
    },
    nodes: {
      dams, ponds, nodes: snowmakingNodes, selectedId: selectedSnowmakingNodeId,
      reconcileSources: (nextDams, nextPonds) => setSnowmakingNodes((existing) =>
        reconcileSnowmakingNodes(existing, [...nextDams], [...nextPonds])),
      rename: (id, name) => setSnowmakingNodes((existing) =>
        existing.map((node) => node.id === id ? { ...node, name } : node)),
      select: (id) => transitionSelection({ kind: 'snowmaking-node', id }),
      structuresVisible: () => packageStateRef.current !== 'preparing',
      synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('snowmaking'),
    },
  });
  const damTool = snowmakingController.dam.state;
  const pondTool = snowmakingController.pond.state;

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
  const { nodeTool, pathTool } = nodePathController;

  const trailController = useTrailController({
    mapRef, lifts, trails, paths: skiPaths, topology, terrain,
    gradeAdapter: terrainGrade, paintAdapter: trailPaint,
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
    sampleProfile, gradeChanged: applyGradePreview, clearCover,
    select: (id) => transitionSelection({ kind: 'trail', id }),
    clearSelected: (id) => setSelectedTrailId((selected) => selected === id ? null : selected),
    closeEditing: () => setTrailEditing(false),
    reportBlockedDelete: (message) => window.alert(message),
    createId: genId, now: () => new Date().toISOString(),
    structuresVisible: () => packageStateRef.current !== 'preparing',
    synchronizeMap: () => mapContributionRegistryRef.current?.synchronizeData('trail'),
  });
  const trailTool = trailController.state;
  const brushWidthM = trailController.brushWidthM;

  /** The one place a committed terrain record reaches React and the dirty flag. */
  function publishTerrainState({ record, edit }: TerrainPublication): void {
    terrainRecordRef.current = record;
    if (edit) markTerrainEdited(edit);
    else setTerrainDirty(TERRAIN_CLEAN);
    setTerrainRecord(record);
  }

  /**
   * Refresh only the map sources a change actually invalidated. A load or
   * package replacement refreshes nothing: a restyle follows it, and that
   * re-mounts terrain and every custom tile source anyway.
   */
  function refreshTerrainSources({ record, edit }: TerrainPublication): void {
    if (edit === 'elevation') {
      refreshElevationSources(record);
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

  function cacheTerrainDisplayAssets(record: TerrainRecord): void {
    const heightChecksum = record.packageManifest?.elevationChecksum ?? record.updatedAt;
    if (terrainHeightCacheRef.current?.checksum !== heightChecksum) {
      terrainHeightCacheRef.current = {
        checksum: heightChecksum,
        heights: Float32Array.from(record.sampleHeights),
      };
    }
    coverDisplayRef.current = record.coverDisplayGeometry && record.bounds
      ? coverDisplayToGeoJSON(record.coverDisplayGeometry, record.bounds)
      : null;
    const imageryCacheKey = record.localImagery
      ? `${record.localImageryMetadata?.checksum ?? record.localImagery.length}:${record.localImageryMetadata?.mimeType ?? 'image/jpeg'}`
      : null;
    if (localImageryCacheKeyRef.current !== imageryCacheKey) {
      if (localImageryUrlRef.current) URL.revokeObjectURL(localImageryUrlRef.current);
      localImageryUrlRef.current = record.localImagery
        ? URL.createObjectURL(new Blob([Uint8Array.from(record.localImagery)], { type: record.localImageryMetadata?.mimeType ?? 'image/jpeg' }))
        : null;
      localImageryCacheKeyRef.current = imageryCacheKey;
    }
  }

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

  // State mirrored into refs so map event handlers, which are registered once,
  // can read live values. These are assigned during *render*, so they are only
  // as fresh as the last commit: an async continuation started in the same tick
  // as a setState will still see the pre-update value, because React schedules
  // that render on a macrotask and any microtask chain drains first. Such a
  // continuation must take what it needs as arguments (see
  // `sampleTrailElevations`) or write through a functional updater — never read
  // it back out of a ref.
  renderQualityRef.current = settings.renderQuality;
  unitsRef.current = settings.units;
  layersRef.current = layers;
  siteBoxRef.current = siteBox;
  siteModeRef.current = siteMode;
  is3DRef.current = is3D;
  toggle3DRef.current = toggle3D;
  liftsRef.current = lifts;
  trailsRef.current = trails;
  roadsRef.current = roads;
  damsRef.current = dams;
  pondsRef.current = ponds;
  snowmakingNodesRef.current = snowmakingNodes;
  skiNodesRef.current = skiNodes;
  skiPathsRef.current = skiPaths;
  junctionsRef.current = junctions;
  selectedLakeIdRef.current = selectedLakeId;
  selectedStreamIdRef.current = selectedStreamId;
  lakeDepthOverridesRef.current = lakeDepthOverrides;
  lakeNameOverridesRef.current = lakeNameOverrides;
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
    packageAbortRef.current?.abort();
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
    if (localImageryUrlRef.current) URL.revokeObjectURL(localImageryUrlRef.current);
    localImageryCacheKeyRef.current = null;
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
          const display = deriveCoverDisplayGeometry(record.coverGrid);
          let upgraded: TerrainRecord = {
            ...record,
            schemaVersion: 5,
            coverDisplayGeometry: display.geometry,
            coverDisplayMetadata: coverDisplayMetadataOf(display.geometry, display.stats),
            updatedAt: new Date().toISOString(),
          };
          upgraded = { ...upgraded, packageManifest: manifestOf(upgraded) };
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
    setSelectedSnowmakingNodeId(null);
    setSelectedNodeId(null);
    setSelectedPathId(null);
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
      case 'snowmaking-node': setSelectedSnowmakingNodeId(target.id); setOpenDock('snowmaking'); break;
      case 'ski-node': setSelectedNodeId(target.id); break;
      case 'ski-path': setSelectedPathId(target.id); break;
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

  // The actual sampler — redefined each render so it closes over fresh state.
  doSampleRef.current = (lngLat) => {
    const map = mapRef.current;
    if (!map) return;
    const z = Math.min(14, Math.max(10, Math.round(map.getZoom())));
    const overlay = activeOverlayRef.current;
    const token = ++sampleTokenRef.current;
    (async () => {
      const localRecord = terrainRecordRef.current;
      const t = localRecord
        ? sampleLocalTerrainAt(lngLat.lng, lngLat.lat)
        : await sampleTerrainAt(lngLat.lng, lngLat.lat, z).catch(() => null);
      if (!t || token !== sampleTokenRef.current) return;
      let coverLabel: string | null = null;
      if (localRecord) {
        const code = sampleLocalCoverAt(lngLat.lng, lngLat.lat);
        coverLabel = code == null ? '—' : WORLD_COVER_LABELS[code] ?? 'Unknown';
      } else if (overlay === 'groundcover') {
        const bucket = await sampleCoverAt(lngLat.lng, lngLat.lat, z).catch(() => null);
        if (token !== sampleTokenRef.current) return;
        coverLabel = bucket ? COVER_LABELS[bucket] : '—';
      }
      setReadout({
        elevationM: t.elevation,
        overlay,
        slopeDeg: t.slopeDeg,
        aspectCompass: compass8(t.aspectDeg),
        coverLabel,
      });
    })();
  };

  useEffect(() => {
    activeOverlayRef.current = activeOverlay;
    if (lastLngLatRef.current) doSampleRef.current(lastLngLatRef.current);
  }, [activeOverlay]);

  function samplePlanningTerrain(lng: number, lat: number, zoom: number) {
    if (!terrainRecordRef.current) return sampleTerrainAt(lng, lat, zoom);
    const sample = sampleLocalTerrainAt(lng, lat);
    return sample ? Promise.resolve(sample) : Promise.reject(new Error('Point is outside the local resort package.'));
  }

  /**
   * `samplePlanningTerrain` that reports an absent point as null instead of
   * rejecting. Profile sampling fans out over hundreds of points through
   * `Promise.all`, where a single rejection discards every sibling result — so
   * callers sampling a *line* use this and repair the gaps, and only callers
   * needing one specific point treat absence as failure.
   */
  function samplePlanningTerrainOrNull(lng: number, lat: number, zoom: number) {
    return samplePlanningTerrain(lng, lat, zoom).then(
      (s) => s,
      () => null
    );
  }

  /** Sample a whole centerline, repairing isolated gaps. Null if none resolved. */
  async function sampleProfile(line: [number, number][], zoom: number): Promise<number[] | null> {
    const samples = await Promise.all(line.map(([lng, lat]) => samplePlanningTerrainOrNull(lng, lat, zoom)));
    return fillElevationGaps(samples.map((s) => (s ? s.elevation : null)));
  }

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
        streamWidthOverridesRef.current);
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
        install: ({ map }) => { analysisTogglesRef.current = installAnalysisLayers(map); },
        synchronizeData: ({ map }) => {
          const record = terrainRecordRef.current;
          if (record) setLocalContextData(map, record, lakeNameOverridesRef.current,
            streamWidthOverridesRef.current);
          setSelectedLake(map, selectedLakeIdRef.current);
          setSelectedStream(map, selectedStreamIdRef.current);
        },
        visibility: () => analysisTogglesRef.current,
        visibilityChanged: ({ map }, id, visible) => {
          if (id === 'satellite') applyCoverOpacity(map, visible);
        },
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
      snowmakingController.nodes.contribution,
    ];
  }

  if (!mapContributionRegistryRef.current) {
    mapContributionRegistryRef.current = new MapContributionRegistry(createMapContributions());
  }
  const mapContributions = mapContributionRegistryRef.current;

  // (Re)attach analysis layers + site box + 3D after any style (re)load. Shared
  // by the initial load and the light<->dark basemap swap. Reads live state from
  // refs and re-applies the current layer-visibility model.
  function reinitAfterStyle(map: maplibregl.Map) {
    tuneBasemap(map);
    const applied = layerTogglesOf(mapContributions.synchronizeStyle());
    // Installed before the camera is posed below: the LOD falloff curve only
    // engages when pitched, and it decides which zooms the tilted view asks for.
    applyTileLod(map, renderQualityRef.current);
    // Resort view = a local terrain package is active. Terrain is mounted here
    // (and re-mounted after every restyle, since setStyle drops it) so it is
    // always present and the 2D↔3D switch stays a pure camera move. The
    // worldwide picker has no package, so it stays flat.
    if (terrainRecordRef.current && !TERRAIN_DISABLED) {
      mountTerrain(map);
      if (!resortReadyRef.current) {
        resortReadyRef.current = true;
        // First entry into the resort: honor a resumed 2D/3D choice, otherwise
        // default into the 3D-native view.
        const want3D = initialSave?.is3D ?? true;
        if (want3D !== is3DRef.current) setIs3D(want3D);
        // Pose the camera at its FINAL pitch *before* warming. This used to
        // reveal flat and then easeTo(PITCH_3D), which meant readiness was
        // measured in a camera pose the player never sees: tilting to 60°
        // enlarges the viewport footprint enormously and, once applyTileLod's
        // falloff engages, changes which zooms are requested — so every tile the
        // 3D view actually needed streamed in *after* the veil had lifted.
        if (initialSave) {
          const pose = resumeCameraOf(initialSave, {
            center: INITIAL_CENTER, zoom: INITIAL_ZOOM, bearing: 0, pitch: 0,
          });
          // Reapply the complete saved pose after terrain/maxBounds mount. In
          // particular, do not replace a player's non-default 3D pitch.
          map.jumpTo(pose);
        } else {
          map.jumpTo({ pitch: want3D ? PITCH_3D : 0 });
        }
        // Hold the loading screen until the resort is genuinely fully drawn:
        // (1) preload every reachable diorama tile into the cache (determinate
        // progress), then (2) wait for MapLibre to have all tiles loaded and go
        // idle — so the map is revealed already-complete, never mid-stream.
        // There is deliberately no safety timeout: rather than dump the player
        // into a half-drawn resort, the loading screen offers "Enter anyway"
        // once a load overruns.
        const rec = terrainRecordRef.current;
        let revealed = false;
        const reveal = () => {
          if (revealed) return;
          revealed = true;
          setRenderConcurrency(1); // restore calm serial rendering for play
          bootControls.current = null;
          if (bootControlsRef) bootControlsRef.current = null;
          reportBoot({ type: 'ready' });
        };
        const controller = new AbortController();
        warmAbortRef.current = controller;
        bootControls.current = { reveal, abort: () => controller.abort() };
        if (bootControlsRef) bootControlsRef.current = bootControls.current;
        void (async () => {
          reportStage({ stage: 'warm' });
          if (rec) {
            // Coalesce to ~8 reports/sec: warmResortTiles fires per tile, and
            // thousands of React renders would compete with the rasterizer for
            // the same main thread the bar is reporting on.
            let lastReport = 0;
            await warmResortTiles(
              rec,
              (completed, total) => {
                const now = performance.now();
                if (completed < total && now - lastReport < 120) return;
                lastReport = now;
                reportStage({ stage: 'warm', completed, total });
              },
              controller.signal
            );
          }
          if (controller.signal.aborted) return;
          // Catch any stragglers MapLibre requested outside the warm set: wait
          // for a fully-loaded, idle map to hold across two consecutive frames.
          reportStage({ stage: 'settle' });
          let stable = 0;
          const settle = () => {
            if (revealed || controller.signal.aborted) return;
            const ready = map.areTilesLoaded() && getResortRenderStats().pending === 0 && map.loaded();
            stable = ready ? stable + 1 : 0;
            if (stable >= 2) reveal();
            else requestAnimationFrame(settle);
          };
          requestAnimationFrame(settle);
        })();
      }
    } else {
      unmountTerrain(map);
      // Nothing to warm — the ?flat harness, or the picker before preparation.
      // Release any loading screen immediately rather than gate on a warm-up
      // that will never run. resortReadyRef stays false in the picker so the
      // real reveal still arms once preparation produces a package.
      if (mode === 'playing' && !resortReadyRef.current) {
        resortReadyRef.current = true;
        reportBoot({ type: 'ready' });
      } else {
        showLocalBoot(null);
      }
    }
    setLayers(applied);
  }

  // Create the map once.
  const mapCanStart = mode !== 'playing' || packageState === 'ready';
  useEffect(() => {
    if (!mapCanStart || mapRef.current || !containerRef.current) return;

    const start = resumeCameraOf(initialSave, {
      center: INITIAL_CENTER, zoom: INITIAL_ZOOM, bearing: 0, pitch: 0,
    });
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: basemapFor(resolvedTheme, { offline: mode === 'playing' }),
      center: start.center,
      zoom: start.zoom,
      bearing: start.bearing,
      pitch: start.pitch,
      pixelRatio: pixelRatioFor(settings.renderQuality),
      // Manual compact control (added below) instead of the default text blob, so
      // attribution sits clear of the bottom dock and stays license-compliant.
      attributionControl: false,
    });
    mapRef.current = map;
    mapContributions.attach(map, () => toolCoordinator.snapshot.activeTool === null);
    // Exposed for the Playwright verification harness (readyGlobal: "appMap").
    (window as unknown as { appMap: maplibregl.Map }).appMap = map;

    map.dragRotate.enable();
    map.keyboard.enable();
    // Compact ⓘ, bottom-right — just left of the zoom/compass map controls (the
    // dock now occupies the bottom-left). Aggregates the map-source attributions;
    // customAttribution adds the fetch-time services that aren't persistent
    // sources (USGS 3DEP elevation, Nominatim geocoding).
    map.addControl(
      new maplibregl.AttributionControl({
        compact: true,
        customAttribution: [
          'Elevation: USGS 3DEP',
          'Geocoding © OpenStreetMap contributors (Nominatim)',
        ],
      }),
      'bottom-right'
    );
    map.addControl(
      new maplibregl.NavigationControl({ visualizePitch: true, showZoom: true, showCompass: true }),
      'bottom-right'
    );

    map.on('style.load', () => {
      reinitAfterStyle(map);
      // Diorama camera: bound panning to the play box grown by ~1 km — enough to
      // orbit every side of the box, but not out to the coarse 3 km ring edge
      // (which looks janky) or into blank paper. Relief still *renders* past the
      // camera limit out to the DEM/surround extent. Ring-less packages fall
      // back to the box extent.
      if (siteModeRef.current === 'locked') {
        const rec = terrainRecordRef.current;
        const cam = rec ? resortCameraBounds(rec) : undefined;
        if (cam) map.setMaxBounds(cam);
        else if (siteBoxRef.current) map.setMaxBounds(siteBoxRef.current.bounds);
      }
    });

    const onMove = (e: maplibregl.MapMouseEvent) => {
      lastLngLatRef.current = { lng: e.lngLat.lng, lat: e.lngLat.lat };
      if (rafPendingRef.current) return;
      rafPendingRef.current = true;
      requestAnimationFrame(() => {
        rafPendingRef.current = false;
        if (lastLngLatRef.current) doSampleRef.current(lastLngLatRef.current);
      });
    };
    map.on('mousemove', onMove);
    map.on('mouseout', () => {
      lastLngLatRef.current = null;
      setReadout(null);
    });

    // Keep the 2D/3D button honest about the camera, however it got tilted —
    // the button press, a drag, or the boot-time ease into the 3D-native view.
    const onPitch = () => setPitchDeg(map.getPitch());
    map.on('pitch', onPitch);
    map.on('pitchend', onPitch);

    return () => {
      warmAbortRef.current?.abort();
      setRenderConcurrency(1);
      mapInteractionLeaseRef.current?.dispose();
      mapContributions.dispose();
      delete (window as unknown as { appSetCaptureTransients?: (hidden: boolean) => void })
        .appSetCaptureTransients;
      map.remove();
      mapRef.current = null;
      setLayers([]);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapCanStart]);

  useEffect(() => {
    mapContributions.synchronizeData('analysis');
  }, [selectedLakeId, mapContributions]);

  useEffect(() => {
    mapContributions.synchronizeData('analysis');
  }, [selectedStreamId, mapContributions]);

  // A single scale bar whose unit follows the Units setting.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const ctrl = new maplibregl.ScaleControl({
      unit: settings.units === 'metric' ? 'metric' : 'imperial',
    });
    map.addControl(ctrl, 'bottom-right');
    return () => {
      // On unmount the map may already be torn down (its own effect nulls the
      // ref). Only remove the control while the map is still alive, else
      // removeControl throws on the dead instance and crashes the tree.
      if (mapRef.current) mapRef.current.removeControl(ctrl);
    };
  }, [settings.units]);

  // Live render-quality change: re-supersample the canvas in place. Skips the
  // first run — the constructor already set pixelRatio from the persisted tier.
  const firstQualityRun = useRef(true);
  useEffect(() => {
    if (firstQualityRun.current) {
      firstQualityRun.current = false;
      return;
    }
    const map = mapRef.current;
    if (!map) return;
    map.setPixelRatio(pixelRatioFor(settings.renderQuality));
    applyTileLod(map, settings.renderQuality);
  }, [settings.renderQuality]);

  // Live light<->dark basemap swap. Skips the first run (initial style is correct).
  const firstThemeRun = useRef(true);
  useEffect(() => {
    if (firstThemeRun.current) {
      firstThemeRun.current = false;
      return;
    }
    const map = mapRef.current;
    if (!map) return;
    map.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resolvedTheme]);

  // Contour values and labels are generated in the selected display unit.
  // Rebuild the owned style when units change so the local source is replaced
  // atomically and no network elevation/contour source is introduced.
  const firstUnitsStyleRun = useRef(true);
  useEffect(() => {
    if (firstUnitsStyleRun.current) {
      firstUnitsStyleRun.current = false;
      return;
    }
    const map = mapRef.current;
    if (map) map.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settings.units]);

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

  // Backfill elevations for lifts that were confirmed offline (null endpoint
  // elevations in the save). Idempotent; results keyed by lift id.
  useEffect(() => {
    const missing = liftsRef.current.filter((l) => l.endpointElevM.some((e) => e == null));
    if (missing.length === 0) return;
    let stale = false;
    void Promise.allSettled(
      missing.map(async (l) => {
        const samples = await Promise.all(l.points.map(([lng, lat]) => samplePlanningTerrainOrNull(lng, lat, 13)));
        const [a, b] = samples;
        if (!a || !b) throw new Error('No terrain data at this lift.');
        return { id: l.id, elevs: [a.elevation, b.elevation] as [number, number] };
      })
    ).then((results) => {
      if (stale) return;
      const byId = new Map<string, [number, number]>();
      for (const r of results) if (r.status === 'fulfilled') byId.set(r.value.id, r.value.elevs);
      if (byId.size === 0) return;
      setLifts((prev) =>
        prev.map((l) => {
          const elevs = byId.get(l.id);
          if (!elevs) return l;
          const o = orientBottomToTop(l.points, elevs);
          const stats = liftStats(o.points, o.elevs);
          return {
            ...l,
            points: o.points,
            endpointElevM: o.elevs,
            lengthM: stats.lengthM,
            verticalM: stats.verticalM,
          };
        })
      );
    });
    return () => {
      stale = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Backfill centerline elevations for legacy/offline runs.
  useEffect(() => {
    const missing = trailsRef.current.filter((t) => t.parts.some((p) => p.centerlineElevM.length !== p.centerline.length));
    if (missing.length === 0) return;
    let stale = false;
    void Promise.allSettled(
      missing.map(async (t) => {
        const parts = await Promise.all(t.parts.map(async (part) => {
          const elevs = await sampleProfile(part.centerline, 13);
          if (!elevs) throw new Error('No terrain data covers this run.');
          const o = orientTopToBottom(part.centerline, elevs);
          return { ...part, centerline: o.spine, centerlineElevM: o.elevM };
        }));
        return { id: t.id, parts };
      })
    ).then((results) => {
      if (stale) return;
      const byId = new Map<string, SavedTrail['parts']>();
      for (const r of results) if (r.status === 'fulfilled') byId.set(r.value.id, r.value.parts);
      if (byId.size === 0) return;
      const backfill = topology.begin();
      backfill.mapTrails((t) => {
        const parts = byId.get(t.id);
        if (!parts) return t;
        const stats = trailPartsStats(parts);
        return {
          ...t,
          parts,
          lengthM: stats.lengthM,
          verticalM: stats.verticalM,
          avgSlopeDeg: stats.avgSlopeDeg,
          maxSlopeDeg: stats.maxSlopeDeg,
          difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
        };
      });
      backfill.commit();
    });
    return () => {
      stale = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function armRoadTool(roadType: RoadType) {
    roadController.arm(roadType);
  }

  function armDamTool() {
    snowmakingController.dam.arm();
  }

  function cancelDamTool() {
    snowmakingController.dam.cancel();
  }

  function armPondTool() {
    snowmakingController.pond.arm();
  }

  function cancelPondTool() {
    snowmakingController.pond.cancel();
  }

  function cancelRoadTool() {
    roadController.cancel();
  }

  function armLiftTool() {
    liftController.arm();
  }

  function cancelLiftTool() {
    liftController.cancel();
  }

  /**
   * The single clearing engine shared by lifts, trails, and roads. Fells the given
   * clearings (each an outer ring plus optional tree-island holes) to grassland,
   * stamping the analytical cover grid and re-deriving its vector display in a
   * worker. Then recomputes metadata + manifest, validates, saves, and updates
   * the map. Best-effort: failures never lose the infrastructure object that
   * triggered the edit.
   */
  async function clearCover(clearings: CoverClearing[]): Promise<void> {
    // Serialized by the terrain document, and handed the snapshot current when
    // this edit actually starts — never the one current when it was queued.
    await terrain.runCoverEdit((snapshot) => clearCoverAgainst(snapshot, clearings));
  }

  async function clearCoverAgainst(
    { record, revision }: TerrainSnapshot,
    clearings: CoverClearing[]
  ): Promise<void> {
    const map = mapRef.current;
    if (!map || !record || !record.coverGrid || !record.bounds) return;
    try {
      const workerGrid = {
        ...record.coverGrid,
        bounds: { ...record.coverGrid.bounds },
        data: Uint8Array.from(record.coverGrid.data),
      } as unknown as CoverGrid;
      const hasVectorDisplay = !!record.coverDisplayGeometry && !!record.coverDisplayMetadata;
      const result = await coverEdit.run({
        grid: workerGrid,
        clearings,
        deriveDisplay: hasVectorDisplay,
      });
      if (result.changed === 0) return;
      const grid = { ...record.coverGrid, bounds: { ...record.coverGrid.bounds },
        data: result.gridData } as unknown as CoverGrid;

      // Checksums are produced beside the edited transferable buffers in the
      // worker, avoiding another full-grid pass on the UI thread.
      let upgraded = {
        ...record,
        coverGrid: grid,
        coverMetadata: result.coverMetadata,
        updatedAt: new Date().toISOString(),
      } as unknown as TerrainRecord;

      // v5+ packages render vector cover. Re-derive the whole display geometry
      // from the freshly-stamped grid (the merged source of truth) rather than
      // appending each cleared strip as its own feature: overlapping clears then
      // merge into single polygons — no alpha-doubled overlap, no internal
      // outlines — tree islands become true holes, and forest cells that were
      // felled actually disappear instead of showing grass blended over forest.
      // v4 raster-only packages skip this and rely on the grid stamp + tile-cache
      // refresh below.
      if (hasVectorDisplay) {
        if (!result.displayGeometry || !result.displayMetadata) {
          throw new Error('Ground-cover worker returned no vector display geometry.');
        }
        upgraded = {
          ...upgraded,
          coverDisplayGeometry: result.displayGeometry,
          coverDisplayMetadata: result.displayMetadata,
        };
      }

      upgraded = { ...upgraded, packageManifest: manifestWithUpdatedCover(upgraded) };
      const validation = validateTerrainCoverEdit(upgraded);
      if (!validation.ok) {
        console.warn('Cover-clear produced an invalid package; keeping the previous cover.', validation.errors.join(' '));
        return;
      }
      // No write here: the edit lives in memory until the player saves, and the
      // tile protocols the commit refreshes read the in-memory record, not the
      // package on disk.
      const commit = terrain.commit({ expectedRevision: revision, record: upgraded, kind: 'cover' });
      if (!commit.ok) {
        console.warn('Cover-clear finished against a superseded terrain package; keeping the previous cover.');
      }
    } catch (error) {
      console.warn('Cover-clear failed; keeping the previous cover.', error);
    }
  }

  /**
   * Fell a minimum-50-foot corridor under a newly-drawn lift. Independent,
   * irregular outward noise softens both treelines without ever narrowing the
   * guaranteed base clearing. The saved lift line itself remains exact.
   */
  async function applyLiftCoverClear(lift: SavedLift): Promise<void> {
    const record = terrain.record;
    if (!record || !record.bounds) return;
    const ring = liftClearingRing(lift.points, record.bounds, lift.id);
    await clearCover([{ polygon: [ring] }]);
  }

  /** Patch a non-geometric field (name/chairs/capacity/status) of a built lift. */
  function patchLift(id: string, patch: Partial<SavedLift>) {
    liftController.patch(id, patch);
  }

  function deleteLift(id: string) {
    liftController.remove(id);
  }

  // ---- User-declared connectivity: anchors, nodes, connector paths ---------

  function armNodeTool(phase: 'add' | 'remove') {
    nodePathController.armNode(phase);
  }

  function cancelNodeTool() {
    nodePathController.cancelNode();
  }

  function confirmAddNode() {
    nodePathController.confirmAddNode();
  }

  function removeGraphNode(id: string) {
    nodePathController.removeNode(id);
  }

  function confirmRemoveNode() {
    nodePathController.confirmRemoveNode();
  }

  /** Legacy free-standing pins from saves made before nodes became graph nodes. */
  function deleteSkiNode(id: string) {
    nodePathController.deleteLegacyNode(id);
  }

  function selectGraphNode(id: string) {
    nodePathController.selectNode(id);
  }

  function armPathTool() {
    nodePathController.armPath();
  }

  function cancelPathTool() {
    nodePathController.cancelPath();
  }

  function undoPathPoint() {
    nodePathController.undoPath();
  }

  /**
   * Finish a connector. The last drawn point must resolve to an anchor — a path
   * that lands in open snow connects nothing, so Enter is simply ignored until
   * the route ends on a run, lift, path or node.
   */
  function finishPathRoute() {
    nodePathController.finishPath();
  }

  function confirmPath() {
    nodePathController.confirmPath();
  }

  function deleteSkiPath(id: string) {
    nodePathController.removePath(id);
  }

  /** Patch a non-geometric field (closed) of a built connector path. */
  function patchSkiPath(id: string, patch: Partial<SavedPath>) {
    nodePathController.patchPath(id, patch);
  }

  function armTrailTool() {
    trailController.arm();
  }

  function changeTrailHead() {
    trailController.changeHead();
  }

  function cancelTrailTool() {
    trailController.cancel();
  }

  function setTrailPaintModeState(mode: 'paint' | 'erase') {
    trailController.setPaintMode(mode);
  }

  function undoTrailPaint() {
    trailController.undoPaint();
  }

  function clearTrailPaint() {
    trailController.clearPaint();
  }

  function finishTrailPaint() {
    trailController.finishPaint();
  }

  function backToTrailPaint() {
    trailController.backToPaint();
  }

  function changeTrailBrushWidth(widthM: number) {
    trailController.changeBrushWidth(widthM);
  }

  function patchTrailDraft(patch: Parameters<typeof trailController.patchDraft>[0]) {
    trailController.patchDraft(patch);
  }

  function setTrailTerrainGrading(enabled: boolean) {
    trailController.setGrading(enabled);
  }

  function retryTrailElevation() {
    trailController.retryElevation();
  }

  async function confirmTrail() {
    await trailController.confirm();
  }

  /** Patch a non-geometric field (name/status) of a built run. */
  function patchTrail(id: string, patch: Partial<SavedTrail>) {
    trailController.patch(id, patch);
  }

  function deleteTrail(id: string) {
    trailController.remove(id);
  }

  /** Close/open a bottom dock, yielding any active draw tool of the others. */
  function toggleDock(which: DockId) {
    const isOpen = which === 'layers' ? layersOpen : which === 'lifts' ? liftsOpen
      : which === 'trails' ? trailsOpen : which === 'snowmaking' ? snowmakingOpen : infrastructureOpen;
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
      setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null); }
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
        setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null); }
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

  function handleSearchResult(r: GeocodeResult) {
    mapRef.current?.flyTo({ center: [r.lng, r.lat], zoom: 12, duration: 1200 });
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
    packageStateRef.current = 'preparing';
    setPackageState('preparing');
    // Preparation owns the screen from here — its own gate has the step
    // checklist and a Cancel button, so any resort loading screen stands down.
    reportBoot({ type: 'handoff' });
    setPackageProgress({ phase: 'elevation', message: 'Starting resort preparation', completed: 0, total: 10 });
    packageAbortRef.current?.abort();
    const controller = new AbortController();
    packageAbortRef.current = controller;
    mapRef.current?.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
    try {
      const record = await prepareResortPackage(
        site,
        name,
        { sampleSiteCoverGrid },
        { onProgress: setPackageProgress, signal: controller.signal }
      );
      const validation = validateTerrainPackage(record);
      if (!validation.ok) throw new Error(validation.errors.join(' '));
      // Ingest persisted this package itself, so it starts clean.
      terrain.replace(record);
      packageStateRef.current = 'ready';
      setPackageState('ready');
      // Cover the first resort render; the style.load reveal drops it once the
      // resort is fully drawn. Set unconditionally: repairing a save that
      // failed to load has no map yet (mapCanStart was false), and the one it
      // is about to construct needs covering just as much as a restyle does.
      showLocalBoot({ stage: 'build' });
      // A restyle re-mounts terrain and every custom tile source.
      mapRef.current?.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
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
      if (packageAbortRef.current === controller) packageAbortRef.current = null;
    }
  }

  function cancelPackagePreparation() {
    packageAbortRef.current?.abort();
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
      const center = map.getCenter();
      const checkpoint = withResumeCheckpoint(persisted, {
        center: [center.lng, center.lat],
        zoom: map.getZoom(),
        bearing: map.getBearing(),
        pitch: map.getPitch(),
      }, is3DRef.current);
      const savedCheckpoint = await saveGame(checkpoint).catch((error: unknown) => ({
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
    const committedTopology = committedTopologyRef.current;
    const committedTerrain = terrain.snapshot().record;
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
      nodes: committedTopology.nodes,
      paths: committedTopology.paths,
      junctions: committedTopology.junctions,
      snowmakingNodes: snowmakingNodesRef.current,
      lakeDepthOverrides: lakeDepthOverridesRef.current,
      lakeNameOverrides: lakeNameOverridesRef.current,
      streamWidthOverrides: streamWidthOverridesRef.current,
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
      nodes: committedTopology.nodes,
      paths: committedTopology.paths,
      junctions: committedTopology.junctions,
      snowmakingNodes: snowmakingNodesRef.current,
      lakeDepthOverrides: lakeDepthOverridesRef.current,
      lakeNameOverrides: lakeNameOverridesRef.current,
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
    const res = await saveGame(next);
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
    const result = await saveGame(next);
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
    const res = await saveGame(next);
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
    const choice = await new Promise<UnsavedChoice>((resolve) => {
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
    sessionControlsRef.current = { checkpointForExit, confirmExit };
    return () => {
      sessionControlsRef.current = null;
    };
  });

  const picking = mode === 'picking';
  const awaitingName = picking && siteMode === 'locked' && !saved;

  // Lift panel is open when the user opened it OR the tool is mid-draw / a lift
  // is selected (detail or edit); layers yield to it so the two roll-ups never
  // overlap. selectedLift resolves the id to the live lift (null if it was
  // deleted out from under the selection).
  const liftActive = toolCoordinatorState.activeTool === 'lift' || selectedLiftId !== null;
  const trailActive = toolCoordinatorState.activeTool === 'trail' ||
    toolCoordinatorState.activeTool === 'ski-node' || toolCoordinatorState.activeTool === 'ski-path' ||
    selectedTrailId !== null;
  /** The Trails roll-up swaps its body in place for a selection or active tool. */
  const trailPanelBusy = trailTool.phase !== 'idle' || trailEditing ||
    nodeTool.phase !== 'idle' || pathTool.phase !== 'idle' || selectedTrailId !== null;
  const activeTrailsTool: TrailsTool =
    trailTool.phase !== 'idle' ? 'trail'
      : nodeTool.phase === 'add' ? 'node-add'
        : nodeTool.phase === 'remove' ? 'node-remove'
          : pathTool.phase !== 'idle' ? 'path'
            : 'none';
  /** Everything needed to turn an id into a name, shared by every readout. */
  const anchorWorld = useMemo(
    () => ({ trails, lifts, junctions, nodes: skiNodes, paths: skiPaths }),
    [trails, lifts, junctions, skiNodes, skiPaths]);
  /** The graph nodes as the panel lists them: numbered, named, removable or not. */
  const junctionRows = useMemo(() => summarizeJunctions(anchorWorld), [anchorWorld]);
  /**
   * Designer-facing connectivity warnings. `unanchoredTrailIds` is deliberately
   * left out: every run built before this feature has no declared start, so
   * warning on it would shout on every existing save.
   */
  const trailNetworkWarnings = useMemo(() => {
    const d = network.diagnostics;
    const out: string[] = [];
    const plural = (n: number, one: string, many: string) => (n === 1 ? one : many);
    if (d.orphanTrailIds.length > 0) {
      out.push(`${d.orphanTrailIds.length} ${plural(d.orphanTrailIds.length, 'run is', 'runs are')} not reachable from any lift.`);
    }
    const unresolved = d.unresolvedAnchorTrailIds.length + d.unresolvedAnchorPathIds.length;
    if (unresolved > 0) {
      out.push(`${unresolved} start ${plural(unresolved, 'connection no longer resolves', 'connections no longer resolve')} — the target was moved or deleted.`);
    }
    if (d.overreachingAnchorIds.length > 0) {
      out.push(`${d.overreachingAnchorIds.length} ${plural(d.overreachingAnchorIds.length, 'connection spans', 'connections span')} an unusually long gap.`);
    }
    if (d.degeneratePathIds.length > 0) {
      out.push(`${d.degeneratePathIds.length} ${plural(d.degeneratePathIds.length, 'path starts', 'paths start')} and ends at the same junction.`);
    }
    if (d.componentCount > 1) {
      out.push(`The mountain is in ${d.componentCount} disconnected pieces.`);
    }
    return out;
  }, [network]);
  const infrastructureActive = toolCoordinatorState.activeTool === 'road';
  const snowmakingActive = toolCoordinatorState.activeTool === 'dam' ||
    toolCoordinatorState.activeTool === 'pond' ||
    selectedDamId !== null || selectedPondId !== null || selectedSnowmakingNodeId !== null;
  const selectedLakeFeature = selectedLakeId
    ? terrainRecord?.vectorFeatures?.waterPolygons.find((lake) => lake.id === selectedLakeId) ?? null
    : null;
  const selectedLake = useMemo(() => selectedLakeFeature && terrainRecord
    ? analyzeLake(selectedLakeFeature, terrainRecord, lakeDepthOverrides[selectedLakeFeature.id],
      lakeNameOverrides[selectedLakeFeature.id])
    : null, [selectedLakeFeature, terrainRecord, lakeDepthOverrides, lakeNameOverrides]);
  const selectedDam = selectedDamId ? dams.find((dam) => dam.id === selectedDamId) ?? null : null;
  const selectedPond = selectedPondId ? ponds.find((pond) => pond.id === selectedPondId) ?? null : null;
  const selectedSnowmakingNode = selectedSnowmakingNodeId
    ? snowmakingNodes.find((node) => node.id === selectedSnowmakingNodeId) ?? null
    : null;
  const selectedStreamFeature = selectedStreamId
    ? terrainRecord?.vectorFeatures?.waterLines.find((stream) => stream.id === selectedStreamId) ?? null
    : null;
  const selectedStream = useMemo(() => selectedStreamFeature
    ? analyzeStream(selectedStreamFeature, streamWidthOverrides[selectedStreamFeature.id]) : null,
    [selectedStreamFeature, streamWidthOverrides]);
  const lakeOpen = !!saved && selectedLake !== null;
  const streamOpen = !!saved && selectedStream !== null;
  const waterDetailOpen = lakeOpen || streamOpen;
  const liftsOpen = !!saved && !waterDetailOpen && (openDock === 'lifts' || liftActive);
  const trailsOpen = !!saved && !waterDetailOpen && !liftsOpen && (openDock === 'trails' || trailActive);
  const snowmakingOpen = !!saved && !waterDetailOpen && !liftsOpen && !trailsOpen &&
    (openDock === 'snowmaking' || snowmakingActive);
  const infrastructureOpen = !!saved && !waterDetailOpen && !liftsOpen && !trailsOpen &&
    !snowmakingOpen && (openDock === 'infrastructure' || infrastructureActive);
  const layersOpen = !!saved && !waterDetailOpen && !liftsOpen && (openDock === 'layers' || layersAlongsideBuild);
  const selectedLift = selectedLiftId ? lifts.find((l) => l.id === selectedLiftId) ?? null : null;
  const selectedTrail = selectedTrailId ? trails.find((t) => t.id === selectedTrailId) ?? null : null;
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

      {checkpointError && (
        <div className="checkpoint-error" role="alert">
          <span>{checkpointError}</span>
          <button type="button" onClick={() => setCheckpointError(null)}>Dismiss</button>
        </div>
      )}

      {unsavedPrompt && (
        <UnsavedChangesModal
          saving={saving}
          onChoice={(choice) => unsavedChoiceRef.current?.(choice)}
        />
      )}

      {showPackageGate && (
        <div className="package-gate" role="dialog" aria-modal="true" aria-live="polite">
          <div className={`package-card${packageState === 'error' ? ' is-error' : ''}`}>
            {packageState !== 'error' && (
              <svg className="topo-motif" viewBox="0 0 120 120" aria-hidden="true">
                <defs>
                  <path id="topoRing" d="M60 42 C73 42 80 50 80 60 C80 72 71 80 60 80 C49 80 40 71 40 60 C40 49 47 42 60 42 Z" />
                </defs>
                <g fill="none" stroke="currentColor" strokeWidth="1.5">
                  <use href="#topoRing" className="topo-ring" style={{ '--i': 0 } as CSSProperties} transform="translate(60 60) scale(0.5) translate(-60 -60)" />
                  <use href="#topoRing" className="topo-ring" style={{ '--i': 1 } as CSSProperties} transform="translate(60 60) scale(1) translate(-60 -60)" />
                  <use href="#topoRing" className="topo-ring" style={{ '--i': 2 } as CSSProperties} transform="translate(60 60) scale(1.55) translate(-60 -60)" />
                  <use href="#topoRing" className="topo-ring" style={{ '--i': 3 } as CSSProperties} transform="translate(60 60) scale(2.1) translate(-60 -60)" />
                </g>
                <circle cx="60" cy="60" r="3.4" className="topo-peak" fill="currentColor" />
              </svg>
            )}
            <div className="package-kicker">LOCAL RESORT DATA</div>
            <h2>{packageState === 'preparing' ? 'Preparing resort data' : 'Preparation failed'}</h2>
            <p>
              {packageState === 'preparing'
                ? 'Fetching terrain, ground cover, and contours for your build site.'
                : packageError ?? 'Elevation, contours, and ground cover must be saved locally before designing.'}
            </p>
            {packageState === 'preparing' && packageProgress && (() => {
              const { completed, total } = packageProgress;
              const pct = Math.round((completed / total) * 100);
              return (
                <>
                  <ul className="package-steps">
                    {PREP_STEPS.map((s, i) => {
                      const state = completed > i ? 'done' : completed === i ? 'active' : 'pending';
                      return (
                        <li key={s.key} className={`package-step is-${state}`}>
                          <span className="package-step-dot" aria-hidden="true" />
                          <span className="package-step-label">
                            {s.label}
                            {state === 'active' && <span className="package-step-detail">{packageProgress.message}</span>}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                  <div className="package-progress"><span style={{ width: `${pct}%` }} /></div>
                  <div className="package-progress-label">{pct}% · Step {Math.min(total, completed + 1)} of {total}</div>
                  <div className="package-actions">
                    <button className="site-btn" onClick={cancelPackagePreparation}>Cancel</button>
                  </div>
                </>
              );
            })()}
            {packageState === 'error' && (
              <div className="package-actions">
                <button className="site-btn" onClick={onQuit}>Back to menu</button>
                <button className="site-btn site-btn-primary" onClick={() => void createSave()}>
                  Prepare Resort Data
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* The New Game prepare→play handoff has no App-owned loading screen, so
          it drives the same surface locally rather than drawing the resort in
          front of the player. Resuming a save renders this from App instead. */}
      {localBoot && !showPackageGate && (
        <ResortLoadingScreen
          title={saved?.name || nameDraft.trim() || 'Your resort'}
          progress={localBoot}
          imageryUrl={localImageryUrlRef.current}
          state="loading"
          onBack={onQuit}
          onEnterAnyway={() => bootControls.current?.reveal()}
        />
      )}

      {/* Top-right app menu (Save / Load / Settings / Credits / Main Menu) */}
      <GameMenu
        canSave={!!saved}
        saving={saving}
        unsaved={!!saved && hasUnsavedChanges()}
        onSave={() => void saveProgress()}
        onLoad={onLoadGame}
        onSettings={onOpenSettings}
        onCredits={() => setShowCredits(true)}
        onRebuildCover={terrainRecord && terrainRecord.schemaVersion < 6 ? () => void repairAndContinue() : undefined}
        onQuit={onQuit}
      />

      {picking && !saved && <SearchBox onResult={handleSearchResult} />}

      {/* Site-picking + 3D controls (top-right, below the Menu button) */}
      <div className="top-right-stack">
        {picking && !saved && (
          <SiteControl
            mode={siteMode}
            box={siteBox}
            onStart={startSelect}
            onConfirm={confirmSite}
            onCancel={cancelSelect}
            onExit={exitSite}
          />
        )}
        {terrainRecord && <View3DControl is3D={!isOverhead} onToggle={toggle3D} />}
      </div>

      {buildingActivity && <ConstructionStatusBug activity={buildingActivity} />}

      {/* Mountain dashboards toggle (top-left). Sits above the overlay it
          opens, so the same button closes it. */}
      {saved && (
        <div className="top-left-stack">
          <button
            className="site-btn"
            aria-pressed={showNetwork}
            title="Mountain Dashboards — Trails & Lifts and Snowmaking network views (1 / 2)"
            onClick={() => setShowNetwork((v) => !v)}
          >
            {showNetwork ? '✕ Mountain Dashboards' : 'Mountain Dashboards'}
          </button>
        </div>
      )}

      {saved && showNetwork && (
        <MountainDashboards
          dashboard={dashboard}
          onDashboardChange={setDashboard}
          networkProps={{
            network,
            units: settings.units,
            selectedLiftId: networkLiftId,
            selectedEdgeId: networkEdgeId,
            onSelectLift: setNetworkLiftId,
            onSelectEdge: setNetworkEdgeId,
            onToggleTrailClosed: (id, closed) => patchTrail(id, { closed }),
            onToggleLiftClosed: (id, closed) => patchLift(id, { closed }),
            onTogglePathClosed: (id, closed) => patchSkiPath(id, { closed }),
          }}
          snowmakingProps={{
            dams,
            ponds,
            trails,
            lifts,
            nodes: snowmakingNodes,
            coverDisplay: coverDisplayRef.current,
            terrainRecord,
            units: settings.units,
            selectedNodeId: selectedSnowmakingNodeId,
            onSelectNode: (id) => (id
              ? snowmakingController.nodes.select(id)
              : setSelectedSnowmakingNodeId(null)),
          }}
          onClose={() => setShowNetwork(false)}
        />
      )}

      {/* Site-picking readout floats lower-left; in-game it lives on the toolbar. */}
      {!saved && <CursorReadout readout={readout} units={settings.units} />}

      {/* Bottom dock: layers/lifts roll-up circles above the status toolbar */}
      {saved && (
        <div className="game-dock">
          <div className="dock-stack">
            <div className="dock-rollups">
              {streamOpen && selectedStream && (
                <div className="dock-rollup dock-stream" data-panel="stream">
                  <div className="dock-panel">
                    <StreamDetail
                      stream={selectedStream}
                      units={settings.units}
                      onWidthOverride={(widthM) => {
                        setStreamWidthOverrides((current) => {
                          const next = { ...current };
                          if (widthM == null) delete next[selectedStream.id];
                          else next[selectedStream.id] = widthM;
                          return next;
                        });
                      }}
                      onClose={() => setSelectedStreamId(null)}
                    />
                  </div>
                </div>
              )}
              {lakeOpen && selectedLake && (
                <div className="dock-rollup dock-lake" data-panel="lake">
                  <div className="dock-panel">
                    <LakeDetail
                      lake={selectedLake}
                      units={settings.units}
                      onNameOverride={(name) => {
                        setLakeNameOverrides((current) => {
                          const next = { ...current };
                          if (name == null) delete next[selectedLake.id];
                          else next[selectedLake.id] = name;
                          return next;
                        });
                      }}
                      onDepthOverride={(depthM) => {
                        setLakeDepthOverrides((current) => {
                          const next = { ...current };
                          if (depthM == null) delete next[selectedLake.id];
                          else next[selectedLake.id] = depthM;
                          return next;
                        });
                      }}
                      onClose={() => setSelectedLakeId(null)}
                    />
                  </div>
                </div>
              )}
              {layersOpen && (
              <div className="dock-rollup dock-layers">
                {/* Contextual legend floats above the dock so switching overlays
                    never resizes the menu itself (it stays a constant height). */}
                {activeOverlay && (
                  <div className="dock-legend-popover">
                    <Legend overlay={activeOverlay} />
                  </div>
                )}
                <div className="dock-panel">
                  <div className="dock-head">
                    <span className="dock-head-title">Layers</span>
                    <button
                      className="settings-close-x"
                      aria-label="Close"
                      onClick={() => {
                        setLayersAlongsideBuild(false);
                        setOpenDock((current) => current === 'layers' ? null : current);
                      }}
                    >
                      ✕
                    </button>
                  </div>
                  <LayerList
                    layers={layers}
                    onToggle={handleToggle}
                    activeOverlay={activeOverlay}
                    inlineLegend={false}
                  />
                </div>
              </div>
            )}
              {trailsOpen && (
              <div className="dock-rollup dock-trails" data-panel="trails">
                <div className="dock-panel">
                  {trailPanelBusy ? (
                    nodeTool.phase !== 'idle' ? (
                      <div className="site-control site-control-wide trail-panel">
                        <div className="dock-head">
                          <span className="dock-head-title">
                            {nodeTool.phase === 'add' ? 'Add node' : 'Remove node'}
                          </span>
                          <button className="settings-close-x" aria-label="Close" onClick={cancelNodeTool}>✕</button>
                        </div>
                        <div className="site-hint">
                          {nodeTool.phase === 'add'
                            ? 'Click anywhere along a run to split it there.'
                            : 'Click a node on a run. Only one the run passes straight through can go.'}
                        </div>
                        {nodeTool.phase === 'add' && nodeTool.candidate && (
                          <div className="readout-line">
                            <span className="lift-stat-label">Splits</span>
                            <span className="lift-stat-value">
                              <AnchorValue anchor={nodeTool.candidate} world={anchorWorld} />
                            </span>
                          </div>
                        )}
                        {nodeTool.phase === 'remove' && nodeTool.junctionId && (() => {
                          const row = junctionRows.find((r) => r.id === nodeTool.junctionId);
                          return row ? (
                            <div className="readout-line">
                              <span className="lift-stat-label">Node {row.number}</span>
                              <span className="lift-stat-value">{row.label}</span>
                            </div>
                          ) : null;
                        })()}
                        {nodeTool.error && <div className="lift-warning">{nodeTool.error}</div>}
                        <div className="site-actions">
                          <button className="site-btn" onClick={cancelNodeTool}>Done</button>
                          {nodeTool.phase === 'add' ? (
                            <button className="site-btn site-btn-primary" disabled={!nodeTool.candidate}
                              onClick={confirmAddNode}>Add node</button>
                          ) : (
                            <button className="site-btn site-btn-primary"
                              disabled={!nodeTool.junctionId || nodeTool.error !== null}
                              onClick={confirmRemoveNode}>Remove node</button>
                          )}
                        </div>
                      </div>
                    ) : pathTool.phase !== 'idle' ? (
                      <div className="site-control site-control-wide trail-panel">
                        <div className="dock-head">
                          <span className="dock-head-title">Draw path</span>
                          <button className="settings-close-x" aria-label="Close" onClick={cancelPathTool}>✕</button>
                        </div>
                        {pathTool.phase === 'review' ? (
                          <>
                            <input
                              className="name-entry-input lift-name-input"
                              value={pathTool.name}
                              onChange={(e) => nodePathController.renamePath(e.target.value)}
                            />
                            <div className="readout-line">
                              <span className="lift-stat-label">From</span>
                              <span className="lift-stat-value">{describeAnchor(pathTool.from)}</span>
                            </div>
                            <div className="readout-line">
                              <span className="lift-stat-label">To</span>
                              <span className="lift-stat-value">{describeAnchor(pathTool.to)}</span>
                            </div>
                            <div className="readout-line">
                              <span className="lift-stat-label">Length</span>
                              <span className="lift-stat-value">
                                {fmtDistance(pathLengthM(pathTool.points), settings.units)}
                              </span>
                            </div>
                            <div className="site-actions">
                              <button className="site-btn" onClick={cancelPathTool}>Cancel</button>
                              <button className="site-btn site-btn-primary" onClick={confirmPath}>Build path</button>
                            </div>
                          </>
                        ) : (
                          <>
                            <div className="site-hint">
                              {pathTool.phase === 'armed'
                                ? 'Click anywhere along a ski trail to start the connector.'
                                : 'Click along the route. Finish on a different ski trail — a node is added where each end meets a run.'}
                            </div>
                            <div className="site-actions">
                              <button
                                className="site-btn"
                                onClick={undoPathPoint}
                                disabled={pathTool.phase !== 'drawing'}
                              >
                                Undo point
                              </button>
                              <button
                                className="site-btn site-btn-primary"
                                onClick={finishPathRoute}
                                disabled={pathTool.phase !== 'drawing' || pathTool.points.length < 2}
                              >
                                Finish
                              </button>
                            </div>
                            <button className="site-btn" onClick={cancelPathTool}>Cancel</button>
                          </>
                        )}
                      </div>
                    ) : trailTool.phase === 'idle' && selectedTrail && !trailEditing ? (
                      <TrailDetail
                        trail={selectedTrail}
                        units={settings.units}
                        onEdit={() => setTrailEditing(true)}
                        onRemove={() => deleteTrail(selectedTrail.id)}
                        onToggleClosed={(closed) => patchTrail(selectedTrail.id, { closed })}
                        onClose={() => {
                          setSelectedTrailId(null);
                          setOpenDock('trails');
                        }}
                      />
                    ) : (
                      <TrailControl
                        tool={trailTool}
                        trails={trails}
                        world={anchorWorld}
                        selectedId={trailTool.phase === 'idle' ? selectedTrailId : null}
                        units={settings.units}
                        brushWidthM={brushWidthM}
                        onBrushWidthChange={changeTrailBrushWidth}
                        onCancel={cancelTrailTool}
                        onModeChange={setTrailPaintModeState}
                        onUndo={undoTrailPaint}
                        onClear={clearTrailPaint}
                        onFinish={finishTrailPaint}
                        onDraftChange={patchTrailDraft}
                        onGradingChange={setTrailTerrainGrading}
                        onConfirm={confirmTrail}
                        building={building}
                        onEditPatch={patchTrail}
                        onCloseEdit={() => setTrailEditing(false)}
                        onDelete={deleteTrail}
                        onRetryElevation={retryTrailElevation}
                        onChangeHead={changeTrailHead}
                        onBackToPaint={backToTrailPaint}
                      />
                    )
                  ) : (
                    <TrailsPanel
                      trails={trails}
                      junctions={junctionRows}
                      legacyNodes={skiNodes}
                      paths={skiPaths}
                      units={settings.units}
                      selectedTrailId={selectedTrailId}
                      selectedNodeId={selectedNodeId}
                      selectedPathId={selectedPathId}
                      activeTool={activeTrailsTool}
                      warnings={trailNetworkWarnings}
                      onPaintRun={armTrailTool}
                      onAddNode={() => armNodeTool('add')}
                      onRemoveNodeTool={() => armNodeTool('remove')}
                      onDrawPath={armPathTool}
                      onSelectTrail={(id) => selectTrailRef.current(id)}
                      onSelectNode={selectGraphNode}
                      onSelectPath={(id) => id
                        ? transitionSelection({ kind: 'ski-path', id })
                        : setSelectedPathId(null)}
                      onDeleteNode={removeGraphNode}
                      onDeleteLegacyNode={deleteSkiNode}
                      onDeletePath={deleteSkiPath}
                      onClose={() => setOpenDock(null)}
                    />
                  )}
                </div>
              </div>
            )}
              {liftsOpen && (
              <div className="dock-rollup dock-lifts">
                <div className="dock-panel">
                  {liftTool.phase === 'idle' && selectedLift && !liftEditing ? (
                    // Clicking a lift opens its read-only detail first.
                    <LiftDetail
                      lift={selectedLift}
                      units={settings.units}
                      onEdit={() => setLiftEditing(true)}
                      onRemove={() => deleteLift(selectedLift.id)}
                      onToggleClosed={(closed) => patchLift(selectedLift.id, { closed })}
                      onClose={() => {
                        // Back up to the full lift list (keep the dock open).
                        setSelectedLiftId(null);
                        setOpenDock('lifts');
                      }}
                    />
                  ) : liftTool.phase === 'idle' && !selectedLift ? (
                    <LiftOverview
                      lifts={lifts}
                      units={settings.units}
                      onArm={armLiftTool}
                      onSelect={liftController.select}
                      onClose={() => setOpenDock(null)}
                    />
                  ) : (
                    // Draw / review a new lift, or edit the selected one.
                    <LiftControl
                      tool={liftTool}
                      lifts={lifts}
                      selectedId={liftTool.phase === 'idle' ? selectedLiftId : null}
                      units={settings.units}
                      onArm={armLiftTool}
                      onCancel={cancelLiftTool}
                      onDraftChange={liftController.patchDraft}
                      onConfirm={() => void liftController.confirm()}
                      building={building}
                      onSelect={liftController.select}
                      onEditPatch={patchLift}
                      onCloseEdit={() => setLiftEditing(false)}
                      onDelete={deleteLift}
                      onRetryElevation={liftController.retryElevation}
                    />
                  )}
                </div>
              </div>
            )}
              {snowmakingOpen && (
              <div className="dock-rollup dock-snowmaking">
                <div className="dock-panel">
                  <SnowmakingControl
                    damTool={damTool}
                    pondTool={pondTool}
                    dams={dams}
                    ponds={ponds}
                    selectedDam={selectedDam}
                    selectedPond={selectedPond}
                    nodes={snowmakingNodes}
                    selectedNode={selectedSnowmakingNode}
                    units={settings.units}
                    onArmDam={armDamTool}
                    onCancelDam={cancelDamTool}
                    onDamDraftChange={snowmakingController.dam.patchDraft}
                    onConfirmDam={snowmakingController.dam.confirm}
                    onSelectDam={snowmakingController.dam.select}
                    onDeleteDam={snowmakingController.dam.remove}
                    onCloseDam={() => setSelectedDamId(null)}
                    onArmPond={armPondTool}
                    onCancelPond={cancelPondTool}
                    onUndoPond={snowmakingController.pond.undo}
                    onFinishPond={snowmakingController.pond.finish}
                    onPondDraftChange={snowmakingController.pond.patchDraft}
                    onPondElevationChange={snowmakingController.pond.changeElevation}
                    onPondExcavationChange={snowmakingController.pond.changeExcavation}
                    onConfirmPond={snowmakingController.pond.confirm}
                    onSelectPond={snowmakingController.pond.select}
                    onDeletePond={snowmakingController.pond.remove}
                    onPondSnowmakingChange={snowmakingController.pond.setSnowmaking}
                    onClosePond={() => setSelectedPondId(null)}
                    onSelectNode={snowmakingController.nodes.select}
                    onRenameNode={snowmakingController.nodes.rename}
                    onCloseNode={() => setSelectedSnowmakingNodeId(null)}
                    building={building}
                    onClose={() => setOpenDock(null)}
                  />
                </div>
              </div>
            )}
              {infrastructureOpen && (
              <div className="dock-rollup dock-infrastructure">
                <div className="dock-panel">
                  <InfrastructureControl
                    tool={roadTool}
                    roads={roads}
                    units={settings.units}
                    onArm={armRoadTool}
                    onCancel={cancelRoadTool}
                    onUndo={roadController.undo}
                    onFinish={roadController.finish}
                    onDraftChange={roadController.patchDraft}
                    onConfirm={roadController.confirm}
                    building={building}
                    onClose={() => setOpenDock(null)}
                  />
                </div>
              </div>
            )}

            </div>

            <div className="dock-circles">
              <button
                className={`dock-circle dock-circle-layers${layersOpen ? ' is-active' : ''}`}
                onClick={() => toggleDock('layers')}
                aria-pressed={layersOpen}
                title="Layers"
                aria-label="Layers"
              >
                <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
                  <path d="M12 3 2 8l10 5 10-5-10-5Z" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" />
                  <path d="M2 12l10 5 10-5M2 16l10 5 10-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" />
                </svg>
              </button>
              <button
                className={`dock-circle dock-circle-lifts${liftsOpen ? ' is-active' : ''}`}
                onClick={() => toggleDock('lifts')}
                aria-pressed={liftsOpen}
                title="Ski lifts"
                aria-label="Ski lifts"
              >
                <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
                  <path d="M3 6l18-3" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
                  <circle cx="10" cy="5.4" r="1.1" fill="currentColor" />
                  <path d="M10 6.5v2.8m-2.4 0h4.8l-.7 3.4H8.3l-.7-3.4Z" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round" />
                </svg>
              </button>
              <button
                className={`dock-circle dock-circle-trails${trailsOpen ? ' is-active' : ''}`}
                onClick={() => toggleDock('trails')}
                aria-pressed={trailsOpen}
                title="Ski runs"
                aria-label="Ski runs"
              >
                <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
                  <path d="M3 20 12 4l9 16Z" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinejoin="round" />
                  <path d="M8.5 12q2 2.4 3.5 0t3.5 0" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
                </svg>
              </button>
              <button
                className={`dock-circle dock-circle-snowmaking${snowmakingOpen ? ' is-active' : ''}`}
                onClick={() => toggleDock('snowmaking')}
                aria-pressed={snowmakingOpen}
                title="Snowmaking"
                aria-label="Snowmaking"
              >
                <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
                  <path d="M12 3v18M4.2 7.5l15.6 9M19.8 7.5l-15.6 9" fill="none" stroke="currentColor"
                    strokeWidth="1.7" strokeLinecap="round" />
                  <path d="M9.6 4.8 12 7.2l2.4-2.4M9.6 19.2 12 16.8l2.4 2.4" fill="none" stroke="currentColor"
                    strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
              <button
                className={`dock-circle dock-circle-infrastructure${infrastructureOpen ? ' is-active' : ''}`}
                onClick={() => toggleDock('infrastructure')}
                aria-pressed={infrastructureOpen}
                title="Infrastructure"
                aria-label="Infrastructure"
              >
                <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
                  <path d="M5 22c0-7 4-8 4-13 0-3-1-5-1-7M19 22c0-7-4-8-4-13 0-3 1-5 1-7"
                    fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
                  <path d="M12 20v-3m0-3v-3m0-3V5" fill="none" stroke="currentColor"
                    strokeWidth="1.5" strokeLinecap="round" />
                </svg>
              </button>
            </div>
          </div>

          <GameToolbar
            resortName={saved.name}
            onOpenStats={() => setShowStats(true)}
            readout={readout}
            units={settings.units}
          />
        </div>
      )}

      {/* Name-and-start panel once a New Game site is locked */}
      {awaitingName && (
        <div className="name-entry">
          <div className="name-entry-title">Name your resort</div>
          <input
            className="name-entry-input"
            type="text"
            placeholder="e.g. Crystal Peak Resort"
            value={nameDraft}
            autoFocus
            onChange={(e) => setNameDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void createSave();
            }}
          />
          <div className="name-entry-actions">
            <button className="site-btn" onClick={exitSite}>
              Redraw
            </button>
            <button
              className="site-btn site-btn-primary"
              onClick={() => void createSave()}
              disabled={saving}
            >
              {saving ? 'Creating…' : 'Start Designing'}
            </button>
          </div>
        </div>
      )}

      {saved && showStats && (
        <ResortStatsPanel
          name={saved.name}
          onRename={renameResort}
          lifts={lifts}
          trails={trails}
          dams={dams}
          ponds={ponds}
          center={resortCenter()}
          units={settings.units}
          onClose={() => setShowStats(false)}
        />
      )}

      {showCredits && <CreditsPanel onClose={() => setShowCredits(false)} />}
    </>
  );
}
