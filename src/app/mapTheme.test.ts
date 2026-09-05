import { describe, expect, it, vi } from 'vitest';
import type { Map as MapLibreMap } from 'maplibre-gl';
import { applyMapTheme, MAP_PALETTES } from './mapTheme';
import { basemapFor } from './basemapStyle';

describe('cartographic themes', () => {
  it('changes cartography without changing raster paint, sources, visibility or ordering', () => {
    for (const offline of [true, false]) {
      const light = basemapFor('light', { offline }), dark = basemapFor('dark', { offline });
      expect(dark.sources).toEqual(light.sources);
      expect(dark.layers.map((layer) => [layer.id, layer.layout])).toEqual(light.layers.map((layer) => [layer.id, layer.layout]));
      expect(dark.layers.find((layer) => layer.id === 'mp-satellite')).toEqual(light.layers.find((layer) => layer.id === 'mp-satellite'));
      expect(dark.layers[0].paint).toEqual({ 'background-color': MAP_PALETTES.dark.paper });
      expect(dark.layers[0].paint).not.toEqual(light.layers[0].paint);
    }
  });
  it('updates only owned paint properties and never rebuilds a style or changes draft data', () => {
    const setPaintProperty = vi.fn();
    const map = { getStyle: () => ({ layers: [{ id: 'mp-paper' }, { id: 'lift-draft' }, { id: 'trail-fill' }] }),
      getPaintProperty: () => null, setPaintProperty } as unknown as MapLibreMap;
    applyMapTheme(map, 'dark');
    expect(setPaintProperty.mock.calls).toEqual([['mp-paper', 'background-color', MAP_PALETTES.dark.paper]]);
  });
});
