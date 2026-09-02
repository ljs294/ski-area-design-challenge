import { describe, expect, it, vi } from 'vitest';
import {
  addBuildingLayers,
  BUILDING_BUILT_LAYER_IDS,
  BUILDING_DRAFT_LAYER_IDS,
  BUILDING_EXTRUSION_LAYER_ID,
  buildingExtrusionHeightM,
  buildingDraftGeoJSON,
  buildingGeoJSON,
  setBuildingCaptureTransient,
} from './buildingLayers';

const building = {
  id: 'pump-1', name: 'Pump house 1', center: [-121.47, 46.92] as const,
  bearingDeg: 22, dimensions: { lengthM: 18.288, widthM: 12.192, eaveHeightM: 4.8768 },
  foundation: { kind: 'flattened', perimeterElevationsM: [100, 100, 100, 100] },
};

class FakeMap {
  readonly sources = new Map<string, { setData: ReturnType<typeof vi.fn> }>();
  readonly layers = new Map<string, unknown>();
  readonly addLayerCalls: string[] = [];
  readonly layout: string[] = [];
  addSource(id: string): void { this.sources.set(id, { setData: vi.fn() }); }
  getSource(id: string): unknown { return this.sources.get(id); }
  addLayer(layer: { id: string }): void { this.layers.set(layer.id, layer); this.addLayerCalls.push(layer.id); }
  getLayer(id: string): unknown { return this.layers.get(id); }
  removeLayer(id: string): void { this.layers.delete(id); }
  removeSource(id: string): void { this.sources.delete(id); }
  setLayoutProperty(id: string, property: string, value: unknown): void {
    this.layout.push(`${id}:${property}:${String(value)}`);
  }
}

describe('building GeoJSON support layers', () => {
  it('emits footprint and apron features with the selected id', () => {
    const fc = buildingGeoJSON([building], 'pump-1');
    expect(fc.features.map((item) => item.properties?.kind)).toEqual([
      'building-footprint', 'building-foundation',
    ]);
    expect(fc.features[0].properties).toMatchObject({ id: 'pump-1', selected: true });
    expect(fc.features[0].properties).toMatchObject({
      heightM: buildingExtrusionHeightM(building), minHeightM: 0,
    });
    expect(fc.features[1].geometry.type).toBe('Polygon');
    expect((fc.features[1].geometry as GeoJSON.Polygon).coordinates[0]).toHaveLength(5);
  });

  it('keeps draft placement, apron, and grade polygons in a separate source', () => {
    const fc = buildingDraftGeoJSON({
      center: building.center, lengthM: building.dimensions.lengthM,
      widthM: building.dimensions.widthM, bearingDeg: building.bearingDeg,
      foundationMode: 'flattened', gradePolygons: [[[-121.48, 46.91], [-121.47, 46.91], [-121.47, 46.92]]],
    });
    expect(fc.features.map((item) => item.properties?.kind)).toEqual([
      'foundation', 'footprint', 'grade',
    ]);
    expect((fc.features[2].geometry as GeoJSON.Polygon).coordinates[0][0]).toEqual(
      (fc.features[2].geometry as GeoJSON.Polygon).coordinates[0].at(-1),
    );
  });

  it('adds all built and draft layer roles and restores capture exactly once', () => {
    const map = new FakeMap();
    addBuildingLayers(map as never);
    expect(map.addLayerCalls).toEqual([...BUILDING_BUILT_LAYER_IDS, ...BUILDING_DRAFT_LAYER_IDS]);
    expect(map.layers.get(BUILDING_EXTRUSION_LAYER_ID)).toMatchObject({
      type: 'fill-extrusion',
      paint: {
        'fill-extrusion-height': ['get', 'heightM'],
        'fill-extrusion-base': ['get', 'minHeightM'],
        'fill-extrusion-opacity': 0.88,
        'fill-extrusion-vertical-gradient': true,
      },
    });
    const draft = { center: building.center, lengthM: 18, widthM: 12, bearingDeg: 0 };
    const source = map.sources.get('building-draft')!;
    setBuildingCaptureTransient(map as never, false, draft); // no-op before a hide
    setBuildingCaptureTransient(map as never, true, draft);
    setBuildingCaptureTransient(map as never, true, draft);
    expect(source.setData).toHaveBeenCalledTimes(1);
    setBuildingCaptureTransient(map as never, false, null);
    setBuildingCaptureTransient(map as never, false, null);
    expect(source.setData).toHaveBeenCalledTimes(2);
    expect(source.setData.mock.calls[0][0].features).toHaveLength(0);
    expect(source.setData.mock.calls[1][0].features).toHaveLength(2);
  });
});
