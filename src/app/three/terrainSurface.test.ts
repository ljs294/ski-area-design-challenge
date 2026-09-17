import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import type { TerrainRecord } from '../../types/terrain';
import { createLocalTerrainFrame, sampleTerrainElevation } from './terrainSurface';
import { intersectTerrainRay } from './terrainPicking';

function record(): TerrainRecord {
  return {
    schemaVersion: 6, key: 'p2a', mountainName: 'P2A fixture', latitude: 0.5, longitude: 0.5,
    areaSizeMeters: 111_320, bounds: { west: 0, south: 0, east: 1, north: 1 },
    sampleGridSize: 2, sampleHeights: Float32Array.from([100, 200, 300, 400]),
    climate: { monthly: [] }, sourceType: 'preset', createdAt: '', updatedAt: '',
  };
}

describe('Three.js product terrain surface', () => {
  it('samples the authoritative north-to-south terrain grid', () => {
    const terrain = record();
    expect(sampleTerrainElevation(terrain, 0, 1)).toBe(100);
    expect(sampleTerrainElevation(terrain, 1, 0)).toBe(400);
    expect(sampleTerrainElevation(terrain, 0.5, 0.5)).toBe(250);
    expect(sampleTerrainElevation(terrain, 2, 2)).toBeNull();
  });

  it('picks the analytical terrain independently of render mesh triangles', () => {
    const terrain = record(), frame = createLocalTerrainFrame(terrain);
    const vertical = intersectTerrainRay(new THREE.Ray(new THREE.Vector3(0, 1_000, 0),
      new THREE.Vector3(0, -1, 0)), terrain, frame);
    expect(vertical?.distance).toBeCloseTo(1_000, 5);
    const origin = new THREE.Vector3(-frame.widthM * .2, 800, -frame.depthM * .2);
    const direction = new THREE.Vector3(frame.widthM * .2, -800, frame.depthM * .2).normalize();
    expect(intersectTerrainRay(new THREE.Ray(origin, direction), terrain, frame)?.point[1]).toBeCloseTo(0, 1);
  });

  it('returns explicit misses outside the package and for upward rays', () => {
    const terrain = record(), frame = createLocalTerrainFrame(terrain);
    expect(intersectTerrainRay(new THREE.Ray(new THREE.Vector3(frame.widthM, 100, 0),
      new THREE.Vector3(0, -1, 0)), terrain, frame)).toBeNull();
    expect(intersectTerrainRay(new THREE.Ray(new THREE.Vector3(0, 100, 0),
      new THREE.Vector3(0, 1, 0)), terrain, frame)).toBeNull();
  });
});
