import * as THREE from 'three';
import type { DesignSaveDocument } from '../../src/types/designSave';
import type { TerrainRecord } from '../../src/types/terrain';
import { sampleTerrainElevation, type LocalTerrainFrame } from './terrainSurface';
import { createTerrainMeshPresentation, type TerrainMeshStage } from './terrainMeshPresentation';

export type ThreeFeatureKind = 'lift' | 'trail' | 'node' | 'path' | 'junction';
export interface ThreeFeatureSelection { kind: ThreeFeatureKind; id: string }
export interface TerrainSceneResult {
  root: THREE.Group;
  pickables: THREE.Object3D[];
  contours: THREE.Group;
  imagery: THREE.Group;
  prepareTerrain(record: TerrainRecord, changedSampleIndices?: readonly number[]): Promise<TerrainSceneStage>;
  activateTerrain(stage: TerrainSceneStage): void;
  refineTerrainAt(record: TerrainRecord, x: number, z: number): void;
  dispose(): void;
}

export interface TerrainSceneStage {
  readonly record: TerrainRecord;
  readonly mesh: TerrainMeshStage;
  readonly contourPositions: Float32Array;
  readonly imageryPositions?: Float32Array;
  readonly boundaryPositions?: Float32Array;
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

function contourPositions(record: TerrainRecord, frame: LocalTerrainFrame): Float32Array {
  const segments = record.contourSegments ?? [], bounds = record.bounds;
  if (!bounds) return new Float32Array();
  const points: number[] = [];
  for (let index = 0; index + 4 < segments.length; index += 5) {
    for (const offset of [0, 2]) {
      const lng = bounds.west + segments[index + offset]! * (bounds.east - bounds.west);
      const lat = bounds.north - segments[index + offset + 1]! * (bounds.north - bounds.south);
      const local = frame.toLocal(lng, lat, segments[index + 4]!);
      points.push(local[0], local[1] + 1.2, local[2]);
    }
  }
  return Float32Array.from(points);
}

function contourLines(record: TerrainRecord, frame: LocalTerrainFrame): THREE.Group {
  const group = new THREE.Group();
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.BufferAttribute(contourPositions(record, frame), 3));
  group.add(new THREE.LineSegments(geometry, new THREE.LineBasicMaterial({ color: 0xf4f0da,
    transparent: true, opacity: .55 })));
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

function boundaryPositions(save: DesignSaveDocument, record: TerrainRecord,
  frame: LocalTerrainFrame): Float32Array | undefined {
  if (!save.site) return undefined;
  const [[west, south], [east, north]] = save.site.bounds;
  const points: [number, number][] = [[west, north], [east, north], [east, south], [west, south], [west, north]];
  return Float32Array.from(points.flatMap((point) => sampledPoint(frame, record, point).toArray()));
}

function drapeImagery(record: TerrainRecord, frame: LocalTerrainFrame,
  geometry: THREE.BufferGeometry): Float32Array {
  const source = geometry.getAttribute('position') as THREE.BufferAttribute;
  const positions = new Float32Array(source.count * 3);
  for (let index = 0; index < source.count; index++) {
    const x = source.getX(index), z = source.getZ(index);
    const [lng, lat] = frame.toLngLat(x, z);
    const elevation = sampleTerrainElevation(record, lng, lat) ?? frame.centerElevationM;
    positions.set([x, elevation - frame.centerElevationM + 1.5, z], index * 3);
  }
  return positions;
}

function imageryOverlay(record: TerrainRecord, frame: LocalTerrainFrame): THREE.Group {
  const group = new THREE.Group(), metadata = record.localImageryMetadata, bytes = record.localImagery;
  if (!metadata || !bytes) return group;
  const geometry = new THREE.PlaneGeometry(
    (metadata.bounds.east - metadata.bounds.west) / (frame.bounds.east - frame.bounds.west) * frame.widthM,
    (metadata.bounds.north - metadata.bounds.south) / (frame.bounds.north - frame.bounds.south) * frame.depthM,
    64, 64);
  geometry.rotateX(-Math.PI / 2);
  const centerLng = (metadata.bounds.west + metadata.bounds.east) / 2;
  const centerLat = (metadata.bounds.south + metadata.bounds.north) / 2;
  const center = frame.toLocal(centerLng, centerLat);
  geometry.translate(center[0], 0, center[2]);
  (geometry.getAttribute('position') as THREE.BufferAttribute).set(drapeImagery(record, frame, geometry));
  geometry.computeVertexNormals();
  const material = new THREE.MeshBasicMaterial({ transparent: true, opacity: .58, depthWrite: false });
  const mesh = new THREE.Mesh(geometry, material); group.add(mesh);
  const url = URL.createObjectURL(new Blob([Uint8Array.from(bytes).buffer], { type: metadata.mimeType }));
  new THREE.TextureLoader().load(url, (texture) => {
    URL.revokeObjectURL(url); texture.colorSpace = THREE.SRGBColorSpace;
    if (group.userData.disposed) { texture.dispose(); return; }
    material.map = texture; material.needsUpdate = true;
  }, undefined, () => URL.revokeObjectURL(url));
  return group;
}

export function createTerrainScene(save: DesignSaveDocument, record: TerrainRecord,
  frame: LocalTerrainFrame): TerrainSceneResult {
  const root = new THREE.Group();
  const terrain = createTerrainMeshPresentation(record, frame); root.add(terrain.group);
  const contours = contourLines(record, frame); contours.visible = false; root.add(contours);
  const imagery = imageryOverlay(record, frame); imagery.visible = false; root.add(imagery);
  const features = designFeatures(save, record, frame); root.add(features.group);
  const site = boundary(save, record, frame); if (site) root.add(site);
  const prepareTerrain = async (next: TerrainRecord,
    changedSampleIndices?: readonly number[]): Promise<TerrainSceneStage> => ({
    record: next,
    mesh: await terrain.prepareAsync(next, changedSampleIndices),
    contourPositions: contourPositions(next, frame),
    ...(imagery.children[0] instanceof THREE.Mesh
      ? { imageryPositions: drapeImagery(next, frame, imagery.children[0].geometry) } : {}),
    ...(boundaryPositions(save, next, frame) ? { boundaryPositions: boundaryPositions(save, next, frame) } : {}),
  });
  const activateTerrain = (stage: TerrainSceneStage) => {
    terrain.activate(stage.mesh);
    const line = contours.children[0] as THREE.LineSegments;
    const oldGeometry = line.geometry;
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(stage.contourPositions, 3));
    line.geometry = geometry; oldGeometry.dispose();
    const imageryMesh = imagery.children[0];
    if (stage.imageryPositions && imageryMesh instanceof THREE.Mesh) {
      const attribute = imageryMesh.geometry.getAttribute('position') as THREE.BufferAttribute;
      (attribute.array as Float32Array).set(stage.imageryPositions);
      attribute.clearUpdateRanges(); attribute.addUpdateRange(0, stage.imageryPositions.length);
      attribute.needsUpdate = true; imageryMesh.geometry.computeBoundingBox();
      imageryMesh.geometry.computeBoundingSphere();
    }
    if (stage.boundaryPositions && site) {
      const attribute = site.geometry.getAttribute('position') as THREE.BufferAttribute;
      (attribute.array as Float32Array).set(stage.boundaryPositions);
      attribute.clearUpdateRanges(); attribute.addUpdateRange(0, stage.boundaryPositions.length);
      attribute.needsUpdate = true; site.geometry.computeBoundingBox(); site.geometry.computeBoundingSphere();
    }
  };
  return { root, pickables: features.pickables, contours, imagery, prepareTerrain, activateTerrain,
    refineTerrainAt: (next, x, z) => terrain.refineAt(next, x, z),
    dispose: () => { terrain.dispose(); disposeTree(root); } };
}
