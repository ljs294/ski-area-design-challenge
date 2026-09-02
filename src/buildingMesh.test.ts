import { describe, expect, it } from 'vitest';
import {
  DEFAULT_BUILDING_MATERIALS,
  generateRectangularGableMesh,
  orientedRectFootprint,
  RECTANGULAR_GABLE_GENERATOR_VERSION,
} from './buildingMesh';

describe('versioned rectangular-gable building mesh', () => {
  const pumpHouse = {
    lengthM: 18.288,
    widthM: 12.192,
    eaveHeightM: 4.8768,
  } as const;

  it('uses the exact canonical pump-house dimensions and ridge rise', () => {
    const mesh = generateRectangularGableMesh(pumpHouse);
    expect(mesh.generatorVersion).toBe(RECTANGULAR_GABLE_GENERATOR_VERSION);
    expect(mesh.roofRiseM).toBeCloseTo(2.032, 8);
    expect(mesh.ridgeHeightM).toBeCloseTo(6.9088, 8);
    expect(mesh.bounds.maxZ).toBeCloseTo(6.9088, 5);
    expect(mesh.collision.footprint).toHaveLength(4);
  });

  it('rotates the collision footprint clockwise from north', () => {
    const north = orientedRectFootprint(10, 4, 0);
    const east = orientedRectFootprint(10, 4, 90);
    expect(north.map((point) => point[0])).toEqual([-2, -2, 2, 2]);
    expect(north.map((point) => point[1])).toEqual([-5, 5, 5, -5]);
    expect(east.map((point) => point[0])).toEqual([-5, 5, 5, -5]);
    east.forEach((point, index) => expect(point[1]).toBeCloseTo([2, 2, -2, -2][index], 8));
  });

  it('emits outward unit normals with stable material groups', () => {
    const mesh = generateRectangularGableMesh({ ...pumpHouse, bearingDeg: 31 });
    expect(mesh.groups.map((group) => group.material)).toEqual([
      'wall', 'gable', 'roof', 'foundation',
    ]);
    expect(mesh.vertices).toEqual(generateRectangularGableMesh({ ...pumpHouse, bearingDeg: 31 }).vertices);
    for (let i = 0; i < mesh.normals.length; i += 3) {
      const length = Math.hypot(mesh.normals[i], mesh.normals[i + 1], mesh.normals[i + 2]);
      expect(length).toBeCloseTo(1, 5);
    }
    // The first face is the negative-long-axis wall at a zero bearing (north
    // is the mesh's negative local-long-axis after the canonical transform).
    const wall = generateRectangularGableMesh(pumpHouse);
    expect(wall.normals[0]).toBeCloseTo(0, 5);
    expect(wall.normals[1]).toBeCloseTo(-1, 5);
    expect(wall.normals[2]).toBeCloseTo(0, 5);
  });

  it('supports catalog color overrides without mutating the default catalog', () => {
    const mesh = generateRectangularGableMesh(pumpHouse, {
      roof: { color: [0.9, 0.1, 0.2, 1] },
    });
    expect(mesh.materials.roof.color).toEqual([0.9, 0.1, 0.2, 1]);
    expect(DEFAULT_BUILDING_MATERIALS.roof.color).toEqual([0.16, 0.18, 0.21, 1]);
  });

  it('uses persisted slope samples for foundation bottoms relative to floor', () => {
    const mesh = generateRectangularGableMesh({
      ...pumpHouse,
      finishedFloorElevationM: 100,
      perimeterGroundElevationsM: [99, 99.5, 98.5, 99.25],
    });
    expect(mesh.bounds.minZ).toBeCloseTo(-1.5, 5);
  });
});
