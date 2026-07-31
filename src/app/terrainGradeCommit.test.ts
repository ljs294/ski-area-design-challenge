import { describe, expect, it } from 'vitest';
import type { SiteCoverGrid, TerrainRecord } from '../types';
import {
  contourMetadataOf,
  coverGeometryMetadataOf,
  coverMetadataOf,
  manifestOf,
  validateTerrainPackage,
} from '../terrainPackage';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';

function record(): TerrainRecord {
  const bounds = { west: -121.5, south: 46.9, east: -121.49, north: 46.91 };
  const coverGrid: SiteCoverGrid = {
    bounds,
    width: 2,
    height: 2,
    cellSizeM: 10,
    data: [10, 10, 20, 30],
    complete: true,
    nodataCount: 0,
    source: 'esa-worldcover-2021-v200',
    vintage: '2021',
  };
  const contours = [0, 0, 1, 1, 1000];
  const coverSegments = [0, 0, 1, 0, 10];
  let value: TerrainRecord = {
    schemaVersion: 4,
    key: 'grade-test',
    mountainName: 'Grade Test',
    latitude: 46.905,
    longitude: -121.495,
    areaSizeMeters: 2000,
    bounds,
    sampleGridSize: 2,
    sampleHeights: [1000, 1010, 1020, 1030],
    coverGrid,
    coverMetadata: coverMetadataOf(coverGrid),
    coverBoundarySegments: coverSegments,
    coverGeometryMetadata: coverGeometryMetadataOf(coverSegments),
    contourSegments: contours,
    contourMetadata: contourMetadataOf(contours, 2, 6.096),
    climate: { monthly: [] },
    sourceType: 'live',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
  };
  value = { ...value, packageManifest: manifestOf(value) };
  return value;
}

describe('terrain grade commit', () => {
  it('applies the preview patch and rebuilds a valid revisioned package', () => {
    const original = record();
    const checksum = original.packageManifest!.elevationChecksum;
    const upgraded = applyTerrainGradeToRecord(original, {
      patchIndices: Uint32Array.from([1, 2]),
      patchHeights: Float32Array.from([1005, 1015]),
      contourSegments: Float32Array.from([0, 0.25, 1, 0.75, 1005]),
      contourGridSize: 2,
      contourIntervalM: 6.096,
      baseElevationChecksum: checksum,
    }, '2026-01-02T00:00:00.000Z');

    expect(original.sampleHeights).toEqual([1000, 1010, 1020, 1030]);
    expect(upgraded.sampleHeights).toEqual([1000, 1005, 1015, 1030]);
    expect(upgraded.packageManifest!.elevationChecksum).not.toBe(checksum);
    expect(upgraded.packageManifest!.contours?.checksum)
      .toBe(upgraded.contourMetadata!.checksum);
    expect(validateTerrainPackage(upgraded)).toEqual({ ok: true, errors: [] });
  });

  it('rejects stale previews and malformed sparse patches', () => {
    const original = record();
    const base = {
      patchIndices: Uint32Array.from([1]),
      patchHeights: Float32Array.from([1005]),
      contourSegments: Float32Array.from([0, 0, 1, 1, 1000]),
      contourGridSize: 2,
      contourIntervalM: 6.096,
    };
    expect(() => applyTerrainGradeToRecord(original, {
      ...base,
      baseElevationChecksum: 'stale',
    })).toThrow(/terrain changed/i);
    expect(() => applyTerrainGradeToRecord(original, {
      ...base,
      patchIndices: Uint32Array.from([99]),
      baseElevationChecksum: original.packageManifest!.elevationChecksum,
    })).toThrow(/invalid elevation/i);
  });
});
