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
import { buildSkiNetwork, makeFrame, toMeters } from '../network';
import {
  DEFAULT_PATH_WIDTH_M,
  describeAnchor,
  nextPathName,
  pathLengthM,
  sanitizeNodes,
  sanitizePaths,
} from '../skiNodes';
import {
  addNodePathDraftLayers,
  addNodePathLayers,
  setNodePathData,
  setNodePathDraftData,
  type NodePathDraft,
} from './nodePathLayers';
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
import type { AnchorRef, GameSave, RoadType, SavedDam, SavedJunction, SavedLift,
  SavedNode, SavedPath, SavedPond, SavedRoad, SavedSnowmakingNode, SavedTrail,
  SavedTrailPart, TerrainPackageProgress, TerrainRecord, CoverGrid } from '../types';
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
import {
  jitterPolygon,
  liftClearingRing,
  TRAIL_CLEAR_JITTER_M,
  type CoverClearing,
} from '../coverEdit';
import { CoverEditAdapter } from './coverEditClient';
import { DamAnalysisAdapter } from './damAnalysisClient';
import { TerrainGradeAdapter } from './terrainGradeClient';
import { TrailPaintAdapter } from './trailPaintClient';
import { clearResortCoverCache, getResortRenderStats, RESORT_COVER_PROTOCOL,
  resortCameraBounds, sampleLocalCoverAt, sampleLocalTerrainAt,
  setActiveResortTerrain, setRenderConcurrency, warmResortTiles, WORLD_COVER_LABELS } from './resortProtocols';
import { LiftControl, type LiftTool, type DraftLift } from './LiftControl';
import { addLiftLayers, setLiftData, liftsToGeoJSON, LIFT_BUILT_LAYER_IDS, type DraftLine } from './liftLayers';
import { AnchorValue, TrailControl, type TrailTool, type DraftTrail } from './TrailControl';
import { nearestTrailHeadAnchor, nearestTrailTailAnchor, type TrailHeadAnchor } from './trailHeadAnchor';
import { TrailDetail } from './TrailDetail';
import { InfrastructureControl, type DraftRoad, type RoadTool } from './InfrastructureControl';
import { SnowmakingControl, type DamTool, type DraftDam, type DraftPond, type PondTool } from './SnowmakingControl';
import { addRoadDraftLayers, addRoadLayers, ROAD_BUILT_LAYER_IDS, setRoadData,
  setRoadDraftData, type RoadDraftLine } from './roadLayers';
import { damCrestElevationAt, nextDamName, sanitizeDams, snapDamEndpoint } from '../damAnalysis';
import { addDamLayers, DAM_BUILT_LAYER_IDS, DAM_HIT_LAYERS, setDamData, setDamDraftData, setSelectedDam } from './damLayers';
import { analyzeStandalonePond, nextPondName, sanitizePonds,
  suggestedPondTopElevationM } from '../pondAnalysis';
import { designPondEarthwork, MAX_POND_BERM_HEIGHT_M, pondTerrainPatch } from '../pondEarthwork';
import { earthworkTerrainPatch, type EarthworkTerrainPatch } from '../earthwork';
import { addPondLayers, POND_BUILT_LAYER_IDS, POND_HIT_LAYERS, setPondData,
  setPondDraftData, setSelectedPond } from './pondLayers';
import { reconcileSnowmakingNodes, sanitizeSnowmakingNodes } from '../snowmakingNodes';
import { addSnowmakingLayers, setSnowmakingData, setSelectedSnowmakingNode,
  SNOWMAKING_HIT_LAYERS, SNOWMAKING_BUILT_LAYER_IDS } from './snowmakingLayers';
import {
  addTrailLayers,
  draftToGeoJSON,
  setTrailData,
  setTrailDraftData,
  setTrailPaintMode,
  setTrailPaintPreview,
  trailsToGeoJSON,
  TRAIL_BUILT_LAYER_IDS,
} from './trailLayers';
import { strokeToPolygon } from './trailBrush';
import { terrainGradeGeometryKey, type TerrainGradeResponse } from './terrainGradeProtocol';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
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
  FIXED_GRIP_SPEC,
  fmtDistance,
  liftStats,
  nextLiftName,
  orientBottomToTop,
  sanitizeLifts,
} from '../lifts';
import {
  sanitizeTrails,
  nextTrailName,
  orientTopToBottom,
  fillElevationGaps,
  pinTrailEndpoints,
  trailPartContains,
  trailAreaM2,
  trailPartsStats,
  difficultyForSlopes,
  DEFAULT_BRUSH_WIDTH_M,
} from '../trails';
import { canRemoveJunction, hydrateTopology, summarizeJunctions, withTopologyPart } from '../topology';
import { nextRoadName, roadClearingPolygons, roadLengthM, sanitizeRoads,
  TWO_LANE_ROAD_WIDTH_M } from '../roads';
import { haversineMeters } from '../geo';
import { resumeCameraOf, withResumeCheckpoint } from './resumeCheckpoint';
import { ConstructionStatusBug } from './ConstructionStatusBug';
import type { ConstructionActivity } from './constructionLock';
import { TerrainDocument, type TerrainDocumentPorts, type TerrainPublication,
  type TerrainCommitRequest, type TerrainRecordView, type TerrainSnapshot } from './terrainDocument';
import { TopologyDocument, topologyProjection, type TopologyState } from './topologyDocument';
import { commitDocuments } from './committedDocumentTransaction';
import { hitGuardLayers, orderContributions, orderHitContributions,
  type MapContribution } from './mapContribution';

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
export type NodeTool =
  | { phase: 'idle' }
  | { phase: 'add'; candidate: Extract<AnchorRef, { kind: 'trail' }> | null; error: string | null }
  | { phase: 'remove'; junctionId: string | null; error: string | null };

/** Drawing a connector path. Both ends must land on a valid anchor target,
 *  which is the whole point of a path — it declares a connection. */
export type PathTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'drawing'; points: [number, number][]; cursor: [number, number] | null; from: AnchorRef | null }
  | { phase: 'review'; points: [number, number][]; from: AnchorRef; to: AnchorRef; name: string };

type SelectionTarget =
  | { kind: 'lift' | 'trail' | 'dam' | 'pond' | 'snowmaking-node' | 'ski-node' | 'ski-path'; id: string }
  | { kind: 'lake' | 'stream'; id: string }
  | { kind: 'none' };

/** How close a click must land to a run/lift/path to count as anchoring to it. */
const ANCHOR_PICK_M = 60;

// Reject lift terminals closer than this — avoids accidental zero-length lifts
// from a double-click.
const MIN_LIFT_M = 50;
// A run benches inside what the player painted; the cut/fill volume is its
// price. Slopes and the grade band are engine constants — see trailCrossSection.
const TRAIL_GRADE_POLICY = { envelope: 'footprint' } as const;

interface TrailPaintCommand {
  mode: 'paint' | 'erase';
  path: [number, number][];
  seed?: boolean;
  /** Erase is one user action but two worker operations: erase, then repaint seed. */
  restoreSeed?: [number, number];
}

function trailHeadPreview(tool: TrailTool): {
  candidate: [number, number] | null;
  head: [number, number] | null;
  tail: [number, number] | null;
} {
  if (tool.phase === 'place-head') return { candidate: tool.candidate?.point ?? null, head: null, tail: null };
  if (tool.phase === 'place-tail') return { candidate: tool.candidate?.point ?? null,
    head: tool.anchor.point, tail: null };
  if (tool.phase === 'paint') return { candidate: null, head: tool.anchor.point, tail: null };
  if (tool.phase === 'analyzing') return { candidate: null, head: tool.anchor.point, tail: tool.tailAnchor.point };
  if (tool.phase === 'review') return { candidate: null, head: tool.draft.anchor?.point ?? null,
    tail: tool.draft.tailAnchor?.point ?? null };
  return { candidate: null, head: null, tail: null };
}

function sameTrailHeadAnchor(a: TrailHeadAnchor | null, b: TrailHeadAnchor | null): boolean {
  if (a === b) return true;
  if (!a || !b || a.kind !== b.kind) return false;
  const samePoint = a.point[0] === b.point[0] && a.point[1] === b.point[1];
  return samePoint && (a.kind === 'lift'
    ? b.kind === 'lift' && a.liftId === b.liftId && a.end === b.end
    : b.kind === 'trail' && a.trailId === b.trailId);
}

function hasUserTrailStroke(commands: TrailPaintCommand[], head: [number, number]): boolean {
  return commands.some((command) => !command.seed && command.mode === 'paint' &&
    command.path.some((point) => haversineMeters(point, head) >= 0.5));
}
// A road has no painted shoulder, so it alone grades outside its pavement.
const ROAD_GRADE_POLICY = { envelope: 'expand', maxWidthMultiplier: 3 } as const;

/** The in-progress lift line to render for the current tool state, if any. */
function draftLineOf(tool: LiftTool): DraftLine | null {
  if (tool.phase === 'anchored') {
    return { points: [tool.a, tool.cursor ?? tool.a] };
  }
  if (tool.phase === 'review') {
    return { points: tool.draft.points };
  }
  return null;
}

function roadDraftOf(tool: RoadTool): RoadDraftLine | null {
  if (tool.phase === 'drawing') return { points: tool.points, cursor: tool.cursor };
  if (tool.phase === 'review') return { points: tool.draft.points, cursor: null,
    gradingPolygons: tool.draft.gradingPolygons,
    infeasibleLines: tool.draft.gradingInfeasibleLines };
  return null;
}

function damDraftOf(tool: DamTool) {
  if (tool.phase === 'anchored') return { points: [tool.first], cursor: tool.cursor };
  if (tool.phase === 'analyzing') return { points: tool.points, cursor: null };
  if (tool.phase === 'review') return { points: tool.draft.points, cursor: null,
    pondRings: tool.draft.pondRings, crestElevationM: tool.draft.crestElevationM,
    averageDepthM: tool.draft.averageDepthM, footprintRings: tool.draft.footprintRings,
    crestRing: tool.draft.crestRing };
  return null;
}

/** The embankment toe traced from a grading patch: the polygon whose outer ring
 * is longest, since the structure is one connected body of earth. */
function largestFootprint(polygons: [number, number][][][]): [number, number][][] | undefined {
  if (!polygons.length) return undefined;
  return polygons.reduce((best, polygon) => polygon[0].length > best[0].length ? polygon : best);
}

function pondDraftOf(tool: PondTool) {
  if (tool.phase === 'drawing') return { points: tool.points, cursor: tool.cursor, closed: false };
  if (tool.phase === 'review') return { points: tool.draft.boundary.slice(0, -1), cursor: null, closed: true,
    topElevationM: tool.draft.topElevationM, averageDepthM: tool.draft.averageDepthM };
  return null;
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
  const [snapHover, setSnapHover] = useState<[number, number] | null>(null);
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
  const [liftTool, setLiftTool] = useState<LiftTool>({ phase: 'idle' });
  const [selectedLiftId, setSelectedLiftId] = useState<string | null>(null);
  // A selected lift opens its read-only detail first; Edit flips this to the
  // LiftControl edit panel. Reset to false whenever a (different) lift is opened.
  const [liftEditing, setLiftEditing] = useState(false);
  const [trails, setTrails] = useState<SavedTrail[]>(initialTopology.trails);
  const [trailTool, setTrailTool] = useState<TrailTool>({ phase: 'idle' });
  const [selectedTrailId, setSelectedTrailId] = useState<string | null>(null);
  const [trailEditing, setTrailEditing] = useState(false);
  const [roads, setRoads] = useState<SavedRoad[]>(() => sanitizeRoads(initialSave?.roads ?? []));
  const [roadTool, setRoadTool] = useState<RoadTool>({ phase: 'idle' });
  const [dams, setDams] = useState<SavedDam[]>(() => sanitizeDams(initialSave?.dams ?? []));
  const [damTool, setDamTool] = useState<DamTool>({ phase: 'idle' });
  const [selectedDamId, setSelectedDamId] = useState<string | null>(null);
  const [ponds, setPonds] = useState<SavedPond[]>(() => sanitizePonds(initialSave?.ponds ?? []));
  const [pondTool, setPondTool] = useState<PondTool>({ phase: 'idle' });
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
  const [nodeTool, setNodeTool] = useState<NodeTool>({ phase: 'idle' });
  const [pathTool, setPathTool] = useState<PathTool>({ phase: 'idle' });
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
  // Last-used brush width, kept across arms so it persists between runs.
  const [brushWidthM, setBrushWidthM] = useState(DEFAULT_BRUSH_WIDTH_M);
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
  const liftToolRef = useRef<LiftTool>(liftTool);
  const liftSampleTokenRef = useRef(0);
  const selectLiftRef = useRef<(id: string) => void>(() => {});
  const skiNodesRef = useRef<SavedNode[]>(skiNodes);
  const skiPathsRef = useRef<SavedPath[]>(skiPaths);
  const junctionsRef = useRef<SavedJunction[]>(junctions);
  const nodeToolRef = useRef<NodeTool>(nodeTool);
  const pathToolRef = useRef<PathTool>(pathTool);
  const trailsRef = useRef<SavedTrail[]>(trails);
  const trailToolRef = useRef<TrailTool>(trailTool);
  const trailSampleTokenRef = useRef(0);
  const trailReplayRef = useRef<TrailPaintCommand[]>([]);
  const trailGradeResultRef = useRef<Extract<TerrainGradeResponse, { ok: true }> | null>(null);
  const roadGradeResultRef = useRef<Extract<TerrainGradeResponse, { ok: true }> | null>(null);
  const trailCommandsRef = useRef<TrailPaintCommand[]>([]);
  const trailPendingUntilRef = useRef(0);
  const trailPreviewPathRef = useRef<[number, number][]>([]);
  const trailBrushCursorRef = useRef<[number, number] | null>(null);
  const selectTrailRef = useRef<(id: string) => void>(() => {});
  const selectLakeRef = useRef<(id: string) => void>(() => {});
  const selectStreamRef = useRef<(id: string) => void>(() => {});
  const selectedLakeIdRef = useRef<string | null>(selectedLakeId);
  const selectedStreamIdRef = useRef<string | null>(selectedStreamId);
  const lakeDepthOverridesRef = useRef(lakeDepthOverrides);
  const lakeNameOverridesRef = useRef(lakeNameOverrides);
  const streamWidthOverridesRef = useRef(streamWidthOverrides);
  const roadsRef = useRef<SavedRoad[]>(roads);
  const roadToolRef = useRef<RoadTool>(roadTool);
  const damsRef = useRef<SavedDam[]>(dams);
  const damToolRef = useRef<DamTool>(damTool);
  const selectedDamIdRef = useRef<string | null>(selectedDamId);
  const pondsRef = useRef<SavedPond[]>(ponds);
  const pondToolRef = useRef<PondTool>(pondTool);
  const selectedPondIdRef = useRef<string | null>(selectedPondId);
  const snowmakingNodesRef = useRef<SavedSnowmakingNode[]>(snowmakingNodes);
  const selectedSnowmakingNodeIdRef = useRef<string | null>(selectedSnowmakingNodeId);
  // Grading patch for whichever water structure is in review. It drives the
  // pre-build contour highlight and, for dams, the commit itself. Dams and
  // ponds cancel each other, so one slot is enough.
  const earthworkPatchRef = useRef<EarthworkTerrainPatch | null>(null);
  const brushWidthRef = useRef(brushWidthM);
  const renderQualityRef = useRef(settings.renderQuality);
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

  function setVisibleContours(record: TerrainRecordView): void {
    setTerrainContourData(mapRef.current, record, settings.units === 'imperial');
  }

  /** Paint the contours a pending grade would move in yellow. `null` clears. */
  function setEditedContours(segments: ArrayLike<number> | null): void {
    setGradedContourPreview(mapRef.current, segments,
      terrainRecordRef.current?.bounds, settings.units === 'imperial');
  }

  /** Whichever tool is holding a grade up for approval owns the contours on
   * screen: the map shows the ground as it *would* be, with the moved lines
   * highlighted, until the player builds or cancels. */
  function activeGradePreview(): {
    contourSegments: ArrayLike<number>; editedContourSegments: ArrayLike<number>;
  } | null {
    const trail = trailToolRef.current, road = roadToolRef.current;
    if (trail.phase === 'review' && trail.draft.gradingEnabled && trailGradeResultRef.current)
      return trailGradeResultRef.current;
    if (road.phase === 'review' && roadGradeResultRef.current) return roadGradeResultRef.current;
    return earthworkPatchRef.current;
  }

  function applyGradePreview(): void {
    const record = terrainRecordRef.current;
    if (!record) return;
    const preview = activeGradePreview();
    if (preview) {
      setVisibleContours({ ...record, contourSegments: Array.from(preview.contourSegments) });
      setEditedContours(preview.editedContourSegments);
    } else {
      setVisibleContours(record);
      setEditedContours(null);
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
  layersRef.current = layers;
  siteBoxRef.current = siteBox;
  siteModeRef.current = siteMode;
  is3DRef.current = is3D;
  toggle3DRef.current = toggle3D;
  liftsRef.current = lifts;
  liftToolRef.current = liftTool;
  trailsRef.current = trails;
  trailToolRef.current = trailTool;
  roadsRef.current = roads;
  roadToolRef.current = roadTool;
  damsRef.current = dams;
  damToolRef.current = damTool;
  selectedDamIdRef.current = selectedDamId;
  pondsRef.current = ponds;
  pondToolRef.current = pondTool;
  selectedPondIdRef.current = selectedPondId;
  snowmakingNodesRef.current = snowmakingNodes;
  selectedSnowmakingNodeIdRef.current = selectedSnowmakingNodeId;
  skiNodesRef.current = skiNodes;
  skiPathsRef.current = skiPaths;
  junctionsRef.current = junctions;
  nodeToolRef.current = nodeTool;
  pathToolRef.current = pathTool;
  selectedLakeIdRef.current = selectedLakeId;
  selectedStreamIdRef.current = selectedStreamId;
  lakeDepthOverridesRef.current = lakeDepthOverrides;
  lakeNameOverridesRef.current = lakeNameOverrides;
  streamWidthOverridesRef.current = streamWidthOverrides;
  brushWidthRef.current = brushWidthM;
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

  // Clicking a lift (on the map or in the list) opens its read-only detail.
  selectLiftRef.current = (id: string) => {
    liftSampleTokenRef.current++;
    transitionSelection({ kind: 'lift', id });
  };

  // Clicking a run opens its read-only detail.
  selectTrailRef.current = (id: string) => {
    trailSampleTokenRef.current++;
    transitionSelection({ kind: 'trail', id });
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
      : setupAnalysisLayers(map, terrainRecordRef.current, settings.units, coverDisplayRef.current,
        localImageryUrlRef.current, lakeNameOverridesRef.current,
        streamWidthOverridesRef.current);
    const prev = layersRef.current;
    const applied = fresh.map((f) => {
      const was = prev.find((p) => p.id === f.id);
      if (was && was.visible !== f.visible) {
        for (const lid of f.layerIds)
          map.setLayoutProperty(lid, 'visibility', was.visible ? 'visible' : 'none');
        return { ...f, visible: was.visible };
      }
      return f;
    });
    // setupAnalysisLayers bakes the cover opacity assuming the aerial is on;
    // reconcile it to the aerial's actual (possibly toggled-off) visibility.
    const aerialOn = applied.find((f) => f.id === 'satellite')?.visible ?? true;
    applyCoverOpacity(map, aerialOn);
    return applied;
  }

  function installSiteBoundaryLayers(map: maplibregl.Map): void {
    addSiteBoxLayers(map);
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

  /**
   * The legacy map contributions, one per family, ordered by the declared
   * bottom-to-top paint order. Feature controllers take these over in E1–E5;
   * today each closes over MapView's refs. Rebuilt per traversal so the
   * analysis family can hand its toggle model back to the caller.
   */
  function mapContributions(
    map: maplibregl.Map,
    onAnalysisToggles: (toggles: LayerToggle[]) => void = () => {},
  ): MapContribution[] {
    return orderContributions([
      { id: 'analysis', install: () => onAnalysisToggles(installAnalysisLayers(map)) },
      { id: 'site-boundary', install: () => installSiteBoundaryLayers(map) },
      {
        // Roads sit with the basemap context; their transient construction
        // overlay remains beneath ski runs and lifts.
        id: 'road',
        install: () => {
          addRoadLayers(map);
          addRoadDraftLayers(map);
          setRoadData(map, roadsRef.current);
          setRoadDraftData(map, roadDraftOf(roadToolRef.current));
        },
        setCaptureTransient: (hidden) => setRoadDraftData(map,
          hidden ? null : roadDraftOf(roadToolRef.current)),
      },
      {
        id: 'dam',
        install: () => {
          const rec = terrainRecordRef.current;
          addDamLayers(map);
          setDamData(map, damsRef.current, rec);
          setDamDraftData(map, damDraftOf(damToolRef.current), rec);
          setSelectedDam(map, selectedDamIdRef.current);
        },
        setCaptureTransient: (hidden) => hidden
          ? setDamDraftData(map, null)
          : setDamDraftData(map, damDraftOf(damToolRef.current), terrainRecordRef.current),
      },
      {
        id: 'pond',
        install: () => {
          const rec = terrainRecordRef.current;
          addPondLayers(map);
          setPondData(map, pondsRef.current, rec);
          setPondDraftData(map, pondDraftOf(pondToolRef.current), rec);
          setSelectedPond(map, selectedPondIdRef.current);
        },
        setCaptureTransient: (hidden) => hidden
          ? setPondDraftData(map, null)
          : setPondDraftData(map, pondDraftOf(pondToolRef.current), terrainRecordRef.current),
      },
      {
        id: 'ski-node-path',
        install: () => {
          addNodePathLayers(map);
          addNodePathDraftLayers(map);
          setNodePathData(map, skiNodesRef.current, skiPathsRef.current, junctionsRef.current);
        },
      },
      {
        // Runs beneath lifts (ski-map convention): trails first, lifts on top.
        id: 'trail',
        install: () => {
          addTrailLayers(map);
          setTrailData(map, trailsToGeoJSON(trailsRef.current));
          const tt = trailToolRef.current;
          setTrailDraftData(map, tt.phase === 'paint' || tt.phase === 'place-tail' || tt.phase === 'analyzing'
            ? draftToGeoJSON(tt.polygons)
            : tt.phase === 'review' ? draftToGeoJSON([], { parts: tt.draft.parts,
              difficulty: tt.draft.difficulty, name: tt.draft.name,
              infeasibleLines: tt.draft.infeasibleLines })
              : draftToGeoJSON([]));
          if (activeGradePreview()) applyGradePreview();
          setTrailPaintPreview(map, { path: [], cursor: null, brushWidthM: brushWidthRef.current,
            ...trailHeadPreview(trailToolRef.current) });
        },
        setCaptureTransient: (hidden) => {
          const trail = trailToolRef.current;
          if (hidden) {
            setTrailDraftData(map, draftToGeoJSON([]));
            setTrailPaintPreview(map, { path: [], cursor: null, brushWidthM: brushWidthRef.current });
            return;
          }
          setTrailDraftData(map, trail.phase === 'paint' || trail.phase === 'analyzing'
            ? draftToGeoJSON(trail.polygons)
            : trail.phase === 'review'
            ? draftToGeoJSON([], {
                parts: trail.draft.parts,
                difficulty: trail.draft.difficulty,
                name: trail.draft.name,
                infeasibleLines: trail.draft.infeasibleLines,
              })
            : draftToGeoJSON([]));
          setTrailPaintPreview(map, {
            path: trailPreviewPathRef.current,
            cursor: trailBrushCursorRef.current,
            brushWidthM: brushWidthRef.current,
            ...trailHeadPreview(trail),
          });
        },
      },
      {
        id: 'lift',
        install: () => {
          addLiftLayers(map);
          setLiftData(map, liftsToGeoJSON(liftsRef.current, draftLineOf(liftToolRef.current)));
        },
        setCaptureTransient: (hidden) => setLiftData(map,
          liftsToGeoJSON(liftsRef.current, hidden ? null : draftLineOf(liftToolRef.current))),
      },
      {
        // Nodes render last, on top of every other structure family.
        id: 'snowmaking',
        install: () => {
          addSnowmakingLayers(map);
          setSnowmakingData(map, snowmakingNodesRef.current);
          setSelectedSnowmakingNode(map, selectedSnowmakingNodeIdRef.current);
        },
      },
    ]);
  }

  // (Re)attach analysis layers + site box + 3D after any style (re)load. Shared
  // by the initial load and the light<->dark basemap swap. Reads live state from
  // refs and re-applies the current layer-visibility model.
  function reinitAfterStyle(map: maplibregl.Map) {
    tuneBasemap(map);
    // The loop lives here so the paint order is applied in exactly one place;
    // each family only knows how to install itself.
    let applied: LayerToggle[] = [];
    for (const contribution of mapContributions(map, (toggles) => { applied = toggles; })) {
      contribution.install();
    }
    setSelectedLake(map, selectedLakeIdRef.current);
    setSelectedStream(map, selectedStreamIdRef.current);
    // Toggles for the player's built structures, added once the lift/trail layers
    // exist above. Visibility is reconciled from the previous state so a restyle
    // (light↔dark) keeps whatever the player hid. Skipped while preparing (no
    // analysis layers either). Uses the generic handleToggle/setLayoutProperty path.
    if (packageStateRef.current !== 'preparing') {
      const prev = layersRef.current;
      applied = applied.map((entry) => entry.id === 'bm-roads'
        ? { ...entry, layerIds: [...entry.layerIds, ...ROAD_BUILT_LAYER_IDS] }
        : entry);
      const structures: { id: string; label: string; layerIds: string[] }[] = [
        { id: 'trails', label: 'Ski trails', layerIds: TRAIL_BUILT_LAYER_IDS },
        { id: 'lifts', label: 'Ski lifts', layerIds: LIFT_BUILT_LAYER_IDS },
        { id: 'dams', label: 'Snowmaking ponds', layerIds: DAM_BUILT_LAYER_IDS },
        { id: 'standalone-ponds', label: 'Standalone ponds', layerIds: POND_BUILT_LAYER_IDS },
        { id: 'snowmaking-network', label: 'Snowmaking network', layerIds: SNOWMAKING_BUILT_LAYER_IDS },
      ];
      for (const s of structures) {
        const wasVisible = prev.find((p) => p.id === s.id)?.visible ?? true;
        for (const lid of s.layerIds)
          if (map.getLayer(lid)) map.setLayoutProperty(lid, 'visibility', wasVisible ? 'visible' : 'none');
        applied = [...applied, { ...s, visible: wasVisible, section: 'Structures' }];
      }
    }
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

    // Click a built lift to open its edit panel. Delegated to the wide white
    // casing (bigger hit target than the 3px red line) plus the terminal dots;
    // both carry the lift `id`. Gated to idle play so it never steals the
    // terminal-placing clicks while a lift is being drawn. Delegated listeners
    // survive the light/dark style swap (they query at event time), so this is
    // registered once with the map.
    const allToolsIdle = () => toolCoordinator.snapshot.activeTool === null;
    const onHoverEnter = () => {
      if (allToolsIdle()) map.getCanvas().style.cursor = 'pointer';
    };
    const onHoverLeave = () => {
      if (allToolsIdle()) map.getCanvas().style.cursor = '';
    };

    // One declared chain rather than a guard re-accumulated in each handler: a
    // family yields whenever something that picks ahead of it has a feature
    // under the cursor, so exactly one handler acts on any overlap. Snowmaking
    // nodes render above every other structure family and so yield to nothing.
    const hitContributions = orderHitContributions([
      { id: 'snowmaking', layerIds: SNOWMAKING_HIT_LAYERS,
        select: (id) => selectSnowmakingNode(id) },
      { id: 'lift', layerIds: ['lift-line-casing', 'lift-terminals'],
        select: (id) => selectLiftRef.current(id) },
      { id: 'trail', layerIds: ['trail-fill'], select: (id) => selectTrailRef.current(id) },
      { id: 'dam', layerIds: DAM_HIT_LAYERS,
        select: (id) => transitionSelection({ kind: 'dam', id }) },
      { id: 'pond', layerIds: POND_HIT_LAYERS,
        select: (id) => transitionSelection({ kind: 'pond', id }) },
      { id: 'stream', layerIds: ['local-water-line-hit'],
        select: (id) => selectStreamRef.current(id) },
      { id: 'lake', layerIds: ['local-water-fill'], select: (id) => selectLakeRef.current(id) },
    ]);
    for (const contribution of hitContributions) {
      const guard = hitGuardLayers(contribution.id, hitContributions);
      const layerIds = [...contribution.layerIds];
      map.on('click', layerIds, (e: maplibregl.MapLayerMouseEvent) => {
        if (!allToolsIdle()) return;
        const above = guard.filter((layerId) => map.getLayer(layerId));
        if (above.length && map.queryRenderedFeatures(e.point, { layers: above }).length) return;
        const id = e.features?.[0]?.properties?.id;
        if (typeof id === 'string') contribution.select(id);
      });
      map.on('mouseenter', layerIds, onHoverEnter);
      map.on('mouseleave', layerIds, onHoverLeave);
    }

    return () => {
      warmAbortRef.current?.abort();
      setRenderConcurrency(1);
      mapInteractionLeaseRef.current?.dispose();
      map.remove();
      mapRef.current = null;
      setLayers([]);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapCanStart]);

  useEffect(() => {
    setSelectedLake(mapRef.current, selectedLakeId);
  }, [selectedLakeId]);

  useEffect(() => {
    setSelectedStream(mapRef.current, selectedStreamId);
  }, [selectedStreamId]);

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

  // Push lift + draft geometry into the map source whenever either changes.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setLiftData(map, liftsToGeoJSON(lifts, draftLineOf(liftTool)));
  }, [lifts, liftTool]);

  useEffect(() => {
    const map = mapRef.current;
    const record = terrainRecordRef.current;
    if (map && record) setLocalContextData(map, record, lakeNameOverrides, streamWidthOverrides);
  }, [lakeNameOverrides, streamWidthOverrides]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setRoadData(map, roads);
    setRoadDraftData(map, roadDraftOf(roadTool));
  }, [roads, roadTool]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setDamData(map, dams, terrainRecord);
    setDamDraftData(map, damDraftOf(damTool), terrainRecord);
    setSelectedDam(map, selectedDamId);
  }, [dams, damTool, selectedDamId, terrainRecord]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setPondData(map, ponds, terrainRecord);
    setPondDraftData(map, pondDraftOf(pondTool), terrainRecord);
    setSelectedPond(map, selectedPondId);
  }, [ponds, pondTool, selectedPondId, terrainRecord]);

  // Intakes follow their water: build a pond, get a node; delete or
  // de-designate it, lose the node. Kept separate from the map-data-push
  // effect below — reconciliation is pure state, independent of whether a
  // map exists yet.
  useEffect(() => {
    setSnowmakingNodes((prev) => reconcileSnowmakingNodes(prev, dams, ponds));
  }, [dams, ponds]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setSnowmakingData(map, snowmakingNodes);
    setSelectedSnowmakingNode(map, selectedSnowmakingNodeId);
  }, [snowmakingNodes, selectedSnowmakingNodeId]);

  // Saved trails are stable while painting; drafts use their own source.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setTrailData(map, trailsToGeoJSON(trails));
  }, [trails]);

  const draftPolygons = trailTool.phase === 'paint' || trailTool.phase === 'place-tail' ||
    trailTool.phase === 'analyzing' ? trailTool.polygons : null;
  const reviewDraft = trailTool.phase === 'review' ? trailTool.draft : null;
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (draftPolygons) setTrailDraftData(map, draftToGeoJSON(draftPolygons));
    else if (reviewDraft) setTrailDraftData(map, draftToGeoJSON([], { parts: reviewDraft.parts,
      difficulty: reviewDraft.difficulty, name: reviewDraft.name,
      infeasibleLines: reviewDraft.infeasibleLines }));
    else setTrailDraftData(map, draftToGeoJSON([]));
  // eslint-disable-next-line react-hooks/exhaustive-deps -- Only rendered draft fields should retrigger source synchronization; review errors do not affect map data.
  }, [draftPolygons, reviewDraft?.parts, reviewDraft?.difficulty, reviewDraft?.name,
    reviewDraft?.infeasibleLines]);

  // Keep brush geometry and the transient trailhead/candidate marker in sync.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setTrailPaintPreview(map, {
      path: trailTool.phase === 'paint' ? trailPreviewPathRef.current : [],
      cursor: trailTool.phase === 'paint' ? trailBrushCursorRef.current : null,
      brushWidthM,
      ...trailHeadPreview(trailTool),
    });
  }, [brushWidthM, trailTool]);

  // Stage one of Create Trail: choose one exact graph target, then immediately
  // seed the painter at it. Invalid clicks leave the prompt active.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || trailTool.phase !== 'place-head') return;
    const canvas = map.getCanvas();
    const interaction = acquireMapInteractions('trail', map, { cursor: 'crosshair' });
    const candidateAt = (e: maplibregl.MapMouseEvent) => nearestTrailHeadAnchor(
      [e.lngLat.lng, e.lngLat.lat], liftsRef.current, trailsRef.current, ANCHOR_PICK_M);
    const onMove = (e: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(e);
      setTrailTool((tool) => tool.phase === 'place-head' && !sameTrailHeadAnchor(tool.candidate, candidate)
        ? { ...tool, candidate, error: candidate ? null : tool.error } : tool);
    };
    const onClick = (e: maplibregl.MapMouseEvent) => {
      const anchor = candidateAt(e);
      if (!anchor) {
        setTrailTool((tool) => tool.phase === 'place-head' ? { ...tool,
          candidate: null,
          error: 'Choose the top terminal of a lift or an existing trail centerline.' } : tool);
        return;
      }
      beginTrailPainting(anchor);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') cancelTrailTool(); };
    const onLeave = () => setTrailTool((tool) => tool.phase === 'place-head'
      ? { ...tool, candidate: null } : tool);
    map.on('mousemove', onMove);
    map.on('click', onClick);
    canvas.addEventListener('mouseleave', onLeave);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('mousemove', onMove);
      map.off('click', onClick);
      canvas.removeEventListener('mouseleave', onLeave);
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- Tool callbacks intentionally read live refs; resubscribe only when the phase changes.
  }, [trailTool.phase]);

  // Stage three: after brushing, choose a destination already covered by the
  // same painted component as the immutable trailhead seed.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || trailTool.phase !== 'place-tail') return;
    const interaction = acquireMapInteractions('trail', map, { cursor: 'crosshair' });
    const candidateAt = (e: maplibregl.MapMouseEvent) => nearestTrailTailAnchor(
      [e.lngLat.lng, e.lngLat.lat], liftsRef.current, trailsRef.current, ANCHOR_PICK_M);
    const isConnected = (tool: Extract<TrailTool, { phase: 'place-tail' }>, point: [number, number]) =>
      haversineMeters(tool.anchor.point, point) >= 8 &&
      tool.polygons.some((polygon) => trailPartContains({ polygon }, tool.anchor.point) &&
        trailPartContains({ polygon }, point));
    const onMove = (e: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(e);
      setTrailTool((tool) => tool.phase === 'place-tail'
        ? { ...tool, candidate: candidate && isConnected(tool, candidate.point) ? candidate : null,
          error: candidate && !isConnected(tool, candidate.point)
            ? 'The painted trail must reach this endpoint in one connected footprint.' : tool.error }
        : tool);
    };
    const onClick = (e: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(e);
      const current = trailToolRef.current;
      if (!candidate || current.phase !== 'place-tail' || !isConnected(current, candidate.point)) {
        setTrailTool((tool) => tool.phase === 'place-tail' ? { ...tool, candidate: null,
          error: 'Choose a lift base or trail centerline reached by the painted footprint.' } : tool);
        return;
      }
      setTrailTool({ phase: 'analyzing', polygons: current.polygons, areaM2: current.areaM2,
        anchor: current.anchor, tailAnchor: candidate });
      trailPaint.post({ type: 'finish' });
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') backToTrailPaint(); };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); };
  }, [trailTool.phase, trailPaint]);

  /** Sample both terminal elevations for the review draft. Token-guarded so a
   *  cancel/confirm/redraw discards in-flight results. */
  function sampleDraftElevations(points: [[number, number], [number, number]]) {
    const map = mapRef.current;
    const z = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++liftSampleTokenRef.current;
    setLiftTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'pending' } } : t
    );
    void Promise.all(points.map(([lng, lat]) => samplePlanningTerrainOrNull(lng, lat, z))).then(
      (samples) => {
        if (token !== liftSampleTokenRef.current) return;
        // Both terminals are load-bearing here — a lift's vertical *is* the
        // difference between them, so there is nothing sane to interpolate from
        // if one is missing. Unlike a trail profile, a gap is a real failure.
        const [a, b] = samples;
        if (!a || !b) {
          setLiftTool((t) =>
            t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'error' } } : t
          );
          return;
        }
        setLiftTool((t) =>
          t.phase === 'review'
            ? {
                phase: 'review',
                draft: {
                  ...t.draft,
                  elev: [a.elevation, b.elevation],
                  elevStatus: 'ok',
                },
              }
            : t
        );
      },
      () => {
        if (token !== liftSampleTokenRef.current) return;
        setLiftTool((t) =>
          t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'error' } } : t
        );
      }
    );
  }

  // Lift drawing: click-click placement while the tool is armed/anchored.
  // dragPan stays enabled (unlike the site tool) so the user can pan and zoom
  // between placing the two terminals of a long lift; only double-click zoom
  // is suspended, since finishing a line involves two quick clicks.
  useEffect(() => {
    const map = mapRef.current;
    const phase = liftTool.phase;
    if (!map || (phase !== 'armed' && phase !== 'anchored')) return;

    const interaction = acquireMapInteractions('lift', map, {
      cursor: 'crosshair',
      doubleClickZoomEnabled: false,
    });

    const onClick = (e: maplibregl.MapMouseEvent) => {
      const p: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      const t = liftToolRef.current;
      if (t.phase === 'armed') {
        setLiftTool({ phase: 'anchored', a: p, cursor: null });
      } else if (t.phase === 'anchored') {
        if (haversineMeters(t.a, p) < MIN_LIFT_M) return; // ignore double-click jitter
        const points: [[number, number], [number, number]] = [t.a, p];
        setLiftTool({
          phase: 'review',
          draft: {
            points,
            elev: [null, null],
            elevStatus: 'pending',
            chairSize: FIXED_GRIP_SPEC.defaultChairSize,
            status: 'planning',
            name: nextLiftName(liftsRef.current),
          },
        });
        sampleDraftElevations(points);
      }
    };
    const onMove = (e: maplibregl.MapMouseEvent) => {
      const t = liftToolRef.current;
      if (t.phase !== 'anchored') return;
      setLiftTool({ ...t, cursor: [e.lngLat.lng, e.lngLat.lat] });
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') cancelLiftTool();
    };

    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liftTool.phase]);

  // Road drawing: successive clicks append centerline vertices. The map keeps
  // its normal pan/zoom navigation; the small draft source follows the cursor.
  useEffect(() => {
    const map = mapRef.current;
    const phase = roadTool.phase;
    if (!map || (phase !== 'armed' && phase !== 'drawing')) return;
    const interaction = acquireMapInteractions('road', map, { cursor: 'crosshair' });

    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = roadToolRef.current;
      if (current.phase === 'armed') {
        setRoadTool({ phase: 'drawing', roadType: current.roadType, points: [point], cursor: null });
      } else if (current.phase === 'drawing') {
        const last = current.points.at(-1);
        if (last && haversineMeters(last, point) < 1) return;
        setRoadTool({ ...current, points: [...current.points, point], cursor: null });
      }
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const current = roadToolRef.current;
      if (current.phase === 'drawing') setRoadTool({ ...current,
        cursor: [event.lngLat.lng, event.lngLat.lat] });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelRoadTool();
      else if (event.key === 'Backspace') { event.preventDefault(); undoRoadPoint(); }
      else if (event.key === 'Enter') finishRoadRoute();
    };

    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [roadTool.phase]);

  // Standalone ponds use a freehand polygon boundary. The full-pool elevation
  // is entered in review and the terrain-integrated capacity updates from it.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || (pondTool.phase !== 'armed' && pondTool.phase !== 'drawing')) return;
    const interaction = acquireMapInteractions('pond', map, { cursor: 'crosshair' });
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = pondToolRef.current;
      const bounds = terrainRecordRef.current?.bounds;
      if (!bounds || point[0] < bounds.west || point[0] > bounds.east ||
        point[1] < bounds.south || point[1] > bounds.north) {
        setPondTool(current.phase === 'drawing' ? { ...current,
          error: 'Keep the pond boundary inside the available terrain.' } :
          { phase: 'armed', error: 'Choose a point inside the available terrain.' });
        return;
      }
      if (current.phase === 'armed') setPondTool({ phase: 'drawing', points: [point], cursor: null, error: null });
      else if (current.phase === 'drawing') {
        const last = current.points.at(-1);
        if (last && haversineMeters(last, point) < 1) return;
        setPondTool({ ...current, points: [...current.points, point], cursor: null, error: null });
      }
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const current = pondToolRef.current;
      if (current.phase === 'drawing') setPondTool({ ...current,
        cursor: [event.lngLat.lng, event.lngLat.lat] });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelPondTool();
      else if (event.key === 'Backspace') { event.preventDefault(); undoPondPoint(); }
      else if (event.key === 'Enter') finishPondBoundary();
    };
    map.on('click', onClick); map.on('mousemove', onMove); window.addEventListener('keydown', onKey);
    return () => { map.off('click', onClick); map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey); interaction.release(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pondTool.phase]);

  // Dam drawing: the first bank fixes the crest elevation; the opposite bank
  // snaps to the same DEM contour before pond analysis runs off the UI thread.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || (damTool.phase !== 'armed' && damTool.phase !== 'anchored')) return;
    const interaction = acquireMapInteractions('dam', map, { cursor: 'crosshair' });
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const current = damToolRef.current;
      const record = terrainRecordRef.current;
      if (current.phase !== 'anchored' || !record) return;
      const cursor: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const snapped = snapDamEndpoint(record, current.first, cursor);
      setDamTool({ ...current, cursor: snapped, error: snapped ? null : 'No matching crest contour near the cursor.' });
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const current = damToolRef.current;
      const record = terrainRecordRef.current;
      if (!record) return;
      if (current.phase === 'armed') {
        const first: [number, number] = [event.lngLat.lng, event.lngLat.lat];
        const bounds = record.bounds;
        if (!bounds || first[0] < bounds.west || first[0] > bounds.east ||
          first[1] < bounds.south || first[1] > bounds.north) {
          setDamTool({ phase: 'armed', error: 'Choose a bank inside the resort terrain boundary.' });
          return;
        }
        // The core sample grid, not the surround-blended resort sampler: the
        // snap and the flood both read this grid, and near the box edge the two
        // disagree by enough to lift full pool off the bank that was clicked.
        const elevationM = damCrestElevationAt(record, first);
        if (elevationM == null) {
          setDamTool({ phase: 'armed', error: 'Choose a point within the available terrain.' });
          return;
        }
        setDamTool({ phase: 'anchored', first, crestElevationM: elevationM, cursor: null, error: null });
        return;
      }
      if (current.phase !== 'anchored' || !current.cursor || !record.bounds) return;
      const points: [[number, number], [number, number]] = [current.first, current.cursor];
      setDamTool({ phase: 'analyzing', points, crestElevationM: current.crestElevationM });
      damAnalysis.run({
        heights: Float32Array.from(record.sampleHeights),
        gridSize: record.sampleGridSize, bounds: record.bounds,
        points, crestElevationM: current.crestElevationM,
        streams: (record.vectorFeatures?.waterLines ?? []).map((stream) => ({ ...stream,
          widthM: streamWidthOverridesRef.current[stream.id] ?? stream.widthM })),
      }, {
        onResult: (analysis) => {
          // Trace the embankment's contours and footprint here rather than in the
          // worker: the patch has to be stamped against the live package so the
          // preview the player approves is exactly what gets committed.
          const patch = earthworkTerrainPatch(record, analysis.patchIndices, analysis.patchHeights);
          earthworkPatchRef.current = patch;
          setDamTool({ phase: 'review', error: null, draft: {
            name: nextDamName(damsRef.current), points, crestElevationM: current.crestElevationM,
            streamId: analysis.crossing.stream.id,
            streamName: analysis.crossing.stream.name ?? `Unnamed ${analysis.crossing.stream.waterClass}`,
            sourceWidthM: analysis.sourceWidthM, inflowM3s: analysis.inflowM3s,
            pondRings: analysis.pondRings, areaM2: analysis.areaM2,
            averageDepthM: analysis.averageDepthM, capacityM3: analysis.capacityM3,
            averageDamHeightM: analysis.averageDamHeightM,
            maxDamHeightM: analysis.maxDamHeightM,
            damCrestElevationM: analysis.damCrestElevationM,
            crestRing: analysis.crestRing,
            footprintRings: largestFootprint(patch.disturbancePolygons),
            builtLengthM: analysis.builtLengthM,
            disturbedAreaM2: analysis.disturbedAreaM2,
            earthwork: analysis.earthwork,
          } });
          applyGradePreview();
        },
        onError: (error) => {
          setDamTool({ phase: 'anchored', first: points[0],
            crestElevationM: current.crestElevationM, cursor: null, error });
        },
      });
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') cancelDamTool(); };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [damTool.phase]);

  // Built nodes + paths pushed to their own source, the same way lifts are.
  useEffect(() => {
    const map = mapRef.current;
    if (map) setNodePathData(map, skiNodes, skiPaths, junctions);
  }, [skiNodes, skiPaths, junctions, terrainRecord]);

  // The in-progress connector line follows the cursor between clicks, and every
  // tool that has to attach to a run shows the point it would take as an amber
  // ring — the same "here is what you are about to pick" the trailhead anchor
  // gives you. A tool with no line still gets a draft so its ring has somewhere
  // to render.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const highlight = snapHover ? [snapHover] : [];
    // Add-node picks a spot on one click and commits on another, so the pick
    // has to stay on the map in between — the hover ring moves off with the
    // cursor, and the panel can name the run but not point at the metre.
    const pick = nodeTool.phase === 'add' ? nodeTool.candidate?.point ?? null : null;
    const draft: NodePathDraft | null =
      pathTool.phase === 'drawing'
        ? { points: pathTool.points, cursor: pathTool.cursor, highlight }
        : pathTool.phase === 'review'
          ? { points: pathTool.points, cursor: null, highlight: [] }
          : pathTool.phase === 'armed' || nodeTool.phase !== 'idle'
            ? { points: [], cursor: null, highlight, pick }
            : null;
    setNodePathDraftData(map, draft);
  }, [pathTool, nodeTool, snapHover]);

  // Node editing. A click only ever picks a target and previews what will happen
  // to it — `confirmAddNode`/`confirmRemoveNode` do the committing. Elevation is
  // never sampled here: `splitTrailAt` interpolates the new node's height from
  // the run's own profile, which is both exact and offline-safe.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || nodeTool.phase === 'idle') return;
    const phase = nodeTool.phase;
    const interaction = acquireMapInteractions('ski-node', map, { cursor: 'crosshair' });
    // Add snaps onto a run's centerline; remove snaps onto an existing node.
    // Both preview the snap under the cursor so you aim at the ring, not at the
    // pixel — a node 20 m off the run it was meant to split is worse than a
    // missed click, because it looks like it worked.
    const snapAt = (point: [number, number]) => phase === 'add'
      ? trailAnchorAt(point)?.point ?? null
      : junctionAt(point)?.point ?? null;
    const onMove = (e: maplibregl.MapMouseEvent) => {
      setSnapHover(snapAt([e.lngLat.lng, e.lngLat.lat]));
    };
    const onClick = (e: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      if (phase === 'add') {
        const candidate = trailAnchorAt(raw);
        setNodeTool({ phase: 'add', candidate,
          error: candidate ? null : 'Nodes sit on a run — click along one you have painted.' });
        return;
      }
      const junction = junctionAt(raw);
      if (!junction) {
        setNodeTool({ phase: 'remove', junctionId: null, error: 'No node there — click one of the dots on a run.' });
        return;
      }
      const check = canRemoveJunction(trailsRef.current, junctionsRef.current,
        skiPathsRef.current, junction.id);
      setNodeTool({ phase: 'remove', junctionId: junction.id,
        error: check.ok ? null : check.reason });
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') cancelNodeTool();
    };
    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      interaction.release();
      setSnapHover(null);
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- Tool callbacks use the stable coordinator plus live refs; only phase changes resubscribe map listeners.
  }, [nodeTool.phase]);

  // Path drawing, modelled on the road tool: click to append, Backspace to
  // undo, Enter to finish, Escape to cancel. The difference is that the FIRST
  // and LAST clicks must land on a valid anchor target — a connector that
  // connects nothing has no purpose.
  useEffect(() => {
    const map = mapRef.current;
    const phase = pathTool.phase;
    if (!map || (phase !== 'armed' && phase !== 'drawing')) return;
    const interaction = acquireMapInteractions('ski-path', map, { cursor: 'crosshair' });

    const onClick = (e: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      const t = pathToolRef.current;
      // A click within reach of a run takes the run's centerline instead of the
      // raw point. That is what makes the ends *connect*: `confirmPath` splits
      // each run at the endpoint it was given, so an endpoint a few metres off
      // the centerline would put the new node beside the run rather than on it.
      const snapped = trailAnchorAt(raw);
      if (t.phase === 'armed') {
        if (!snapped) return; // the start must attach to a run
        setPathTool({ phase: 'drawing', points: [snapped.point], cursor: null, from: snapped });
        return;
      }
      if (t.phase !== 'drawing') return;
      const point = snapped ? snapped.point : raw;
      const last = t.points.at(-1);
      if (last && haversineMeters(last, point) < 1) return;
      setPathTool({ ...t, points: [...t.points, point], cursor: null });
    };
    const onMove = (e: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      setSnapHover(trailAnchorAt(raw)?.point ?? null);
      const t = pathToolRef.current;
      if (t.phase === 'drawing') setPathTool({ ...t, cursor: raw });
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') cancelPathTool();
      else if (e.key === 'Backspace') { e.preventDefault(); undoPathPoint(); }
      else if (e.key === 'Enter') finishPathRoute();
    };
    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      interaction.release();
      setSnapHover(null);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathTool.phase]);

  /**
   * Sample the shape-derived centerlines, orient each top→bottom, and grade.
   *
   * Both endpoints arrive as arguments and neither is recovered from
   * `trailToolRef`. The caller in the worker's `analysis` branch calls this in
   * the same tick as the `setTrailTool` that enters review, and with an offline
   * package every sample resolves as a microtask — so the continuation below
   * runs before React has committed that render and the ref would still read
   * `analyzing`. Requiring `tail` here is what stops that from being possible.
   */
  function sampleTrailElevations(parts: DraftTrail['parts'], anchor: AnchorRef, tail: AnchorRef) {
    const map = mapRef.current;
    const z = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++trailSampleTokenRef.current;
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'pending', elevError: null } } : t
    );
    const fail = (message: string) => {
      setTrailTool((t) => t.phase === 'review'
        ? { phase: 'review', draft: { ...t.draft, elevStatus: 'error', elevError: message } } : t);
    };
    void Promise.all(parts.map(async (part) => {
      const centerlineElevM = await sampleProfile(part.centerline, z);
      return centerlineElevM ? { ...part, centerlineElevM } : null;
    })).then(
      (sampled) => {
        if (token !== trailSampleTokenRef.current) return;
        const resolvedParts = sampled.filter((p): p is SavedTrailPart => p !== null);
        if (resolvedParts.length !== sampled.length) {
          fail('No terrain data covers this run. Check that the resort package finished downloading.');
          return;
        }
        const pinnedParts = pinTrailEndpoints(resolvedParts, anchor.point, tail.point);
        if (!pinnedParts) {
          fail('The trailhead and trail end are not joined by one painted footprint.');
          return;
        }
        const stats = trailPartsStats(pinnedParts);
        const recommended = difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg);
        setTrailTool((t) =>
          t.phase === 'review'
            ? {
                phase: 'review',
                draft: {
                  ...t.draft,
                  parts: pinnedParts,
                  ungradedParts: pinnedParts,
                  elevStatus: 'ok',
                  elevError: null,
                  difficulty: recommended,
                  anchor,
                  tailAnchor: tail,
                },
              }
            : t
        );
      },
      (error: unknown) => {
        if (token !== trailSampleTokenRef.current) return;
        fail(error instanceof Error ? error.message : 'Elevation unavailable.');
      }
    );
  }

  // Pointer movement only updates the small preview source. Completed strokes
  // are transferred to the worker; React never receives the growing path.
  const trailDrawing = trailTool.phase === 'paint';
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !trailDrawing) return;

    const canvas = map.getCanvas();
    const interaction = acquireMapInteractions('trail', map, {
      cursor: 'none',
      dragPanEnabled: false,
      doubleClickZoomEnabled: false,
    });
    const renderPreview = () => setTrailPaintPreview(map, { path: trailPreviewPathRef.current,
      cursor: trailBrushCursorRef.current, brushWidthM: brushWidthRef.current,
      ...trailHeadPreview(trailToolRef.current) });
    renderPreview();

    let painting = false;
    let path: [number, number][] = [];
    let previewPath: [number, number][] = [];
    let previewRaf = 0;
    let lastMetricAt = 0;
    const drawPreview = () => {
      previewRaf = 0;
      renderPreview();
    };
    const schedulePreview = () => { if (!previewRaf) previewRaf = requestAnimationFrame(drawPreview); };
    const finish = () => {
      painting = false;
      if (path.length === 1) path.push(path[0]); // a click is a valid brush dab
      const tool = trailToolRef.current;
      const mode = tool.phase === 'paint' ? tool.mode : 'paint';
      const command: TrailPaintCommand = { mode, path: path.slice(),
        restoreSeed: mode === 'erase' && tool.phase === 'paint' ? tool.anchor.point : undefined };
      trailCommandsRef.current.push(command);
      submitTrailCommand(command);
    };

    const down = (e: maplibregl.MapMouseEvent) => {
      const tool = trailToolRef.current;
      if (tool.phase !== 'paint' || tool.pending) return;
      const raw: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      painting = true;
      path = [raw];
      previewPath = path;
      trailPreviewPathRef.current = previewPath;
      trailBrushCursorRef.current = path[0];
      schedulePreview();
    };
    const move = (e: maplibregl.MapMouseEvent) => {
      const raw: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      const p = raw;
      trailBrushCursorRef.current = p;
      if (!painting) { schedulePreview(); return; }
      const gap = Math.max(0.5, Math.min(2, brushWidthRef.current / 16));
      if (haversineMeters(path[path.length - 1], p) < gap) { schedulePreview(); return; }
      path.push(p);
      const lastPreview = previewPath[previewPath.length - 1];
      if (!lastPreview || Math.hypot(map.project(lastPreview).x - map.project(p).x,
        map.project(lastPreview).y - map.project(p).y) >= 2) {
        previewPath = [...previewPath, p];
        trailPreviewPathRef.current = previewPath;
      }
      schedulePreview();
      const now = performance.now();
      if (now - lastMetricAt >= 100) {
        lastMetricAt = now;
        let length = 0;
        for (let i = 1; i < path.length; i++) length += haversineMeters(path[i - 1], path[i]);
        const swept = Math.PI * (brushWidthRef.current / 2) ** 2 + length * brushWidthRef.current;
        setTrailTool((t) => t.phase === 'paint' ? { ...t,
          activeAreaM2: t.mode === 'paint' ? t.areaM2 + swept : Math.max(0, t.areaM2 - swept) } : t);
      }
    };
    const up = () => {
      if (painting) finish();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (painting) {
          painting = false; path = []; previewPath = []; trailPreviewPathRef.current = [];
          renderPreview();
        }
        else cancelTrailTool();
      }
    };
    const leave = () => {
      trailBrushCursorRef.current = null;
      if (!painting) { previewPath = []; trailPreviewPathRef.current = []; }
      renderPreview();
    };

    map.on('mousedown', down);
    map.on('mousemove', move);
    window.addEventListener('mouseup', up);
    canvas.addEventListener('mouseleave', leave);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('mousedown', down);
      map.off('mousemove', move);
      window.removeEventListener('mouseup', up);
      canvas.removeEventListener('mouseleave', leave);
      if (previewRaf) cancelAnimationFrame(previewRaf);
      trailPreviewPathRef.current = [];
      trailBrushCursorRef.current = null;
      setTrailPaintPreview(map, { path: [], cursor: null, brushWidthM: brushWidthRef.current,
        ...trailHeadPreview(trailToolRef.current) });
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trailDrawing]);

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
    if (siteModeRef.current === 'selecting') return;
    if (!toolCoordinator.activate('road')) return;
    clearSelectionState();
    setOpenDock('infrastructure');
    setRoadTool({ phase: 'armed', roadType });
  }

  function armDamTool() {
    if (siteModeRef.current === 'selecting' || !terrainRecordRef.current) return;
    if (!toolCoordinator.activate('dam')) return;
    clearSelectionState();
    setOpenDock('snowmaking');
    setDamTool({ phase: 'armed', error: null });
  }

  function cancelDamTool() {
    damAnalysis.cancel();
    setDamTool({ phase: 'idle' });
    earthworkPatchRef.current = null;
    applyGradePreview();
    if (mapRef.current) setDamDraftData(mapRef.current, null);
    toolCoordinator.release('dam');
  }

  function patchDamDraft(patch: Partial<DraftDam>) {
    setDamTool((current) => current.phase === 'review'
      ? { ...current, draft: { ...current.draft, ...patch } } : current);
  }

  /** Build the dam: cut its embankment into the terrain package, then record
   * the structure and fell whatever stood on the ground it moved. */
  async function confirmDam() {
    const current = damToolRef.current;
    if (current.phase !== 'review') return;
    const draft = current.draft;
    const patch = earthworkPatchRef.current;
    // Ownership is taken synchronously here, so a second confirmation in the
    // same tick is rejected rather than building this dam twice.
    await terrain.runConstruction('dam', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        if (!patch) throw new Error('This embankment has no grading design. Redraw the dam.');
        const commit = terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record, patch), kind: 'elevation' });
        if (!commit.ok) throw new Error('The terrain changed while building. Redraw the dam.');
        earthworkPatchRef.current = null;
        setDams((existing) => [...existing, { ...draft, id: genId(),
          name: draft.name.trim() || nextDamName(existing), terrainGraded: true,
          createdAt: new Date().toISOString() }]);
        setDamTool({ phase: 'idle' });
        toolCoordinator.release('dam');
        // The embankment and its toe are bare fill now: fell what stood there.
        await clearCover(patch.disturbancePolygons.map((polygon) => ({ polygon })));
      } catch (error) {
        setDamTool((active) => active.phase === 'review' ? { ...active,
          error: error instanceof Error ? error.message : 'Unable to build this dam.' } : active);
      }
    });
  }

  function selectDam(id: string) {
    transitionSelection({ kind: 'dam', id });
  }

  function deleteDam(id: string) {
    setDams((existing) => existing.filter((dam) => dam.id !== id));
    setSelectedDamId((selected) => selected === id ? null : selected);
  }

  function armPondTool() {
    if (siteModeRef.current === 'selecting' || !terrainRecordRef.current) return;
    if (!toolCoordinator.activate('pond')) return;
    clearSelectionState();
    setOpenDock('snowmaking'); setPondTool({ phase: 'armed', error: null });
  }

  function cancelPondTool() {
    setPondTool({ phase: 'idle' });
    earthworkPatchRef.current = null;
    applyGradePreview();
    if (mapRef.current) setPondDraftData(mapRef.current, null);
    toolCoordinator.release('pond');
  }

  /** Re-trace the contours the pond as currently designed would leave behind,
   * so the player sees the reshaped ground before committing to it. */
  function previewPondGrade(topElevationM: number, excavationDepthM: number,
    boundary: [number, number][], areaM2: number): void {
    const record = terrainRecordRef.current;
    const design = record && designPondEarthwork(record, boundary,
      { topElevationM, excavationDepthM, poolAreaM2: areaM2 });
    earthworkPatchRef.current = design && record ? pondTerrainPatch(record, design) : null;
    applyGradePreview();
  }

  function undoPondPoint() {
    const current = pondToolRef.current;
    if (current.phase !== 'drawing') return;
    if (current.points.length <= 1) setPondTool({ phase: 'armed', error: null });
    else setPondTool({ ...current, points: current.points.slice(0, -1), cursor: null, error: null });
  }

  function finishPondBoundary() {
    const current = pondToolRef.current, record = terrainRecordRef.current;
    if (current.phase !== 'drawing' || current.points.length < 3 || !record) return;
    const topElevationM = suggestedPondTopElevationM(record, current.points);
    if (topElevationM == null) {
      setPondTool({ ...current, cursor: null, error: 'The pond boundary does not have valid terrain coverage.' });
      return;
    }
    const outcome = analyzeStandalonePond(record, current.points, topElevationM);
    if (!outcome.ok) { setPondTool({ ...current, cursor: null, error: outcome.error }); return; }
    setPondTool({ phase: 'review', error: null, draft: {
      name: nextPondName(pondsRef.current), isSnowmaking: true, ...outcome.result,
    } });
    previewPondGrade(topElevationM, outcome.result.excavationDepthM,
      outcome.result.boundary, outcome.result.areaM2);
  }

  function patchPondDraft(patch: Partial<DraftPond>) {
    setPondTool((current) => current.phase === 'review'
      ? { ...current, draft: { ...current.draft, ...patch } } : current);
  }

  /** Re-solve the pool, the berm and the earthwork bill after any design change. */
  function redesignPond(topElevationM: number, excavationDepthM: number) {
    const current = pondToolRef.current, record = terrainRecordRef.current;
    if (current.phase !== 'review' || !record) return;
    const points = current.draft.boundary.slice(0, -1);
    const outcome = analyzeStandalonePond(record, points, topElevationM, excavationDepthM);
    if (!outcome.ok) {
      setPondTool({ phase: 'review', error: outcome.error,
        draft: { ...current.draft, topElevationM, excavationDepthM } });
      // A design that will not build has no ground change worth showing.
      earthworkPatchRef.current = null;
      applyGradePreview();
      return;
    }
    setPondTool({ phase: 'review', draft: { ...current.draft, ...outcome.result }, error: null });
    previewPondGrade(topElevationM, outcome.result.excavationDepthM,
      outcome.result.boundary, outcome.result.areaM2);
  }

  function changePondElevation(topElevationM: number) {
    const current = pondToolRef.current;
    if (current.phase !== 'review') return;
    redesignPond(topElevationM, current.draft.excavationDepthM ?? 0);
  }

  function changePondExcavation(excavationDepthM: number) {
    const current = pondToolRef.current;
    if (current.phase !== 'review') return;
    redesignPond(current.draft.topElevationM, excavationDepthM);
  }

  async function confirmPond() {
    const current = pondToolRef.current;
    if (current.phase !== 'review' || current.error) return;
    const draft = current.draft;
    await terrain.runConstruction('pond', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        // Re-solve against the live terrain rather than trusting the review pass,
        // so a grade committed by another tool since then cannot be overwritten.
        const design = designPondEarthwork(record, draft.boundary, {
          topElevationM: draft.topElevationM,
          excavationDepthM: draft.excavationDepthM ?? 0,
          poolAreaM2: draft.areaM2,
        });
        if (!design) throw new Error('The pond could not be graded into this terrain.');
        if (design.truncated || design.maxBermHeightM > MAX_POND_BERM_HEIGHT_M)
          throw new Error('The berm no longer fits this terrain. Adjust the top of pond and try again.');
        const patch = pondTerrainPatch(record, design);
        const commit = terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record, patch), kind: 'elevation' });
        if (!commit.ok) throw new Error('The terrain changed while building. Redraw the pond.');
        earthworkPatchRef.current = null;
        setPonds((existing) => [...existing, { ...draft, id: genId(),
          name: draft.name.trim() || nextPondName(existing),
          crestElevationM: design.crestElevationM,
          excavationDepthM: design.excavationDepthM,
          maxBermHeightM: design.maxBermHeightM,
          bermLengthM: design.bermLengthM,
          maxCutDepthM: design.maxCutDepthM,
          disturbedAreaM2: design.disturbedAreaM2,
          terrainGraded: true,
          earthwork: { cutM3: design.cutM3, fillM3: design.fillM3, balanceM3: design.balanceM3 },
          createdAt: new Date().toISOString() }]);
        setPondTool({ phase: 'idle' });
        toolCoordinator.release('pond');
        // The pool and its berm are bare ground now: fell whatever stood on them.
        await clearCover(patch.disturbancePolygons.map((polygon) => ({ polygon })));
      } catch (error) {
        setPondTool((active) => active.phase === 'review' ? { ...active,
          error: error instanceof Error ? error.message : 'Unable to build this pond.' } : active);
      }
    });
  }

  function selectPond(id: string) {
    transitionSelection({ kind: 'pond', id });
  }

  function deletePond(id: string) {
    setPonds((existing) => existing.filter((pond) => pond.id !== id));
    setSelectedPondId((selected) => selected === id ? null : selected);
  }

  function changePondSnowmaking(id: string, isSnowmaking: boolean) {
    setPonds((existing) => existing.map((pond) => pond.id === id ? { ...pond, isSnowmaking } : pond));
  }

  function selectSnowmakingNode(id: string) {
    transitionSelection({ kind: 'snowmaking-node', id });
  }

  function renameSnowmakingNode(id: string, name: string) {
    setSnowmakingNodes((existing) => existing.map((n) => n.id === id ? { ...n, name } : n));
  }

  function cancelRoadTool() {
    terrain.preview.invalidate();
    terrainGrade.stop();
    roadGradeResultRef.current = null;
    const record = terrainRecordRef.current;
    if (record) setVisibleContours(record);
    setEditedContours(null);
    setRoadTool({ phase: 'idle' });
    if (mapRef.current) setRoadDraftData(mapRef.current, null);
    toolCoordinator.release('road');
  }

  function undoRoadPoint() {
    const current = roadToolRef.current;
    if (current.phase !== 'drawing') return;
    if (current.points.length <= 1) setRoadTool({ phase: 'armed', roadType: current.roadType });
    else setRoadTool({ ...current, points: current.points.slice(0, -1), cursor: null });
  }

  function finishRoadRoute() {
    const current = roadToolRef.current;
    if (current.phase !== 'drawing' || current.points.length < 2) return;
    const draft: DraftRoad = {
      name: nextRoadName(roadsRef.current),
      roadType: current.roadType,
      points: current.points,
      gradingStatus: 'pending',
      gradingError: null,
      gradingPolygons: [],
      earthwork: null,
      maxFaceSlopePct: 0,
      maxGroundCrossSlopePct: 0,
      maxDisturbedWidthM: 0,
      ungradedLengthM: 0,
      gradingInfeasibleLines: [],
    };
    setRoadTool({ phase: 'review', draft });
    startRoadTerrainGrade(draft);
  }

  function patchRoadDraft(patch: Partial<DraftRoad>) {
    setRoadTool((current) => current.phase === 'review'
      ? { phase: 'review', draft: { ...current.draft, ...patch } } : current);
  }

  function failRoadGrade(gradingError: string) {
    setRoadTool((current) => current.phase === 'review' ? { phase: 'review', draft: {
      ...current.draft, gradingStatus: 'error', gradingError,
    } } : current);
  }

  function startRoadTerrainGrade(draft: DraftRoad) {
    const record = terrainRecordRef.current;
    const polygon = strokeToPolygon(draft.points, TWO_LANE_ROAD_WIDTH_M);
    const parts = polygon.length ? [{
      polygon,
      centerline: draft.points,
      centerlineElevM: [],
    }] : [];
    const requestId = terrain.preview.claim();
    const bounds = record?.bounds;
    roadGradeResultRef.current = null;
    if (!record || !bounds || parts.length === 0) {
      failRoadGrade('The local elevation package or road footprint is unavailable.');
      return;
    }
    requestAnimationFrame(() => {
      if (!terrain.preview.isCurrent(requestId)) return;
      const baseElevationChecksum = record.packageManifest?.elevationChecksum ?? '';
      const cachedHeights = terrainHeightCacheRef.current;
      const heights = cachedHeights &&
        cachedHeights.checksum === (record.packageManifest?.elevationChecksum ?? record.updatedAt)
        ? cachedHeights.heights.slice()
        : Float32Array.from(record.sampleHeights);
      terrainGrade.run({
        id: requestId,
        kind: 'road',
        heights,
        gridSize: record.sampleGridSize,
        bounds,
        parts,
        brushWidthM: TWO_LANE_ROAD_WIDTH_M,
        ...ROAD_GRADE_POLICY,
        baseElevationChecksum,
        trailGeometryKey: terrainGradeGeometryKey(parts, TWO_LANE_ROAD_WIDTH_M,
          [], 'road', ROAD_GRADE_POLICY),
        contourGridSize: record.contourMetadata?.gridSize,
        contourIntervalM: record.contourMetadata?.intervalM,
      }, {
        isCurrent: (id) => terrain.preview.isCurrent(id),
        live: () => {
          const active = roadToolRef.current;
          return {
            baseElevationChecksum:
              terrainRecordRef.current?.packageManifest?.elevationChecksum ?? '',
            trailGeometryKey: active.phase === 'review'
              ? terrainGradeGeometryKey([{
                polygon: strokeToPolygon(active.draft.points, TWO_LANE_ROAD_WIDTH_M),
                centerline: active.draft.points,
                centerlineElevM: [],
              }], TWO_LANE_ROAD_WIDTH_M, [], 'road', ROAD_GRADE_POLICY)
              : '',
          };
        },
        onResult: (response) => {
          roadGradeResultRef.current = response;
          setRoadTool((current) => current.phase === 'review' ? { phase: 'review', draft: {
            ...current.draft,
            gradingStatus: 'ok',
            gradingError: null,
            gradingPolygons: response.expandedPolygons,
            earthwork: { cutM3: response.cutM3, fillM3: response.fillM3,
              balanceM3: response.balanceM3 },
            maxFaceSlopePct: response.maxFaceSlopePct,
            maxGroundCrossSlopePct: response.maxGroundCrossSlopePct,
            maxDisturbedWidthM: response.maxDisturbedWidthM,
            ungradedLengthM: response.ungradedLengthM,
            gradingInfeasibleLines: response.infeasibleLines,
          } } : current);
          setVisibleContours({ ...record,
            contourSegments: Array.from(response.contourSegments) });
          setEditedContours(response.editedContourSegments);
        },
        onSuperseded: () =>
          failRoadGrade('The road or terrain changed while grading. Refinish the route.'),
        onError: failRoadGrade,
        onCrash: () => failRoadGrade('Road grading worker stopped unexpectedly.'),
      });
    });
  }

  async function confirmRoad() {
    const current = roadToolRef.current;
    const result = roadGradeResultRef.current;
    if (current.phase !== 'review' ||
        current.draft.gradingStatus !== 'ok' || !result) return;
    const road: SavedRoad = {
      id: genId(),
      name: current.draft.name.trim() || nextRoadName(roadsRef.current),
      roadType: 'two-lane',
      widthM: TWO_LANE_ROAD_WIDTH_M,
      points: current.draft.points,
      lengthM: roadLengthM(current.draft.points),
      terrainGraded: true,
      earthwork: current.draft.earthwork ?? undefined,
      createdAt: new Date().toISOString(),
    };
    await terrain.runConstruction('road', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const { record, revision } = terrain.snapshot();
        if (!record) throw new Error('The local elevation package is unavailable.');
        const commit = terrain.commit({ expectedRevision: revision,
          record: applyTerrainGradeToRecord(record, result), kind: 'elevation' });
        if (!commit.ok) throw new Error('The terrain changed while building. Refinish the route.');
        setRoads((previous) => [...previous, road]);
        roadGradeResultRef.current = null;
        terrainGrade.stop();
        setRoadTool({ phase: 'idle' });
        toolCoordinator.release('road');
        await clearCover([
          ...roadClearingPolygons(road.points).map((polygon) => ({ polygon })),
          ...result.disturbancePolygons.map((polygon) => ({ polygon })),
        ]);
      } catch (error) {
        setRoadTool((active) => active.phase === 'review' ? { phase: 'review', draft: {
          ...active.draft,
          gradingStatus: 'error',
          gradingError: error instanceof Error ? error.message : 'Unable to save the road grade.',
        } } : active);
      }
    });
  }

  function armLiftTool() {
    if (siteModeRef.current === 'selecting') return; // never two draw tools at once
    if (!toolCoordinator.activate('lift')) return;
    clearSelectionState();
    setLiftTool({ phase: 'armed' });
  }

  function cancelLiftTool() {
    liftSampleTokenRef.current++; // discard any in-flight sampling
    setLiftTool({ phase: 'idle' });
    toolCoordinator.release('lift');
  }

  function patchLiftDraft(patch: Partial<DraftLift>) {
    setLiftTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, ...patch } } : t
    );
  }

  function retryLiftElevation() {
    const t = liftToolRef.current;
    if (t.phase === 'review') sampleDraftElevations(t.draft.points);
  }

  async function confirmLift() {
    const t = liftToolRef.current;
    if (t.phase !== 'review') return;
    const d = t.draft;
    const o = orientBottomToTop(d.points, d.elev);
    const stats = liftStats(o.points, o.elevs);
    const lift: SavedLift = {
      id: genId(),
      name: d.name.trim() || nextLiftName(liftsRef.current),
      liftClass: 'fixed-grip',
      points: o.points,
      endpointElevM: o.elevs,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      chairSize: d.chairSize,
      status: d.status,
      createdAt: new Date().toISOString(),
    };
    // Keep the review panel up with the build button spinning while the cover is
    // felled and re-vectorized in a worker — a best-effort edit that must never
    // block or fail the lift itself. Yield a frame first so both indicators
    // paint before processing begins.
    await terrain.runConstruction('lift', async () => {
      liftSampleTokenRef.current++;
      setLifts((prev) => [...prev, lift]);
      try {
        await new Promise(requestAnimationFrame);
        await applyLiftCoverClear(lift);
      } finally {
        setLiftTool({ phase: 'idle' });
        toolCoordinator.release('lift');
      }
    });
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

  /**
   * Fell grassland under a newly-built trail. Each painted part's footprint is
   * already a polygon (outer ring + tree-island holes). Restore the original
   * subtle edge treatment: deterministic +/-2 m value noise, smoothstep-blended
   * between random nodes, instead of adding outward bubble dabs. The authored
   * brush edge remains the baseline and tree islands remain polygon holes.
   */
  async function applyTrailCoverClear(
    trail: SavedTrail,
    gradingPolygons?: [number, number][][][]
  ): Promise<void> {
    const source = gradingPolygons ?? trail.parts.map((part) => part.polygon);
    const clearings: CoverClearing[] = source.map((polygon, i) => ({
      polygon: jitterPolygon(polygon, TRAIL_CLEAR_JITTER_M, `${trail.id}:${i}`),
    }));
    await clearCover(clearings);
  }

  /** Patch a non-geometric field (name/chairs/capacity/status) of a built lift. */
  function patchLift(id: string, patch: Partial<SavedLift>) {
    setLifts((prev) => prev.map((l) => (l.id === id ? { ...l, ...patch } : l)));
  }

  function deleteLift(id: string) {
    setLifts((prev) => prev.filter((l) => l.id !== id));
    setSelectedLiftId((cur) => (cur === id ? null : cur));
    setLiftEditing(false);
  }

  // ---- User-declared connectivity: anchors, nodes, connector paths ---------

  /**
   * Nearest point on a painted run, snapped onto its centerline. Runs only: a
   * node is a station ON a run, and a run is the only thing `splitTrailAt` can
   * cut, so an empty lift list is deliberate — a terminal near the click must
   * not win the snap and hand back an unsplittable target.
   */
  function trailAnchorAt(click: [number, number]): Extract<AnchorRef, { kind: 'trail' }> | null {
    const anchor = nearestTrailTailAnchor(click, [], trailsRef.current, ANCHOR_PICK_M);
    return anchor?.kind === 'trail' ? anchor : null;
  }

  /** Nearest existing graph node to a click, within the same pick radius. */
  function junctionAt(click: [number, number]): SavedJunction | null {
    const frame = makeFrame([click]);
    const pm = toMeters(frame, click);
    let best: { junction: SavedJunction; d: number } | null = null;
    for (const junction of junctionsRef.current) {
      const m = toMeters(frame, junction.point);
      const d = Math.hypot(m.x - pm.x, m.y - pm.y);
      if (d <= ANCHOR_PICK_M && (!best || d < best.d)) best = { junction, d };
    }
    return best ? best.junction : null;
  }

  function armNodeTool(phase: 'add' | 'remove') {
    if (siteModeRef.current === 'selecting') return;
    if (!toolCoordinator.activate('ski-node')) return;
    clearSelectionState();
    setOpenDock('trails');
    setNodeTool(phase === 'add'
      ? { phase: 'add', candidate: null, error: null }
      : { phase: 'remove', junctionId: null, error: null });
  }

  function cancelNodeTool() {
    setNodeTool({ phase: 'idle' });
    toolCoordinator.release('ski-node');
  }

  function confirmAddNode() {
    const t = nodeToolRef.current;
    if (t.phase !== 'add' || !t.candidate) return;
    const edit = topology.begin();
    if (!edit.splitTrail(t.candidate.trailId, t.candidate.point, genId)) {
      edit.abort();
      setNodeTool({ phase: 'add', candidate: null, error: 'That run cannot be split there.' });
      return;
    }
    // A split hands back the existing junction, lists untouched, when the click
    // lands on one. Nothing was added, so say so rather than flash success.
    if (!edit.changed.junctions) {
      edit.abort();
      setNodeTool({ phase: 'add', candidate: null, error: 'There is already a node there.' });
      return;
    }
    edit.commit();
    // Stay armed — splitting a run is something you do several times in a row.
    setNodeTool({ phase: 'add', candidate: null, error: null });
  }

  function removeGraphNode(id: string) {
    const edit = topology.begin();
    if (!edit.removeJunction(id)) {
      edit.abort();
      return;
    }
    edit.commit();
    setSelectedNodeId((cur) => (cur === id ? null : cur));
  }

  function confirmRemoveNode() {
    const t = nodeToolRef.current;
    if (t.phase !== 'remove' || !t.junctionId) return;
    removeGraphNode(t.junctionId);
    setNodeTool({ phase: 'remove', junctionId: null, error: null });
  }

  /** Legacy free-standing pins from saves made before nodes became graph nodes. */
  function deleteSkiNode(id: string) {
    const edit = topology.begin();
    edit.removeNode(id);
    edit.commit();
  }

  function selectGraphNode(id: string) {
    transitionSelection({ kind: 'ski-node', id });
    const junction = junctionsRef.current.find((j) => j.id === id);
    if (junction) mapRef.current?.easeTo({ center: junction.point, duration: 400 });
  }

  function armPathTool() {
    if (siteModeRef.current === 'selecting') return;
    if (!toolCoordinator.activate('ski-path')) return;
    clearSelectionState();
    setOpenDock('trails');
    setPathTool({ phase: 'armed' });
  }

  function cancelPathTool() {
    setPathTool({ phase: 'idle' });
    if (mapRef.current) setNodePathDraftData(mapRef.current, null);
    toolCoordinator.release('ski-path');
  }

  function undoPathPoint() {
    const t = pathToolRef.current;
    if (t.phase !== 'drawing') return;
    // The first point IS the start anchor, so undoing it re-arms the tool.
    if (t.points.length <= 1) setPathTool({ phase: 'armed' });
    else setPathTool({ ...t, points: t.points.slice(0, -1), cursor: null });
  }

  /**
   * Finish a connector. The last drawn point must resolve to an anchor — a path
   * that lands in open snow connects nothing, so Enter is simply ignored until
   * the route ends on a run, lift, path or node.
   */
  function finishPathRoute() {
    const t = pathToolRef.current;
    if (t.phase !== 'drawing' || t.points.length < 2 || !t.from) return;
    const end = t.points.at(-1) as [number, number];
    const to = trailAnchorAt(end);
    if (!to || t.from.kind !== 'trail' || to.trailId === t.from.trailId) return;
    setPathTool({
      phase: 'review',
      points: [...t.points.slice(0, -1), to.point],
      from: t.from,
      to,
      name: nextPathName(skiPathsRef.current),
    });
  }

  function confirmPath() {
    const t = pathToolRef.current;
    if (t.phase !== 'review') return;
    if (t.from.kind !== 'trail' || t.to.kind !== 'trail' || t.from.trailId === t.to.trailId) return;
    const edit = topology.begin();
    const fromJunction = edit.splitTrail(t.from.trailId, t.from.point, genId);
    if (!fromJunction) { edit.abort(); return; }
    const toJunction = edit.splitTrail(t.to.trailId, t.to.point, genId);
    if (!toJunction) { edit.abort(); return; }
    edit.addPath({
      id: genId(),
      name: t.name.trim() || nextPathName(skiPathsRef.current),
      points: t.points,
      pointElevM: [],
      widthM: DEFAULT_PATH_WIDTH_M,
      from: t.from,
      to: t.to,
      fromJunctionId: fromJunction.id,
      toJunctionId: toJunction.id,
      lengthM: pathLengthM(t.points),
      status: 'complete',
      createdAt: new Date().toISOString(),
    });
    edit.commit();
    setPathTool({ phase: 'idle' });
    toolCoordinator.release('ski-path');
    if (mapRef.current) setNodePathDraftData(mapRef.current, null);
  }

  function deleteSkiPath(id: string) {
    const edit = topology.begin();
    edit.removePath(id);
    edit.commit();
    setSelectedPathId((cur) => (cur === id ? null : cur));
  }

  /** Patch a non-geometric field (closed) of a built connector path. */
  function patchSkiPath(id: string, patch: Partial<SavedPath>) {
    const edit = topology.begin();
    edit.patchPath(id, patch);
    edit.commit();
  }

  function armTrailTool() {
    if (siteModeRef.current === 'selecting') return;
    if (!toolCoordinator.activate('trail')) return;
    clearSelectionState();
    setOpenDock('trails');
    trailCommandsRef.current = [];
    trailPendingUntilRef.current = 0;
    terrainGrade.stop();
    trailGradeResultRef.current = null;
    trailPaint.allowRestart();
    setTrailTool({ phase: 'place-head', candidate: null, error: null });
  }

  function beginTrailPainting(anchor: TrailHeadAnchor) {
    const seed: TrailPaintCommand = { mode: 'paint', path: [anchor.point, anchor.point], seed: true };
    trailCommandsRef.current = [seed];
    trailPaint.allowRestart();
    setTrailTool({ phase: 'paint', mode: 'paint', polygons: [], areaM2: 0,
      activeAreaM2: null, canUndo: false, pending: true, error: null, anchor,
      hasUserStroke: false });
    startTrailWorker(brushWidthRef.current, [seed]);
  }

  function changeTrailHead() {
    trailPaint.stop();
    trailCommandsRef.current = [];
    trailPendingUntilRef.current = 0;
    trailPreviewPathRef.current = [];
    trailBrushCursorRef.current = null;
    setTrailTool({ phase: 'place-head', candidate: null, error: null });
  }

  function cancelTrailTool() {
    trailSampleTokenRef.current++;
    terrain.preview.invalidate();
    terrainGrade.stop();
    trailGradeResultRef.current = null;
    const record = terrainRecordRef.current;
    if (record) setVisibleContours(record);
    setEditedContours(null);
    trailPaint.stop();
    trailCommandsRef.current = [];
    trailPendingUntilRef.current = 0;
    trailPreviewPathRef.current = [];
    trailBrushCursorRef.current = null;
    if (mapRef.current) setTrailPaintPreview(mapRef.current, { path: [], cursor: null,
      brushWidthM: brushWidthRef.current });
    setTrailTool({ phase: 'idle' });
    toolCoordinator.release('trail');
  }

  function startTrailWorker(widthM: number, replay: TrailPaintCommand[]) {
    // Held until the engine says it is ready, and replayed onto it then. A
    // restart snapshots the strokes at the moment it crashed for the same
    // reason: the replacement canvas is empty and has to be repainted.
    trailReplayRef.current = replay;
    const map = mapRef.current;
    const center = map?.getCenter();
    const origin: [number, number] = center ? [center.lng, center.lat] : INITIAL_CENTER;
    trailPaint.start({ origin, brushWidthM: widthM }, {
      onReady: () => {
        const pending = trailReplayRef.current;
        trailReplayRef.current = [];
        for (const command of pending) submitTrailCommand(command);
      },
      onFailure: (error) => {
        if (trailToolRef.current.phase === 'paint' && trailToolRef.current.pending &&
            trailCommandsRef.current.length > 1) trailCommandsRef.current.pop();
        setTrailTool((t) => t.phase === 'paint'
          ? { ...t, pending: false, activeAreaM2: null, error,
              canUndo: trailCommandsRef.current.length > 1,
              hasUserStroke: hasUserTrailStroke(trailCommandsRef.current, t.anchor.point) }
          : t.phase === 'analyzing' ? { phase: 'place-tail', mode: 'paint', polygons: t.polygons,
            areaM2: t.areaM2, activeAreaM2: null, canUndo: trailCommandsRef.current.length > 1,
            pending: false, error, anchor: t.anchor,
            hasUserStroke: hasUserTrailStroke(trailCommandsRef.current, t.anchor.point), candidate: null } : t);
      },
      onPreview: (message) => {
        trailPreviewPathRef.current = [];
        if (mapRef.current) setTrailPaintPreview(mapRef.current, { path: [],
          cursor: trailBrushCursorRef.current, brushWidthM: brushWidthRef.current,
          ...trailHeadPreview(trailToolRef.current) });
        setTrailTool((t) => t.phase === 'paint' ? { ...t, polygons: message.polygons,
          areaM2: message.areaM2, activeAreaM2: null,
          canUndo: trailCommandsRef.current.length > 1,
          hasUserStroke: hasUserTrailStroke(trailCommandsRef.current, t.anchor.point),
          pending: message.id < trailPendingUntilRef.current, error: null } : t);
      },
      onAnalysis: (message) => {
        const current = trailToolRef.current;
        if (current.phase !== 'analyzing') return;
        if (message.parts.length === 0) {
          setTrailTool({ phase: 'place-tail', mode: 'paint',
            polygons: current.polygons,
            areaM2: current.areaM2,
            activeAreaM2: null, canUndo: trailCommandsRef.current.length > 1, pending: false,
            error: 'Paint a longer connected footprint so a centerline can be found.',
            anchor: current.anchor,
            hasUserStroke: hasUserTrailStroke(trailCommandsRef.current, current.anchor.point), candidate: null });
          return;
        }
        const anchoredParts = pinTrailEndpoints(message.parts, current.anchor.point, current.tailAnchor.point);
        if (!anchoredParts) {
          setTrailTool({ phase: 'place-tail', mode: 'paint', polygons: current.polygons,
            areaM2: current.areaM2, activeAreaM2: null,
            canUndo: trailCommandsRef.current.length > 1, pending: false,
            error: 'The trailhead and trail end must be connected by one painted footprint.',
            anchor: current.anchor, hasUserStroke: true, candidate: null });
          return;
        }
        const draft: DraftTrail = { parts: anchoredParts, ungradedParts: anchoredParts,
          areaM2: message.areaM2, ungradedAreaM2: message.areaM2,
          brushWidthM: brushWidthRef.current, name: nextTrailName(trailsRef.current), status: 'planning',
          difficulty: 'blue', elevStatus: 'pending', elevError: null, gradingEnabled: false,
          gradingStatus: 'idle', gradingError: null,
          earthwork: null, maxGroundCrossSlopePct: 0, maxFaceSlopePct: 0,
          maxDisturbedWidthM: 0, ungradedLengthM: 0,
          infeasibleLines: [],
          anchor: current.anchor, tailAnchor: current.tailAnchor };
        setTrailTool({ phase: 'review', draft });
        sampleTrailElevations(anchoredParts, current.anchor, current.tailAnchor);
      },
      onRestart: () => {
        trailReplayRef.current = trailCommandsRef.current.map((command) =>
          ({ ...command, path: command.path.slice() }));
        setTrailTool((t) => t.phase === 'paint'
          ? { ...t, pending: trailReplayRef.current.length > 0,
              error: 'Restarting trail analysis…' } : t);
      },
      onLost: () => setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: false,
        error: 'Trail analysis worker stopped. Cancel and reopen the painter to retry.' } : t),
    });
  }

  function postTrailStroke(path: [number, number][], mode: 'paint' | 'erase'): number {
    const coordinates = new Float64Array(path.length * 2);
    path.forEach((point, i) => { coordinates[i * 2] = point[0]; coordinates[i * 2 + 1] = point[1]; });
    return trailPaint.post({ type: 'stroke', mode, coordinates }, [coordinates.buffer]);
  }

  function submitTrailCommand(command: TrailPaintCommand) {
    setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: true, activeAreaM2: null } : t);
    let finalId = postTrailStroke(command.path, command.mode);
    if (command.restoreSeed) finalId = postTrailStroke(
      [command.restoreSeed, command.restoreSeed], 'paint');
    trailPendingUntilRef.current = finalId;
  }

  function setTrailPaintModeState(mode: 'paint' | 'erase') {
    setTrailTool((t) => t.phase === 'paint' ? { ...t, mode } : t);
    if (mapRef.current) setTrailPaintMode(mapRef.current, mode);
  }

  function undoTrailPaint() {
    if (trailCommandsRef.current.length <= 1) return;
    const removed = trailCommandsRef.current.pop()!;
    setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: true,
      canUndo: trailCommandsRef.current.length > 1,
      hasUserStroke: hasUserTrailStroke(trailCommandsRef.current, t.anchor.point) } : t);
    let finalId = trailPaint.post({ type: 'undo' });
    if (removed.restoreSeed) finalId = trailPaint.post({ type: 'undo' });
    trailPendingUntilRef.current = finalId;
  }

  function clearTrailPaint() {
    const tool = trailToolRef.current;
    if (tool.phase !== 'paint') return;
    const seed: TrailPaintCommand = { mode: 'paint', path: [tool.anchor.point, tool.anchor.point], seed: true };
    trailCommandsRef.current = [seed];
    setTrailTool({ ...tool, pending: true, mode: 'paint', canUndo: false,
      hasUserStroke: false, activeAreaM2: null, error: null });
    trailPaint.post({ type: 'clear' });
    const finalId = postTrailStroke(seed.path, 'paint');
    trailPendingUntilRef.current = finalId;
  }

  function finishTrailPaint() {
    const t = trailToolRef.current;
    if (t.phase !== 'paint' || t.pending || !t.hasUserStroke) return;
    setTrailTool({ ...t, phase: 'place-tail', candidate: null, error: null });
  }

  function backToTrailPaint() {
    const t = trailToolRef.current;
    if (t.phase !== 'place-tail') return;
    setTrailTool({ phase: 'paint', mode: t.mode, polygons: t.polygons, areaM2: t.areaM2,
      activeAreaM2: null, canUndo: t.canUndo, pending: false, error: null,
      anchor: t.anchor, hasUserStroke: t.hasUserStroke });
  }

  function changeTrailBrushWidth(widthM: number) {
    setBrushWidthM(widthM);
    const t = trailToolRef.current;
    if (t.phase === 'paint' && !t.hasUserStroke) {
      const seed: TrailPaintCommand = { mode: 'paint', path: [t.anchor.point, t.anchor.point], seed: true };
      trailCommandsRef.current = [seed];
      setTrailTool({ ...t, polygons: [], areaM2: 0, activeAreaM2: null,
        pending: true, canUndo: false, error: null });
      startTrailWorker(widthM, [seed]);
    }
  }

  function patchTrailDraft(patch: Partial<DraftTrail>) {
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, ...patch } } : t
    );
  }

  function failTrailGrade(gradingError: string) {
    setTrailTool((t) => t.phase === 'review' ? { phase: 'review', draft: {
      ...t.draft, gradingStatus: 'error', gradingError,
    } } : t);
  }

  function setTrailTerrainGrading(enabled: boolean) {
    const current = trailToolRef.current;
    const record = terrainRecordRef.current;
    if (current.phase !== 'review') return;
    const requestId = terrain.preview.claim();
    trailGradeResultRef.current = null;
    if (!enabled) {
      const stats = trailPartsStats(current.draft.ungradedParts);
      setTrailTool({ phase: 'review', draft: {
        ...current.draft,
        parts: current.draft.ungradedParts,
        areaM2: current.draft.ungradedAreaM2,
        difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
        gradingEnabled: false, gradingStatus: 'idle', gradingError: null,
        earthwork: null, maxGroundCrossSlopePct: 0, maxFaceSlopePct: 0,
        maxDisturbedWidthM: 0, ungradedLengthM: 0,
        infeasibleLines: [],
      } });
      if (record) setVisibleContours(record);
      setEditedContours(null);
      return;
    }
    const bounds = record?.bounds;
    if (!record || !bounds) {
      // Checked, but unable to grade: the box stays on so unchecking is what
      // clears the error, exactly as it would after a refused grade.
      setTrailTool({ phase: 'review', draft: { ...current.draft, gradingEnabled: true,
        gradingStatus: 'error', gradingError: 'The local elevation package is unavailable.' } });
      return;
    }
    setTrailTool({ phase: 'review', draft: { ...current.draft, gradingEnabled: true,
      gradingStatus: 'pending', gradingError: null, earthwork: null } });
    // Paint the checked/pending state before allocating the transferable grid.
    requestAnimationFrame(() => {
      if (!terrain.preview.isCurrent(requestId)) return;
      const cachedHeights = terrainHeightCacheRef.current;
      const baseElevationChecksum = record.packageManifest?.elevationChecksum ?? '';
      const protectedPolygons = trailsRef.current.flatMap((trail) =>
        trail.parts.map((part) => part.polygon));
      const heights = cachedHeights &&
        cachedHeights.checksum === (record.packageManifest?.elevationChecksum ?? record.updatedAt)
          ? cachedHeights.heights.slice()
          : Float32Array.from(record.sampleHeights);
      terrainGrade.run({
        id: requestId, heights, gridSize: record.sampleGridSize, bounds,
        parts: current.draft.ungradedParts, brushWidthM: current.draft.brushWidthM,
        kind: 'trail',
        protectedPolygons,
        ...TRAIL_GRADE_POLICY,
        baseElevationChecksum,
        trailGeometryKey: terrainGradeGeometryKey(
          current.draft.ungradedParts,
          current.draft.brushWidthM,
          protectedPolygons,
          'trail',
          TRAIL_GRADE_POLICY
        ),
        contourGridSize: record.contourMetadata?.gridSize,
        contourIntervalM: record.contourMetadata?.intervalM,
      }, {
        isCurrent: (id) => terrain.preview.isCurrent(id),
        live: () => {
          const activeTrail = trailToolRef.current;
          return {
            baseElevationChecksum:
              terrainRecordRef.current?.packageManifest?.elevationChecksum ?? '',
            trailGeometryKey: activeTrail.phase === 'review'
              ? terrainGradeGeometryKey(activeTrail.draft.ungradedParts,
                activeTrail.draft.brushWidthM,
                trailsRef.current.flatMap((trail) => trail.parts.map((part) => part.polygon)),
                'trail', TRAIL_GRADE_POLICY)
              : '',
          };
        },
        onSuperseded: () => {
          trailGradeResultRef.current = null;
          const activeRecord = terrainRecordRef.current;
          if (activeRecord) setVisibleContours(activeRecord);
          setEditedContours(null);
          failTrailGrade('The trail or terrain changed while grading. Uncheck and retry the preview.');
        },
        onError: failTrailGrade,
        onCrash: () => failTrailGrade('Terrain grading worker stopped unexpectedly.'),
        onResult: (response) => {
          trailGradeResultRef.current = response;
          setTrailTool((t) => {
            if (t.phase !== 'review' || !t.draft.gradingEnabled) return t;
            const parts = t.draft.ungradedParts.map((part, i) => ({
              ...part,
              polygon: response.expandedPolygons[i] ?? part.polygon,
              centerlineElevM: response.gradedElevations[i] ?? part.centerlineElevM,
            }));
            const stats = trailPartsStats(parts);
            return { phase: 'review', draft: { ...t.draft, parts,
              areaM2: trailAreaM2(parts),
              difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
              gradingStatus: 'ok',
              gradingError: null,
              earthwork: {
                cutM3: response.cutM3,
                fillM3: response.fillM3,
                balanceM3: response.balanceM3,
              },
              maxGroundCrossSlopePct: response.maxGroundCrossSlopePct,
              maxFaceSlopePct: response.maxFaceSlopePct,
              maxDisturbedWidthM: response.maxDisturbedWidthM,
              ungradedLengthM: response.ungradedLengthM,
              infeasibleLines: response.infeasibleLines,
            } };
          });
          const preview: TerrainRecord = { ...record,
            contourSegments: Array.from(response.contourSegments) };
          setVisibleContours(preview);
          setEditedContours(response.editedContourSegments);
        },
      });
    });
  }

  function retryTrailElevation() {
    const t = trailToolRef.current;
    if (t.phase === 'review' && t.draft.anchor && t.draft.tailAnchor) {
      sampleTrailElevations(t.draft.parts, t.draft.anchor, t.draft.tailAnchor);
    }
  }

  function trailTerrainGradeCommit(): TerrainCommitRequest {
    const { record, revision } = terrain.snapshot();
    const result = trailGradeResultRef.current;
    if (!record || !result) throw new Error('The terrain grading preview is not ready.');
    const current = trailToolRef.current;
    if (current.phase !== 'review' ||
        result.trailGeometryKey !== terrainGradeGeometryKey(
          current.draft.ungradedParts,
          current.draft.brushWidthM,
          trailsRef.current.flatMap((trail) => trail.parts.map((part) => part.polygon)),
          'trail',
          TRAIL_GRADE_POLICY
        )) {
      throw new Error('The trail changed after this grading preview. Recalculate the grade and try again.');
    }
    return { expectedRevision: revision,
      record: applyTerrainGradeToRecord(record, result), kind: 'elevation' };
  }

  async function confirmTrail() {
    const t = trailToolRef.current;
    if (t.phase !== 'review') return;
    const d = t.draft;
    const commitGrading = d.status === 'complete' && d.gradingEnabled;
    if (commitGrading && (d.gradingStatus !== 'ok' || !trailGradeResultRef.current)) return;
    // Preserve the paint-time invariant even if confirmation is invoked outside
    // the visible button: every new run starts exactly at a lift top.
    if (!d.anchor || !d.tailAnchor || (d.anchor.kind !== 'lift' && d.anchor.kind !== 'trail') ||
        (d.tailAnchor.kind !== 'lift' && d.tailAnchor.kind !== 'trail')) return;
    if ((d.anchor.kind === 'lift' && d.anchor.end !== 'top') ||
        (d.tailAnchor.kind === 'lift' && d.tailAnchor.end !== 'base')) return;
    const pinned = pinTrailEndpoints(commitGrading ? d.parts : d.ungradedParts,
      d.anchor.point, d.tailAnchor.point);
    if (!pinned) return;
    // Both anchors are materialized now, against the revision this confirmation
    // started from, and land with the run itself once the grade has committed.
    const edit = topology.begin();
    const materialize = (anchor: AnchorRef): SavedJunction | null => {
      if (anchor.kind === 'trail') return edit.splitTrail(anchor.trailId, anchor.point, genId);
      if (anchor.kind === 'lift')
        return edit.liftTerminalJunction(liftsRef.current, anchor.liftId, anchor.end,
          anchor.point, genId);
      return null;
    };
    const headJunction = materialize(d.anchor);
    const tailJunction = materialize(d.tailAnchor);
    if (!headJunction || !tailJunction || headJunction.id === tailJunction.id) {
      edit.abort();
      return;
    }
    const trailId = genId();
    const parts = pinned.map((part, index) => withTopologyPart(part, headJunction.id,
      tailJunction.id, `${trailId}:${index}:segment:0`));
    const stats = trailPartsStats(parts);
    const trail: SavedTrail = {
      id: trailId,
      name: d.name.trim() || nextTrailName(trailsRef.current),
      parts,
      brushWidthM: d.brushWidthM,
      areaM2: d.areaM2,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      avgSlopeDeg: stats.avgSlopeDeg,
      maxSlopeDeg: stats.maxSlopeDeg,
      difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
      terrainGraded: commitGrading,
      earthwork: commitGrading && d.earthwork ? d.earthwork : undefined,
      status: d.status,
      anchor: d.anchor,
      createdAt: new Date().toISOString(),
    };
    // Keep the review panel up with the build button spinning while the cover is
    // felled and re-vectorized in a worker — a best-effort edit that must never
    // block or fail the trail itself. Yield a frame first so both indicators
    // paint before processing begins.
    let confirmed = false;
    const gradingClearPolygons = commitGrading
      ? trailGradeResultRef.current?.disturbancePolygons : undefined;
    await terrain.runConstruction('trail', async () => {
      try {
        await new Promise(requestAnimationFrame);
        const terrainCommit = commitGrading ? trailTerrainGradeCommit() : undefined;
        edit.addTrail(trail);
        const commit = commitDocuments({ terrain, topology: edit, terrainCommit });
        if (!commit.ok) {
          if (commit.reason === 'terrain-stale') {
            throw new Error(
              'The terrain changed after this grading preview. Recalculate the grade and try again.'
            );
          }
          throw new Error('The trail network changed while building. Repaint the run.');
        }
        if (!commitGrading) {
          const record = terrain.record;
          if (record) setVisibleContours(record);
        }
        // The edit is terrain now, not a proposal.
        setEditedContours(null);
        trailSampleTokenRef.current++;
        trailPaint.stop();
        terrainGrade.stop();
        trailGradeResultRef.current = null;
        confirmed = true;
        await applyTrailCoverClear(trail, gradingClearPolygons);
      } catch (error) {
        setTrailTool((current) => current.phase === 'review' ? { phase: 'review', draft: {
          ...current.draft, gradingStatus: 'error',
          gradingError: error instanceof Error ? error.message : 'Unable to save the terrain grade.',
        } } : current);
      } finally {
        if (confirmed) {
          setTrailTool({ phase: 'idle' });
          toolCoordinator.release('trail');
        }
      }
    });
  }

  /** Patch a non-geometric field (name/status) of a built run. */
  function patchTrail(id: string, patch: Partial<SavedTrail>) {
    const edit = topology.begin();
    edit.patchTrail(id, patch);
    edit.commit();
  }

  function deleteTrail(id: string) {
    const target = trailsRef.current.find((t) => t.id === id);
    if (!target) return;
    const owned = new Set(target.parts.flatMap((part) => (part.segments ?? []).flatMap((segment) =>
      [segment.fromJunctionId, segment.toJunctionId])));
    const dependentTrails = trailsRef.current.filter((trail) => trail.id !== id && trail.parts.some((part) =>
      (part.segments ?? []).some((segment) => owned.has(segment.fromJunctionId) || owned.has(segment.toJunctionId))));
    const dependentPaths = skiPathsRef.current.filter((path) =>
      (path.fromJunctionId && owned.has(path.fromJunctionId)) || (path.toJunctionId && owned.has(path.toJunctionId)));
    if (dependentTrails.length || dependentPaths.length) {
      const names = [...dependentTrails.map((trail) => trail.name), ...dependentPaths.map((path) => path.name)];
      window.alert(`Remove connected ${names.join(', ')} before deleting ${target.name}.`);
      return;
    }
    // The run and every junction nothing references any more leave together.
    const edit = topology.begin();
    if (!edit.removeTrail(id)) {
      edit.abort();
      return;
    }
    edit.commit();
    setSelectedTrailId((cur) => (cur === id ? null : cur));
    setTrailEditing(false);
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
    for (const contribution of mapContributions(map)) contribution.setCaptureTransient?.(hidden);
    if (hidden) {
      const record = terrainRecordRef.current;
      if (record) setVisibleContours(record);
      setEditedContours(null);
      return;
    }
    applyGradePreview();
  }

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
    const map = mapRef.current;
    if (!map) return;
    setLayers((prev) => {
      const target = prev.find((l) => l.id === id);
      if (!target) return prev;
      const nextVisible = !target.visible;
      return prev.map((l) => {
        if (
          nextVisible &&
          target.exclusiveGroup &&
          l.exclusiveGroup === target.exclusiveGroup &&
          l.id !== id &&
          l.visible
        ) {
          for (const lid of l.layerIds) map.setLayoutProperty(lid, 'visibility', 'none');
          return { ...l, visible: false };
        }
        if (l.id === id) {
          for (const lid of l.layerIds)
            map.setLayoutProperty(lid, 'visibility', nextVisible ? 'visible' : 'none');
          // The cover is a translucent tint over the aerial photo but must carry
          // the map at heavier opacity when the aerial is off — otherwise it
          // washes out over the bare paper background.
          if (id === 'satellite') applyCoverOpacity(map, nextVisible);
          return { ...l, visible: nextVisible };
        }
        return l;
      });
    });
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
            onSelectNode: (id) => (id ? selectSnowmakingNode(id) : setSelectedSnowmakingNodeId(null)),
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
                              onChange={(e) => setPathTool((t) =>
                                t.phase === 'review' ? { ...t, name: e.target.value } : t)}
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
                      onSelect={(id) => selectLiftRef.current(id)}
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
                      onDraftChange={patchLiftDraft}
                      onConfirm={confirmLift}
                      building={building}
                      onSelect={(id) => selectLiftRef.current(id)}
                      onEditPatch={patchLift}
                      onCloseEdit={() => setLiftEditing(false)}
                      onDelete={deleteLift}
                      onRetryElevation={retryLiftElevation}
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
                    onDamDraftChange={patchDamDraft}
                    onConfirmDam={confirmDam}
                    onSelectDam={selectDam}
                    onDeleteDam={deleteDam}
                    onCloseDam={() => setSelectedDamId(null)}
                    onArmPond={armPondTool}
                    onCancelPond={cancelPondTool}
                    onUndoPond={undoPondPoint}
                    onFinishPond={finishPondBoundary}
                    onPondDraftChange={patchPondDraft}
                    onPondElevationChange={changePondElevation}
                    onPondExcavationChange={changePondExcavation}
                    onConfirmPond={confirmPond}
                    onSelectPond={selectPond}
                    onDeletePond={deletePond}
                    onPondSnowmakingChange={changePondSnowmaking}
                    onClosePond={() => setSelectedPondId(null)}
                    onSelectNode={selectSnowmakingNode}
                    onRenameNode={renameSnowmakingNode}
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
                    onUndo={undoRoadPoint}
                    onFinish={finishRoadRoute}
                    onDraftChange={patchRoadDraft}
                    onConfirm={confirmRoad}
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
