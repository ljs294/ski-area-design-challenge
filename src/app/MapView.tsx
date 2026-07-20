import { useEffect, useRef, useState } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { setupAnalysisLayers, type LayerToggle } from './analysisLayers';
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
  computeOuterBounds,
  type SiteBox,
} from './sitePicker';
import { SearchBox, type GeocodeResult } from './SearchBox';
import { tuneBasemap, basemapFor } from './basemapStyle';
import { View3DControl } from './View3DControl';
import { enable3D, disable3D } from './terrain3d';
import { useSettings, pixelRatioFor } from './SettingsContext';
import { applyTileLod } from './terrainLod';
import { saveGame } from '../gameSaveClient';
import type { GameSave, SavedLift, SavedTrail, TerrainPackageProgress, TerrainRecord } from '../types';
import { loadTerrain } from '../terrainStorageClient';
import { prepareResortPackage } from '../terrainIngest';
import { validateTerrainPackage } from '../terrainPackage';
import { sampleLocalCoverAt, sampleLocalTerrainAt, setActiveResortTerrain, WORLD_COVER_LABELS } from './resortProtocols';
import { LiftControl, type LiftTool, type DraftLift } from './LiftControl';
import { addLiftLayers, setLiftData, liftsToGeoJSON, type DraftLine } from './liftLayers';
import { TrailControl, type TrailTool, type DraftTrail } from './TrailControl';
import { TrailOverview } from './TrailOverview';
import { TrailDetail } from './TrailDetail';
import {
  addTrailLayers,
  setTrailData,
  setTrailPaintWidth,
  trailsToGeoJSON,
  type TrailReview,
} from './trailLayers';
import { strokeToPolygon, resampleSpine } from './trailBrush';
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
  trailStats,
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

// Reject a painted run shorter than this — a stray click/tiny drag isn't a run.
const MIN_TRAIL_M = 30;

// Minimum brush-path spacing (m) between stored points — decimates the raw
// mousemove stream so a stroke stays a few dozen points, not thousands.
const BRUSH_PATH_GAP_M = 4;

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

/** The run being reviewed, for map rendering, if any. */
function trailReviewOf(tool: TrailTool): TrailReview | null {
  if (tool.phase === 'review') {
    const d = tool.draft;
    return { polygon: d.polygon, spine: d.spine, difficulty: d.difficulty, name: d.name };
  }
  return null;
}

/** The live brush path to preview, if painting. */
function paintPathOf(tool: TrailTool): [number, number][] | null {
  return tool.phase === 'painting' ? tool.path : null;
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
  const [saved, setSaved] = useState<GameSave | null>(initialSave);
  const [nameDraft, setNameDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const [terrainRecord, setTerrainRecord] = useState<TerrainRecord | null>(null);
  const [packageState, setPackageState] = useState<'ready' | 'loading' | 'missing' | 'preparing' | 'error'>(
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
  const liftsRef = useRef<SavedLift[]>(lifts);
  const liftToolRef = useRef<LiftTool>(liftTool);
  const liftSampleTokenRef = useRef(0);
  const selectLiftRef = useRef<(id: string) => void>(() => {});
  const trailsRef = useRef<SavedTrail[]>(trails);
  const trailToolRef = useRef<TrailTool>(trailTool);
  const trailSampleTokenRef = useRef(0);
  const selectTrailRef = useRef<(id: string) => void>(() => {});
  const brushWidthRef = useRef(brushWidthM);
  const renderQualityRef = useRef(settings.renderQuality);
  const packageAbortRef = useRef<AbortController | null>(null);
  // Loaded local package backing cursor sampling, MapLibre protocols, and
  // style reinitialization. Gameplay never populates it from network data.
  const terrainRecordRef = useRef<TerrainRecord | null>(null);

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

  useEffect(() => () => packageAbortRef.current?.abort(), []);

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
    void loadTerrain(key).then((record) => {
      if (cancelled) return;
      if (!record) {
        setPackageError('The local resort package is missing. Prepare it again to continue.');
        setPackageState('missing');
        return;
      }
      const validation = validateTerrainPackage(record);
      if (!validation.ok) {
        setPackageError(validation.errors.join(' '));
        setPackageState('error');
        return;
      }
      setActiveResortTerrain(record);
      setTerrainRecord(record);
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
      : setupAnalysisLayers(map, terrainRecordRef.current, settings.units);
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

    addSiteBoxLayers(map);
    if (siteModeRef.current === 'locked' && siteBoxRef.current) {
      setSiteBox(map, siteBoxRef.current);
      setBoundaryMode(map, 'locked', siteBoxRef.current);
    }
    // Runs beneath lifts (ski-map convention): add trails first, lifts on top.
    addTrailLayers(map);
    setTrailData(
      map,
      trailsToGeoJSON(trailsRef.current, trailReviewOf(trailToolRef.current), paintPathOf(trailToolRef.current))
    );
    addLiftLayers(map);
    setLiftData(map, liftsToGeoJSON(liftsRef.current, draftLineOf(liftToolRef.current)));
    if (is3DRef.current) enable3D(map);
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
      style: basemapFor(resolvedTheme),
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
      // A resumed site locks the pannable area to its context ring.
      if (siteModeRef.current === 'locked' && siteBoxRef.current) {
        map.setMaxBounds(computeOuterBounds(siteBoxRef.current));
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
    map.setStyle(basemapFor(resolvedTheme));
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
    if (map) map.setStyle(basemapFor(resolvedTheme));
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

  // Push run + draft + live-brush geometry into the trail source.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    setTrailData(map, trailsToGeoJSON(trails, trailReviewOf(trailTool), paintPathOf(trailTool)));
  }, [trails, trailTool]);

  // Keep the live brush-preview width (px) in sync with the brush size (m).
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (trailTool.phase === 'armed' || trailTool.phase === 'painting') {
      setTrailPaintWidth(map, brushWidthM / metersPerPixel(map));
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

  /** Sample terrain elevation at every spine station, orient the run top→bottom,
   *  and recommend a difficulty from the resulting slopes. Token-guarded. */
  function sampleTrailElevations(spine: [number, number][]) {
    const map = mapRef.current;
    const z = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++trailSampleTokenRef.current;
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, elevStatus: 'pending' } } : t
    );
    void Promise.all(spine.map(([lng, lat]) => samplePlanningTerrain(lng, lat, z))).then(
      (samples) => {
        if (token !== trailSampleTokenRef.current) return;
        const o = orientTopToBottom(spine, samples.map((s) => s.elevation));
        const stats = trailStats(o.spine, o.elevM);
        const recommended = difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg);
        setTrailTool((t) =>
          t.phase === 'review'
            ? {
                phase: 'review',
                draft: {
                  ...t.draft,
                  spine: o.spine,
                  spineElevM: o.elevM,
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

  // Trail brush: drag across the slope to paint a run. dragPan is disabled while
  // armed/painting so the drag paints instead of panning; on mouse-up the stroke
  // is traced into a run polygon (strokeToPolygon) and resampled into a spine,
  // then reviewed. Escape cancels. The live preview width tracks zoom.
  //
  // Keyed on a stable "drawing" flag (not trailTool.phase) so the armed→painting
  // transition on mousedown does NOT tear down and re-attach the drag listeners
  // mid-stroke — the local painting/path closure must survive the whole drag.
  const trailDrawing = trailTool.phase === 'armed' || trailTool.phase === 'painting';
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !trailDrawing) return;

    map.dragPan.disable();
    map.doubleClickZoom.disable();
    map.getCanvas().style.cursor = 'crosshair';
    const syncWidth = () => setTrailPaintWidth(map, brushWidthRef.current / metersPerPixel(map));
    syncWidth();
    map.on('zoom', syncWidth);

    let painting = false;
    let path: [number, number][] = [];

    const finish = () => {
      painting = false;
      let strokeM = 0;
      for (let i = 1; i < path.length; i++) strokeM += haversineMeters(path[i - 1], path[i]);
      if (path.length < 2 || strokeM < MIN_TRAIL_M) {
        setTrailTool({ phase: 'armed' }); // too small — let them try again
        return;
      }
      const width = brushWidthRef.current;
      const polygon = strokeToPolygon(path, width);
      if (polygon.length === 0) {
        setTrailTool({ phase: 'armed' });
        return;
      }
      const spine = resampleSpine(path);
      setTrailTool({
        phase: 'review',
        draft: {
          polygon,
          spine,
          spineElevM: [],
          elevStatus: 'pending',
          brushWidthM: width,
          name: nextTrailName(trailsRef.current),
          status: 'planning',
          difficulty: 'blue',
        },
      });
      sampleTrailElevations(spine);
    };

    const down = (e: maplibregl.MapMouseEvent) => {
      painting = true;
      path = [[e.lngLat.lng, e.lngLat.lat]];
      setTrailTool({ phase: 'painting', path });
    };
    const move = (e: maplibregl.MapMouseEvent) => {
      if (!painting) return;
      const p: [number, number] = [e.lngLat.lng, e.lngLat.lat];
      if (haversineMeters(path[path.length - 1], p) < BRUSH_PATH_GAP_M) return;
      path = [...path, p];
      setTrailTool({ phase: 'painting', path });
    };
    const up = () => {
      if (painting) finish();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        painting = false;
        setTrailTool({ phase: 'idle' });
      }
    };

    map.on('mousedown', down);
    map.on('mousemove', move);
    map.on('mouseup', up);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('mousedown', down);
      map.off('mousemove', move);
      map.off('mouseup', up);
      map.off('zoom', syncWidth);
      window.removeEventListener('keydown', onKey);
      map.dragPan.enable();
      map.doubleClickZoom.enable();
      map.getCanvas().style.cursor = '';
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

  // Backfill spine elevations for runs saved offline (empty spineElevM), so their
  // profiles + slope grades resolve on next load. Idempotent; keyed by run id.
  useEffect(() => {
    const missing = trailsRef.current.filter((t) => t.spineElevM.length !== t.spine.length);
    if (missing.length === 0) return;
    let stale = false;
    void Promise.allSettled(
      missing.map(async (t) => {
        const samples = await Promise.all(t.spine.map(([lng, lat]) => samplePlanningTerrain(lng, lat, 13)));
        return { id: t.id, elevM: samples.map((s) => s.elevation) };
      })
    ).then((results) => {
      if (stale) return;
      const byId = new Map<string, number[]>();
      for (const r of results) if (r.status === 'fulfilled') byId.set(r.value.id, r.value.elevM);
      if (byId.size === 0) return;
      setTrails((prev) =>
        prev.map((t) => {
          const elevM = byId.get(t.id);
          if (!elevM) return t;
          const o = orientTopToBottom(t.spine, elevM);
          const stats = trailStats(o.spine, o.elevM);
          return {
            ...t,
            spine: o.spine,
            spineElevM: o.elevM,
            lengthM: stats.lengthM,
            verticalM: stats.verticalM,
            avgSlopeDeg: stats.avgSlopeDeg,
            maxSlopeDeg: stats.maxSlopeDeg,
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
    setOpenDock('trails'); // keep the Trails dock open after each run is built
    setTrailTool({ phase: 'armed' });
  }

  function cancelTrailTool() {
    trailSampleTokenRef.current++; // discard any in-flight sampling
    setTrailTool({ phase: 'idle' });
  }

  function patchTrailDraft(patch: Partial<DraftTrail>) {
    setTrailTool((t) =>
      t.phase === 'review' ? { phase: 'review', draft: { ...t.draft, ...patch } } : t
    );
  }

  function retryTrailElevation() {
    const t = trailToolRef.current;
    if (t.phase === 'review') sampleTrailElevations(t.draft.spine);
  }

  function confirmTrail() {
    const t = trailToolRef.current;
    if (t.phase !== 'review') return;
    const d = t.draft;
    const stats = trailStats(d.spine, d.spineElevM);
    const trail: SavedTrail = {
      id: genId(),
      name: d.name.trim() || nextTrailName(trailsRef.current),
      polygon: d.polygon,
      spine: d.spine,
      brushWidthM: d.brushWidthM,
      spineElevM: d.spineElevM,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      avgSlopeDeg: stats.avgSlopeDeg,
      maxSlopeDeg: stats.maxSlopeDeg,
      difficulty: d.difficulty,
      status: d.status,
      createdAt: new Date().toISOString(),
    };
    trailSampleTokenRef.current++;
    setTrails((prev) => [...prev, trail]);
    setTrailTool({ phase: 'idle' });
  }

  /** Patch a non-geometric field (name/rating/status) of a built run. */
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
    const outer = computeOuterBounds(siteBox);
    setBoundaryMode(map, 'locked', siteBox);
    map.setMaxBounds(outer);
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
    if (!map) return;
    if (is3D) disable3D(map);
    else {
      enable3D(map);
      applyTileLod(map, renderQualityRef.current); // new terrain-dem source needs the curve
    }
    setIs3D((v) => !v);
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
    setPackageProgress({ phase: 'elevation', message: 'Starting resort preparation', completed: 0, total: 6 });
    packageAbortRef.current?.abort();
    const controller = new AbortController();
    packageAbortRef.current = controller;
    mapRef.current?.setStyle(basemapFor(resolvedTheme));
    try {
      const record = await prepareResortPackage(site, name, setPackageProgress, controller.signal);
      const validation = validateTerrainPackage(record);
      if (!validation.ok) throw new Error(validation.errors.join(' '));
      terrainRecordRef.current = record;
      setActiveResortTerrain(record);
      setTerrainRecord(record);
      packageStateRef.current = 'ready';
      setPackageState('ready');
      const map = mapRef.current;
      if (map) {
        map.setStyle(basemapFor(resolvedTheme));
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
      schemaVersion: 1,
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
          <div className="package-card">
            <div className="package-kicker">LOCAL RESORT DATA</div>
            <h2>{packageState === 'loading' ? 'Loading resort package' : packageState === 'preparing' ? 'Preparing resort data' : 'Resort data required'}</h2>
            <p>
              {packageState === 'preparing'
                ? packageProgress?.message ?? 'Preparing terrain and ground cover'
                : packageError ?? 'Elevation, contours, and ground cover must be saved locally before designing.'}
            </p>
            {packageState === 'preparing' && packageProgress && (
              <>
                <div className="package-progress"><span style={{ width: `${Math.round((packageProgress.completed / packageProgress.total) * 100)}%` }} /></div>
                <div className="package-progress-label">Step {Math.min(packageProgress.total, packageProgress.completed + 1)} of {packageProgress.total}</div>
                <div className="package-actions">
                  <button className="site-btn" onClick={cancelPackagePreparation}>Cancel</button>
                </div>
              </>
            )}
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

      {/* Top-right app menu (Save / Load / Settings / Credits / Main Menu) */}
      <GameMenu
        canSave={!!saved}
        saving={saving}
        onSave={saveProgress}
        onLoad={onLoadGame}
        onSettings={onOpenSettings}
        onCredits={() => setShowCredits(true)}
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
        <View3DControl is3D={is3D} onToggle={toggle3D} />
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
                      onBrushWidthChange={setBrushWidthM}
                      onCancel={cancelTrailTool}
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
