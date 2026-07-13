import type maplibregl from 'maplibre-gl';
import type { RenderQuality } from './SettingsContext';

// Distance-based level-of-detail for the draped surface in 3D.
//
// MapLibre v5 lets a source redefine which tiles (at what zoom = detail) load at
// high pitch angles via `source.calculateTileZoom`. The built-in curve drops
// tile zoom as tiles get farther from the camera (constant on-screen texel
// density), so distant terrain in 3D reads coarse. Damping that falloff keeps
// the sharp LOD region reaching further toward the horizon — at the cost of
// loading more, higher-zoom tiles (network + GPU memory). Only matters when
// pitched; flat top-down all tiles are ~equidistant so this is a no-op.

// Signature per the MapLibre `CalculateTileZoomFunction` contract.
type CalcTileZoom = (
  centerZoom: number,
  distanceToTile2D: number,
  distanceToTileZ: number,
  distanceToCenter3D: number,
  cameraVerticalFOV: number
) => number;

/**
 * Falloff exponent per render tier. ~1 mimics MapLibre's default screen-space
 * LOD; smaller keeps distant tiles sharper. `null` means "leave the source
 * untouched" so Standard is exactly MapLibre's stock behavior (no regression).
 */
export function tileZoomFalloffFor(quality: RenderQuality): number | null {
  switch (quality) {
    case 'ultra':
      return 0.2; // near-constant full detail to the horizon (heavy, gorgeous)
    case 'high':
      return 0.5;
    default:
      return null;
  }
}

/** LOD curve: distant tiles stay sharper as `falloff` shrinks below 1. */
function makeTileZoom(falloff: number): CalcTileZoom {
  return (centerZoom, d2d, dz, dCenter3D) => {
    const dTile3D = Math.hypot(d2d, dz);
    if (dTile3D <= 0 || dCenter3D <= 0) return centerZoom;
    // Each doubling of distance costs `falloff` zoom levels of detail.
    return centerZoom - falloff * Math.log2(dTile3D / dCenter3D);
  };
}

/**
 * Apply (or clear) the distance-LOD curve on every source in the current style
 * — basemap, hillshade DEM, terrain DEM, and the analysis overlays — so the
 * whole draped surface keeps detail into the distance together. Idempotent;
 * re-run after the source set changes (style load, 3D toggle) or on tier change.
 */
export function applyTileLod(map: maplibregl.Map, quality: RenderQuality): void {
  // Sources only exist once the style is loaded; callers that fire earlier
  // (e.g. a mount-time effect) are no-ops — the load handler re-applies.
  if (!map.isStyleLoaded()) return;
  const falloff = tileZoomFalloffFor(quality);
  const fn = falloff == null ? undefined : makeTileZoom(falloff);
  const ids = Object.keys(map.getStyle()?.sources ?? {});
  for (const id of ids) {
    const src = map.getSource(id) as unknown as { calculateTileZoom?: CalcTileZoom } | undefined;
    if (src) src.calculateTileZoom = fn;
  }
  map.triggerRepaint();
}
