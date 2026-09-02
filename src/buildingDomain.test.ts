import { describe, expect, it } from 'vitest';
import {
  buildingFootprintsOverlap,
  buildingFootprintAreaM2,
  createSavedBuilding,
  defaultBuildingDraft,
  feetToMeters,
  gableRidgeHeightM,
  getBuildingArchetype,
  hasBuildingCollision,
  isBuildingFootprintInsideBounds,
  offsetLngLat,
  sanitizeBuilding,
  sanitizeBuildingState,
} from './buildings';
import { PUMP_HOUSE_DEFAULTS } from './buildingUnits';
import type { SavedBuilding } from './types/buildings';
import type { SavedSnowmakingNode } from './types/snowmaking';

function flatBuilding(id: string, center: [number, number] = [0, 0]): SavedBuilding {
  return createSavedBuilding({
    id,
    name: 'Pump House',
    center,
    foundation: {
      kind: 'flattened', finishedFloorElevationM: 100,
      terrainGraded: true, earthwork: { cutM3: 2, fillM3: 3, balanceM3: -1 },
    },
    nodeId: `${id}-pump`,
    createdAt: '2026-01-01T00:00:00.000Z',
  });
}

function pumpFor(building: SavedBuilding): SavedSnowmakingNode {
  return {
    id: building.connection.nodeId,
    name: `${building.name} Pump`,
    kind: 'pump',
    labelNumber: 1,
    point: building.center,
    elevM: building.foundation.finishedFloorElevationM,
    ownerBuildingId: building.id,
    pumpRating: { horsepowerHp: 1000, efficiency: 0.85 },
    createdAt: building.createdAt,
  };
}

describe('pump-house domain', () => {
  it('keeps the exact canonical pump-house dimensions and ridge calculation', () => {
    expect(feetToMeters(60)).toBe(18.288);
    expect(feetToMeters(40)).toBe(12.192);
    expect(feetToMeters(16)).toBe(4.8768);
    expect(gableRidgeHeightM(PUMP_HOUSE_DEFAULTS.widthM, PUMP_HOUSE_DEFAULTS.eaveHeightM, 4, 12))
      .toBe(6.9088);
    expect(getBuildingArchetype('snowmaking-pump-house').defaultDimensionsM)
      .toEqual({ lengthM: 18.288, widthM: 12.192, eaveHeightM: 4.8768 });
    expect(defaultBuildingDraft().foundationMode).toBe('flattened');
  });

  it('uses a positive-area collision test while allowing edge touching', () => {
    const a = flatBuilding('a');
    const touchingCenter = offsetLngLat([0, 0], a.dimensions.widthM, 0);
    const touching = flatBuilding('touching', touchingCenter);
    const overlappingCenter = offsetLngLat([0, 0], a.dimensions.widthM - 0.01, 0);
    const overlapping = flatBuilding('overlapping', overlappingCenter);
    expect(buildingFootprintsOverlap(a, touching)).toBe(false);
    expect(buildingFootprintsOverlap(a, overlapping)).toBe(true);
    expect(hasBuildingCollision(overlapping, [a])).toBe(true);
    expect(buildingFootprintAreaM2(a)).toBeCloseTo(18.288 * 12.192, 10);
  });

  it('checks every rotated corner against geographic site bounds', () => {
    const building = flatBuilding('bounds', [-120, 45]);
    expect(isBuildingFootprintInsideBounds(building, {
      west: -121, east: -119, south: 44, north: 46,
    })).toBe(true);
    expect(isBuildingFootprintInsideBounds(building, {
      west: -120.00001, east: -119.99999, south: 44.99999, north: 45.00001,
    })).toBe(false);
  });

  it('requires a reciprocal center pump and strips invalid ownership from orphans', () => {
    const building = flatBuilding('valid');
    const validNode = pumpFor(building);
    const invalidBuilding = { ...building, id: 'dangling', connection: {
      kind: 'snowmaking-pump' as const, nodeId: 'missing',
    } };
    const orphan: SavedSnowmakingNode = {
      ...validNode, id: 'orphan', ownerBuildingId: 'unknown',
    };
    const state = sanitizeBuildingState([building, invalidBuilding], [validNode, orphan]);
    expect(state.buildings).toEqual([building]);
    expect(state.nodes.find((node) => node.id === 'orphan')).toEqual(expect.not.objectContaining({
      ownerBuildingId: expect.anything(), pumpRating: expect.anything(),
    }));
    expect(sanitizeBuilding({ ...building, connection: {
      kind: 'snowmaking-pump', nodeId: 'not-the-node',
    } })).not.toBeNull();
  });

  it('normalizes authored heading and keeps TBD economics', () => {
    const building = flatBuilding('heading');
    const sanitized = sanitizeBuilding({ ...building, bearingDeg: -90 });
    expect(sanitized?.bearingDeg).toBe(270);
    expect(sanitized?.economics).toEqual({
      capitalCostUsd: null, maintenanceCostUsd: null, maintenanceCadence: 'unspecified',
    });
  });
});
