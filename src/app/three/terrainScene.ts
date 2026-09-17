import * as THREE from 'three';
import type { DesignSaveDocument } from '../../types/designSave';
import type { TerrainRecord } from '../../types/terrain';
import { sampleTerrainCover, sampleTerrainElevation, type LocalTerrainFrame } from './terrainSurface';

const COVER_COLORS: Record<number, THREE.Color> = {
  1: new THREE.Color('#264f37'), 10: new THREE.Color('#264f37'),
  2: new THREE.Color('#879b61'), 20: new THREE.Color('#6f8955'),
  3: new THREE.Color('#a4af69'), 30: new THREE.Color('#a4af69'),
  4: new THREE.Color('#315f72'), 80: new THREE.Color('#315f72'),
  40: new THREE.Color('#b0aa72'), 50: new THREE.Color('#857d70'),
  60: new THREE.Color('#a59b81'), 70: new THREE.Color('#e5e5dc'),
  90: new THREE.Color('#668878'), 95: new THREE.Color('#55796f'), 100: new THREE.Color('#8b9570'),
};

export type ThreeFeatureKind = 'lift' | 'trail' | 'node' | 'path' | 'junction';
export interface ThreeFeatureSelection { kind: ThreeFeatureKind; id: string }
export interface TerrainSceneResult {
  root: THREE.Group;
  pickables: THREE.Object3D[];
  contours: THREE.Group;
  imagery: THREE.Group;
  dispose(): void;
}

function sampledPoint(frame: LocalTerrainFrame, record: TerrainRecord,
  point: [number, number], elevation?: number | null): THREE.Vector3 {
  const value = elevation ?? sampleTerrainElevation(record, ...point) ?? frame.centerElevationM;
  const local = frame.toLocal(point[0], point[1], value);
  return new THREE.Vector3(local[0], local[1] + 2.5, local[2]);
}

function featureLine(points: THREE.Vector3[], color: number, selection: ThreeFeatureSelection,
  width = 2): THREE.Line {
  const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(points),
    new THREE.LineBasicMaterial({ color, linewidth: width, depthTest: true }));
  line.userData.selection = selection;
  line.userData.baseColor = color;
  return line;
}

function disposeTree(root: THREE.Object3D): void {
  root.traverse((child) => {
    child.userData.disposed = true;
    if (!(child instanceof THREE.Mesh || child instanceof THREE.Line || child instanceof THREE.Points)) return;
    child.geometry.dispose();
    for (const material of Array.isArray(child.material) ? child.material : [child.material]) {
      const map = 'map' in material ? material.map as THREE.Texture | null : null;
      map?.dispose(); material.dispose();
    }
  });
}

function terrainMeshes(record: TerrainRecord, frame: LocalTerrainFrame,
  resolution = 257, chunkCells = 64): THREE.Group {
  const group = new THREE.Group(), cells = resolution - 1;
  for (let rowStart = 0; rowStart < cells; rowStart += chunkCells) {
    for (let columnStart = 0; columnStart < cells; columnStart += chunkCells) {
      const rows = Math.min(chunkCells, cells - rowStart), columns = Math.min(chunkCells, cells - columnStart);
      const positions = new Float32Array((rows + 1) * (columns + 1) * 3);
      const colors = new Float32Array(positions.length);
      const indices = new Uint32Array(rows * columns * 6);
      let vertex = 0;
      for (let row = 0; row <= rows; row++) for (let column = 0; column <= columns; column++) {
        const u = (columnStart + column) / cells, v = (rowStart + row) / cells;
        const x = (u - .5) * frame.widthM, z = (v - .5) * frame.depthM;
        const [lng, lat] = frame.toLngLat(x, z);
        const elevation = sampleTerrainElevation(record, lng, lat) ?? frame.centerElevationM;
        const height = THREE.MathUtils.clamp((elevation - frame.centerElevationM + 300) / 1_100, 0, 1);
        const base = new THREE.Color('#66745f').lerp(new THREE.Color('#d9ddd1'), height);
        const cover = COVER_COLORS[sampleTerrainCover(record, lng, lat) ?? -1];
        const color = cover ? base.clone().lerp(cover, .86) : base;
        positions.set([x, elevation - frame.centerElevationM, z], vertex * 3);
        colors.set([color.r, color.g, color.b], vertex * 3); vertex++;
      }
      let index = 0;
      for (let row = 0; row < rows; row++) for (let column = 0; column < columns; column++) {
        const a = row * (columns + 1) + column, b = a + 1, c = a + columns + 1, d = c + 1;
        indices.set([a, c, b, b, c, d], index); index += 6;
      }
      const geometry = new THREE.BufferGeometry();
      geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
      geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
      geometry.setIndex(new THREE.BufferAttribute(indices, 1)); geometry.computeVertexNormals();
      geometry.computeBoundingSphere();
      group.add(new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({ vertexColors: true,
        roughness: .92, metalness: 0, side: THREE.DoubleSide })));
    }
  }
  return group;
}

function contourLines(record: TerrainRecord, frame: LocalTerrainFrame): THREE.Group {
  const group = new THREE.Group(), segments = record.contourSegments ?? [], bounds = record.bounds;
  if (!bounds) return group;
  const points: number[] = [];
  for (let index = 0; index + 4 < segments.length; index += 5) {
    for (const offset of [0, 2]) {
      const lng = bounds.west + segments[index + offset]! * (bounds.east - bounds.west);
      const lat = bounds.north - segments[index + offset + 1]! * (bounds.north - bounds.south);
      const local = frame.toLocal(lng, lat, segments[index + 4]!);
      points.push(local[0], local[1] + 1.2, local[2]);
    }
  }
  if (points.length) {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.Float32BufferAttribute(points, 3));
    group.add(new THREE.LineSegments(geometry, new THREE.LineBasicMaterial({ color: 0xf4f0da,
      transparent: true, opacity: .55 })));
  }
  return group;
}

function designFeatures(save: DesignSaveDocument, record: TerrainRecord,
  frame: LocalTerrainFrame): { group: THREE.Group; pickables: THREE.Object3D[] } {
  const group = new THREE.Group(), pickables: THREE.Object3D[] = [];
  for (const lift of save.lifts) {
    const object = featureLine(lift.points.map((point, index) =>
      sampledPoint(frame, record, point, lift.endpointElevM[index])), 0xf2b544, { kind: 'lift', id: lift.id }, 4);
    group.add(object); pickables.push(object);
  }
  for (const trail of save.trails) for (const part of trail.parts) {
    const object = featureLine(part.centerline.map((point, index) =>
      sampledPoint(frame, record, point, part.centerlineElevM[index])),
    trail.difficulty === 'green' ? 0x38a866 : trail.difficulty === 'blue' ? 0x3186cc : 0x20252a,
    { kind: 'trail', id: trail.id }, 5);
    group.add(object); pickables.push(object);
  }
  for (const path of save.paths) {
    const object = featureLine(path.points.map((point, index) =>
      sampledPoint(frame, record, point, path.pointElevM[index])), 0xe8e5d8, { kind: 'path', id: path.id }, 3);
    group.add(object); pickables.push(object);
  }
  const markerGeometry = new THREE.SphereGeometry(8, 12, 8);
  for (const item of [...save.nodes.map((node) => ({ ...node, kind: 'node' as const })),
    ...save.junctions.map((junction) => ({ ...junction, kind: 'junction' as const }))]) {
    const marker = new THREE.Mesh(markerGeometry.clone(), new THREE.MeshBasicMaterial({
      color: item.kind === 'node' ? 0xf8f6e8 : 0xf2b544 }));
    marker.position.copy(sampledPoint(frame, record, item.point, item.elevM));
    marker.userData.selection = { kind: item.kind, id: item.id } satisfies ThreeFeatureSelection;
    marker.userData.baseColor = (marker.material as THREE.MeshBasicMaterial).color.getHex();
    group.add(marker); pickables.push(marker);
  }
  markerGeometry.dispose();
  return { group, pickables };
}

function boundary(save: DesignSaveDocument, record: TerrainRecord, frame: LocalTerrainFrame): THREE.Line | null {
  if (!save.site) return null;
  const [[west, south], [east, north]] = save.site.bounds;
  const points: [number, number][] = [[west, north], [east, north], [east, south], [west, south], [west, north]];
  return featureLine(points.map((point) => sampledPoint(frame, record, point)), 0xffffff,
    { kind: 'junction', id: '__site-boundary' }, 2);
}

function imageryOverlay(record: TerrainRecord, frame: LocalTerrainFrame): THREE.Group {
  const group = new THREE.Group(), metadata = record.localImageryMetadata, bytes = record.localImagery;
  if (!metadata || !bytes) return group;
  const url = URL.createObjectURL(new Blob([Uint8Array.from(bytes).buffer], { type: metadata.mimeType }));
  new THREE.TextureLoader().load(url, (texture) => {
    URL.revokeObjectURL(url); texture.colorSpace = THREE.SRGBColorSpace;
    if (group.userData.disposed) { texture.dispose(); return; }
    const geometry = new THREE.PlaneGeometry(
      (metadata.bounds.east - metadata.bounds.west) / (frame.bounds.east - frame.bounds.west) * frame.widthM,
      (metadata.bounds.north - metadata.bounds.south) / (frame.bounds.north - frame.bounds.south) * frame.depthM,
      64, 64);
    geometry.rotateX(-Math.PI / 2);
    const positions = geometry.attributes.position as THREE.BufferAttribute;
    const centerLng = (metadata.bounds.west + metadata.bounds.east) / 2;
    const centerLat = (metadata.bounds.south + metadata.bounds.north) / 2;
    const center = frame.toLocal(centerLng, centerLat);
    for (let index = 0; index < positions.count; index++) {
      const x = positions.getX(index) + center[0], z = positions.getZ(index) + center[2];
      const [lng, lat] = frame.toLngLat(x, z);
      const elevation = sampleTerrainElevation(record, lng, lat) ?? frame.centerElevationM;
      positions.setXYZ(index, x, elevation - frame.centerElevationM + 1.5, z);
    }
    geometry.computeVertexNormals();
    group.add(new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ map: texture,
      transparent: true, opacity: .58, depthWrite: false })));
  }, undefined, () => URL.revokeObjectURL(url));
  return group;
}

export function createTerrainScene(save: DesignSaveDocument, record: TerrainRecord,
  frame: LocalTerrainFrame): TerrainSceneResult {
  const root = new THREE.Group();
  root.add(terrainMeshes(record, frame));
  const contours = contourLines(record, frame); contours.visible = false; root.add(contours);
  const imagery = imageryOverlay(record, frame); imagery.visible = false; root.add(imagery);
  const features = designFeatures(save, record, frame); root.add(features.group);
  const site = boundary(save, record, frame); if (site) root.add(site);
  return { root, pickables: features.pickables, contours, imagery,
    dispose: () => disposeTree(root) };
}
