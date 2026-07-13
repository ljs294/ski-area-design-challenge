import { useEffect, useRef, useState } from 'react';
import maplibregl from 'maplibre-gl';
import type { SkySpecification } from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { basemapFor, tuneBasemap } from './basemapStyle';
import { useSettings } from './SettingsContext';

// Crystal Mountain, WA — our testbed. The menu sits over a live, slowly drifting
// 3D relief of it. We raise a real terrain mesh (raster-dem + setTerrain) tilted
// under an alpine sky, drape a hillshade over it for extra crispness, and hide
// the place labels so it reads as scenery rather than a reference map.
const CRYSTAL: [number, number] = [-121.474, 46.928];

const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';
// Terrain and hillshade get separate raster-dem sources: MapLibre v5 renders
// worse (tile-reload flashes) when they share one. The HTTP cache dedupes the
// actual tile downloads.
const MENU_TERRAIN_DEM = 'menu-terrain-dem';
const MENU_HILLSHADE_DEM = 'menu-hillshade-dem';

// Big tilt so the horizon and sky band enter the top of the frame.
const MENU_PITCH = 70;

// Alpine look: deep blue zenith → pale horizon band, cool haze on distant ridges.
const ALPINE_SKY: SkySpecification = {
  'sky-color': '#5f9ed6',
  'horizon-color': '#eef4fb',
  'fog-color': '#dce7f0',
  'sky-horizon-blend': 0.6,
  'horizon-fog-blend': 0.7,
  'fog-ground-blend': 0.8,
  'atmosphere-blend': 0,
};

function demSource(): maplibregl.RasterDEMSourceSpecification {
  return {
    type: 'raster-dem',
    tiles: [TERRARIUM_TILES],
    encoding: 'terrarium',
    tileSize: 256,
    maxzoom: 15,
  };
}

/** (Re)attach 3D terrain, sky, hillshade, and hide labels. Runs on every style (re)load. */
function setupTerrain(map: maplibregl.Map): void {
  tuneBasemap(map);
  // Scenery, not a map — drop the place labels.
  for (const l of map.getStyle().layers ?? []) {
    if (l.type === 'symbol') map.setLayoutProperty(l.id, 'visibility', 'none');
  }
  if (!map.getSource(MENU_TERRAIN_DEM)) map.addSource(MENU_TERRAIN_DEM, demSource());
  if (!map.getSource(MENU_HILLSHADE_DEM)) map.addSource(MENU_HILLSHADE_DEM, demSource());
  map.setTerrain({ source: MENU_TERRAIN_DEM, exaggeration: 1.0 });
  map.setSky(ALPINE_SKY);
  if (!map.getLayer('menu-hillshade')) {
    map.addLayer({
      id: 'menu-hillshade',
      type: 'hillshade',
      source: MENU_HILLSHADE_DEM,
      paint: { 'hillshade-exaggeration': 0.6 },
    });
  }
}

export function MenuBackdrop({ onReady }: { onReady?: () => void }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const { resolvedTheme, settings } = useSettings();
  const reduced = settings.reducedMotion;
  // Held false until every tile has loaded and the first frame is fully rendered.
  const [ready, setReady] = useState(false);

  // Create the map once. Terrain/hillshade are (re)added on every 'style.load'.
  useEffect(() => {
    if (mapRef.current || !containerRef.current) return;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: basemapFor(resolvedTheme),
      center: CRYSTAL,
      zoom: 15,
      bearing: -18,
      pitch: MENU_PITCH,
      maxPitch: 85, // MENU_PITCH exceeds the default 60 cap
      interactive: false,
      attributionControl: false,
    });
    mapRef.current = map;
    // Exposed for the Playwright verification harness (readyGlobal: "menuMap").
    (window as unknown as { menuMap: maplibregl.Map }).menuMap = map;

    const onStyle = () => setupTerrain(map);
    map.on('style.load', onStyle);

    // 'idle' fires once all requested tiles (basemap + DEM) are loaded and the
    // scene has finished rendering — our cue that the backdrop is complete.
    map.once('idle', () => {
      setReady(true);
      onReady?.();
    });

    return () => {
      map.off('style.load', onStyle);
      map.remove();
      mapRef.current = null;
    };
  }, []);

  // Swap basemap when the theme changes (terrain re-adds via the style.load handler).
  useEffect(() => {
    mapRef.current?.setStyle(basemapFor(resolvedTheme));
  }, [resolvedTheme]);

  // Slow drift (a gentle bearing sweep). Starts only once the scene is ready, so
  // the continuous per-frame renders don't prevent the initial 'idle' from firing.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || reduced || !ready) return;
    let raf = 0;
    let last = performance.now();
    const tick = (t: number) => {
      const dt = t - last;
      last = t;
      map.setBearing(map.getBearing() + dt * 0.00035); // ~0.35°/s, gentle drift
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [reduced, ready]);

  return (
    <div className="menu-backdrop">
      <div ref={containerRef} className="menu-backdrop-map" />
      <div className="menu-backdrop-scrim" />
    </div>
  );
}
