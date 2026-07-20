import type maplibregl from 'maplibre-gl';
import type { SkySpecification } from 'maplibre-gl';
import { activeResortTerrain, localTileBounds, RESORT_DEM_PROTOCOL } from './resortProtocols';

// Same Terrarium tiles as the 'dem' source in analysisLayers.ts, but a
// dedicated source: MapLibre v5 warns (and renders worse, with tile-reload
// flashes) when hillshade and 3D terrain share one raster-dem source. The
// browser HTTP cache dedupes the actual downloads.
const TERRAIN_DEM_SOURCE = 'terrain-dem';
const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

// With the default fov (~37°) the horizon only enters the top of the frame at
// pitch ≈ 72; fog is fully faded in by 70. 75 shows fog plus a real sky band.
export const PITCH_3D = 60;
export const MAX_PITCH_3D = 85;
export const MAX_PITCH_2D = 60; // MapLibre default

// Alpine look: deep blue zenith → pale horizon band, cool haze on distant ridges.
const ALPINE_SKY: SkySpecification = {
  'sky-color': '#5f9ed6',
  'horizon-color': '#eef4fb',
  'fog-color': '#dce7f0',
  'sky-horizon-blend': 0.6,
  'horizon-fog-blend': 0.7,
  'fog-ground-blend': 0.8, // fog stays near the horizon; near terrain crisp
  'atmosphere-blend': 0, // globe-only; irrelevant on mercator
};

// MapLibre's own "sky off" representation (what Style.setSky(undefined) uses).
const SKY_OFF: SkySpecification = {
  'sky-color': 'transparent',
  'horizon-color': 'transparent',
  'fog-color': 'transparent',
  'fog-ground-blend': 1,
  'atmosphere-blend': 0,
};

// Pending disable finalizer, cancelled if the user re-enables mid-ease.
let pendingDisable: (() => void) | null = null;

export function enable3D(map: maplibregl.Map): void {
  if (pendingDisable) {
    map.off('moveend', pendingDisable);
    pendingDisable = null;
  }
  if (!map.getSource(TERRAIN_DEM_SOURCE)) {
    const local = activeResortTerrain();
    const key = local ? encodeURIComponent(local.key) : null;
    map.addSource(TERRAIN_DEM_SOURCE, {
      type: 'raster-dem',
      tiles: [key ? `${RESORT_DEM_PROTOCOL}://${key}/{z}/{x}/{y}` : TERRARIUM_TILES],
      encoding: 'terrarium',
      tileSize: 256,
      maxzoom: 15,
      ...(local ? { bounds: localTileBounds(local) } : {}),
      attribution: local ? 'Local resort elevation package' : 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
    });
  }
  map.setMaxPitch(MAX_PITCH_3D); // must precede easeTo — PITCH_3D exceeds the default cap
  map.setTerrain({ source: TERRAIN_DEM_SOURCE, exaggeration: 1.0 });
  map.setSky(ALPINE_SKY);
  map.easeTo({ pitch: PITCH_3D, duration: 1200 }); // center/zoom/bearing untouched
}

export function disable3D(map: maplibregl.Map): void {
  map.setSky(SKY_OFF);
  map.easeTo({ pitch: 0, duration: 1000 });
  // Drop terrain + restore maxPitch only after the ease lands: setMaxPitch(60)
  // at pitch 65 snaps the camera; removing terrain while pitched jumps elevation.
  pendingDisable = () => {
    pendingDisable = null;
    map.setTerrain(null);
    map.setMaxPitch(MAX_PITCH_2D);
  };
  map.once('moveend', pendingDisable);
}
