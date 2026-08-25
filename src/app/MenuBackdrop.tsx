import { useEffect, useRef, useState } from 'react';
import maplibregl from 'maplibre-gl';
import type { SkySpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { basemapFor, tuneBasemap } from './basemapStyle';
import { useSettings } from './SettingsContext';
import { pixelRatioForElement, renderProfileFor, type RenderQuality } from './renderProfile';
import { applyTileLod } from './terrainLod';

const CRYSTAL: [number, number] = [-121.474, 46.928];
const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';
const MENU_TERRAIN_DEM = 'menu-terrain-dem';
const MENU_HILLSHADE_DEM = 'menu-hillshade-dem';
const MENU_PITCH = 70;

const ALPINE_SKY: SkySpecification = {
  'sky-color': '#5f9ed6',
  'horizon-color': '#eef4fb',
  'fog-color': '#dce7f0',
  'sky-horizon-blend': 0.6,
  'horizon-fog-blend': 0.7,
  'fog-ground-blend': 0.8,
  'atmosphere-blend': 0,
};

function demSource(quality: RenderQuality): maplibregl.RasterDEMSourceSpecification {
  return {
    type: 'raster-dem',
    tiles: [TERRARIUM_TILES],
    encoding: 'terrarium',
    tileSize: 256,
    maxzoom: renderProfileFor(quality).terrainMaxZoom,
  };
}

function setupTerrain(map: maplibregl.Map, quality: RenderQuality): void {
  const profile = renderProfileFor(quality);
  tuneBasemap(map);
  for (const layer of map.getStyle().layers ?? []) {
    if (layer.type === 'symbol') map.setLayoutProperty(layer.id, 'visibility', 'none');
  }
  if (!map.getSource(MENU_TERRAIN_DEM)) map.addSource(MENU_TERRAIN_DEM, demSource(quality));
  map.setTerrain({ source: MENU_TERRAIN_DEM, exaggeration: 1.0 });
  map.setSky(ALPINE_SKY);

  if (profile.hillshade === 'full' && !map.getSource(MENU_HILLSHADE_DEM)) {
    map.addSource(MENU_HILLSHADE_DEM, demSource(quality));
  }
  if (profile.hillshade === 'full' && !map.getLayer('menu-hillshade')) {
    map.addLayer({
      id: 'menu-hillshade',
      type: 'hillshade',
      source: MENU_HILLSHADE_DEM,
      paint: { 'hillshade-exaggeration': 0.6 },
    });
  } else if (map.getLayer('menu-hillshade')) {
    map.setLayoutProperty(
      'menu-hillshade',
      'visibility',
      profile.hillshade === 'full' ? 'visible' : 'none',
    );
  }
  applyTileLod(map, quality);
}

export function MenuBackdrop({ onReady }: { onReady?: () => void }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const { resolvedTheme, settings } = useSettings();
  const qualityRef = useRef(settings.renderQuality);
  qualityRef.current = settings.renderQuality;
  const profile = renderProfileFor(settings.renderQuality);
  const cssOnly = profile.menu === 'css';
  const [ready, setReady] = useState(false);

  useEffect(() => {
    if (cssOnly) {
      setReady(true);
      onReady?.();
      return;
    }
    if (mapRef.current || !containerRef.current) return;
    setReady(false);
    const container = containerRef.current;
    const map = new maplibregl.Map({
      container,
      style: basemapFor(resolvedTheme),
      center: CRYSTAL,
      zoom: 15,
      bearing: -18,
      pitch: MENU_PITCH,
      maxPitch: 85,
      pixelRatio: pixelRatioForElement(qualityRef.current, container),
      interactive: false,
      attributionControl: false,
    });
    mapRef.current = map;
    (window as unknown as { menuMap: maplibregl.Map }).menuMap = map;

    let reportedReady = false;
    let readyTimeout = 0;
    const reportReady = () => {
      if (reportedReady) return;
      reportedReady = true;
      setReady(true);
      onReady?.();
    };
    const onStyle = () => {
      setupTerrain(map, qualityRef.current);
      map.once('render', reportReady);
      readyTimeout = window.setTimeout(reportReady, 800);
    };
    map.on('style.load', onStyle);

    return () => {
      map.off('style.load', onStyle);
      window.clearTimeout(readyTimeout);
      map.remove();
      mapRef.current = null;
      delete (window as unknown as { menuMap?: maplibregl.Map }).menuMap;
    };
    // Crossing the CSS-only boundary owns map creation/removal. Theme does not alter the map style.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cssOnly]);

  useEffect(() => {
    const map = mapRef.current;
    const container = containerRef.current;
    if (!map || !container) return;
    const update = () => {
      const next = pixelRatioForElement(settings.renderQuality, container);
      if (Math.abs(map.getPixelRatio() - next) > 0.001) map.setPixelRatio(next);
      if (map.isStyleLoaded()) setupTerrain(map, settings.renderQuality);
    };
    update();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update);
    observer?.observe(container);
    window.addEventListener('resize', update);
    return () => {
      observer?.disconnect();
      window.removeEventListener('resize', update);
    };
  }, [settings.renderQuality]);

  useEffect(() => {
    const map = mapRef.current;
    const menuMode = renderProfileFor(settings.renderQuality).menu;
    if (!map || settings.reducedMotion || !ready || menuMode === 'still' || menuMode === 'css') return;
    let raf = 0;
    let last = performance.now();
    const minFrameMs = menuMode === 'drift-30' ? 1000 / 30 : 1000 / 60;
    const tick = (time: number) => {
      const elapsed = time - last;
      if (document.hidden) {
        last = time;
      } else if (elapsed >= minFrameMs) {
        last = time;
        map.setBearing(map.getBearing() + elapsed * 0.00035);
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [ready, settings.reducedMotion, settings.renderQuality]);

  return (
    <div className={`menu-backdrop${cssOnly ? ' menu-backdrop-css' : ''}`}>
      {!cssOnly && <div ref={containerRef} className="menu-backdrop-map" />}
      <div className="menu-backdrop-scrim" />
    </div>
  );
}
