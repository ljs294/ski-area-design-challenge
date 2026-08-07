import { describe, expect, it } from 'vitest';
import type { TerrainRecord } from '../types';
import { localContextGeoJSON } from './localContextGeoJSON';
import { waterLinePixelWidth } from './waterLineStyle';

const record = {
  vectorFeatures: {
    waterPolygons: [{ id: 'way/42', name: 'OSM Lake', rings: [[[0, 0], [1, 0], [1, 1], [0, 0]]] }],
    waterLines: [{ id: 'way/7', name: 'Cold Creek', waterClass: 'stream', sourceWidthM: 4,
      points: [[0, 0], [1, 1]] }], roads: [], landCover: [], peaks: [],
  },
} as unknown as TerrainRecord;

describe('local lake context', () => {
  it('publishes only a player override as the custom map label', () => {
    const unnamed = localContextGeoJSON(record).features[0];
    const named = localContextGeoJSON(record, { 'way/42': 'Mirror Pond' }).features[0];
    expect(unnamed.properties).toMatchObject({ id: 'way/42', name: 'OSM Lake', customName: '' });
    expect(named.properties).toMatchObject({ id: 'way/42', name: 'OSM Lake', customName: 'Mirror Pond' });
  });

  it('publishes stable waterway properties and the effective width', () => {
    const source = localContextGeoJSON(record).features[1];
    const overridden = localContextGeoJSON(record, {}, { 'way/7': 9 }).features[1];
    expect(source.properties).toMatchObject({ id: 'way/7', name: 'Cold Creek', class: 'stream',
      widthM: 4, widthSource: 'osm' });
    expect(overridden.properties).toMatchObject({ widthM: 9, widthSource: 'override' });
  });

  it('keeps zoom at the top level of the MapLibre water-width expression', () => {
    const expression = waterLinePixelWidth(45) as unknown as unknown[];
    expect(expression[0]).toBe('interpolate');
    expect(expression[2]).toEqual(['zoom']);
    expect(JSON.stringify(expression.slice(3))).not.toContain('["zoom"]');
  });
});
