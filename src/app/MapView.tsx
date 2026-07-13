import { useEffect, useRef, useState } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { setupAnalysisLayers, type LayerToggle } from './analysisLayers';
import { LayerPanel } from './LayerPanel';
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
import { useSettings } from './SettingsContext';
import { saveGame } from '../gameSaveClient';
import type { GameSave } from '../types';

// Crystal Mountain, WA — our canonical test site (used as the New Game start).
const INITIAL_CENTER: [number, number] = [-121.474, 46.928];
const INITIAL_ZOOM = 12;

export type MapMode = 'picking' | 'playing';

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
  const on = layers.find((l) => l.exclusiveGroup === 'overlay' && l.visible);
  return (on?.id as OverlayId) ?? null;
}

interface MapViewProps {
  mode: MapMode;
  /** Present when resuming a saved resort (Load / Continue). */
  initialSave?: GameSave | null;
  onQuit: () => void;
  onOpenSettings: () => void;
}

/**
 * Owns the MapLibre instance. The map lives in a ref (never React state); React
 * state holds the layer-toggle UI model, cursor readout, and game/site status.
 */
export function MapView({ mode, initialSave = null, onQuit, onOpenSettings }: MapViewProps) {
  const { settings, resolvedTheme } = useSettings();

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const [layers, setLayers] = useState<LayerToggle[]>([]);
  const [panelOpen, setPanelOpen] = useState(true);
  const [readout, setReadout] = useState<Readout | null>(null);
  const [siteMode, setSiteMode] = useState<SiteMode>(initialSave?.site ? 'locked' : 'explore');
  const [siteBox, setSiteBoxState] = useState<SiteBox | null>((initialSave?.site as SiteBox) ?? null);
  const [is3D, setIs3D] = useState(initialSave?.is3D ?? false);
  const [saved, setSaved] = useState<GameSave | null>(initialSave);
  const [nameDraft, setNameDraft] = useState('');
  const [saving, setSaving] = useState(false);

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

  layersRef.current = layers;
  siteBoxRef.current = siteBox;
  siteModeRef.current = siteMode;
  is3DRef.current = is3D;

  // The actual sampler — redefined each render so it closes over fresh state.
  doSampleRef.current = (lngLat) => {
    const map = mapRef.current;
    if (!map) return;
    const z = Math.min(14, Math.max(10, Math.round(map.getZoom())));
    const overlay = activeOverlayRef.current;
    const token = ++sampleTokenRef.current;
    (async () => {
      const t = await sampleTerrainAt(lngLat.lng, lngLat.lat, z).catch(() => null);
      if (!t || token !== sampleTokenRef.current) return;
      let coverLabel: string | null = null;
      if (overlay === 'groundcover') {
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

  // (Re)attach analysis layers + site box + 3D after any style (re)load. Shared
  // by the initial load and the light<->dark basemap swap. Reads live state from
  // refs and re-applies the current layer-visibility model.
  function reinitAfterStyle(map: maplibregl.Map) {
    tuneBasemap(map);
    const fresh = setupAnalysisLayers(map);
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
    addSiteBoxLayers(map);
    if (siteModeRef.current === 'locked' && siteBoxRef.current) {
      setSiteBox(map, siteBoxRef.current);
      setBoundaryMode(map, 'locked', siteBoxRef.current);
    }
    if (is3DRef.current) enable3D(map);
    setLayers(applied);
  }

  // Create the map once.
  useEffect(() => {
    if (mapRef.current || !containerRef.current) return;

    const start = initialSave ?? null;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: basemapFor(resolvedTheme),
      center: start ? start.center : INITIAL_CENTER,
      zoom: start ? start.zoom : INITIAL_ZOOM,
      bearing: start?.bearing ?? 0,
      pitch: start?.pitch ?? 0,
      attributionControl: { compact: false },
    });
    mapRef.current = map;
    // Exposed for the Playwright verification harness (readyGlobal: "appMap").
    (window as unknown as { appMap: maplibregl.Map }).appMap = map;

    map.dragRotate.enable();
    map.keyboard.enable();
    map.addControl(
      new maplibregl.NavigationControl({ visualizePitch: true, showZoom: true, showCompass: true }),
      'bottom-right'
    );

    map.on('load', () => {
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

    return () => {
      map.remove();
      mapRef.current = null;
      setLayers([]);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
    map.once('style.load', () => reinitAfterStyle(map));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resolvedTheme]);

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
    else enable3D(map);
    setIs3D((v) => !v);
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
      terrainKey: base?.terrainKey,
      center: [c.lng, c.lat],
      zoom: map.getZoom(),
      bearing: map.getBearing(),
      pitch: map.getPitch(),
      is3D: is3DRef.current,
      site: siteBoxRef.current,
      lifts: base?.lifts ?? [],
      trails: base?.trails ?? [],
      createdAt: base?.createdAt ?? now,
      updatedAt: now,
    };
  }

  async function createSave() {
    const next = snapshot(null);
    if (!next) return;
    setSaving(true);
    const res = await saveGame(next);
    setSaving(false);
    if (res.ok) setSaved(next);
  }

  async function saveProgress() {
    const next = snapshot(saved);
    if (!next) return;
    setSaving(true);
    const res = await saveGame(next);
    setSaving(false);
    if (res.ok) setSaved(next);
  }

  const picking = mode === 'picking';
  const awaitingName = picking && siteMode === 'locked' && !saved;

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

      {/* Top-left game HUD (playing) or "pick a site" hint (new game) */}
      <div className="game-hud">
        <button className="ghost-btn hud-quit" onClick={onQuit} title="Back to Menu">
          ‹ Menu
        </button>
        {saved && <span className="hud-resort">{saved.name}</span>}
        {saved && (
          <button className="hud-save" onClick={saveProgress} disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </button>
        )}
        <button className="ghost-btn hud-settings" onClick={onOpenSettings} title="Settings" aria-label="Settings">
          ⚙
        </button>
      </div>

      {picking && !saved && <SearchBox onResult={handleSearchResult} />}
      <LayerPanel
        layers={layers}
        onToggle={handleToggle}
        open={panelOpen}
        onToggleOpen={() => setPanelOpen((o) => !o)}
        activeOverlay={activeOverlay}
      />
      <CursorReadout readout={readout} units={settings.units} />
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
    </>
  );
}
