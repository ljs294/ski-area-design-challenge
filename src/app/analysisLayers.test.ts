import { describe, expect, it } from 'vitest';
import type { TerrainRecord } from '../types';
import { localContextGeoJSON } from './localContextGeoJSON';

const record = {
  vectorFeatures: {
    waterPolygons: [{ id: 'way/42', name: 'OSM Lake', rings: [[[0, 0], [1, 0], [1, 1], [0, 0]]] }],
    waterLines: [{ id: 'way/7', name: 'Creek', waterClass: 'stream', widthM: 4.5,
      points: [[0, 0], [1, 1]] }], roads: [], landCover: [], peaks: [],
  },
} as unknown as TerrainRecord;

describe('local lake context', () => {
  it('publishes only a player override as the custom map label', () => {
    const unnamed = localContextGeoJSON(record).features[0];
    const named = localContextGeoJSON(record, [], { 'way/42': 'Mirror Pond' }).features[0];
    expect(unnamed.properties).toMatchObject({ id: 'way/42', name: 'OSM Lake', customName: '' });
    expect(named.properties).toMatchObject({ id: 'way/42', name: 'OSM Lake', customName: 'Mirror Pond' });
  });

  it('retains stream identity and publishes its effective rendered width', () => {
    const stream = localContextGeoJSON(record, [], {}, { 'way/7': 8 }).features
      .find((feature) => feature.properties?.kind === 'water-line');
    expect(stream?.id).toBe('way/7');
    expect(stream?.properties).toMatchObject({ id: 'way/7', name: 'Creek', class: 'stream',
      widthM: 4.5, effectiveWidthM: 8 });
  });
});
