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
import { tuneBasemap } from './basemapStyle';

// Crystal Mountain, WA — our canonical test site.
const INITIAL_CENTER: [number, number] = [-121.474, 46.928];
const INITIAL_ZOOM = 12;

const BASEMAP_STYLE = 'https://tiles.openfreemap.org/styles/liberty';
const M_TO_FT = 3.28084;

/** The visible member of the mutually-exclusive overlay group, if any. */
function activeOverlayOf(layers: LayerToggle[]): OverlayId | null {
  const on = layers.find((l) => l.exclusiveGroup === 'overlay' && l.visible);
  return (on?.id as OverlayId) ?? null;
}

/**
 * Owns the MapLibre instance. The map lives in a ref (never React state) per
 * the plan; React state holds only the layer-toggle UI model + cursor readout.
 */
export function MapView() {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const [layers, setLayers] = useState<LayerToggle[]>([]);
  const [panelOpen, setPanelOpen] = useState(true);
  const [readout, setReadout] = useState<Readout | null>(null);
  const [siteMode, setSiteMode] = useState<SiteMode>('explore');
  const [siteBox, setSiteBoxState] = useState<SiteBox | null>(null);

  const activeOverlay = activeOverlayOf(layers);

  // Kept in refs so the once-registered mousemove handler always reads current values.
  const activeOverlayRef = useRef<OverlayId | null>(null);
  const lastLngLatRef = useRef<{ lng: number; lat: number } | null>(null);
  const sampleTokenRef = useRef(0);
  const rafPendingRef = useRef(false);
  const doSampleRef = useRef<(lngLat: { lng: number; lat: number }) => void>(() => {});

  // The actual sampler — redefined each render so it closes over fresh state,
  // then stashed in a ref for the map event handler to call.
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
        elevationFt: t.elevation * M_TO_FT,
        overlay,
        slopeDeg: t.slopeDeg,
        aspectCompass: compass8(t.aspectDeg),
        coverLabel,
      });
    })();
  };

  useEffect(() => {
    activeOverlayRef.current = activeOverlay;
    // Re-sample the last cursor position so the readout stat updates the moment
    // the active overlay changes (without waiting for the next mouse move).
    if (lastLngLatRef.current) doSampleRef.current(lastLngLatRef.current);
  }, [activeOverlay]);

  useEffect(() => {
    if (mapRef.current || !containerRef.current) return;

    const map = new maplibregl.Map({
      container: containerRef.current,
      style: BASEMAP_STYLE,
      center: INITIAL_CENTER,
      zoom: INITIAL_ZOOM,
      attributionControl: { compact: false },
    });
    mapRef.current = map;
    // Exposed for the Playwright verification harness (readyGlobal: "appMap").
    (window as unknown as { appMap: maplibregl.Map }).appMap = map;

    // Rotation via mouse (right/ctrl-drag) and keyboard (Shift+arrows) are on by
    // default; the compass in NavigationControl makes rotation discoverable and
    // resets north on click. Scale bar shows metric + imperial.
    map.dragRotate.enable();
    map.keyboard.enable();
    map.addControl(
      new maplibregl.NavigationControl({ visualizePitch: true, showZoom: true, showCompass: true }),
      'bottom-right'
    );
    map.addControl(new maplibregl.ScaleControl({ unit: 'metric' }), 'bottom-right');
    map.addControl(new maplibregl.ScaleControl({ unit: 'imperial' }), 'bottom-right');

    map.on('load', () => {
      tuneBasemap(map);
      setLayers(setupAnalysisLayers(map));
      addSiteBoxLayers(map);
    });

    // Throttle sampling to one run per animation frame using the latest position.
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
  }, []);

  // Drag-to-draw the site rectangle while in 'selecting' mode. Panning is
  // disabled so the drag draws instead of moving the map.
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
    // Property line stays the drawn box; the pannable area is a margin ring
    // around it so the property edge shows continuous terrain, not a cliff.
    const outer = computeOuterBounds(siteBox);
    setBoundaryMode(map, 'locked', siteBox);
    // maxBounds must be set before fitBounds so the fit respects the new limit.
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

  function handleToggle(id: string) {
    const map = mapRef.current;
    if (!map) return;
    setLayers((prev) => {
      const target = prev.find((l) => l.id === id);
      if (!target) return prev;
      const nextVisible = !target.visible;
      return prev.map((l) => {
        // Turning a layer on switches off any other in its exclusive group.
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
      <SearchBox onResult={handleSearchResult} />
      <LayerPanel
        layers={layers}
        onToggle={handleToggle}
        open={panelOpen}
        onToggleOpen={() => setPanelOpen((o) => !o)}
        activeOverlay={activeOverlay}
      />
      <CursorReadout readout={readout} />
      <SiteControl
        mode={siteMode}
        box={siteBox}
        onStart={startSelect}
        onConfirm={confirmSite}
        onCancel={cancelSelect}
        onExit={exitSite}
      />
    </>
  );
}
