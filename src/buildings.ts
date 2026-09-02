import { getBuildingArchetype, isBuildingTypeId } from './buildingArchetypes';
import { normalizeBearingDeg } from './buildingUnits';
import type {
  AssetEconomics,
  MaintenanceCadence,
} from './types/economics';
import type {
  BuildingFoundation,
  BuildingPerimeterElevationsM,
  FlattenedBuildingFoundation,
  SavedBuilding,
  SlopeBuildingFoundation,
} from './types/buildings';
import type { SavedSnowmakingNode } from './types/snowmaking';

export type { BuildingArchetype, BuildingMaterials } from './buildingArchetypes';
export type { BuildingPoint, BuildingRectangle } from './buildingGeometry';
export {
  formatAssetCostUsd,
  formatAssetEconomics,
  TBD_ASSET_ECONOMICS,
} from './types/economics';
export {
  BUILDING_ARCHETYPES,
  BUILDING_TYPE_REGISTRY,
  defaultBuildingDraft,
  getBuildingType,
  getBuildingArchetype,
  isBuildingTypeId,
  PUMP_HOUSE_ARCHETYPE,
  SNOWMAKING_PUMP_HOUSE_ARCHETYPE,
} from './buildingArchetypes';
export {
  buildingFootprint,
  buildingFootprintAreaM2,
  buildingFootprintCorners,
  buildingFootprintsCollide,
  buildingFootprintsOverlap,
  footprintInsideBounds,
  hasBuildingCollision,
  isBuildingFootprintInsideBounds,
  isFootprintInsideTerrain,
  offsetLngLat,
  orientedRectangleOverlap,
  orientedBuildingFootprint,
  orientedRectanglesOverlap,
  rectangleOverlaps,
  rectangleCornersMeters,
  rotateBuildingOffset,
} from './buildingGeometry';
export {
  FEET_PER_METER,
  feetToM,
  gableRidgeHeightM,
  gableRoofRiseM,
  feetToMeters,
  formatBuildingHeight,
  formatBuildingLength,
  inchesToMeters,
  inchesToM,
  METERS_PER_FOOT,
  METERS_PER_INCH,
  mToFeet,
  mToInches,
  metersToFeet,
  metersToInches,
  normalizeBearingDeg,
  PUMP_HOUSE_DEFAULTS,
  ridgeHeightM,
  roofRiseM,
} from './buildingUnits';

const VALID_CADENCES = new Set<MaintenanceCadence>(['unspecified', 'daily', 'monthly']);
const EPOCH = '1970-01-01T00:00:00.000Z';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 &&
    typeof value[0] === 'number' && Number.isFinite(value[0]) &&
    typeof value[1] === 'number' && Number.isFinite(value[1]) &&
    value[0] >= -180 && value[0] <= 180 && value[1] >= -90 && value[1] <= 90;
}

function finitePositive(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0;
}

function sanitizeEconomics(raw: unknown): AssetEconomics {
  const value = isRecord(raw) ? raw : {};
  const amount = (candidate: unknown): number | null =>
    typeof candidate === 'number' && Number.isFinite(candidate) && candidate >= 0 ? candidate : null;
  const cadence = VALID_CADENCES.has(value.maintenanceCadence as MaintenanceCadence)
    ? value.maintenanceCadence as MaintenanceCadence : 'unspecified';
  return {
    capitalCostUsd: amount(value.capitalCostUsd),
    maintenanceCostUsd: amount(value.maintenanceCostUsd),
    maintenanceCadence: cadence,
  };
}

function sanitizeEarthwork(raw: unknown): { cutM3: number; fillM3: number; balanceM3: number } | null {
  if (!isRecord(raw)) return null;
  if (typeof raw.cutM3 !== 'number' || !Number.isFinite(raw.cutM3) || raw.cutM3 < 0 ||
      typeof raw.fillM3 !== 'number' || !Number.isFinite(raw.fillM3) || raw.fillM3 < 0 ||
      typeof raw.balanceM3 !== 'number' || !Number.isFinite(raw.balanceM3)) return null;
  return { cutM3: raw.cutM3 as number, fillM3: raw.fillM3 as number,
    balanceM3: raw.balanceM3 as number };
}

function sanitizeFoundation(raw: unknown): BuildingFoundation | null {
  if (!isRecord(raw) || typeof raw.finishedFloorElevationM !== 'number' ||
      !Number.isFinite(raw.finishedFloorElevationM)) return null;
  if (raw.kind === 'flattened' || raw.mode === 'flattened') {
    const earthwork = sanitizeEarthwork(raw.earthwork);
    if (!earthwork) return null;
    const foundation: FlattenedBuildingFoundation = {
      kind: 'flattened',
      finishedFloorElevationM: raw.finishedFloorElevationM,
      terrainGraded: true,
      earthwork,
    };
    return foundation;
  }
  if (raw.kind !== 'slope' && raw.mode !== 'slope') return null;
  const samples = raw.perimeterGroundElevationsM ?? raw.perimeterElevationsM ?? raw.groundElevationsM;
  if (!Array.isArray(samples) || samples.length !== 8 ||
      !samples.every((value) => typeof value === 'number' && Number.isFinite(value))) return null;
  const perimeterGroundElevationsM = [...samples] as BuildingPerimeterElevationsM;
  const foundation: SlopeBuildingFoundation = {
    kind: 'slope',
    finishedFloorElevationM: raw.finishedFloorElevationM,
    terrainGraded: false,
    perimeterGroundElevationsM,
  };
  return foundation;
}

function isPumpRating(value: unknown): value is { horsepowerHp: number; efficiency: number } {
  if (!isRecord(value)) return false;
  return typeof value.horsepowerHp === 'number' && Number.isFinite(value.horsepowerHp) &&
    value.horsepowerHp > 0 && typeof value.efficiency === 'number' &&
    Number.isFinite(value.efficiency) && value.efficiency > 0 && value.efficiency <= 1;
}

function samePoint(a: readonly [number, number], b: readonly [number, number]): boolean {
  return a[0] === b[0] && a[1] === b[1];
}

/** Validate a single saved building without checking its reciprocal pump. */
export function sanitizeBuilding(raw: unknown): SavedBuilding | null {
  if (!isRecord(raw) || typeof raw.id !== 'string' || !raw.id.trim() ||
      typeof raw.name !== 'string' || !raw.name.trim() || !isBuildingTypeId(raw.buildingTypeId) ||
      raw.generatorVersion !== 1 || !isPoint(raw.center) ||
      typeof raw.bearingDeg !== 'number' || !Number.isFinite(raw.bearingDeg) ||
      !isRecord(raw.dimensions) || !finitePositive(raw.dimensions.lengthM) ||
      !finitePositive(raw.dimensions.widthM) || !finitePositive(raw.dimensions.eaveHeightM) ||
      !isRecord(raw.roof) || raw.roof.kind !== 'gable' || raw.roof.pitchRise !== 4 ||
      raw.roof.pitchRun !== 12 || !isRecord(raw.connection) ||
      raw.connection.kind !== 'snowmaking-pump' || typeof raw.connection.nodeId !== 'string' ||
      !raw.connection.nodeId.trim()) return null;
  const foundation = sanitizeFoundation(raw.foundation);
  if (!foundation) return null;
  // A catalog entry owns the fixed appearance and roof contract. Saved values
  // remain resolved, but unknown archetype versions do not hydrate.
  const archetype = getBuildingArchetype(raw.buildingTypeId);
  if (archetype.generatorVersion !== raw.generatorVersion) return null;
  return {
    id: raw.id.trim(),
    name: raw.name.trim(),
    buildingTypeId: raw.buildingTypeId,
    generatorVersion: 1,
    center: [...raw.center] as [number, number],
    bearingDeg: normalizeBearingDeg(raw.bearingDeg),
    dimensions: {
      lengthM: raw.dimensions.lengthM as number,
      widthM: raw.dimensions.widthM as number,
      eaveHeightM: raw.dimensions.eaveHeightM as number,
    },
    roof: { kind: 'gable', pitchRise: 4, pitchRun: 12 },
    foundation,
    connection: { kind: 'snowmaking-pump', nodeId: raw.connection.nodeId.trim() },
    economics: sanitizeEconomics(raw.economics),
    createdAt: typeof raw.createdAt === 'string' && raw.createdAt.length > 0 ? raw.createdAt : EPOCH,
  };
}

/**
 * Hydrate the building collection. A building is accepted only with its
 * reciprocal center pump; this prevents a dangling asset from being rendered
 * or saved after a partial/corrupt write.
 */
export function sanitizeBuildings(
  raw: readonly unknown[],
  nodes: readonly SavedSnowmakingNode[] = [],
): SavedBuilding[] {
  const byNodeId = new Map(nodes.map((node) => [node.id, node]));
  const usedBuildingIds = new Set<string>();
  const usedNodeIds = new Set<string>();
  const result: SavedBuilding[] = [];
  for (const item of raw) {
    const building = sanitizeBuilding(item);
    if (!building || usedBuildingIds.has(building.id) || usedNodeIds.has(building.connection.nodeId)) continue;
  const node = byNodeId.get(building.connection.nodeId);
  if (!node || node.kind !== 'pump' || node.ownerBuildingId !== building.id ||
        !samePoint(node.point, building.center) ||
        (node.pumpRating != null && (node.pumpRating.horsepowerHp !== 1000 ||
          node.pumpRating.efficiency !== 0.85))) continue;
    usedBuildingIds.add(building.id);
    usedNodeIds.add(building.connection.nodeId);
    result.push(building);
  }
  return result;
}

export const sanitizeSavedBuildings = sanitizeBuildings;

export interface SanitizedBuildingState {
  buildings: SavedBuilding[];
  nodes: SavedSnowmakingNode[];
}

/**
 * Hydrate buildings and their network nodes together. Invalid ownership is
 * removed from the node, preserving an otherwise useful orphan pump as a
 * free-standing manual pump.
 */
export function sanitizeBuildingState(
  rawBuildings: readonly unknown[],
  rawNodes: readonly SavedSnowmakingNode[],
): SanitizedBuildingState {
  const buildings = sanitizeBuildings(rawBuildings, rawNodes);
  const acceptedByBuilding = new Map(buildings.map((building) => [building.id, building]));
  const nodes = rawNodes.map((node) => {
    if (!node.ownerBuildingId && !node.pumpRating) return node;
    const building = node.ownerBuildingId ? acceptedByBuilding.get(node.ownerBuildingId) : undefined;
    const reciprocal = building && building.connection.nodeId === node.id &&
      samePoint(building.center, node.point) && node.kind === 'pump';
    if (reciprocal) {
      return { ...node, pumpRating: { horsepowerHp: 1000, efficiency: 0.85 } };
    }
    const { ownerBuildingId: _ownerBuildingId, pumpRating: _pumpRating, ...orphan } = node;
    return orphan;
  });
  return { buildings, nodes };
}

/** Alias used by save hydration callers that name this operation a reconcile. */
export const reconcileBuildingOwnership = sanitizeBuildingState;

export interface NewBuildingInput {
  id: string;
  name: string;
  center: [number, number];
  bearingDeg?: number;
  dimensions?: { lengthM: number; widthM: number; eaveHeightM: number };
  foundation: BuildingFoundation;
  nodeId: string;
  createdAt: string;
}

/** Build one canonical saved record from resolved review/terrain values. */
export function createSavedBuilding(input: NewBuildingInput): SavedBuilding {
  const archetype = getBuildingArchetype('snowmaking-pump-house');
  return {
    id: input.id,
    name: input.name.trim() || archetype.displayName,
    buildingTypeId: archetype.id,
    generatorVersion: 1,
    center: [...input.center] as [number, number],
    bearingDeg: normalizeBearingDeg(input.bearingDeg ?? 0),
    dimensions: { ...(input.dimensions ?? archetype.defaultDimensionsM) },
    roof: { ...archetype.roof },
    foundation: structuredClone(input.foundation),
    connection: { kind: 'snowmaking-pump', nodeId: input.nodeId },
    economics: { ...archetype.economics },
    createdAt: input.createdAt,
  };
}

export function buildingPumpRating(building: Pick<SavedBuilding, 'buildingTypeId'>): {
  horsepowerHp: 1000;
  efficiency: 0.85;
} {
  return getBuildingArchetype(building.buildingTypeId).pump;
}

export function isBuildingOwnedPump(
  building: SavedBuilding,
  node: SavedSnowmakingNode | undefined,
): boolean {
  return !!node && node.kind === 'pump' && node.ownerBuildingId === building.id &&
    node.id === building.connection.nodeId && samePoint(node.point, building.center) &&
    (!node.pumpRating || (isPumpRating(node.pumpRating) &&
      node.pumpRating.horsepowerHp === 1000 && node.pumpRating.efficiency === 0.85));
}
