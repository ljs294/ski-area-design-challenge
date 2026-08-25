import { describe, expect, it } from 'vitest';
import { RESORT_DEM_PROTOCOL, resortCameraBounds, resortDemBounds,
  resortProtocolUrl, resortWarmTileKeys, sampleLocalTerrainAt,
  setActiveResortTerrain } from './resortProtocols';
import type { TerrainRecord } from '../types';

// Minimal record: only the fields the geometry helpers read. A ~2 km box with a
// ~3 km surround ring around it.
function makeRecord(): TerrainRecord {
  const core = { west: -121.5, south: 47.0, east: -121.47, north: 47.02 };
  const ring = { west: -121.55, south: 46.96, east: -121.42, north: 47.06 };
  return {
    key: 'test-resort',
    bounds: core,
    surround: { bounds: ring, width: 8, height: 8, heights: new Array(64).fill(1000) },
  } as unknown as TerrainRecord;
}

describe('resortCameraBounds', () => {
  it('pulls the camera in to a ~1 km inset, well inside the 3 km ring', () => {
    const rec = makeRecord();
    const cam = resortCameraBounds(rec)!;
    const ring = resortDemBounds(rec)!;
    // Camera bounds sit strictly inside the ring on every side.
    expect(cam[0]).toBeGreaterThan(ring[0]); // west
    expect(cam[1]).toBeGreaterThan(ring[1]); // south
    expect(cam[2]).toBeLessThan(ring[2]); // east
    expect(cam[3]).toBeLessThan(ring[3]); // north
    // …and still outside the play box (so the player can orbit every side).
    expect(cam[0]).toBeLessThan(rec.bounds!.west);
    expect(cam[2]).toBeGreaterThan(rec.bounds!.east);
  });

  it('never exceeds the ring even with a large margin', () => {
    const rec = makeRecord();
    const cam = resortCameraBounds(rec, 50000)!; // 50 km >> ring
    expect(cam).toEqual(resortDemBounds(rec));
  });
});

describe('resortWarmTileKeys', () => {
  it('enumerates a bounded dem+cover tile set across the zoom band', () => {
    const keys = resortWarmTileKeys(makeRecord());
    expect(keys.length).toBeGreaterThan(0);
    expect(keys.length).toBeLessThan(2048); // fits the cache
    const kinds = new Set(keys.map((k) => k.kind));
    expect(kinds.has('dem')).toBe(true);
    expect(kinds.has('cover')).toBe(true);
    // Zoom band stays within the source's usable range.
    for (const k of keys) {
      expect(k.z).toBeGreaterThanOrEqual(11);
      expect(k.z).toBeLessThanOrEqual(15);
    }
  });

  it('does not warm unused raster cover for a vector-cover package', () => {
    const rec = makeRecord();
    rec.coverDisplayGeometry = [1, 2, 3];
    const standard = resortWarmTileKeys(rec, 'standard');
    expect(new Set(standard.map((key) => key.kind))).toEqual(new Set(['dem']));
  });

  it('uses the bounded raster presentation for Performance', () => {
    const rec = makeRecord();
    rec.coverDisplayGeometry = [1, 2, 3];
    const performance = resortWarmTileKeys(rec, 'performance');
    expect(performance.some((key) => key.kind === 'cover')).toBe(true);
    expect(Math.max(...performance.map((key) => key.z))).toBe(14);
  });
});

describe('sampleLocalTerrainAt', () => {
  // Same box + ring as makeRecord, plus the elevation grids the sampler reads.
  function makeSampledRecord(): TerrainRecord {
    const rec = makeRecord();
    return {
      ...rec,
      sampleGridSize: 4,
      sampleHeights: new Array(16).fill(2000),
    } as unknown as TerrainRecord;
  }

  it('answers for a point in the surround ring, outside the property line', () => {
    // Nothing clamps a painted stroke to the box, and the ring is rendered and
    // skiable-looking — refusing here used to abort a whole run's profile.
    setActiveResortTerrain(makeSampledRecord());
    const outsideCore = sampleLocalTerrainAt(-121.46, 47.01); // east of core.east
    expect(outsideCore).not.toBeNull();
    expect(outsideCore!.elevation).toBeCloseTo(1000, 6); // the ring's height
    setActiveResortTerrain(null);
  });

  it('still reports null outside every extent we hold', () => {
    setActiveResortTerrain(makeSampledRecord());
    expect(sampleLocalTerrainAt(-120, 47)).toBeNull();
    setActiveResortTerrain(null);
  });
});

describe('resortProtocolUrl', () => {
  it('revisions elevation-derived source templates by elevation checksum', () => {
    const rec = makeRecord();
    rec.packageManifest = { elevationChecksum: 'fnv1a32-grade1' } as TerrainRecord['packageManifest'];
    expect(resortProtocolUrl(RESORT_DEM_PROTOCOL, rec))
      .toBe('resort-dem://test-resort/{z}/{x}/{y}?rev=fnv1a32-grade1');
    rec.packageManifest!.elevationChecksum = 'fnv1a32-grade2';
    expect(resortProtocolUrl(RESORT_DEM_PROTOCOL, rec)).toContain('grade2');
  });
});
