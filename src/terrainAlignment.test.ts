// Guards the layer-alignment invariant that a real bug violated: the elevation
// (hence hillshade + 3D mesh + contours) and the ground cover must be pinned to
// ONE geographic footprint, or they slide apart against the satellite imagery.
//
// The regression: USGS `exportImage` snaps the returned raster's extent to the
// requested pixel-`size` aspect ratio, so a square grid over a square-in-meters
// (rectangular-in-degrees) site came back covering a taller latitude range than
// requested. The code stored the requested bbox as record.bounds, compressing
// the terrain ~1.46x north-south relative to the correctly-placed cover/satellite.
import { describe, expect, it } from 'vitest';
import type { SiteCoverGrid, TerrainRecord } from './types';
import { unitToLngLat, lngLatToUnit } from './geo';
import {
  boundsOffsetDegrees,
  contourMetadataOf,
  coverGeometryMetadataOf,
  coverMetadataOf,
  manifestOf,
  validateTerrainPackage,
} from './terrainPackage';

const SITE = { west: -121.5008, south: 46.9092, east: -121.4482, north: 46.9618 };

function alignedRecord(): TerrainRecord {
  const coverGrid: SiteCoverGrid = {
    bounds: { ...SITE },
    width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200', vintage: '2021',
  };
  const contourSegments = [0, 0, 1, 1, 1500];
  const coverBoundarySegments = [0, 0, 1, 0, 10];
  let value: TerrainRecord = {
    schemaVersion: 4, key: 'align', mountainName: 'Align', latitude: 46.9355, longitude: -121.4745,
    areaSizeMeters: 4000, bounds: { ...SITE }, sampleGridSize: 2, sampleHeights: [1000, 1010, 1020, 1030],
    climate: { monthly: [] }, sourceType: 'live', coverGrid, coverMetadata: coverMetadataOf(coverGrid),
    coverBoundarySegments, coverGeometryMetadata: coverGeometryMetadataOf(coverBoundarySegments),
    contourSegments, contourMetadata: contourMetadataOf(contourSegments, 2, 6.096),
    createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
  };
  value = { ...value, packageManifest: manifestOf(value) };
  return value;
}

describe('shared unit <-> lng/lat projection', () => {
  it('places unit-square corners at the bounds corners (row 0 = north)', () => {
    expect(unitToLngLat(0, 0, SITE)).toEqual([SITE.west, SITE.north]); // NW
    expect(unitToLngLat(1, 1, SITE)).toEqual([SITE.east, SITE.south]); // SE
  });

  it('round-trips: lngLatToUnit inverts unitToLngLat exactly', () => {
    for (const [u, v] of [[0, 0], [1, 1], [0.25, 0.9], [0.73, 0.12]]) {
      const [lng, lat] = unitToLngLat(u, v, SITE);
      const [u2, v2] = lngLatToUnit(lng, lat, SITE);
      expect(u2).toBeCloseTo(u, 12);
      expect(v2).toBeCloseTo(v, 12);
    }
  });
});

describe('layer-alignment validation (runs on every download)', () => {
  it('accepts a package whose elevation and cover share one extent', () => {
    expect(validateTerrainPackage(alignedRecord())).toEqual({ ok: true, errors: [] });
  });

  it('rejects the exportImage aspect-snap bug: cover extent offset from terrain extent', () => {
    const value = alignedRecord();
    // Reproduce the bug: the elevation footprint (record.bounds) got stored as
    // the narrower requested latitude range while the cover was sampled at the
    // true, taller extent — the two now disagree by ~0.008 deg.
    value.bounds = { ...SITE, south: 46.9175, north: 46.9535 };
    const result = validateTerrainPackage(value);
    expect(result.ok).toBe(false);
    expect(result.errors.join(' ')).toMatch(/offset|misaligned/i);
  });

  it('tolerates sub-pixel float noise in the shared extent', () => {
    const value = alignedRecord();
    value.bounds = { ...SITE, north: SITE.north + 1e-9 };
    expect(validateTerrainPackage(value).ok).toBe(true);
  });

  it('boundsOffsetDegrees measures the largest single-edge divergence', () => {
    expect(boundsOffsetDegrees(SITE, SITE)).toBe(0);
    expect(boundsOffsetDegrees(SITE, { ...SITE, north: SITE.north + 0.01 })).toBeCloseTo(0.01, 12);
  });
});
