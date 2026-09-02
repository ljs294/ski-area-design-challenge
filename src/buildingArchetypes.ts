import type {
  BuildingDraftParameters,
  BuildingTypeId,
  GableRoofParameters,
} from './types/buildings';
import type { AssetEconomics } from './types/economics';

export interface BuildingMaterials {
  wall: 'light-gray';
  roof: 'charcoal';
  foundation: 'concrete';
}

export interface BuildingArchetype {
  id: BuildingTypeId;
  displayName: string;
  generatorVersion: 1;
  defaultDimensionsM: {
    lengthM: number;
    widthM: number;
    eaveHeightM: number;
  };
  roof: GableRoofParameters;
  defaultFoundationMode: 'flattened';
  materials: BuildingMaterials;
  economics: AssetEconomics;
  pump: {
    horsepowerHp: 1000;
    efficiency: 0.85;
  };
}

const TBD_ECONOMICS: AssetEconomics = Object.freeze({
  capitalCostUsd: null,
  maintenanceCostUsd: null,
  maintenanceCadence: 'unspecified',
});

/** Versioned catalog entry for the initial player-building archetype. */
export const SNOWMAKING_PUMP_HOUSE_ARCHETYPE: BuildingArchetype = Object.freeze({
  id: 'snowmaking-pump-house',
  displayName: 'Snowmaking pump house',
  generatorVersion: 1,
  defaultDimensionsM: Object.freeze({
    lengthM: 18.288,
    widthM: 12.192,
    eaveHeightM: 4.8768,
  }),
  roof: Object.freeze({ kind: 'gable', pitchRise: 4, pitchRun: 12 }),
  defaultFoundationMode: 'flattened',
  materials: Object.freeze({ wall: 'light-gray', roof: 'charcoal', foundation: 'concrete' }),
  economics: TBD_ECONOMICS,
  pump: Object.freeze({ horsepowerHp: 1000, efficiency: 0.85 }),
});

export const BUILDING_ARCHETYPES: Readonly<Record<BuildingTypeId, BuildingArchetype>> = Object.freeze({
  'snowmaking-pump-house': SNOWMAKING_PUMP_HOUSE_ARCHETYPE,
});

export const BUILDING_TYPE_REGISTRY = BUILDING_ARCHETYPES;
export const PUMP_HOUSE_ARCHETYPE = SNOWMAKING_PUMP_HOUSE_ARCHETYPE;

export function getBuildingArchetype(id: BuildingTypeId): BuildingArchetype {
  return BUILDING_ARCHETYPES[id];
}

export const getBuildingType = getBuildingArchetype;

export function isBuildingTypeId(value: unknown): value is BuildingTypeId {
  return value === 'snowmaking-pump-house';
}

export function defaultBuildingDraft(
  center: [number, number] = [0, 0],
  name = 'Pump House 1',
): BuildingDraftParameters {
  const archetype = SNOWMAKING_PUMP_HOUSE_ARCHETYPE;
  return {
    buildingTypeId: archetype.id,
    name,
    center: [...center] as [number, number],
    bearingDeg: 0,
    dimensions: { ...archetype.defaultDimensionsM },
    foundationMode: archetype.defaultFoundationMode,
  };
}
