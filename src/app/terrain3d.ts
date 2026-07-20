import type maplibregl from 'maplibre-gl';
import type { SkySpecification } from 'maplibre-gl';
import { activeResortTerrain, resortDemBounds, RESORT_DEM_PROTOCOL } from './resortProtocols';

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

export const TILT_3D_MS = 1200;
export const TILT_2D_MS = 1000;

// Clean alpine sky: deep blue zenith → pale horizon band, and no ground fog.
// The perimeter ring renders real, hillshaded relief out to its 3 km edge and
// terminates in a crisp floating-clip cutoff (the DEM source simply ends at the
// ring bounds), so we deliberately do NOT haze the mid-field — the terrain reads
// sharp all the way to the edge. `fog-color` is pinned to the horizon colour and
// `fog-ground-blend` to 1 so any residual fog lives only in the thin horizon
// band and is indistinguishable from the sky, never washing over the terrain.
const ALPINE_SKY: SkySpecification = {
  'sky-color': '#5f9ed6',
  'horizon-color': '#eef4fb',
  'fog-color': '#eef4fb',
  'sky-horizon-blend': 0.6,
  'horizon-fog-blend': 0.1,
  'fog-ground-blend': 1, // fog confined to the horizon line; none over the terrain
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

// Mount the 3D terrain mesh + alpine sky and unlock the high pitch cap. Called
// once the resort package is active and re-called after every style reload
// (setStyle drops sources/terrain), so terrain is *always present* in the
// resort view. Because it is never added or removed on a 2D↔3D toggle, the
// switch is a pure camera move — no mid-animation re-tessellation, no elevation
// pop, no DEM-tile flash. Idempotent. At pitch 0 the mounted terrain looks flat
// (straight-down view has no horizon, so the sky doesn't render either); the
// relief simply reveals itself through perspective as the camera tilts.
export function mountTerrain(map: maplibregl.Map): void {
  if (!map.getSource(TERRAIN_DEM_SOURCE)) {
    const local = activeResortTerrain();
    const key = local ? encodeURIComponent(local.key) : null;
    map.addSource(TERRAIN_DEM_SOURCE, {
      type: 'raster-dem',
      tiles: [key ? `${RESORT_DEM_PROTOCOL}://${key}/{z}/{x}/{y}` : TERRARIUM_TILES],
      encoding: 'terrarium',
      tileSize: 256,
      maxzoom: 15,
      // Span the offline perimeter ring (falls back to the core) so MapLibre
      // requests DEM tiles past the property line: neighbouring relief renders
      // out to the ring edge, where the source ends in a clean floating clip.
      ...(local ? { bounds: resortDemBounds(local) } : {}),
      attribution: local ? 'Local resort elevation package' : 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
    });
  }
  map.setMaxPitch(MAX_PITCH_3D);
  map.setTerrain({ source: TERRAIN_DEM_SOURCE, exaggeration: 1.0 });
  map.setSky(ALPINE_SKY);
}

// Tear terrain back down — only for leaving the resort view (e.g. the flat
// worldwide picker or the verification harness). Not used by the 2D↔3D toggle.
export function unmountTerrain(map: maplibregl.Map): void {
  map.setTerrain(null);
  map.setSky(SKY_OFF);
  map.setMaxPitch(MAX_PITCH_2D);
  if (map.getSource(TERRAIN_DEM_SOURCE)) map.removeSource(TERRAIN_DEM_SOURCE);
}

/** Ease the camera between the 3D-native tilt and a perfectly overhead view.
 *  Terrain stays mounted throughout; only pitch changes. Bearing is untouched
 *  so "2D" drops straight overhead without yanking the user's rotation. */
export function tilt3D(map: maplibregl.Map, is3D: boolean): void {
  map.easeTo({ pitch: is3D ? PITCH_3D : 0, duration: is3D ? TILT_3D_MS : TILT_2D_MS });
}
