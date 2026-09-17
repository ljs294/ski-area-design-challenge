import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import type { TerrainRecord } from '../../src/types/terrain';
import { createTerrainMeshPresentation } from './terrainMeshPresentation';
import { createLocalTerrainFrame } from './terrainSurface';

function record(size = 5): TerrainRecord {
  return { schemaVersion: 6, key: 'mesh', mountainName: 'Mesh', latitude: .5, longitude: .5,
    areaSizeMeters: 111_320, bounds: { west: 0, south: 0, east: 1, north: 1 },
    sampleGridSize: size, sampleHeights: Float32Array.from({ length: size * size }, (_, index) => index),
    climate: { monthly: [] }, sourceType: 'preset', createdAt: '', updatedAt: '' };
}

describe('incremental terrain mesh presentation', () => {
  it('keeps topology stable and stages only chunks affected by a sparse grade', () => {
    const terrain = record(), presentation = createTerrainMeshPresentation(terrain,
      createLocalTerrainFrame(terrain), 9, 4);
    const firstMesh = presentation.group.children[0] as THREE.Mesh<THREE.BufferGeometry>;
    const index = firstMesh.geometry.index;
    const edited = { ...terrain, sampleHeights: terrain.sampleHeights.slice() };
    edited.sampleHeights[0] = 100;
    const stage = presentation.prepare(edited, [0]);

    expect(stage.full).toBe(false);
    expect(stage.chunks.length).toBeLessThan(presentation.chunkCount);
    presentation.activate(stage);
    expect((presentation.group.children[0] as THREE.Mesh<THREE.BufferGeometry>).geometry.index).toBe(index);
  });

  it('derives identical positions and normals on shared chunk borders', () => {
    const terrain = record(), presentation = createTerrainMeshPresentation(terrain,
      createLocalTerrainFrame(terrain), 9, 4);
    const left = presentation.group.children[0] as THREE.Mesh<THREE.BufferGeometry>;
    const right = presentation.group.children[1] as THREE.Mesh<THREE.BufferGeometry>;
    const leftPosition = left.geometry.getAttribute('position');
    const rightPosition = right.geometry.getAttribute('position');
    const leftNormal = left.geometry.getAttribute('normal');
    const rightNormal = right.geometry.getAttribute('normal');
    for (let row = 0; row <= 4; row++) {
      const li = row * 5 + 4, ri = row * 5;
      expect(leftPosition.getY(li)).toBeCloseTo(rightPosition.getY(ri), 6);
      expect(leftNormal.getX(li)).toBeCloseTo(rightNormal.getX(ri), 6);
      expect(leftNormal.getY(li)).toBeCloseTo(rightNormal.getY(ri), 6);
      expect(leftNormal.getZ(li)).toBeCloseTo(rightNormal.getZ(ri), 6);
    }
  });

  it('updates every neighbor coherently when a grade crosses a four-chunk corner', () => {
    const terrain = record(), presentation = createTerrainMeshPresentation(terrain,
      createLocalTerrainFrame(terrain), 9, 4);
    const sourceBefore = [...terrain.sampleHeights];
    const edited = { ...terrain, sampleHeights: terrain.sampleHeights.slice() };
    edited.sampleHeights[12] = 200;

    const stage = presentation.prepare(edited, [12]);
    expect(stage.chunks).toHaveLength(4);
    expect(stage.full).toBe(false);
    presentation.activate(stage);
    expect([...terrain.sampleHeights]).toEqual(sourceBefore);

    const left = presentation.group.children[0] as THREE.Mesh<THREE.BufferGeometry>;
    const right = presentation.group.children[1] as THREE.Mesh<THREE.BufferGeometry>;
    const leftNormal = left.geometry.getAttribute('normal');
    const rightNormal = right.geometry.getAttribute('normal');
    const li = 4 * 5 + 4, ri = 4 * 5;
    expect(leftNormal.getX(li)).toBeCloseTo(rightNormal.getX(ri), 6);
    expect(leftNormal.getY(li)).toBeCloseTo(rightNormal.getY(ri), 6);
    expect(leftNormal.getZ(li)).toBeCloseTo(rightNormal.getZ(ri), 6);
  });

  it('uses an explicit full refresh when sparse provenance is unavailable', () => {
    const terrain = record(), presentation = createTerrainMeshPresentation(terrain,
      createLocalTerrainFrame(terrain), 9, 4);
    const stage = presentation.prepare(terrain);
    expect(stage.full).toBe(true);
    expect(stage.chunks).toHaveLength(presentation.chunkCount);
  });

  it('keeps an analytical-resolution interaction patch in the same activation', () => {
    const terrain = record(), presentation = createTerrainMeshPresentation(terrain,
      createLocalTerrainFrame(terrain), 9, 4);
    presentation.refineAt(terrain, 0, 0);
    const detail = presentation.group.children.at(-1) as THREE.Mesh<THREE.BufferGeometry>;
    expect(detail.visible).toBe(true);

    const edited = { ...terrain, sampleHeights: terrain.sampleHeights.slice() };
    edited.sampleHeights[12] = 200;
    const stage = presentation.prepare(edited, [12]);
    expect(stage.detail).toBeDefined();
    presentation.activate(stage);
    expect(detail.geometry.getAttribute('position').count).toBe(33 * 33);
  });
});
