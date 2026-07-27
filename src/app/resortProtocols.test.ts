import { describe, expect, it } from 'vitest';
import { resortCameraBounds, resortDemBounds, resortWarmTileKeys } from './resortProtocols';
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
});
