import type maplibregl from 'maplibre-gl';
import type { RenderQuality } from './renderProfile';
import { renderProfileFor } from './renderProfile';

/** Apply the selected public MapLibre tile-LOD policy to every tiled source. */
export function applyTileLod(map: maplibregl.Map, quality: RenderQuality): void {
  if (!map.isStyleLoaded()) return;
  const { maxZoomLevelsOnScreen, tileCountMaxMinRatio } = renderProfileFor(quality).tileLod;
  map.setSourceTileLodParams(maxZoomLevelsOnScreen, tileCountMaxMinRatio);
  map.triggerRepaint();
}
