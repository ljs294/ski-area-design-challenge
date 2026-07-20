import { describe, expect, it } from 'vitest';
import type { SiteCoverGrid, TerrainRecord } from './types';
import { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf, manifestOf, validateTerrainPackage } from './terrainPackage';
import { hydrateTerrainRecord } from './terrainIngest';

function record(): TerrainRecord {
  const coverGrid: SiteCoverGrid = {
    bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
    width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200', vintage: '2021',
  };
  const contourSegments = [0, 0, 1, 1, 1500];
  const coverBoundarySegments = [0, 0, 1, 0, 10];
  let value: TerrainRecord = {
    schemaVersion: 4, key: 'test', mountainName: 'Test', latitude: 46.905, longitude: -121.495,
    areaSizeMeters: 2000, bounds: coverGrid.bounds, sampleGridSize: 2, sampleHeights: [1000, 1010, 1020, 1030],
    climate: { monthly: [] }, sourceType: 'live', coverGrid, coverMetadata: coverMetadataOf(coverGrid),
    coverBoundarySegments, coverGeometryMetadata: coverGeometryMetadataOf(coverBoundarySegments),
    contourSegments, contourMetadata: contourMetadataOf(contourSegments, 2, 6.096),
    createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
  };
  value = { ...value, packageManifest: manifestOf(value) };
  return value;
}

describe('terrain package manifests', () => {
  it('validates a complete elevation, cover, and contour package', () => {
    expect(validateTerrainPackage(record())).toEqual({ ok: true, errors: [] });
  });
  it('rejects changed cover bytes and nodata instead of treating it as clear land', () => {
    const value = record();
    value.coverGrid!.data[0] = 255;
    value.coverGrid!.complete = false;
    value.coverGrid!.nodataCount = 1;
    const result = validateTerrainPackage(value);
    expect(result.ok).toBe(false);
    expect(result.errors.join(' ')).toMatch(/incomplete|checksum/i);
  });
  it('rejects a truncated contour cache', () => {
    const value = record();
    value.contourSegments!.pop();
    expect(validateTerrainPackage(value).ok).toBe(false);
  });
  it('rejects changed derived cover boundaries', () => {
    const value = record();
    value.coverBoundarySegments![0] = 0.5;
    expect(validateTerrainPackage(value).ok).toBe(false);
  });
});

describe('legacy terrain records', () => {
  it('remain hydratable but are rejected as gameplay packages for one-time preparation', () => {
    const old: TerrainRecord = {
      schemaVersion: 2, key: 'old', mountainName: 'Old', latitude: 46.9, longitude: -121.5,
      areaSizeMeters: 2000, sampleGridSize: 2, sampleHeights: [1, 2, 3, 4],
      climate: { monthly: [] }, sourceType: 'live', createdAt: '2025-01-01', updatedAt: '2025-01-01',
    };
    const hydrated = hydrateTerrainRecord(old);
    expect(hydrated.bounds).toBeDefined();
    expect(hydrated.displayHeights.length).toBeGreaterThan(0);
    expect(validateTerrainPackage(old).ok).toBe(false);
  });
});
