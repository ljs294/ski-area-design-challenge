import { describe, expect, it } from 'vitest';
import { coverDisplayToGeoJSON, deriveCoverDisplayGeometry } from './coverDisplay';
import { maskToPolygonsRect } from './coverPolygons';
import type { SiteCoverGrid, TerrainCoverGrid } from './types';

describe('rectangular cover polygons', () => {
  it('traces closed rings using independent width and height', () => {
    const width = 9, height = 5;
    const mask = new Uint8Array(width * height);
    for (let row = 1; row <= 3; row++) for (let col = 2; col <= 7; col++) mask[row * width + col] = 1;
    const polygons = maskToPolygonsRect(mask, width, height, {
      blurRadius: 0, simplifyTol: 0.1, minAreaCells: 1,
    });
    expect(polygons).toHaveLength(1);
    expect(polygons[0].outer[0]).toEqual(polygons[0].outer.at(-1));
    expect(Math.max(...polygons[0].outer.map((point) => point[0]))).toBeGreaterThan(4);
    expect(Math.max(...polygons[0].outer.map((point) => point[1]))).toBeLessThanOrEqual(height - 1);
  });
});

describe('persisted cover display geometry', () => {
  const grid = (): SiteCoverGrid => ({
    bounds: { west: -121.5, south: 46.9, east: -121.48, north: 46.91 },
    width: 12,
    height: 6,
    cellSizeM: 100,
    data: Array.from({ length: 72 }, (_, index) => index % 12 < 6 ? 10 : 30),
    complete: true,
    nodataCount: 0,
    source: 'esa-worldcover-2021-v200',
    vintage: '2021',
  });

  it('preserves native classes without mutating analytical cells', () => {
    const source = grid();
    const original = source.data.slice();
    const display = deriveCoverDisplayGeometry(source);
    const geojson = coverDisplayToGeoJSON(display.geometry, source.bounds);
    expect(source.data).toEqual(original);
    expect(new Set(geojson.features.map((feature) => feature.properties.code))).toEqual(new Set([10, 30]));
    expect(display.stats.polygonCount).toBe(2);
    expect(display.stats.vertexCount).toBeGreaterThan(0);
    for (const feature of geojson.features) {
      for (const [lng, lat] of feature.geometry.coordinates.flat()) {
        expect(lng).toBeGreaterThanOrEqual(source.bounds.west);
        expect(lng).toBeLessThanOrEqual(source.bounds.east);
        expect(lat).toBeGreaterThanOrEqual(source.bounds.south);
        expect(lat).toBeLessThanOrEqual(source.bounds.north);
      }
    }
  });

  it('rejects malformed binary streams', () => {
    expect(() => coverDisplayToGeoJSON([10, 1, 100, 0, 0], grid().bounds)).toThrow(/ring/i);
  });

  it('encodes the four game-focused terrain classes with detailed geometry settings', () => {
    const source: TerrainCoverGrid = {
      bounds: grid().bounds, width: 16, height: 16, cellSizeM: 10,
      data: Uint8Array.from({ length: 256 }, (_, i) => {
        const row = Math.floor(i / 16), col = i % 16;
        return row < 8 ? col < 8 ? 1 : 2 : col < 8 ? 3 : 4;
      }),
      complete: true, nodataCount: 0, source: 'usgs-four-class-v1', vintage: '2021',
      treelineM: { north: 1800, east: 1800, south: 1800, west: 1800, site: 1800 },
      provenance: {
        processingVersion: 'four-class-v1', confidence: 'reduced', method: 'worldcover-fallback',
        attribution: ['ESA WorldCover'], worldCover: { vintage: '2021', license: 'cc-by-4.0' },
      },
    };
    const display = deriveCoverDisplayGeometry(source);
    const decoded = coverDisplayToGeoJSON(display.geometry, source.bounds);
    expect(new Set(decoded.features.map((feature) => feature.properties.code))).toEqual(new Set([1, 2, 3, 4]));
    expect(display.stats.minFeatureM2).toBe(16);
    expect(display.stats.simplifyM).toBe(2);
  });
});
