import type { BuildingRenderRecord } from './buildingLayers';

/** Stable, production-shaped fixture used by both panes of Graphics Lab. */
export function fixedPumpHouseFixture(center: readonly [number, number]): BuildingRenderRecord {
  return {
    id: 'graphics-lab:pump-house',
    name: 'Pump house fixture',
    center,
    bearingDeg: 28,
    dimensions: {
      lengthM: 18.288,
      widthM: 12.192,
      eaveHeightM: 4.8768,
    },
    foundation: {
      kind: 'flattened',
      finishedFloorElevationM: 0,
      perimeterElevationsM: [-0.3048, -0.3048, -0.3048, -0.3048],
    },
    finishedFloorElevationM: 0,
  };
}

