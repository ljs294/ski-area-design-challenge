import { describe, expect, it, vi } from 'vitest';
import {
  BUILDING_BUILT_LAYER_IDS,
  BUILDING_EXTRUSION_LAYER_ID,
} from './buildingLayers';
import { createBuildingContribution } from './buildingRenderer';
import { fixedPumpHouseFixture } from './buildingFixture';

class FakeMap {
  readonly sources = new Map<string, { setData: ReturnType<typeof vi.fn> }>();
  readonly layers = new Map<string, { id: string; type?: string }>();
  readonly layout = new Map<string, unknown>();
  addSource(id: string): void { this.sources.set(id, { setData: vi.fn() }); }
  getSource(id: string): unknown { return this.sources.get(id); }
  addLayer(layer: { id: string; type?: string }): void { this.layers.set(layer.id, layer); }
  getLayer(id: string): unknown { return this.layers.get(id); }
  removeLayer(id: string): void { this.layers.delete(id); }
  removeSource(id: string): void { this.sources.delete(id); }
  setFilter(): void {}
  setLayoutProperty(id: string, property: string, value: unknown): void {
    this.layout.set(`${id}:${property}`, value);
  }
}

function visibility(map: FakeMap, id: string): unknown {
  return map.layout.get(`${id}:visibility`);
}

describe('native building contribution', () => {
  it('installs and synchronizes the native extrusion without a custom WebGL layer', () => {
    const map = new FakeMap();
    const contribution = createBuildingContribution({
      getBuildings: () => [fixedPumpHouseFixture([-121.47, 46.92])],
    });
    const context = { map: map as never, mapGeneration: 1, styleGeneration: 1 };

    contribution.install(context);
    contribution.synchronizeData(context);

    expect(map.layers.get(BUILDING_EXTRUSION_LAYER_ID)?.type).toBe('fill-extrusion');
    expect(map.sources.get('player-buildings')?.setData).toHaveBeenCalledOnce();
    expect([...map.layers.values()].some((layer) => layer.type === 'custom')).toBe(false);
  });

  it('hides all normal building layers in dashboards and restores the exact descriptor state', () => {
    const map = new FakeMap();
    const contribution = createBuildingContribution({ getBuildings: () => [] });
    const context = { map: map as never, mapGeneration: 1, styleGeneration: 1 };
    contribution.install(context);

    contribution.presentationChanged?.(context, 'dashboard-snowmaking');
    for (const id of BUILDING_BUILT_LAYER_IDS) expect(visibility(map, id)).toBe('none');

    contribution.visibilityChanged?.(context, 'buildings', false);
    contribution.presentationChanged?.(context, null);
    for (const id of BUILDING_BUILT_LAYER_IDS) expect(visibility(map, id)).toBe('none');

    contribution.visibilityChanged?.(context, 'buildings', true);
    for (const id of BUILDING_BUILT_LAYER_IDS) expect(visibility(map, id)).toBe('visible');
  });
});
