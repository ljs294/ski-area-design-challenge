import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import type { TerrainRecord } from '../../../src/types/terrain';
import { createLocalTerrainFrame, sampleTerrainElevation } from './terrainSurface';
import { intersectTerrainRay } from './terrainPicking';

function record(): TerrainRecord {
  return {
    schemaVersion: 6, key: 'p0', mountainName: 'P0 fixture', latitude: 0.5, longitude: 0.5,
    areaSizeMeters: 111_320, bounds: { west: 0, south: 0, east: 1, north: 1 },
    sampleGridSize: 2, sampleHeights: Float32Array.from([100, 200, 300, 400]),
    climate: { monthly: [] }, sourceType: 'preset', createdAt: '', updatedAt: '',
  };
}

describe('P0 analytical terrain surface', () => {
  it('bilinearly samples the authoritative north-to-south raster orientation', () => {
    const terrain = record();
    expect(sampleTerrainElevation(terrain, 0, 1)).toBe(100);
    expect(sampleTerrainElevation(terrain, 1, 0)).toBe(400);
    expect(sampleTerrainElevation(terrain, 0.5, 0.5)).toBe(250);
    expect(sampleTerrainElevation(terrain, 2, 2)).toBeNull();
  });

  it('returns the nearest vertical hit and explicit outside/upward misses', () => {
    const terrain = record();
    const frame = createLocalTerrainFrame(terrain);
    const downward = new THREE.Ray(new THREE.Vector3(0, 1_000, 0), new THREE.Vector3(0, -1, 0));
    const hit = intersectTerrainRay(downward, terrain, frame);
    expect(hit?.distance).toBeCloseTo(1_000, 5);
    expect(hit?.lngLat[0]).toBeCloseTo(0.5, 6);
    expect(intersectTerrainRay(new THREE.Ray(new THREE.Vector3(frame.widthM, 100, 0),
      new THREE.Vector3(0, -1, 0)), terrain, frame)).toBeNull();
    expect(intersectTerrainRay(new THREE.Ray(new THREE.Vector3(0, 100, 0),
      new THREE.Vector3(0, 1, 0)), terrain, frame)).toBeNull();
  });

  it('intersects a tilted camera ray independently of rendered mesh triangles', () => {
    const terrain = record();
    const frame = createLocalTerrainFrame(terrain);
    const origin = new THREE.Vector3(-frame.widthM * .2, 800, -frame.depthM * .2);
    const direction = new THREE.Vector3(frame.widthM * .2, -800, frame.depthM * .2).normalize();
    const hit = intersectTerrainRay(new THREE.Ray(origin, direction), terrain, frame);
    expect(hit).not.toBeNull();
    expect(hit?.point[1]).toBeCloseTo(0, 1);
  });
});
