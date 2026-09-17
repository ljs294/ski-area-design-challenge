import * as THREE from 'three';
import type { TerrainRecord } from '../../src/types/terrain';
import { sampleTerrainCover, sampleTerrainElevation, type LocalTerrainFrame } from './terrainSurface';

const BASE_LOW = new THREE.Color('#66745f');
const BASE_HIGH = new THREE.Color('#d9ddd1');
const COVER_COLORS: Record<number, THREE.Color> = {
  1: new THREE.Color('#264f37'), 10: new THREE.Color('#264f37'),
  2: new THREE.Color('#879b61'), 20: new THREE.Color('#6f8955'),
  3: new THREE.Color('#a4af69'), 30: new THREE.Color('#a4af69'),
  4: new THREE.Color('#315f72'), 80: new THREE.Color('#315f72'),
  40: new THREE.Color('#b0aa72'), 50: new THREE.Color('#857d70'),
  60: new THREE.Color('#a59b81'), 70: new THREE.Color('#e5e5dc'),
  90: new THREE.Color('#668878'), 95: new THREE.Color('#55796f'), 100: new THREE.Color('#8b9570'),
};

interface TerrainChunk {
  readonly mesh: THREE.Mesh<THREE.BufferGeometry, THREE.MeshStandardMaterial>;
  readonly rowStart: number;
  readonly columnStart: number;
  readonly rows: number;
  readonly columns: number;
}

export interface TerrainChunkStage {
  readonly chunk: TerrainChunk;
  readonly minRow: number;
  readonly maxRow: number;
  readonly minColumn: number;
  readonly maxColumn: number;
  readonly positions: Float32Array;
  readonly normals: Float32Array;
  readonly colors: Float32Array;
}

export interface TerrainMeshStage {
  readonly record: TerrainRecord;
  readonly chunks: readonly TerrainChunkStage[];
  readonly detail?: TerrainChunkStage;
  readonly full: boolean;
  readonly uploadedBytes: number;
  readonly longestPreparationSliceMs?: number;
}

export interface TerrainMeshPresentation {
  readonly group: THREE.Group;
  readonly chunkCount: number;
  prepare(record: TerrainRecord, changedSampleIndices?: readonly number[]): TerrainMeshStage;
  prepareAsync(record: TerrainRecord, changedSampleIndices?: readonly number[]): Promise<TerrainMeshStage>;
  activate(stage: TerrainMeshStage): void;
  refineAt(record: TerrainRecord, x: number, z: number): void;
  dispose(): void;
}

interface DisplayRange { minRow: number; maxRow: number; minColumn: number; maxColumn: number }

function dirtyDisplayRange(record: TerrainRecord, frame: LocalTerrainFrame, resolution: number,
  changedSampleIndices: readonly number[]): DisplayRange | null {
  if (!record.bounds || changedSampleIndices.length === 0) return null;
  const sourceCells = record.sampleGridSize - 1;
  if (sourceCells < 1) return null;
  let west = Number.POSITIVE_INFINITY, east = Number.NEGATIVE_INFINITY;
  let south = Number.POSITIVE_INFINITY, north = Number.NEGATIVE_INFINITY;
  for (const index of changedSampleIndices) {
    if (!Number.isInteger(index) || index < 0 || index >= record.sampleHeights.length) continue;
    const row = Math.floor(index / record.sampleGridSize), column = index % record.sampleGridSize;
    const minColumn = Math.max(0, column - 1), maxColumn = Math.min(sourceCells, column + 1);
    const minRow = Math.max(0, row - 1), maxRow = Math.min(sourceCells, row + 1);
    west = Math.min(west, record.bounds.west
      + minColumn / sourceCells * (record.bounds.east - record.bounds.west));
    east = Math.max(east, record.bounds.west
      + maxColumn / sourceCells * (record.bounds.east - record.bounds.west));
    north = Math.max(north, record.bounds.north
      - minRow / sourceCells * (record.bounds.north - record.bounds.south));
    south = Math.min(south, record.bounds.north
      - maxRow / sourceCells * (record.bounds.north - record.bounds.south));
  }
  if (!Number.isFinite(west)) return null;
  const cells = resolution - 1;
  const toColumn = (lng: number) => (lng - frame.bounds.west)
    / (frame.bounds.east - frame.bounds.west) * cells;
  const toRow = (lat: number) => (frame.bounds.north - lat)
    / (frame.bounds.north - frame.bounds.south) * cells;
  // One display-vertex halo covers the central-difference normal stencil and
  // includes both chunks when the affected samples touch a shared border.
  return {
    minColumn: Math.max(0, Math.floor(toColumn(west)) - 1),
    maxColumn: Math.min(cells, Math.ceil(toColumn(east)) + 1),
    minRow: Math.max(0, Math.floor(toRow(north)) - 1),
    maxRow: Math.min(cells, Math.ceil(toRow(south)) + 1),
  };
}

function overlaps(chunk: TerrainChunk, range: DisplayRange): boolean {
  return chunk.columnStart <= range.maxColumn
    && chunk.columnStart + chunk.columns >= range.minColumn
    && chunk.rowStart <= range.maxRow
    && chunk.rowStart + chunk.rows >= range.minRow;
}

function normalAt(record: TerrainRecord, frame: LocalTerrainFrame, x: number, z: number,
  stepX: number, stepZ: number, target: THREE.Vector3): THREE.Vector3 {
  const x0 = Math.max(-frame.widthM / 2, x - stepX), x1 = Math.min(frame.widthM / 2, x + stepX);
  const z0 = Math.max(-frame.depthM / 2, z - stepZ), z1 = Math.min(frame.depthM / 2, z + stepZ);
  const left = sampleTerrainElevation(record, ...frame.toLngLat(x0, z));
  const right = sampleTerrainElevation(record, ...frame.toLngLat(x1, z));
  const north = sampleTerrainElevation(record, ...frame.toLngLat(x, z0));
  const south = sampleTerrainElevation(record, ...frame.toLngLat(x, z1));
  const center = sampleTerrainElevation(record, ...frame.toLngLat(x, z)) ?? frame.centerElevationM;
  const dx = ((right ?? center) - (left ?? center)) / Math.max(0.001, x1 - x0);
  const dz = ((south ?? center) - (north ?? center)) / Math.max(0.001, z1 - z0);
  return target.set(-dx, 1, -dz).normalize();
}

function stageChunk(chunk: TerrainChunk, record: TerrainRecord, frame: LocalTerrainFrame,
  resolution: number, range?: DisplayRange): TerrainChunkStage {
  const minRow = range ? Math.max(0, range.minRow - chunk.rowStart) : 0;
  const maxRow = range ? Math.min(chunk.rows, range.maxRow - chunk.rowStart) : chunk.rows;
  const minColumn = range ? Math.max(0, range.minColumn - chunk.columnStart) : 0;
  const maxColumn = range ? Math.min(chunk.columns, range.maxColumn - chunk.columnStart) : chunk.columns;
  const count = (maxRow - minRow + 1) * (maxColumn - minColumn + 1);
  const positions = new Float32Array(count * 3), normals = new Float32Array(count * 3);
  const colors = new Float32Array(count * 3), cells = resolution - 1;
  const stepX = frame.widthM / cells, stepZ = frame.depthM / cells;
  const normal = new THREE.Vector3(), color = new THREE.Color();
  let vertex = 0;
  for (let row = minRow; row <= maxRow; row++) for (let column = minColumn; column <= maxColumn; column++) {
    const u = (chunk.columnStart + column) / cells, v = (chunk.rowStart + row) / cells;
    const x = (u - .5) * frame.widthM, z = (v - .5) * frame.depthM;
    const [lng, lat] = frame.toLngLat(x, z);
    const elevation = sampleTerrainElevation(record, lng, lat) ?? frame.centerElevationM;
    const height = THREE.MathUtils.clamp((elevation - frame.centerElevationM + 300) / 1_100, 0, 1);
    color.copy(BASE_LOW).lerp(BASE_HIGH, height);
    const cover = COVER_COLORS[sampleTerrainCover(record, lng, lat) ?? -1];
    if (cover) color.lerp(cover, .86);
    normalAt(record, frame, x, z, stepX, stepZ, normal);
    positions.set([x, elevation - frame.centerElevationM, z], vertex * 3);
    normals.set([normal.x, normal.y, normal.z], vertex * 3);
    colors.set([color.r, color.g, color.b], vertex * 3);
    vertex++;
  }
  return { chunk, minRow, maxRow, minColumn, maxColumn, positions, normals, colors };
}

function updateAttribute(attribute: THREE.BufferAttribute, values: Float32Array): void {
  (attribute.array as Float32Array).set(values);
  attribute.clearUpdateRanges();
  attribute.addUpdateRange(0, values.length);
  attribute.needsUpdate = true;
}

function updateChunkAttribute(attribute: THREE.BufferAttribute, values: Float32Array,
  stage: TerrainChunkStage): void {
  const target = attribute.array as Float32Array;
  const width = stage.chunk.columns + 1;
  const rowWidth = stage.maxColumn - stage.minColumn + 1;
  attribute.clearUpdateRanges();
  let sourceOffset = 0;
  for (let row = stage.minRow; row <= stage.maxRow; row++) {
    const targetOffset = (row * width + stage.minColumn) * 3;
    const componentCount = rowWidth * 3;
    target.set(values.subarray(sourceOffset, sourceOffset + componentCount), targetOffset);
    attribute.addUpdateRange(targetOffset, componentCount);
    sourceOffset += componentCount;
  }
  attribute.needsUpdate = true;
}

export function createTerrainMeshPresentation(record: TerrainRecord, frame: LocalTerrainFrame,
  resolution = 257, chunkCells = 64): TerrainMeshPresentation {
  const group = new THREE.Group(), chunks: TerrainChunk[] = [], cells = resolution - 1;
  for (let rowStart = 0; rowStart < cells; rowStart += chunkCells) {
    for (let columnStart = 0; columnStart < cells; columnStart += chunkCells) {
      const rows = Math.min(chunkCells, cells - rowStart);
      const columns = Math.min(chunkCells, cells - columnStart);
      const indices = new Uint32Array(rows * columns * 6);
      let index = 0;
      for (let row = 0; row < rows; row++) for (let column = 0; column < columns; column++) {
        const a = row * (columns + 1) + column, b = a + 1, c = a + columns + 1, d = c + 1;
        indices.set([a, c, b, b, c, d], index); index += 6;
      }
      const geometry = new THREE.BufferGeometry();
      const count = (rows + 1) * (columns + 1);
      for (const name of ['position', 'normal', 'color'] as const) {
        const attribute = new THREE.BufferAttribute(new Float32Array(count * 3), 3);
        attribute.setUsage(THREE.DynamicDrawUsage); geometry.setAttribute(name, attribute);
      }
      geometry.setIndex(new THREE.BufferAttribute(indices, 1));
      const mesh = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({ vertexColors: true,
        roughness: .92, metalness: 0, side: THREE.DoubleSide }));
      const chunk: TerrainChunk = { mesh, rowStart, columnStart, rows, columns };
      chunks.push(chunk); group.add(mesh);
    }
  }
  const detailCells = 32;
  const detailGeometry = new THREE.BufferGeometry();
  const detailCount = (detailCells + 1) * (detailCells + 1);
  for (const name of ['position', 'normal', 'color'] as const) {
    const attribute = new THREE.BufferAttribute(new Float32Array(detailCount * 3), 3);
    attribute.setUsage(THREE.DynamicDrawUsage); detailGeometry.setAttribute(name, attribute);
  }
  const detailIndices = new Uint32Array(detailCells * detailCells * 6);
  let detailIndex = 0;
  for (let row = 0; row < detailCells; row++) for (let column = 0; column < detailCells; column++) {
    const a = row * (detailCells + 1) + column, b = a + 1, c = a + detailCells + 1, d = c + 1;
    detailIndices.set([a, c, b, b, c, d], detailIndex); detailIndex += 6;
  }
  detailGeometry.setIndex(new THREE.BufferAttribute(detailIndices, 1));
  const detailMesh = new THREE.Mesh(detailGeometry, new THREE.MeshStandardMaterial({ vertexColors: true,
    roughness: .92, metalness: 0, side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -1 }));
  detailMesh.visible = false; detailMesh.renderOrder = 1; group.add(detailMesh);
  let detailCenter: { x: number; z: number } | null = null;
  const detailChunk: TerrainChunk = { mesh: detailMesh, rowStart: 0, columnStart: 0,
    rows: detailCells, columns: detailCells };
  const stageDetail = (next: TerrainRecord): TerrainChunkStage | undefined => {
    if (!detailCenter || !next.bounds) return undefined;
    const coreWidth = (next.bounds.east - next.bounds.west)
      / (frame.bounds.east - frame.bounds.west) * frame.widthM;
    const coreDepth = (next.bounds.north - next.bounds.south)
      / (frame.bounds.north - frame.bounds.south) * frame.depthM;
    const stepX = coreWidth / Math.max(1, next.sampleGridSize - 1);
    const stepZ = coreDepth / Math.max(1, next.sampleGridSize - 1);
    const halfWidth = stepX * detailCells / 2, halfDepth = stepZ * detailCells / 2;
    const centerX = THREE.MathUtils.clamp(detailCenter.x, -frame.widthM / 2 + halfWidth,
      frame.widthM / 2 - halfWidth);
    const centerZ = THREE.MathUtils.clamp(detailCenter.z, -frame.depthM / 2 + halfDepth,
      frame.depthM / 2 - halfDepth);
    const positions = new Float32Array(detailCount * 3), normals = new Float32Array(detailCount * 3);
    const colors = new Float32Array(detailCount * 3), normal = new THREE.Vector3(), color = new THREE.Color();
    let vertex = 0;
    for (let row = 0; row <= detailCells; row++) for (let column = 0; column <= detailCells; column++) {
      const x = centerX + (column - detailCells / 2) * stepX;
      const z = centerZ + (row - detailCells / 2) * stepZ;
      const [lng, lat] = frame.toLngLat(x, z);
      const elevation = sampleTerrainElevation(next, lng, lat) ?? frame.centerElevationM;
      const height = THREE.MathUtils.clamp((elevation - frame.centerElevationM + 300) / 1_100, 0, 1);
      color.copy(BASE_LOW).lerp(BASE_HIGH, height);
      const cover = COVER_COLORS[sampleTerrainCover(next, lng, lat) ?? -1]; if (cover) color.lerp(cover, .86);
      normalAt(next, frame, x, z, stepX, stepZ, normal);
      positions.set([x, elevation - frame.centerElevationM + .03, z], vertex * 3);
      normals.set([normal.x, normal.y, normal.z], vertex * 3);
      colors.set([color.r, color.g, color.b], vertex * 3); vertex++;
    }
    return { chunk: detailChunk, minRow: 0, maxRow: detailCells,
      minColumn: 0, maxColumn: detailCells, positions, normals, colors };
  };
  const selectedChunks = (next: TerrainRecord, changedSampleIndices?: readonly number[]) => {
    const range = changedSampleIndices === undefined ? null
      : dirtyDisplayRange(next, frame, resolution, changedSampleIndices);
    return { range, selected: range ? chunks.filter((chunk) => overlaps(chunk, range)) : chunks };
  };
  const finishStage = (next: TerrainRecord, range: DisplayRange | null,
    stages: TerrainChunkStage[], longestPreparationSliceMs?: number): TerrainMeshStage => {
    const detail = stageDetail(next);
    return { record: next, chunks: stages, ...(detail ? { detail } : {}), full: !range,
      ...(longestPreparationSliceMs === undefined ? {} : { longestPreparationSliceMs }),
      uploadedBytes: stages.reduce((sum, item) => sum + item.positions.byteLength
        + item.normals.byteLength + item.colors.byteLength, 0)
        + (detail ? detail.positions.byteLength + detail.normals.byteLength + detail.colors.byteLength : 0) };
  };
  const prepare = (next: TerrainRecord, changedSampleIndices?: readonly number[]): TerrainMeshStage => {
    const { range, selected } = selectedChunks(next, changedSampleIndices);
    return finishStage(next, range,
      selected.map((chunk) => stageChunk(chunk, next, frame, resolution, range ?? undefined)));
  };
  const prepareAsync = async (next: TerrainRecord,
    changedSampleIndices?: readonly number[]): Promise<TerrainMeshStage> => {
    const { range, selected } = selectedChunks(next, changedSampleIndices);
    const stages: TerrainChunkStage[] = [];
    let longestPreparationSliceMs = 0;
    let sliceStarted = performance.now();
    for (let index = 0; index < selected.length; index++) {
      const chunk = selected[index]!;
      stages.push(stageChunk(chunk, next, frame, resolution, range ?? undefined));
      if (index % 2 === 1 && index < selected.length - 1) {
        longestPreparationSliceMs = Math.max(longestPreparationSliceMs, performance.now() - sliceStarted);
        await new Promise<void>((resolve) => globalThis.setTimeout(resolve, 0));
        sliceStarted = performance.now();
      }
    }
    longestPreparationSliceMs = Math.max(longestPreparationSliceMs, performance.now() - sliceStarted);
    return finishStage(next, range, stages, longestPreparationSliceMs);
  };
  const activate = (stage: TerrainMeshStage) => {
    for (const item of stage.chunks) {
      const geometry = item.chunk.mesh.geometry;
      updateChunkAttribute(geometry.getAttribute('position') as THREE.BufferAttribute, item.positions, item);
      updateChunkAttribute(geometry.getAttribute('normal') as THREE.BufferAttribute, item.normals, item);
      updateChunkAttribute(geometry.getAttribute('color') as THREE.BufferAttribute, item.colors, item);
      geometry.computeBoundingBox(); geometry.computeBoundingSphere();
    }
    if (stage.detail) {
      updateAttribute(detailGeometry.getAttribute('position') as THREE.BufferAttribute, stage.detail.positions);
      updateAttribute(detailGeometry.getAttribute('normal') as THREE.BufferAttribute, stage.detail.normals);
      updateAttribute(detailGeometry.getAttribute('color') as THREE.BufferAttribute, stage.detail.colors);
      detailGeometry.computeBoundingBox(); detailGeometry.computeBoundingSphere(); detailMesh.visible = true;
    }
  };
  const refineAt = (next: TerrainRecord, x: number, z: number) => {
    detailCenter = { x, z };
    const detail = stageDetail(next); if (!detail) return;
    updateAttribute(detailGeometry.getAttribute('position') as THREE.BufferAttribute, detail.positions);
    updateAttribute(detailGeometry.getAttribute('normal') as THREE.BufferAttribute, detail.normals);
    updateAttribute(detailGeometry.getAttribute('color') as THREE.BufferAttribute, detail.colors);
    detailGeometry.computeBoundingBox(); detailGeometry.computeBoundingSphere(); detailMesh.visible = true;
  };
  activate(prepare(record));
  return { group, chunkCount: chunks.length, prepare, prepareAsync, activate, refineAt,
    dispose: () => {
      for (const chunk of chunks) { chunk.mesh.geometry.dispose(); chunk.mesh.material.dispose(); }
      detailGeometry.dispose(); detailMesh.material.dispose();
      group.clear();
    } };
}
