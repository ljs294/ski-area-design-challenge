import { describe, expect, it } from 'vitest';
import type { SiteCoverGrid, TerrainCoverGrid, TerrainRecord } from './types';
import {
  contourMetadataOf,
  coverDisplayMetadataOf,
  coverGeometryMetadataOf,
  coverMetadataOf,
  manifestOf,
  manifestWithUpdatedCover,
  originalCoverMetadataOf,
  validateTerrainCoverEdit,
  validateTerrainPackage,
} from './terrainPackage';

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
    value.contourSegments = Array.from(value.contourSegments!).slice(0, -1);
    expect(validateTerrainPackage(value).ok).toBe(false);
  });
  it('rejects changed derived cover boundaries', () => {
    const value = record();
    value.coverBoundarySegments![0] = 0.5;
    expect(validateTerrainPackage(value).ok).toBe(false);
  });
  it('validates a regraded elevation grid with regenerated contours and manifest', () => {
    let value = record();
    const contourSegments = [0, 0.25, 1, 0.75, 1005];
    value = {
      ...value,
      sampleHeights: [1000, 1007, 1015, 1024],
      contourSegments,
      contourMetadata: contourMetadataOf(contourSegments, 2, 6.096),
      updatedAt: '2026-01-02T00:00:00.000Z',
    };
    value = { ...value, packageManifest: manifestOf(value) };
    expect(validateTerrainPackage(value)).toEqual({ ok: true, errors: [] });
  });
  it('requires and validates persisted vector geometry for schema v5', () => {
    let value = record();
    const geometry = [10, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0];
    const stats = { polygonCount: 1, ringCount: 1, vertexCount: 4, smoothingM: 24, simplifyM: 10, minFeatureM2: 600 };
    value = { ...value, schemaVersion: 5, coverDisplayGeometry: geometry, coverDisplayMetadata: coverDisplayMetadataOf(geometry, stats) };
    value.packageManifest = manifestOf(value);
    expect(validateTerrainPackage(value).ok).toBe(true);
    value.coverDisplayGeometry![5] = 0.5;
    expect(validateTerrainPackage(value).errors.join(' ')).toMatch(/vector ground-cover/i);
  });
  it('requires the refined four-class and original recovery grids for schema v6', () => {
    const legacy = record();
    const original = legacy.coverGrid as SiteCoverGrid;
    const refined: TerrainCoverGrid = {
      bounds: original.bounds, width: 2, height: 2, cellSizeM: 2,
      data: Uint8Array.from([1, 2, 3, 4]), complete: true, nodataCount: 0,
      source: 'usgs-four-class-v1', vintage: '2019/2021',
      treelineM: { north: 1800, east: 1825, south: 1850, west: 1810, site: 1820 },
      provenance: {
        processingVersion: 'four-class-v1', confidence: 'reduced', method: 'worldcover-fallback',
        attribution: ['ESA WorldCover'], worldCover: { vintage: '2021', license: 'cc-by-4.0' },
      },
    };
    const geometry = [1, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0];
    const stats = { polygonCount: 1, ringCount: 1, vertexCount: 4, smoothingM: 6, simplifyM: 2, minFeatureM2: 16 };
    let value: TerrainRecord = {
      ...legacy, schemaVersion: 6, coverGrid: refined, coverMetadata: coverMetadataOf(refined),
      originalCoverGrid: original, originalCoverMetadata: originalCoverMetadataOf(original),
      coverDisplayGeometry: geometry, coverDisplayMetadata: coverDisplayMetadataOf(geometry, stats),
    };
    value = { ...value, packageManifest: manifestOf(value) };
    expect(validateTerrainPackage(value)).toEqual({ ok: true, errors: [] });
    delete value.originalCoverGrid;
    expect(validateTerrainPackage(value).errors.join(' ')).toMatch(/original worldcover/i);
  });
  it('refreshes and validates only the cover assets after construction', () => {
    let value = record();
    const geometry = [10, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0];
    const stats = {
      polygonCount: 1, ringCount: 1, vertexCount: 4,
      smoothingM: 24, simplifyM: 10, minFeatureM2: 600,
    };
    value = {
      ...value,
      schemaVersion: 5,
      coverDisplayGeometry: geometry,
      coverDisplayMetadata: coverDisplayMetadataOf(geometry, stats),
    };
    value.packageManifest = manifestOf(value);
    const previous = value.packageManifest;
    const coverGrid = {
      ...value.coverGrid!,
      data: Uint8Array.from([30, 10, 20, 30]),
    } as SiteCoverGrid;
    value = {
      ...value,
      coverGrid,
      coverMetadata: coverMetadataOf(coverGrid),
      updatedAt: '2026-01-02T00:00:00.000Z',
    };
    value.packageManifest = manifestWithUpdatedCover(value);

    expect(value.packageManifest.elevationChecksum).toBe(previous.elevationChecksum);
    expect(value.packageManifest.contours).toEqual(previous.contours);
    expect(value.packageManifest.cover?.checksum).toBe(value.coverMetadata?.checksum);
    expect(validateTerrainCoverEdit(value)).toEqual({ ok: true, errors: [] });

    value.packageManifest = {
      ...value.packageManifest,
      cover: { ...value.packageManifest.cover!, checksum: 'bad' },
    };
    expect(validateTerrainCoverEdit(value).errors.join(' ')).toMatch(/manifest metadata/i);
  });
});

describe('legacy terrain records', () => {
  it('are rejected as gameplay packages for one-time preparation', () => {
    const old: TerrainRecord = {
      schemaVersion: 2, key: 'old', mountainName: 'Old', latitude: 46.9, longitude: -121.5,
      areaSizeMeters: 2000, sampleGridSize: 2, sampleHeights: [1, 2, 3, 4],
      climate: { monthly: [] }, sourceType: 'live', createdAt: '2025-01-01', updatedAt: '2025-01-01',
    };
    expect(validateTerrainPackage(old).ok).toBe(false);
  });
});
