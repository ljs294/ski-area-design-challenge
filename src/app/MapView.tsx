import { useEffect, useRef, useState, type CSSProperties } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { setupAnalysisLayers, type LayerToggle } from './analysisLayers';
import { applyCoverOpacity, setCoverData } from './coverVectorize';
import { LayerList } from './LayerPanel';
import { GameToolbar } from './GameToolbar';
import { GameMenu } from './GameMenu';
import { CreditsPanel } from './CreditsPanel';
import { LiftOverview } from './LiftOverview';
import { LiftDetail } from './LiftDetail';
import { ResortStatsPanel } from './ResortStatsPanel';
import { CursorReadout, type Readout } from './CursorReadout';
import type { OverlayId } from './Legend';
import { sampleTerrainAt, compass8 } from './terrainProtocols';
import { sampleCoverAt, COVER_LABELS } from './worldcoverProtocol';
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
import { applyTileLod } from './terrainLod';
import { saveGame } from '../gameSaveClient';
import type { GameSave, SavedLift, SavedTrail, TerrainPackageProgress, TerrainRecord } from '../types';
import { loadTerrain, saveTerrain } from '../terrainStorageClient';
import { prepareResortPackage } from '../terrainIngest';
import { coverDisplayMetadataOf, coverMetadataOf, manifestOf, validateTerrainPackage } from '../terrainPackage';
import { coverDisplayToGeoJSON, deriveCoverDisplayGeometry, inspectCoverDisplayGeometry, type CoverDisplayGeoJSON } from '../coverDisplay';
import {
  appendCorridorToDisplayGeometry,
  grasslandCodeFor,
  liftCorridorRing,
  stampCorridorIntoGrid,
  LIFT_CLEAR_HALF_WIDTH_M,
  LIFT_CLEAR_JITTER_M,
} from '../coverEdit';
import { clearResortCoverCache, getResortRenderStats, RESORT_COVER_PROTOCOL, resortCameraBounds, sampleLocalCoverAt, sampleLocalTerrainAt, setActiveResortTerrain, setRenderConcurrency, warmResortTiles, WORLD_COVER_LABELS } from './resortProtocols';
import { LiftControl, type LiftTool, type DraftLift } from './LiftControl';
import { addLiftLayers, setLiftData, liftsToGeoJSON, type DraftLine } from './liftLayers';
import { TrailControl, type TrailTool, type DraftTrail } from './TrailControl';
import { TrailOverview } from './TrailOverview';
import { TrailDetail } from './TrailDetail';
import {
  addTrailLayers,
  draftToGeoJSON,
  setTrailData,
  setTrailDraftData,
  setTrailPaintMode,
  setTrailPaintPreview,
  setTrailPaintWidth,
  trailsToGeoJSON,
} from './trailLayers';
import type { TrailPaintRequest, TrailPaintRequestPayload, TrailPaintResponse } from './trailPaintProtocol';
import {
  FIXED_GRIP_SPEC,
  liftStats,
  nextLiftName,
  orientBottomToTop,
  sanitizeLifts,
} from '../lifts';
import {
  sanitizeTrails,
  nextTrailName,
  orientTopToBottom,
  trailPartsStats,
  difficultyForSlopes,
  DEFAULT_BRUSH_WIDTH_M,
} from '../trails';
import { haversineMeters } from '../geo';

// Crystal Mountain, WA — our canonical test site (used as the New Game start).
const INITIAL_CENTER: [number, number] = [-121.474, 46.928];
const INITIAL_ZOOM = 12;

export type MapMode = 'picking' | 'playing';

// Reject lift terminals closer than this — avoids accidental zero-length lifts
// from a double-click.
const MIN_LIFT_M = 50;

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

/** Ground meters per screen pixel at the map center — converts a brush width in
 *  meters to the line-width (px) of the live paint preview. */
function metersPerPixel(map: maplibregl.Map): number {
  const c = map.getCenter();
  const p = map.project(c);
  const q = map.unproject([p.x + 1, p.y]);
  return haversineMeters([c.lng, c.lat], [q.lng, q.lat]) || 1;
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

interface MapViewProps {
  mode: MapMode;
  /** Present when resuming a saved resort (Load / Continue). */
  initialSave?: GameSave | null;
  onQuit: () => void;
  onOpenSettings: () => void;
  /** Open the Load Game modal (owned by App). Menu → Load. */
  onLoadGame: () => void;
}

/**
 * Owns the MapLibre instance. The map lives in a ref (never React state); React
 * state holds the layer-toggle UI model, cursor readout, and game/site status.
 */
export function MapView({ mode, initialSave = null, onQuit, onOpenSettings, onLoadGame }: MapViewProps) {
  const { settings, resolvedTheme } = useSettings();

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const [layers, setLayers] = useState<LayerToggle[]>([]);
  // Bottom-dock roll-ups: user-chosen open panel (the lift panel also force-opens
  // whenever the lift tool is active or a lift is selected — see liftsOpen below).
  const [openDock, setOpenDock] = useState<'layers' | 'lifts' | 'trails' | null>(null);
  const [showStats, setShowStats] = useState(false);
  const [showCredits, setShowCredits] = useState(false);
  const [readout, setReadout] = useState<Readout | null>(null);
  const [siteMode, setSiteMode] = useState<SiteMode>(initialSave?.site ? 'locked' : 'explore');
  const [siteBox, setSiteBoxState] = useState<SiteBox | null>((initialSave?.site as SiteBox) ?? null);
  const [is3D, setIs3D] = useState(initialSave?.is3D ?? false);
  // Loading veil held over the resort until its first complete render, so the
  // map never appears mid-stream (popping terrain / half-drawn custom tiles).
  const [warming, setWarming] = useState(mode === 'playing' && !TERRAIN_DISABLED);
  // Determinate preload progress shown on the warming veil (completed/total warm
  // tiles). null while indeterminate (before the tile set is known).
  const [warmProgress, setWarmProgress] = useState<{ completed: number; total: number } | null>(null);
  const warmAbortRef = useRef<AbortController | null>(null);
  const [saved, setSaved] = useState<GameSave | null>(initialSave);
  const [nameDraft, setNameDraft] = useState('');
  const [saving, setSaving] = useState(false);
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
  const [liftTool, setLiftTool] = useState<LiftTool>({ phase: 'idle' });
  const [selectedLiftId, setSelectedLiftId] = useState<string | null>(null);
  // A selected lift opens its read-only detail first; Edit flips this to the
  // LiftControl edit panel. Reset to false whenever a (different) lift is opened.
  const [liftEditing, setLiftEditing] = useState(false);
  const [trails, setTrails] = useState<SavedTrail[]>(() =>
    sanitizeTrails(initialSave?.trails ?? [])
  );
  const [trailTool, setTrailTool] = useState<TrailTool>({ phase: 'idle' });
  const [selectedTrailId, setSelectedTrailId] = useState<string | null>(null);
  const [trailEditing, setTrailEditing] = useState(false);
  // Last-used brush width, kept across arms so it persists between runs.
  const [brushWidthM, setBrushWidthM] = useState(DEFAULT_BRUSH_WIDTH_M);

  const activeOverlay = activeOverlayOf(layers);

  // Refs so once-registered handlers + the style-swap re-init read current values.
  const activeOverlayRef = useRef<OverlayId | null>(null);
  const lastLngLatRef = useRef<{ lng: number; lat: number } | null>(null);
  const sampleTokenRef = useRef(0);
  const rafPendingRef = useRef(false);
  const doSampleRef = useRef<(lngLat: { lng: number; lat: number }) => void>(() => {});
  const layersRef = useRef<LayerToggle[]>([]);
  const siteBoxRef = useRef<SiteBox | null>(siteBox);
  const siteModeRef = useRef<SiteMode>(siteMode);
  const is3DRef = useRef(is3D);
  // Flips true the first time the resort's terrain is mounted, so the one-time
  // "default into 3D" camera ease fires once — not on every dark/light restyle.
  const resortReadyRef = useRef(false);
  const liftsRef = useRef<SavedLift[]>(lifts);
  const liftToolRef = useRef<LiftTool>(liftTool);
  const liftSampleTokenRef = useRef(0);
  const selectLiftRef = useRef<(id: string) => void>(() => {});
  const trailsRef = useRef<SavedTrail[]>(trails);
  const trailToolRef = useRef<TrailTool>(trailTool);
  const trailSampleTokenRef = useRef(0);
  const trailWorkerRef = useRef<Worker | null>(null);
  const trailWorkerIdRef = useRef(0);
  const trailWorkerAppliedRef = useRef(0);
  const trailWorkerRecoveryRef = useRef(0);
  const trailCommandsRef = useRef<{ mode: 'paint' | 'erase'; path: [number, number][] }[]>([]);
  const trailPreviewPathRef = useRef<[number, number][]>([]);
  const trailBrushCursorRef = useRef<[number, number] | null>(null);
  const selectTrailRef = useRef<(id: string) => void>(() => {});
  const brushWidthRef = useRef(brushWidthM);
  const renderQualityRef = useRef(settings.renderQuality);
  const packageAbortRef = useRef<AbortController | null>(null);
  // Loaded local package backing cursor sampling, MapLibre protocols, and
  // style reinitialization. Gameplay never populates it from network data.
  const terrainRecordRef = useRef<TerrainRecord | null>(null);
  const coverDisplayRef = useRef<CoverDisplayGeoJSON | null>(null);
  const localImageryUrlRef = useRef<string | null>(null);

  function cacheTerrainDisplayAssets(record: TerrainRecord): void {
    coverDisplayRef.current = record.coverDisplayGeometry && record.bounds
      ? coverDisplayToGeoJSON(record.coverDisplayGeometry, record.bounds)
      : null;
    if (localImageryUrlRef.current) URL.revokeObjectURL(localImageryUrlRef.current);
    localImageryUrlRef.current = record.localImagery
      ? URL.createObjectURL(new Blob([Uint8Array.from(record.localImagery)], { type: record.localImageryMetadata?.mimeType ?? 'image/jpeg' }))
      : null;
  }

  renderQualityRef.current = settings.renderQuality;
  layersRef.current = layers;
  siteBoxRef.current = siteBox;
  siteModeRef.current = siteMode;
  is3DRef.current = is3D;
  liftsRef.current = lifts;
  liftToolRef.current = liftTool;
  trailsRef.current = trails;
  trailToolRef.current = trailTool;
  brushWidthRef.current = brushWidthM;
  terrainRecordRef.current = terrainRecord;
  packageStateRef.current = packageState;

  useEffect(() => () => {
    packageAbortRef.current?.abort();
    trailWorkerRef.current?.terminate();
    if (localImageryUrlRef.current) URL.revokeObjectURL(localImageryUrlRef.current);
  }, []);

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
      return;
    }
    void loadTerrain(key).then(async (record) => {
      if (cancelled) return;
      if (!record) {
        setPackageError('The local resort package is missing. Prepare it again to continue.');
        setPackageState('missing');
        return;
      }
      let readyRecord = record;
      if (record.schemaVersion === 4 && record.coverGrid && record.bounds) {
        packageStateRef.current = 'optimizing';
        setPackageState('optimizing');
        setPackageProgress({ phase: 'vectorizing-cover', message: 'Drawing smooth ground cover', completed: 0, total: 1 });
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
      const validation = validateTerrainPackage(readyRecord);
      if (!validation.ok) {
        setPackageError(validation.errors.join(' '));
        setPackageState('error');
        return;
      }
      cacheTerrainDisplayAssets(readyRecord);
      setActiveResortTerrain(readyRecord);
      setTerrainRecord(readyRecord);
      setPackageState('ready');
    }).catch((error) => {
      if (cancelled) return;
      setPackageError(error instanceof Error ? error.message : 'Unable to load the local resort package.');
      setPackageState('error');
    });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Clicking a lift (on the map or in the list) opens its read-only detail, and
  // yields any active trail tool/selection (docks are one-at-a-time). Redefined
  // each render so the map click handler (captured via a ref) stays current.
  selectLiftRef.current = (id: string) => {
    liftSampleTokenRef.current++;
    cancelTrailTool();
    setSelectedTrailId(null);
    setTrailEditing(false);
    setLiftTool({ phase: 'idle' });
    setSelectedLiftId(id);
    setLiftEditing(false);
  };

  // Clicking a run opens its read-only detail, yielding any active lift tool.
  selectTrailRef.current = (id: string) => {
    trailSampleTokenRef.current++;
    cancelLiftTool();
    setSelectedLiftId(null);
    setLiftEditing(false);
    setTrailTool({ phase: 'idle' });
    setSelectedTrailId(id);
    setTrailEditing(false);
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

  // (Re)attach analysis layers + site box + 3D after any style (re)load. Shared
  // by the initial load and the light<->dark basemap swap. Reads live state from
  // refs and re-applies the current layer-visibility model.
  function reinitAfterStyle(map: maplibregl.Map) {
    tuneBasemap(map);
    // While preparation is blocking the game, remove preview DEM/contour/
    // WorldCover sources so they cannot contend with mandatory downloads.
    const fresh = packageStateRef.current === 'preparing'
      ? []
      : setupAnalysisLayers(map, terrainRecordRef.current, settings.units, coverDisplayRef.current, localImageryUrlRef.current);
    const prev = layersRef.current;
    let applied = fresh.map((f) => {
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
    // Runs beneath lifts (ski-map convention): add trails first, lifts on top.
    addTrailLayers(map);
    setTrailData(map, trailsToGeoJSON(trailsRef.current));
    const tt = trailToolRef.current;
    setTrailDraftData(map, tt.phase === 'paint' || tt.phase === 'analyzing'
      ? draftToGeoJSON(tt.polygons)
      : tt.phase === 'review' ? draftToGeoJSON([], { parts: tt.draft.parts, difficulty: tt.draft.difficulty, name: tt.draft.name })
        : draftToGeoJSON([]));
    setTrailPaintPreview(map, { path: [], cursor: null, brushWidthM: brushWidthRef.current });
    addLiftLayers(map);
    setLiftData(map, liftsToGeoJSON(liftsRef.current, draftLineOf(liftToolRef.current)));
    // Resort view = a local terrain package is active. Terrain is mounted here
    // (and re-mounted after every restyle, since setStyle drops it) so it is
    // always present and the 2D↔3D switch stays a pure camera move. The
    // worldwide picker has no package, so it stays flat.
    if (terrainRecordRef.current && !TERRAIN_DISABLED) {
      mountTerrain(map);
      if (!resortReadyRef.current) {
        resortReadyRef.current = true;
        // First entry into the resort: honor a resumed 2D/3D choice, otherwise
        // default into the 3D-native view. Easing after mountTerrain (same
        // frame) means the relief rises through perspective with no pop.
        const want3D = initialSave?.is3D ?? true;
        if (want3D !== is3DRef.current) setIs3D(want3D);
        // Hold the veil until the resort is genuinely fully drawn: (1) preload
        // every reachable diorama tile into the cache (determinate progress),
        // then (2) wait for MapLibre to have all tiles loaded and go idle — so
        // the map is revealed already-complete, never mid-stream. A generous
        // safety timeout guarantees the veil always lifts even if something
        // stalls. Replaces the old first-`idle`/6 s-timer heuristic, which fired
        // early against the serial tile queue and revealed a half-loaded map.
        const rec = terrainRecordRef.current;
        let revealed = false;
        const reveal = () => {
          if (revealed) return;
          revealed = true;
          setRenderConcurrency(1); // restore calm serial rendering for play
          setWarming(false);
          setWarmProgress(null);
          map.easeTo({ pitch: want3D ? PITCH_3D : 0, duration: 1200 });
        };
        const safety = window.setTimeout(reveal, 15000);
        const controller = new AbortController();
        warmAbortRef.current = controller;
        void (async () => {
          setWarmProgress({ completed: 0, total: 0 });
          if (rec) {
            await warmResortTiles(
              rec,
              (completed, total) => setWarmProgress({ completed, total }),
              controller.signal
            );
          }
          if (controller.signal.aborted) return;
          // Catch any stragglers MapLibre requested outside the warm set: wait
          // for a fully-loaded, idle map to hold across two consecutive frames.
          let stable = 0;
          const settle = () => {
            if (revealed || controller.signal.aborted) return;
            const ready = map.areTilesLoaded() && getResortRenderStats().pending === 0 && map.loaded();
            stable = ready ? stable + 1 : 0;
            if (stable >= 2) {
              window.clearTimeout(safety);
              reveal();
            } else {
              requestAnimationFrame(settle);
            }
          };
          requestAnimationFrame(settle);
        })();
      }
    } else {
      unmountTerrain(map);
      setWarming(false);
    }
    applyTileLod(map, renderQualityRef.current);
    setLayers(applied);
  }

  // Create the map once.
  const mapCanStart = mode !== 'playing' || packageState === 'ready';
  useEffect(() => {
    if (!mapCanStart || mapRef.current || !containerRef.current) return;

    const start = initialSave ?? null;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: basemapFor(resolvedTheme, { offline: mode === 'playing' }),
      center: start ? start.center : INITIAL_CENTER,
      zoom: start ? start.zoom : INITIAL_ZOOM,
      bearing: start?.bearing ?? 0,
      pitch: start?.pitch ?? 0,
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

    // Click a built lift to open its edit panel. Delegated to the wide white
    // casing (bigger hit target than the 3px red line) plus the terminal dots;
    // both carry the lift `id`. Gated to idle play so it never steals the
    // terminal-placing clicks while a lift is being drawn. Delegated listeners
    // survive the light/dark style swap (they query at event time), so this is
    // registered once with the map.
    const bothToolsIdle = () =>
      liftToolRef.current.phase === 'idle' && trailToolRef.current.phase === 'idle';

    const LIFT_HIT_LAYERS = ['lift-line-casing', 'lift-terminals'];
    const onLiftClick = (e: maplibregl.MapLayerMouseEvent) => {
      if (!bothToolsIdle()) return;
      const id = e.features?.[0]?.properties?.id;
      if (typeof id === 'string') selectLiftRef.current(id);
    };
    const onLiftEnter = () => {
      if (bothToolsIdle()) map.getCanvas().style.cursor = 'pointer';
    };
    const onLiftLeave = () => {
      if (bothToolsIdle()) map.getCanvas().style.cursor = '';
    };
    map.on('click', LIFT_HIT_LAYERS, onLiftClick);
    map.on('mouseenter', LIFT_HIT_LAYERS, onLiftEnter);
    map.on('mouseleave', LIFT_HIT_LAYERS, onLiftLeave);

    // Click a run's fill to open its detail. Same idle gating so it never steals
    // a brush/terminal click.
    const onTrailClick = (e: maplibregl.MapLayerMouseEvent) => {
      if (!bothToolsIdle()) return;
      const id = e.features?.[0]?.properties?.id;
      if (typeof id === 'string') selectTrailRef.current(id);
    };
    map.on('click', ['trail-fill'], onTrailClick);
    map.on('mouseenter', ['trail-fill'], onLiftEnter);
    map.on('mouseleave', ['trail-fill'], onLiftLeave);

    return () => {
      warmAbortRef.current?.abort();
      setRenderConcurrency(1);
      map.remove();
      mapRef.current = null;
      setLayers([]);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mapCanStart]);

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

  // Saved trails are stable while painting; drafts use their own source.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setTrailData(map, trailsToGeoJSON(trails));
  }, [trails]);

  const draftPolygons = trailTool.phase === 'paint' || trailTool.phase === 'analyzing' ? trailTool.polygons : null;
  const reviewDraft = trailTool.phase === 'review' ? trailTool.draft : null;
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (draftPolygons) setTrailDraftData(map, draftToGeoJSON(draftPolygons));
    else if (reviewDraft) setTrailDraftData(map, draftToGeoJSON([], { parts: reviewDraft.parts,
      difficulty: reviewDraft.difficulty, name: reviewDraft.name }));
    else setTrailDraftData(map, draftToGeoJSON([]));
  }, [draftPolygons, reviewDraft?.parts, reviewDraft?.difficulty, reviewDraft?.name]);

  // Keep the live brush-preview width (px) in sync with the brush size (m).
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (trailTool.phase === 'paint') {
      setTrailPaintWidth(map, brushWidthM / metersPerPixel(map));
      setTrailPaintPreview(map, { path: trailPreviewPathRef.current,
        cursor: trailBrushCursorRef.current, brushWidthM });
    }
  }, [brushWidthM, trailTool.phase]);

  /** Sample both terminal elevations for the review draft. Token-guarded so a
   *  cancel/confirm/redraw discards in-flight results. */
  function sampleDraftElevations(points: [[number, number], [number, number]]) {
    const map = mapRef.current;
    const z = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++liftSampleTokenRef.current;
    setLiftTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'pending' } } : t
    );
    void Promise.all(points.map(([lng, lat]) => samplePlanningTerrain(lng, lat, z))).then(
      (samples) => {
        if (token !== liftSampleTokenRef.current) return;
        setLiftTool((t) =>
          t.phase === 'review'
            ? {
                phase: 'review',
                draft: {
                  ...t.draft,
                  elev: [samples[0].elevation, samples[1].elevation],
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

    map.doubleClickZoom.disable();
    map.getCanvas().style.cursor = 'crosshair';

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
      if (e.key === 'Escape') setLiftTool({ phase: 'idle' });
    };

    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      map.doubleClickZoom.enable();
      map.getCanvas().style.cursor = '';
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liftTool.phase]);

  /** Sample the shape-derived centerlines, orient each top→bottom, and grade. */
  function sampleTrailElevations(parts: DraftTrail['parts']) {
    const map = mapRef.current;
    const z = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++trailSampleTokenRef.current;
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'pending' } } : t
    );
    void Promise.all(parts.map(async (part) => {
      const samples = await Promise.all(part.centerline.map(([lng, lat]) => samplePlanningTerrain(lng, lat, z)));
      const oriented = orientTopToBottom(part.centerline, samples.map((s) => s.elevation));
      return { ...part, centerline: oriented.spine, centerlineElevM: oriented.elevM };
    })).then(
      (resolvedParts) => {
        if (token !== trailSampleTokenRef.current) return;
        const stats = trailPartsStats(resolvedParts);
        const recommended = difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg);
        setTrailTool((t) =>
          t.phase === 'review'
            ? {
                phase: 'review',
                draft: {
                  ...t.draft,
                  parts: resolvedParts,
                  elevStatus: 'ok',
                  difficulty: recommended,
                },
              }
            : t
        );
      },
      () => {
        if (token !== trailSampleTokenRef.current) return;
        setTrailTool((t) =>
          t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'error' } } : t
        );
      }
    );
  }

  // Pointer movement only updates the small preview source. Completed strokes
  // are transferred to the worker; React never receives the growing path.
  const trailDrawing = trailTool.phase === 'paint';
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !trailDrawing) return;

    map.dragPan.disable();
    map.doubleClickZoom.disable();
    const canvas = map.getCanvas();
    canvas.style.cursor = 'none';
    const renderPreview = () => setTrailPaintPreview(map, { path: trailPreviewPathRef.current,
      cursor: trailBrushCursorRef.current, brushWidthM: brushWidthRef.current });
    const syncWidth = () => {
      setTrailPaintWidth(map, brushWidthRef.current / metersPerPixel(map));
      renderPreview();
    };
    syncWidth();
    map.on('zoom', syncWidth);

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
      const mode = trailToolRef.current.phase === 'paint' ? trailToolRef.current.mode : 'paint';
      trailCommandsRef.current.push({ mode, path: path.slice() });
      submitTrailStroke(path, mode);
    };

    const down = (e: maplibregl.MapMouseEvent) => {
      if (trailToolRef.current.phase !== 'paint' || trailToolRef.current.pending) return;
      painting = true;
      path = [[e.lngLat.lng, e.lngLat.lat]];
      previewPath = path;
      trailPreviewPathRef.current = previewPath;
      trailBrushCursorRef.current = path[0];
      schedulePreview();
    };
    const move = (e: maplibregl.MapMouseEvent) => {
      const p: [number, number] = [e.lngLat.lng, e.lngLat.lat];
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
    map.on('mouseup', up);
    canvas.addEventListener('mouseleave', leave);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('mousedown', down);
      map.off('mousemove', move);
      map.off('mouseup', up);
      canvas.removeEventListener('mouseleave', leave);
      map.off('zoom', syncWidth);
      if (previewRaf) cancelAnimationFrame(previewRaf);
      trailPreviewPathRef.current = [];
      trailBrushCursorRef.current = null;
      setTrailPaintPreview(map, { path: [], cursor: null, brushWidthM: brushWidthRef.current });
      window.removeEventListener('keydown', onKey);
      map.dragPan.enable();
      map.doubleClickZoom.enable();
      canvas.style.cursor = '';
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
        const samples = await Promise.all(l.points.map(([lng, lat]) => samplePlanningTerrain(lng, lat, 13)));
        return { id: l.id, elevs: [samples[0].elevation, samples[1].elevation] as [number, number] };
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
          const samples = await Promise.all(part.centerline.map(([lng, lat]) => samplePlanningTerrain(lng, lat, 13)));
          const o = orientTopToBottom(part.centerline, samples.map((s) => s.elevation));
          return { ...part, centerline: o.spine, centerlineElevM: o.elevM };
        }));
        return { id: t.id, parts };
      })
    ).then((results) => {
      if (stale) return;
      const byId = new Map<string, SavedTrail['parts']>();
      for (const r of results) if (r.status === 'fulfilled') byId.set(r.value.id, r.value.parts);
      if (byId.size === 0) return;
      setTrails((prev) =>
        prev.map((t) => {
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
        })
      );
    });
    return () => {
      stale = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function armLiftTool() {
    if (siteModeRef.current === 'selecting') return; // never two draw tools at once
    cancelTrailTool(); // yield the other draw tool (docks are one-at-a-time)
    setSelectedTrailId(null);
    setTrailEditing(false);
    setSelectedLiftId(null); // close any open detail/edit panel
    setLiftEditing(false);
    setLiftTool({ phase: 'armed' });
  }

  function cancelLiftTool() {
    liftSampleTokenRef.current++; // discard any in-flight sampling
    setLiftTool({ phase: 'idle' });
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

  function confirmLift() {
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
    liftSampleTokenRef.current++;
    setLifts((prev) => [...prev, lift]);
    setLiftTool({ phase: 'idle' });
    // Clearing the cover under the new lift is a background, best-effort edit to
    // the resort package — it must never block or fail the lift itself.
    void applyLiftCoverClear(lift);
  }

  /**
   * Fell a grassland corridor under a newly-drawn lift and persist it to the
   * resort package. Stamps the analytical cover grid and appends one polygon to
   * the vector display geometry — no full re-vectorize (see coverEdit.ts). All
   * failures are swallowed so a bad edit can never lose the lift.
   */
  async function applyLiftCoverClear(lift: SavedLift): Promise<void> {
    const map = mapRef.current;
    const record = terrainRecordRef.current;
    if (!map || !record || !record.coverGrid || !record.bounds) return;
    try {
      const ring = liftCorridorRing(lift.points, record.bounds, {
        halfWidthM: LIFT_CLEAR_HALF_WIDTH_M,
        jitterM: LIFT_CLEAR_JITTER_M,
        seed: lift.id,
      });
      const { grid, changed } = stampCorridorIntoGrid(record.coverGrid, ring);
      if (changed === 0) return;

      // coverMetadata must travel with the grid: the desktop package writer
      // re-verifies the written .cover.bin against record.coverMetadata (not the
      // manifest), so a stale checksum here rejects the whole save.
      let upgraded: TerrainRecord = { ...record, coverGrid: grid, coverMetadata: coverMetadataOf(grid), updatedAt: new Date().toISOString() };

      // v5+ packages render vector cover; append the corridor polygon so the
      // clearing shows without re-tracing. v4 raster-only packages skip this and
      // rely on the grid stamp + tile-cache refresh below.
      const hasVectorDisplay = !!record.coverDisplayGeometry && !!record.coverDisplayMetadata;
      if (hasVectorDisplay) {
        const geometry = appendCorridorToDisplayGeometry(record.coverDisplayGeometry!, ring, record.bounds, grasslandCodeFor(grid));
        const counts = inspectCoverDisplayGeometry(geometry);
        const prev = record.coverDisplayMetadata!;
        const metadata = coverDisplayMetadataOf(geometry, {
          ...counts,
          smoothingM: prev.smoothingM,
          simplifyM: prev.simplifyM,
          minFeatureM2: prev.minFeatureM2,
        });
        upgraded = { ...upgraded, coverDisplayGeometry: geometry, coverDisplayMetadata: metadata };
      }

      upgraded = { ...upgraded, packageManifest: manifestOf(upgraded) };
      const validation = validateTerrainPackage(upgraded);
      if (!validation.ok) {
        console.warn('Lift cover-clear produced an invalid package; keeping the previous cover.', validation.errors.join(' '));
        return;
      }
      const saved = await saveTerrain(upgraded);
      if (!saved.ok) {
        console.warn('Lift cover-clear could not be saved; keeping the previous cover.', saved.error);
        return;
      }

      cacheTerrainDisplayAssets(upgraded);
      setActiveResortTerrain(upgraded);
      if (hasVectorDisplay && coverDisplayRef.current) {
        setCoverData(map, coverDisplayRef.current);
      } else {
        // Raster fallback: refetch the resort-cover tiles from the mutated grid.
        clearResortCoverCache();
        const src = map.getSource('worldcover') as { setTiles?: (tiles: string[]) => void } | undefined;
        src?.setTiles?.([`${RESORT_COVER_PROTOCOL}://${encodeURIComponent(upgraded.key)}/{z}/{x}/{y}`]);
      }
      setTerrainRecord(upgraded);
    } catch (error) {
      console.warn('Lift cover-clear failed; keeping the previous cover.', error);
    }
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

  function armTrailTool() {
    if (siteModeRef.current === 'selecting') return;
    cancelLiftTool(); // yield the other draw tool
    setSelectedLiftId(null);
    setLiftEditing(false);
    setSelectedTrailId(null);
    setTrailEditing(false);
    setOpenDock('trails');
    trailCommandsRef.current = [];
    trailWorkerRecoveryRef.current = 0;
    startTrailWorker(brushWidthRef.current);
    setTrailTool({ phase: 'paint', mode: 'paint', polygons: [], areaM2: 0,
      activeAreaM2: null, canUndo: false, pending: false, error: null });
  }

  function cancelTrailTool() {
    trailSampleTokenRef.current++;
    trailWorkerRef.current?.terminate();
    trailWorkerRef.current = null;
    trailCommandsRef.current = [];
    trailPreviewPathRef.current = [];
    trailBrushCursorRef.current = null;
    if (mapRef.current) setTrailPaintPreview(mapRef.current, { path: [], cursor: null,
      brushWidthM: brushWidthRef.current });
    setTrailTool({ phase: 'idle' });
  }

  function startTrailWorker(widthM: number, replay: { mode: 'paint' | 'erase'; path: [number, number][] }[] = []) {
    trailWorkerRef.current?.terminate();
    const worker = new Worker(new URL('./trailPaint.worker.ts', import.meta.url), { type: 'module' });
    trailWorkerRef.current = worker;
    trailWorkerAppliedRef.current = 0;
    worker.onmessage = (event: MessageEvent<TrailPaintResponse>) => {
      const message = event.data;
      if (message.id < trailWorkerAppliedRef.current) return;
      trailWorkerAppliedRef.current = message.id;
      if (!message.ok) {
        if (trailToolRef.current.phase === 'paint' && trailToolRef.current.pending)
          trailCommandsRef.current.pop();
        setTrailTool((t) => t.phase === 'paint'
          ? { ...t, pending: false, activeAreaM2: null, error: message.error }
          : t.phase === 'analyzing' ? { phase: 'paint', mode: 'paint', polygons: t.polygons,
            areaM2: t.areaM2, activeAreaM2: null, canUndo: trailCommandsRef.current.length > 0,
            pending: false, error: message.error } : t);
        return;
      }
      if (message.type === 'ready' && replay.length > 0) {
        for (const command of replay) {
          const coordinates = new Float64Array(command.path.length * 2);
          command.path.forEach((point, i) => { coordinates[i * 2] = point[0]; coordinates[i * 2 + 1] = point[1]; });
          postTrailRequest({ type: 'stroke', mode: command.mode, coordinates }, [coordinates.buffer]);
        }
        return;
      }
      if (message.type === 'preview') {
        trailPreviewPathRef.current = [];
        if (mapRef.current) setTrailPaintPreview(mapRef.current, { path: [],
          cursor: trailBrushCursorRef.current, brushWidthM: brushWidthRef.current });
        setTrailTool((t) => t.phase === 'paint' ? { ...t, polygons: message.polygons,
          areaM2: message.areaM2, activeAreaM2: null, canUndo: message.canUndo, pending: false, error: null } : t);
      } else if (message.type === 'analysis') {
        if (message.parts.length === 0) {
          const current = trailToolRef.current;
          setTrailTool({ phase: 'paint', mode: 'paint',
            polygons: current.phase === 'analyzing' ? current.polygons : [],
            areaM2: current.phase === 'analyzing' ? current.areaM2 : 0,
            activeAreaM2: null, canUndo: trailCommandsRef.current.length > 0, pending: false,
            error: 'Paint a longer connected footprint so a centerline can be found.' });
          return;
        }
        const draft: DraftTrail = { parts: message.parts, areaM2: message.areaM2,
          brushWidthM: brushWidthRef.current, name: nextTrailName(trailsRef.current), status: 'planning',
          difficulty: 'blue', elevStatus: 'pending' };
        setTrailTool({ phase: 'review', draft });
        sampleTrailElevations(message.parts);
      }
    };
    worker.onerror = () => {
      if (trailWorkerRecoveryRef.current++ === 0) {
        const replayCommands = trailCommandsRef.current.map((command) => ({ ...command, path: command.path.slice() }));
        setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: replayCommands.length > 0,
          error: 'Restarting trail analysis…' } : t);
        startTrailWorker(widthM, replayCommands);
      } else setTrailTool((t) => t.phase === 'paint'
        ? { ...t, pending: false, error: 'Trail analysis worker stopped. Cancel and reopen the painter to retry.' } : t);
    };
    const map = mapRef.current;
    const center = map?.getCenter();
    const origin: [number, number] = center ? [center.lng, center.lat] : INITIAL_CENTER;
    postTrailRequest({ type: 'init', origin, brushWidthM: widthM });
  }

  function postTrailRequest(request: TrailPaintRequestPayload, transfer: Transferable[] = []) {
    const worker = trailWorkerRef.current;
    if (!worker) return;
    const message = { ...request, id: ++trailWorkerIdRef.current } as TrailPaintRequest;
    worker.postMessage(message, transfer);
  }

  function submitTrailStroke(path: [number, number][], mode: 'paint' | 'erase') {
    const coordinates = new Float64Array(path.length * 2);
    path.forEach((point, i) => { coordinates[i * 2] = point[0]; coordinates[i * 2 + 1] = point[1]; });
    setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: true, activeAreaM2: null } : t);
    postTrailRequest({ type: 'stroke', mode, coordinates }, [coordinates.buffer]);
  }

  function setTrailPaintModeState(mode: 'paint' | 'erase') {
    setTrailTool((t) => t.phase === 'paint' ? { ...t, mode } : t);
    if (mapRef.current) setTrailPaintMode(mapRef.current, mode);
  }

  function undoTrailPaint() {
    setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: true } : t);
    trailCommandsRef.current.pop();
    postTrailRequest({ type: 'undo' });
  }

  function clearTrailPaint() {
    trailCommandsRef.current = [];
    setTrailTool((t) => t.phase === 'paint' ? { ...t, pending: true } : t);
    postTrailRequest({ type: 'clear' });
  }

  function finishTrailPaint() {
    const t = trailToolRef.current;
    if (t.phase !== 'paint' || t.pending || t.areaM2 <= 0) return;
    setTrailTool({ phase: 'analyzing', polygons: t.polygons, areaM2: t.areaM2 });
    postTrailRequest({ type: 'finish' });
  }

  function changeTrailBrushWidth(widthM: number) {
    setBrushWidthM(widthM);
    const t = trailToolRef.current;
    if (t.phase === 'paint' && t.areaM2 === 0 && !t.pending) startTrailWorker(widthM);
  }

  function patchTrailDraft(patch: Partial<DraftTrail>) {
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, ...patch } } : t
    );
  }

  function retryTrailElevation() {
    const t = trailToolRef.current;
    if (t.phase === 'review') sampleTrailElevations(t.draft.parts);
  }

  function confirmTrail() {
    const t = trailToolRef.current;
    if (t.phase !== 'review') return;
    const d = t.draft;
    const stats = trailPartsStats(d.parts);
    const trail: SavedTrail = {
      id: genId(),
      name: d.name.trim() || nextTrailName(trailsRef.current),
      parts: d.parts,
      brushWidthM: d.brushWidthM,
      areaM2: d.areaM2,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      avgSlopeDeg: stats.avgSlopeDeg,
      maxSlopeDeg: stats.maxSlopeDeg,
      difficulty: d.difficulty,
      status: d.status,
      createdAt: new Date().toISOString(),
    };
    trailSampleTokenRef.current++;
    trailWorkerRef.current?.terminate();
    trailWorkerRef.current = null;
    setTrails((prev) => [...prev, trail]);
    setTrailTool({ phase: 'idle' });
  }

  /** Patch a non-geometric field (name/status) of a built run. */
  function patchTrail(id: string, patch: Partial<SavedTrail>) {
    setTrails((prev) => prev.map((t) => (t.id === id ? { ...t, ...patch } : t)));
  }

  function deleteTrail(id: string) {
    setTrails((prev) => prev.filter((t) => t.id !== id));
    setSelectedTrailId((cur) => (cur === id ? null : cur));
    setTrailEditing(false);
  }

  /** Close/open a bottom dock, yielding any active draw tool of the others. */
  function toggleDock(which: 'layers' | 'lifts' | 'trails') {
    const isOpen = which === 'layers' ? layersOpen : which === 'lifts' ? liftsOpen : trailsOpen;
    if (which !== 'lifts') {
      cancelLiftTool();
      setSelectedLiftId(null);
      setLiftEditing(false);
    }
    if (which !== 'trails') {
      cancelTrailTool();
      setSelectedTrailId(null);
      setTrailEditing(false);
    }
    if (isOpen) {
      if (which === 'lifts') {
        cancelLiftTool();
        setSelectedLiftId(null);
        setLiftEditing(false);
      }
      if (which === 'trails') {
        cancelTrailTool();
        setSelectedTrailId(null);
        setTrailEditing(false);
      }
      setOpenDock(null);
    } else {
      setOpenDock(which);
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
    const next = !is3D;
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
    setPackageProgress({ phase: 'elevation', message: 'Starting resort preparation', completed: 0, total: 10 });
    packageAbortRef.current?.abort();
    const controller = new AbortController();
    packageAbortRef.current = controller;
    mapRef.current?.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
    try {
      const record = await prepareResortPackage(site, name, setPackageProgress, controller.signal);
      const validation = validateTerrainPackage(record);
      if (!validation.ok) throw new Error(validation.errors.join(' '));
      cacheTerrainDisplayAssets(record);
      terrainRecordRef.current = record;
      setActiveResortTerrain(record);
      setTerrainRecord(record);
      packageStateRef.current = 'ready';
      setPackageState('ready');
      const map = mapRef.current;
      if (map) {
        // Veil the first resort render (restyle re-mounts terrain + custom
        // tiles); the style.load reveal drops it once fully drawn.
        setWarming(true);
        map.setStyle(basemapFor(resolvedTheme, { offline: mode === 'playing' }));
      }
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

  /** Snapshot the current camera + site + 3D into a GameSave shape. */
  function snapshot(base: GameSave | null): GameSave | null {
    const map = mapRef.current;
    if (!map) return base;
    const c = map.getCenter();
    const now = new Date().toISOString();
    return {
      schemaVersion: 2,
      key: base?.key ?? genId(),
      name: base?.name ?? (nameDraft.trim() || 'Untitled Resort'),
      mountainId: base?.mountainId,
      terrainKey: terrainRecordRef.current?.key ?? base?.terrainKey,
      center: [c.lng, c.lat],
      zoom: map.getZoom(),
      bearing: map.getBearing(),
      pitch: map.getPitch(),
      is3D: is3DRef.current,
      site: siteBoxRef.current,
      lifts: liftsRef.current,
      trails: trailsRef.current,
      createdAt: base?.createdAt ?? now,
      updatedAt: now,
    };
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
    const res = await saveGame(next);
    setSaving(false);
    if (res.ok) setSaved(next);
  }

  async function repairAndContinue() {
    const base = saved ?? initialSave;
    if (!base) return;
    const record = await prepareLocalPackage(base.name);
    if (!record) return;
    const next: GameSave = { ...base, terrainKey: record.key, updatedAt: new Date().toISOString() };
    const result = await saveGame(next);
    if (result.ok) setSaved(next);
    else {
      setPackageError(result.error);
      setPackageState('error');
    }
  }

  async function saveProgress() {
    const next = snapshot(saved);
    if (!next) return;
    setSaving(true);
    const res = await saveGame(next);
    setSaving(false);
    if (res.ok) setSaved(next);
  }

  /** Live-rename the resort; persists on the next Save (snapshot reads saved.name). */
  function renameResort(name: string) {
    setSaved((s) => (s ? { ...s, name } : s));
  }

  const picking = mode === 'picking';
  const awaitingName = picking && siteMode === 'locked' && !saved;

  // Lift panel is open when the user opened it OR the tool is mid-draw / a lift
  // is selected (detail or edit); layers yield to it so the two roll-ups never
  // overlap. selectedLift resolves the id to the live lift (null if it was
  // deleted out from under the selection).
  const liftActive = liftTool.phase !== 'idle' || selectedLiftId !== null;
  const trailActive = trailTool.phase !== 'idle' || selectedTrailId !== null;
  const liftsOpen = !!saved && (openDock === 'lifts' || liftActive);
  const trailsOpen = !!saved && !liftsOpen && (openDock === 'trails' || trailActive);
  const layersOpen = openDock === 'layers' && !liftsOpen && !trailsOpen;
  const selectedLift = selectedLiftId ? lifts.find((l) => l.id === selectedLiftId) ?? null : null;
  const selectedTrail = selectedTrailId ? trails.find((t) => t.id === selectedTrailId) ?? null : null;
  const showPackageGate = packageState !== 'ready' &&
    (mode === 'playing' || packageState === 'preparing' || packageState === 'error');

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
            <h2>{packageState === 'loading' ? 'Loading resort package' : packageState === 'optimizing' ? 'Optimizing ground cover' : packageState === 'preparing' ? 'Preparing resort data' : packageState === 'error' ? 'Preparation failed' : 'Resort data required'}</h2>
            <p>
              {packageState === 'preparing'
                ? 'Fetching terrain, ground cover, and contours for your build site.'
                : packageState === 'optimizing'
                ? 'Drawing smooth vector ground cover once for faster future loads.'
                : packageState === 'loading'
                ? 'Restoring your saved terrain, ground cover, and contours.'
                : packageError ?? 'Elevation, contours, and ground cover must be saved locally before designing.'}
            </p>
            {(packageState === 'loading' || packageState === 'optimizing') && (
              <div className="package-progress is-indeterminate"><span /></div>
            )}
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
            {(packageState === 'missing' || packageState === 'error') && (
              <div className="package-actions">
                <button className="site-btn" onClick={onQuit}>Back to menu</button>
                <button className="site-btn site-btn-primary" onClick={() => void (mode === 'playing' ? repairAndContinue() : createSave())}>
                  Prepare Resort Data
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {warming && !showPackageGate && (
        <div
          className="map-warming"
          role="status"
          aria-live="polite"
          style={{
            position: 'absolute',
            inset: 0,
            zIndex: 6,
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            gap: '14px',
            background: '#0e1a2a',
            color: '#e9eef6',
            font: '600 14px system-ui, sans-serif',
            letterSpacing: '0.03em',
          }}
        >
          <div>Preloading terrain…</div>
          {(() => {
            const total = warmProgress?.total ?? 0;
            const completed = warmProgress?.completed ?? 0;
            const pct = total > 0 ? Math.round((completed / total) * 100) : 0;
            const indeterminate = total === 0;
            return (
              <div style={{ width: 'min(320px, 60vw)' }}>
                <div className={`package-progress${indeterminate ? ' is-indeterminate' : ''}`}>
                  <span style={indeterminate ? undefined : { width: `${pct}%` }} />
                </div>
                {!indeterminate && <div className="package-progress-label">{pct}%</div>}
              </div>
            );
          })()}
        </div>
      )}

      {/* Top-right app menu (Save / Load / Settings / Credits / Main Menu) */}
      <GameMenu
        canSave={!!saved}
        saving={saving}
        onSave={saveProgress}
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
        {terrainRecord && <View3DControl is3D={is3D} onToggle={toggle3D} />}
      </div>

      {/* Site-picking readout floats lower-left; in-game it lives on the toolbar. */}
      {!saved && <CursorReadout readout={readout} units={settings.units} />}

      {/* Bottom dock: layers/lifts roll-up circles above the status toolbar */}
      {saved && (
        <div className="game-dock">
          <div className="dock-stack">
            {layersOpen && (
              <div className="dock-rollup dock-layers">
                <div className="dock-panel">
                  <div className="dock-head">
                    <span className="dock-head-title">Layers</span>
                    <button
                      className="settings-close-x"
                      aria-label="Close"
                      onClick={() => setOpenDock(null)}
                    >
                      ✕
                    </button>
                  </div>
                  <LayerList
                    layers={layers}
                    onToggle={handleToggle}
                    activeOverlay={activeOverlay}
                  />
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
            {trailsOpen && (
              <div className="dock-rollup dock-trails">
                <div className="dock-panel">
                  {trailTool.phase === 'idle' && selectedTrail && !trailEditing ? (
                    // Clicking a run opens its read-only detail first.
                    <TrailDetail
                      trail={selectedTrail}
                      units={settings.units}
                      onEdit={() => setTrailEditing(true)}
                      onRemove={() => deleteTrail(selectedTrail.id)}
                      onClose={() => {
                        setSelectedTrailId(null);
                        setOpenDock('trails');
                      }}
                    />
                  ) : trailTool.phase === 'idle' && !selectedTrail ? (
                    <TrailOverview
                      trails={trails}
                      units={settings.units}
                      onArm={armTrailTool}
                      onSelect={(id) => selectTrailRef.current(id)}
                      onClose={() => setOpenDock(null)}
                    />
                  ) : (
                    // Paint / review a new run, or edit the selected one.
                    <TrailControl
                      tool={trailTool}
                      trails={trails}
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
                      onConfirm={confirmTrail}
                      onEditPatch={patchTrail}
                      onCloseEdit={() => setTrailEditing(false)}
                      onDelete={deleteTrail}
                      onRetryElevation={retryTrailElevation}
                    />
                  )}
                </div>
              </div>
            )}

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
          center={resortCenter()}
          units={settings.units}
          onClose={() => setShowStats(false)}
        />
      )}

      {showCredits && <CreditsPanel onClose={() => setShowCredits(false)} />}
    </>
  );
}
