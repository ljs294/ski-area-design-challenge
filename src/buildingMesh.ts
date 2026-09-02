/**
 * Versioned, dependency-neutral geometry for player buildings.
 *
 * Coordinates are local metres: x is east, y is north and z is up from the
 * finished floor datum.  Keeping the mesh in this coordinate system makes it
 * useful to both the native MapLibre renderer and the Graphics Lab, while the
 * renderer is responsible for placing the anchor in Mercator space.
 */

export const RECTANGULAR_GABLE_GENERATOR_VERSION = 1 as const;
export const DEFAULT_ROOF_PITCH_RISE = 4 as const;
export const DEFAULT_ROOF_PITCH_RUN = 12 as const;
export const DEFAULT_FOUNDATION_DEPTH_M = 0.3048;

export type BuildingMaterialId = 'wall' | 'roof' | 'gable' | 'foundation';

export interface BuildingMaterial {
  readonly color: readonly [number, number, number, number];
}

export type BuildingMaterialCatalog = Readonly<Record<BuildingMaterialId, BuildingMaterial>>;

/** The catalog is intentionally small and fixed for the first archetype. */
export const DEFAULT_BUILDING_MATERIALS: BuildingMaterialCatalog = Object.freeze({
  wall: { color: [0.78, 0.8, 0.81, 1] },
  roof: { color: [0.16, 0.18, 0.21, 1] },
  gable: { color: [0.72, 0.74, 0.76, 1] },
  foundation: { color: [0.48, 0.49, 0.48, 1] },
});

export interface RectangularGableMeshInput {
  readonly lengthM: number;
  readonly widthM: number;
  readonly eaveHeightM: number;
  /** Clockwise from north. The mesh remains local; this rotates its footprint. */
  readonly bearingDeg?: number;
  /** Ground/floor datum in metres. It is retained for renderer placement. */
  readonly finishedFloorElevationM?: number;
  readonly roofPitchRise?: number;
  readonly roofPitchRun?: number;
  /**
   * Four or eight perimeter ground elevations in clockwise order starting at
   * the local south-west corner. Eight samples use SW, S, SE, E, NE, N, NW, W
   * (the persisted slope-foundation contract); four samples are accepted as a
   * compact corner-only adapter. A scalar is also accepted for a flat pad.
   */
  readonly perimeterGroundElevationsM?: readonly number[] | number;
  /** Alias used by a few terrain adapters. */
  readonly foundationGroundElevationsM?: readonly number[] | number;
  readonly foundationDepthM?: number;
}

export interface MeshMaterialGroup {
  readonly material: BuildingMaterialId;
  /** Index offset and count, both measured in index entries. */
  readonly start: number;
  readonly count: number;
}

export interface BuildingCollisionGeometry {
  /** Counter-clockwise local footprint in east/north metres. */
  readonly footprint: readonly [number, number][];
}

export interface RectangularGableMesh {
  readonly generatorVersion: typeof RECTANGULAR_GABLE_GENERATOR_VERSION;
  readonly vertices: Float32Array;
  readonly normals: Float32Array;
  readonly indices: Uint32Array;
  readonly materials: BuildingMaterialCatalog;
  readonly groups: readonly MeshMaterialGroup[];
  readonly collision: BuildingCollisionGeometry;
  readonly ridgeHeightM: number;
  readonly roofRiseM: number;
  readonly bounds: Readonly<{ minX: number; maxX: number; minY: number; maxY: number; minZ: number; maxZ: number }>;
}

interface Vec3 { x: number; y: number; z: number; }

function finitePositive(value: number, name: string): number {
  if (!Number.isFinite(value) || value <= 0) throw new Error(`${name} must be a positive finite number.`);
  return value;
}

function finiteOr(value: number | undefined, fallback: number): number {
  return value != null && Number.isFinite(value) ? value : fallback;
}

function mergeMaterials(overrides?: Partial<BuildingMaterialCatalog>): BuildingMaterialCatalog {
  if (!overrides) return DEFAULT_BUILDING_MATERIALS;
  return Object.freeze({
    ...DEFAULT_BUILDING_MATERIALS,
    ...overrides,
  });
}

function cross(a: Vec3, b: Vec3, c: Vec3): Vec3 {
  const ab = { x: b.x - a.x, y: b.y - a.y, z: b.z - a.z };
  const ac = { x: c.x - a.x, y: c.y - a.y, z: c.z - a.z };
  const n = {
    x: ab.y * ac.z - ab.z * ac.y,
    y: ab.z * ac.x - ab.x * ac.z,
    z: ab.x * ac.y - ab.y * ac.x,
  };
  const length = Math.hypot(n.x, n.y, n.z) || 1;
  return { x: n.x / length, y: n.y / length, z: n.z / length };
}

/**
 * Returns a clockwise-from-north oriented rectangle in local east/north
 * metres. This helper is also the collision footprint used by the renderer.
 */
export function orientedRectFootprint(
  lengthM: number,
  widthM: number,
  bearingDeg = 0,
): readonly [number, number][] {
  finitePositive(lengthM, 'lengthM');
  finitePositive(widthM, 'widthM');
  const theta = (bearingDeg * Math.PI) / 180;
  const along = { x: Math.sin(theta), y: Math.cos(theta) };
  const across = { x: Math.cos(theta), y: -Math.sin(theta) };
  const hl = lengthM / 2;
  const hw = widthM / 2;
  // Counter-clockwise as seen from above. This order is convenient for
  // collision tests and preserves an outward-up normal for the top face.
  return [
    [-along.x * hl - across.x * hw, -along.y * hl - across.y * hw],
    [along.x * hl - across.x * hw, along.y * hl - across.y * hw],
    [along.x * hl + across.x * hw, along.y * hl + across.y * hw],
    [-along.x * hl + across.x * hw, -along.y * hl + across.y * hw],
  ];
}

function asGroundSamples(input: RectangularGableMeshInput): readonly number[] | number {
  return input.perimeterGroundElevationsM ?? input.foundationGroundElevationsM ??
    (input.foundationDepthM != null ? -Math.abs(input.foundationDepthM) : -DEFAULT_FOUNDATION_DEPTH_M);
}

function groundAt(samples: readonly number[] | number, index: number, floorElevationM: number): number {
  if (typeof samples === 'number') return Number.isFinite(samples) ? samples : -DEFAULT_FOUNDATION_DEPTH_M;
  const value = samples[samples.length >= 8 ? index * 2 : index];
  return Number.isFinite(value) ? value - floorElevationM : -DEFAULT_FOUNDATION_DEPTH_M;
}

function pushFace(
  positions: number[],
  normals: number[],
  indices: number[],
  groups: Map<BuildingMaterialId, MeshMaterialGroup>,
  material: BuildingMaterialId,
  a: Vec3,
  b: Vec3,
  c: Vec3,
  d: Vec3,
): void {
  const normal = cross(a, b, c);
  const startVertex = positions.length / 3;
  for (const point of [a, b, c, a, c, d]) {
    positions.push(point.x, point.y, point.z);
    normals.push(normal.x, normal.y, normal.z);
  }
  for (let i = 0; i < 6; i += 1) indices.push(startVertex + i);
  const group = groups.get(material);
  if (group) {
    groups.set(material, { ...group, count: group.count + 6 });
  } else {
    groups.set(material, { material, start: indices.length - 6, count: 6 });
  }
}

function pushTriangle(
  positions: number[],
  normals: number[],
  indices: number[],
  groups: Map<BuildingMaterialId, MeshMaterialGroup>,
  material: BuildingMaterialId,
  a: Vec3,
  b: Vec3,
  c: Vec3,
): void {
  const normal = cross(a, b, c);
  const startVertex = positions.length / 3;
  for (const point of [a, b, c]) {
    positions.push(point.x, point.y, point.z);
    normals.push(normal.x, normal.y, normal.z);
  }
  indices.push(startVertex, startVertex + 1, startVertex + 2);
  const group = groups.get(material);
  if (group) groups.set(material, { ...group, count: group.count + 3 });
  else groups.set(material, { material, start: indices.length - 3, count: 3 });
}

function groupsInStableOrder(groups: Map<BuildingMaterialId, MeshMaterialGroup>): readonly MeshMaterialGroup[] {
  return (['wall', 'gable', 'roof', 'foundation'] as const)
    .map((material) => groups.get(material))
    .filter((group): group is MeshMaterialGroup => group != null);
}

/** Generate the v1 rectangular-gable pump-house mesh. */
export function generateRectangularGableMesh(
  input: RectangularGableMeshInput,
  materialOverrides?: Partial<BuildingMaterialCatalog>,
): RectangularGableMesh {
  const lengthM = finitePositive(input.lengthM, 'lengthM');
  const widthM = finitePositive(input.widthM, 'widthM');
  const eaveHeightM = finitePositive(input.eaveHeightM, 'eaveHeightM');
  const pitchRise = finitePositive(finiteOr(input.roofPitchRise, DEFAULT_ROOF_PITCH_RISE), 'roofPitchRise');
  const pitchRun = finitePositive(finiteOr(input.roofPitchRun, DEFAULT_ROOF_PITCH_RUN), 'roofPitchRun');
  const roofRiseM = (widthM / 2) * (pitchRise / pitchRun);
  const ridgeHeightM = eaveHeightM + roofRiseM;
  const bearingDeg = finiteOr(input.bearingDeg, 0);
  const footprint = orientedRectFootprint(lengthM, widthM, bearingDeg);
  const floorElevationM = finiteOr(input.finishedFloorElevationM, 0);

  // Internally use a simple long-axis frame (u = long axis, v = across axis),
  // then rotate every emitted vertex and normal into the authored east/north
  // bearing below. The collision footprint uses the same transform.
  const halfL = lengthM / 2;
  const halfW = widthM / 2;
  const z0 = 0;
  const ze = eaveHeightM;
  const zr = ridgeHeightM;
  const points = {
    sw: { x: -halfL, y: -halfW, z: z0 },
    se: { x: halfL, y: -halfW, z: z0 },
    ne: { x: halfL, y: halfW, z: z0 },
    nw: { x: -halfL, y: halfW, z: z0 },
    swE: { x: -halfL, y: -halfW, z: ze },
    seE: { x: halfL, y: -halfW, z: ze },
    neE: { x: halfL, y: halfW, z: ze },
    nwE: { x: -halfL, y: halfW, z: ze },
    swR: { x: -halfL, y: 0, z: zr },
    seR: { x: halfL, y: 0, z: zr },
  };
  const positions: number[] = [];
  const normals: number[] = [];
  const indices: number[] = [];
  const groups = new Map<BuildingMaterialId, MeshMaterialGroup>();

  // Exterior walls, counter-clockwise when viewed from outside.
  pushFace(positions, normals, indices, groups, 'wall', points.sw, points.swE, points.nwE, points.nw);
  pushFace(positions, normals, indices, groups, 'wall', points.sw, points.se, points.seE, points.swE);
  pushFace(positions, normals, indices, groups, 'wall', points.se, points.ne, points.neE, points.seE);
  pushFace(positions, normals, indices, groups, 'wall', points.ne, points.nw, points.nwE, points.neE);
  // Gable-end triangles sit above the end walls.
  pushTriangle(positions, normals, indices, groups, 'gable', points.swE, points.swR, points.nwE);
  pushTriangle(positions, normals, indices, groups, 'gable', points.seE, points.neE, points.seR);
  // Pitched roof planes.
  pushFace(positions, normals, indices, groups, 'roof', points.swE, points.seE, points.seR, points.swR);
  pushFace(positions, normals, indices, groups, 'roof', points.neE, points.nwE, points.swR, points.seR);

  // Foundation walls are kept as separate concrete faces. In slope mode the
  // four persisted corner samples set their bottom edge; in flat mode a scalar
  // depth supplies a deterministic shallow foundation.
  const ground = asGroundSamples(input);
  const groundCorners: Vec3[] = [
    { ...points.sw, z: groundAt(ground, 0, floorElevationM) },
    { ...points.se, z: groundAt(ground, 1, floorElevationM) },
    { ...points.ne, z: groundAt(ground, 2, floorElevationM) },
    { ...points.nw, z: groundAt(ground, 3, floorElevationM) },
  ];
  const topCorners = [points.sw, points.se, points.ne, points.nw];
  for (let i = 0; i < 4; i += 1) {
    const next = (i + 1) % 4;
    // Reverse the outside wall order so the foundation's outward normal is
    // consistent with the wall immediately above it.
    pushFace(positions, normals, indices, groups, 'foundation',
      topCorners[next], topCorners[i], groundCorners[i], groundCorners[next]);
  }

  const theta = (bearingDeg * Math.PI) / 180;
  // Rotate the generated local frame into the authored bearing. MapLibre's
  // Mercator y axis is handled by the renderer; these remain east/north metres.
  for (let index = 0; index < positions.length; index += 3) {
    const localX = positions[index];
    const localY = positions[index + 1];
    positions[index] = Math.sin(theta) * localX + Math.cos(theta) * localY;
    positions[index + 1] = Math.cos(theta) * localX - Math.sin(theta) * localY;
    const normalX = normals[index];
    const normalY = normals[index + 1];
    normals[index] = Math.sin(theta) * normalX + Math.cos(theta) * normalY;
    normals[index + 1] = Math.cos(theta) * normalX - Math.sin(theta) * normalY;
  }

  const bounds = positions.reduce((result, _, index) => {
    if (index % 3 === 0) result.minX = Math.min(result.minX, positions[index]);
    else if (index % 3 === 1) result.minY = Math.min(result.minY, positions[index]);
    else result.minZ = Math.min(result.minZ, positions[index]);
    if (index % 3 === 0) result.maxX = Math.max(result.maxX, positions[index]);
    else if (index % 3 === 1) result.maxY = Math.max(result.maxY, positions[index]);
    else result.maxZ = Math.max(result.maxZ, positions[index]);
    return result;
  }, { minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity });

  return {
    generatorVersion: RECTANGULAR_GABLE_GENERATOR_VERSION,
    vertices: Float32Array.from(positions),
    normals: Float32Array.from(normals),
    indices: Uint32Array.from(indices),
    materials: mergeMaterials(materialOverrides),
    groups: groupsInStableOrder(groups),
    collision: { footprint },
    ridgeHeightM,
    roofRiseM,
    bounds,
  };
}
