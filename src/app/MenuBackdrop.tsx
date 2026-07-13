import { useEffect, useRef } from 'react';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { basemapFor, tuneBasemap } from './basemapStyle';
import { useSettings } from './SettingsContext';

// Crystal Mountain, WA — our testbed. The menu sits over a live, slowly drifting
// shaded-relief map of it, dimmed by a scrim so the sign stays legible. We use
// hillshade (a raster layer) rather than a 3D terrain mesh: it reads as crisp
// topography and avoids the terrain-mesh shader path, which is fragile on some
// GPUs / software WebGL.
const CRYSTAL: [number, number] = [-121.474, 46.928];

const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';
const MENU_DEM = 'menu-dem';

/** (Re)attach the hillshade relief. Runs on every style (re)load. */
function setupRelief(map: maplibregl.Map): void {
  tuneBasemap(map);
  if (!map.getSource(MENU_DEM)) {
    map.addSource(MENU_DEM, {
      type: 'raster-dem',
      tiles: [TERRARIUM_TILES],
      encoding: 'terrarium',
      tileSize: 256,
      maxzoom: 15,
    });
  }
  if (!map.getLayer('menu-hillshade')) {
    // Insert below the first symbol layer so place labels stay on top.
    const firstSymbol = (map.getStyle().layers ?? []).find((l) => l.type === 'symbol')?.id;
    map.addLayer(
      { id: 'menu-hillshade', type: 'hillshade', source: MENU_DEM, paint: { 'hillshade-exaggeration': 0.6 } },
      firstSymbol
    );
  }
}

export function MenuBackdrop() {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const { resolvedTheme, settings } = useSettings();
  const reduced = settings.reducedMotion;

  // Create the map once. Relief is (re)added on every 'style.load'.
  useEffect(() => {
    if (mapRef.current || !containerRef.current) return;
    const map = new maplibregl.Map({
      container: containerRef.current,
      style: basemapFor(resolvedTheme),
      center: CRYSTAL,
      zoom: 12.4,
      bearing: -18,
      interactive: false,
      attributionControl: false,
    });
    mapRef.current = map;
    // Exposed for the Playwright verification harness (readyGlobal: "menuMap").
    (window as unknown as { menuMap: maplibregl.Map }).menuMap = map;

    const onStyle = () => setupRelief(map);
    map.on('style.load', onStyle);

    return () => {
      map.off('style.load', onStyle);
      map.remove();
      mapRef.current = null;
    };
  }, []);

  // Swap basemap when the theme changes (relief re-adds via the style.load handler).
  useEffect(() => {
    mapRef.current?.setStyle(basemapFor(resolvedTheme));
  }, [resolvedTheme]);

  // Slow drift (a gentle bearing sweep), paused for reduced-motion.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || reduced) return;
    let raf = 0;
    let last = performance.now();
    const tick = (t: number) => {
      const dt = t - last;
      last = t;
      map.setBearing(map.getBearing() + dt * 0.0012); // ~0.07°/s
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [reduced]);

  return (
    <div className="menu-backdrop">
      <div ref={containerRef} className="menu-backdrop-map" />
      <div className="menu-backdrop-scrim" />
    </div>
  );
}
