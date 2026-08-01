import { describe, expect, it } from 'vitest';
import { analyzeLake, estimateLakeAverageDepthM, formatLakeVolume, lakeSurfaceAreaM2,
  sanitizeLakeDepthOverrides, sanitizeLakeNameOverrides } from './lakeAnalysis';
import type { TerrainRecord, WaterPolygonFeature } from './types';

const square = (id: string, west: number, south: number, east: number, north: number): WaterPolygonFeature => ({
  id, rings: [[[west, south], [east, south], [east, north], [west, north], [west, south]]],
});

function terrain(fn: (lng: number, lat: number) => number): TerrainRecord {
  const size = 101;
  const heights: number[] = [];
  for (let row = 0; row < size; row++) {
    const lat = 0.002 - row / (size - 1) * 0.004;
    for (let col = 0; col < size; col++) {
      const lng = -0.002 + col / (size - 1) * 0.004;
      heights.push(fn(lng, lat));
    }
  }
  return {
    schemaVersion: 3, key: 'terrain', mountainName: 'Test', latitude: 0, longitude: 0,
    areaSizeMeters: 445, bounds: { west: -0.002, east: 0.002, south: -0.002, north: 0.002 },
    sampleGridSize: size, sampleHeights: heights, climate: { monthly: [] }, sourceType: 'live',
    createdAt: '', updatedAt: '',
  };
}

describe('lake analysis', () => {
  it('subtracts contained holes but ignores malformed holes outside the lake', () => {
    const lake = square('lake', 0, 0, 0.001, 0.001);
    lake.rings.push(square('hole', 0.00025, 0.00025, 0.00075, 0.00075).rings[0]);
    const outside = square('outside', 0.002, 0.002, 0.003, 0.003).rings[0];
    const withHole = lakeSurfaceAreaM2(lake);
    lake.rings.push(outside);
    expect(lakeSurfaceAreaM2(lake)).toBeCloseTo(withHole, 5);
    expect(withHole).toBeGreaterThan(9_000);
    expect(withHole).toBeLessThan(10_000);
  });

  it('derives a bounded positive estimate from rising shoreline terrain', () => {
    const lake = square('lake', -0.0005, -0.0005, 0.0005, 0.0005);
    const record = terrain((lng, lat) => {
      const edge = Math.max(Math.abs(lng), Math.abs(lat));
      return 100 + Math.max(0, edge - 0.0005) * 100_000;
    });
    const first = estimateLakeAverageDepthM(lake, record);
    const second = estimateLakeAverageDepthM(lake, record);
    expect(first.averageDepthM).not.toBeNull();
    expect(first).toEqual(second);
    expect(first.averageDepthM!).toBeGreaterThanOrEqual(0.5);
    expect(first.averageDepthM!).toBeLessThanOrEqual(50);
  });

  it('does not invent a depth for flat terrain and accepts an override', () => {
    const lake = square('lake', -0.0005, -0.0005, 0.0005, 0.0005);
    const record = terrain(() => 100);
    expect(estimateLakeAverageDepthM(lake, record).averageDepthM).toBeNull();
    const analysis = analyzeLake(lake, record, 3);
    expect(analysis.depthSource).toBe('override');
    expect(analysis.volumeM3).toBeCloseTo(analysis.areaM2 * 3);
  });

  it('formats large liquid volumes in thousands or millions', () => {
    expect(formatLakeVolume(10, 'metric')).toBe('10.0K L');
    expect(formatLakeVolume(10_000, 'metric')).toBe('10.0M L');
    expect(formatLakeVolume(10, 'imperial')).toBe('2.6K gal');
  });

  it('sanitizes persisted overrides from legacy/untrusted save data', () => {
    expect(sanitizeLakeDepthOverrides({ lake: 3, zero: 0, text: '4', huge: 1001 }))
      .toEqual({ lake: 3 });
    expect(sanitizeLakeDepthOverrides(undefined)).toEqual({});
    expect(sanitizeLakeNameOverrides({ lake: '  Mirror Pond  ', blank: ' ', bad: 4 }))
      .toEqual({ lake: 'Mirror Pond' });
  });

  it('uses player names ahead of OSM names without changing the source feature', () => {
    const feature = { ...square('lake', -0.0005, -0.0005, 0.0005, 0.0005), name: 'OSM Lake' };
    const analysis = analyzeLake(feature, terrain(() => 100), undefined, 'My Pond');
    expect(analysis.name).toBe('My Pond');
    expect(analysis.sourceName).toBe('OSM Lake');
    expect(analysis.nameSource).toBe('player');
    expect(feature.name).toBe('OSM Lake');
  });

});
