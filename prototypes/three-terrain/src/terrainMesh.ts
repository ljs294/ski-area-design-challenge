import * as THREE from 'three';
import type { TerrainRecord } from '../../../src/types/terrain';
import { sampleTerrainCover, sampleTerrainElevation, type LocalTerrainFrame } from './terrainSurface';

const COVER_COLORS: Record<number, THREE.Color> = {
  1: new THREE.Color('#264f37'), 10: new THREE.Color('#264f37'),
  2: new THREE.Color('#879b61'), 20: new THREE.Color('#6f8955'),
  3: new THREE.Color('#a4af69'), 30: new THREE.Color('#a4af69'),
  4: new THREE.Color('#315f72'), 80: new THREE.Color('#315f72'),
  40: new THREE.Color('#b0aa72'), 50: new THREE.Color('#857d70'),
  60: new THREE.Color('#a59b81'), 70: new THREE.Color('#e5e5dc'),
  90: new THREE.Color('#668878'), 95: new THREE.Color('#55796f'),
  100: new THREE.Color('#8b9570'),
};

export interface TerrainMeshStats { chunks: number; triangles: number; vertices: number; uploadedBytes: number;
  surfaceMaxErrorM: number; surfaceRmsErrorM: number }

export function createTerrainMeshes(record: TerrainRecord, frame: LocalTerrainFrame,
  resolution = 257, chunkCells = 64): { group: THREE.Group; stats: TerrainMeshStats } {
  const group = new THREE.Group();
  const cells = resolution - 1;
  let triangles = 0, vertices = 0, uploadedBytes = 0;
  let squaredError = 0, maxError = 0, errorSamples = 0;
  for (let rowStart = 0; rowStart < cells; rowStart += chunkCells) {
    for (let columnStart = 0; columnStart < cells; columnStart += chunkCells) {
      const rows = Math.min(chunkCells, cells - rowStart);
      const columns = Math.min(chunkCells, cells - columnStart);
      const positions = new Float32Array((rows + 1) * (columns + 1) * 3);
      const colors = new Float32Array(positions.length);
      const indices = new Uint32Array(rows * columns * 6);
      let vertex = 0;
      for (let row = 0; row <= rows; row++) for (let column = 0; column <= columns; column++) {
        const u = (columnStart + column) / cells;
        const v = (rowStart + row) / cells;
        const x = (u - 0.5) * frame.widthM, z = (v - 0.5) * frame.depthM;
        const [lng, lat] = frame.toLngLat(x, z);
        const elevation = sampleTerrainElevation(record, lng, lat) ?? frame.centerElevationM;
        const normalizedHeight = THREE.MathUtils.clamp((elevation - frame.centerElevationM + 300) / 1_100, 0, 1);
        const base = new THREE.Color('#66745f').lerp(new THREE.Color('#d9ddd1'), normalizedHeight);
        const cover = COVER_COLORS[sampleTerrainCover(record, lng, lat) ?? -1];
        const core = record.bounds;
        let coverWeight = cover ? 0.88 : 0;
        if (cover && core) {
          const dx = Math.min(lng - core.west, core.east - lng) / Math.max(1e-9, (core.east - core.west) * .06);
          const dy = Math.min(lat - core.south, core.north - lat) / Math.max(1e-9, (core.north - core.south) * .06);
          coverWeight *= THREE.MathUtils.smoothstep(Math.min(dx, dy), 0, 1);
        }
        const color = cover ? base.clone().lerp(cover, coverWeight) : base;
        positions.set([x, elevation - frame.centerElevationM, z], vertex * 3);
        colors.set([color.r, color.g, color.b], vertex * 3);
        vertex++;
      }
      let index = 0;
      for (let row = 0; row < rows; row++) for (let column = 0; column < columns; column++) {
        const a = row * (columns + 1) + column, b = a + 1;
        const c = a + columns + 1, d = c + 1;
        indices.set([a, c, b, b, c, d], index); index += 6;
      }
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
      geometry.setIndex(new THREE.BufferAttribute(indices, 1));
      geometry.computeVertexNormals();
      geometry.computeBoundingBox();
      geometry.computeBoundingSphere();
      const material = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.92,
        metalness: 0, side: THREE.DoubleSide });
      const mesh = new THREE.Mesh(geometry, material);
      mesh.receiveShadow = true;
      group.add(mesh);
      triangles += rows * columns * 2;
      vertices += vertex;
      uploadedBytes += positions.byteLength + colors.byteLength + indices.byteLength;
    }
  }
  // Quantify the visible fixed-grid surface against the authoritative sampler
  // at cell centers. Picking remains analytical regardless of this display error.
  for (let row = 0; row < cells; row++) for (let column = 0; column < cells; column++) {
    const elevationAt = (u: number, v: number) => {
      const [lng, lat] = frame.toLngLat((u - .5) * frame.widthM, (v - .5) * frame.depthM);
      return sampleTerrainElevation(record, lng, lat);
    };
    const u0 = column / cells, u1 = (column + 1) / cells;
    const v0 = row / cells, v1 = (row + 1) / cells;
    const a = elevationAt(u0, v0), b = elevationAt(u1, v0), c = elevationAt(u0, v1), d = elevationAt(u1, v1);
    const actual = elevationAt((u0 + u1) / 2, (v0 + v1) / 2);
    if (a == null || b == null || c == null || d == null || actual == null) continue;
    const error = Math.abs(actual - (a + b + c + d) / 4);
    maxError = Math.max(maxError, error); squaredError += error * error; errorSamples++;
  }
  return { group, stats: { chunks: group.children.length, triangles, vertices, uploadedBytes,
    surfaceMaxErrorM: maxError, surfaceRmsErrorM: Math.sqrt(squaredError / Math.max(1, errorSamples)) } };
}
