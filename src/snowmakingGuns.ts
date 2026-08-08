import { haversineMeters } from './geo';
import type {
  SavedSnowgun,
  SavedSnowmakingNode,
  SavedSnowmakingPipe,
  SnowgunVariantId,
} from './types/snowmaking';

export const SNOWGUN_HOSE_REACH_M = 15.24;

export interface SnowgunPerformanceStage {
  stage: 1 | 2 | 3 | 4 | 5;
  conditions: 'Marginal' | 'Fair' | 'Good' | 'Excellent' | 'Optimal';
  wetBulbF: number;
  waterFlowGpm: number;
  airFlowCfm: number;
}

export interface SnowgunVariant {
  id: SnowgunVariantId;
  modelId: 'HKD_ImpulseR5';
  label: string;
  shortLabel: string;
  mount: 'sled' | 'tower';
  towerLengthFt: 10 | 20 | 30;
  throwFt: 30 | 80 | 125;
  priceUsd: 7000 | 8000 | 9000;
}

export const HKD_IMPULSE_R5 = Object.freeze({
  id: 'HKD_ImpulseR5' as const,
  name: 'HKD Impulse R5',
  type: 'Low-E Tower Gun',
  maxWetBulbF: 28,
  waterFlowGpm: Object.freeze({ min: 16, max: 80 }),
  minimumWaterPressurePsi: 200,
  airFlowCfm: Object.freeze({ min: 5, max: 111 }),
  minimumAirPressurePsi: 85,
  stages: Object.freeze<SnowgunPerformanceStage[]>([
    { stage: 1, conditions: 'Marginal', wetBulbF: 28, waterFlowGpm: 18, airFlowCfm: 56 },
    { stage: 2, conditions: 'Fair', wetBulbF: 24, waterFlowGpm: 28, airFlowCfm: 56 },
    { stage: 3, conditions: 'Good', wetBulbF: 19, waterFlowGpm: 38, airFlowCfm: 56 },
    { stage: 4, conditions: 'Excellent', wetBulbF: 14, waterFlowGpm: 48, airFlowCfm: 16 },
    { stage: 5, conditions: 'Optimal', wetBulbF: 9, waterFlowGpm: 58, airFlowCfm: 16 },
  ]),
});

export const SNOWGUN_VARIANTS: readonly SnowgunVariant[] = Object.freeze([
  { id: 'HKD_ImpulseR5_10s', modelId: 'HKD_ImpulseR5', label: 'HKD Impulse R5 10 ft Sled',
    shortLabel: 'R5 10S', mount: 'sled', towerLengthFt: 10, throwFt: 30, priceUsd: 7000 },
  { id: 'HKD_ImpulseR5_10t', modelId: 'HKD_ImpulseR5', label: 'HKD Impulse R5 10 ft Tower',
    shortLabel: 'R5 10 ft Tower', mount: 'tower', towerLengthFt: 10, throwFt: 30, priceUsd: 7000 },
  { id: 'HKD_ImpulseR5_20t', modelId: 'HKD_ImpulseR5', label: 'HKD Impulse R5 20 ft Tower',
    shortLabel: 'R5 20 ft Tower', mount: 'tower', towerLengthFt: 20, throwFt: 80, priceUsd: 8000 },
  { id: 'HKD_ImpulseR5_30t', modelId: 'HKD_ImpulseR5', label: 'HKD Impulse R5 30 ft Tower',
    shortLabel: 'R5 30 ft Tower', mount: 'tower', towerLengthFt: 30, throwFt: 125, priceUsd: 9000 },
]);

const VARIANT_BY_ID = new Map(SNOWGUN_VARIANTS.map((variant) => [variant.id, variant]));

export function isSnowgunVariantId(value: unknown): value is SnowgunVariantId {
  return typeof value === 'string' && VARIANT_BY_ID.has(value as SnowgunVariantId);
}

export function snowgunVariant(id: SnowgunVariantId): SnowgunVariant {
  return VARIANT_BY_ID.get(id)!;
}

export function snowgunCatalogValue(guns: readonly Pick<SavedSnowgun, 'variantId'>[]): number {
  return guns.reduce((total, gun) => total + snowgunVariant(gun.variantId).priceUsd, 0);
}

export function snowgunHydrantDistanceM(
  gun: Pick<SavedSnowgun, 'point'>,
  hydrant: Pick<SavedSnowmakingNode, 'point'>,
): number {
  return haversineMeters(gun.point, hydrant.point);
}

function isPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every((coordinate) =>
    typeof coordinate === 'number' && Number.isFinite(coordinate)) &&
    value[0] >= -180 && value[0] <= 180 && value[1] >= -90 && value[1] <= 90;
}

/** Preserve valid hookups, then connect unserved guns in saved placement order. */
export function reconcileSnowgunConnections(
  guns: readonly SavedSnowgun[],
  nodes: readonly SavedSnowmakingNode[],
): SavedSnowgun[] {
  const hydrants = nodes.filter((node) => node.kind === 'hydrant');
  const hydrantById = new Map(hydrants.map((node) => [node.id, node]));
  const used = new Set<string>();
  let changed = false;
  const next = guns.map((gun) => {
    if (!gun.hydrantId) return gun;
    const hydrant = hydrantById.get(gun.hydrantId);
    if (!hydrant || used.has(hydrant.id) ||
      snowgunHydrantDistanceM(gun, hydrant) > SNOWGUN_HOSE_REACH_M + 1e-6) {
      changed = true;
      return { ...gun, hydrantId: null };
    }
    used.add(hydrant.id);
    return gun;
  });

  for (let index = 0; index < next.length; index += 1) {
    const gun = next[index];
    if (gun.hydrantId) continue;
    const available = hydrants
      .filter((hydrant) => !used.has(hydrant.id))
      .map((hydrant) => ({ hydrant, distanceM: snowgunHydrantDistanceM(gun, hydrant) }))
      .filter(({ distanceM }) => distanceM <= SNOWGUN_HOSE_REACH_M + 1e-6)
      .sort((left, right) => left.distanceM - right.distanceM ||
        left.hydrant.id.localeCompare(right.hydrant.id));
    const closest = available[0]?.hydrant;
    if (!closest) continue;
    next[index] = { ...gun, hydrantId: closest.id };
    used.add(closest.id);
    changed = true;
  }
  return changed ? next : guns as SavedSnowgun[];
}

export function sanitizeSnowguns(
  raw: unknown[],
  nodes: readonly SavedSnowmakingNode[],
  pipes: readonly SavedSnowmakingPipe[],
): SavedSnowgun[] {
  const usedIds = new Set<string>([
    ...nodes.map((node) => node.id),
    ...pipes.map((pipe) => pipe.id),
  ]);
  const hydrantIds = new Set(nodes.filter((node) => node.kind === 'hydrant').map((node) => node.id));
  const guns: SavedSnowgun[] = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const value = item as Record<string, unknown>;
    if (typeof value.id !== 'string' || !value.id || usedIds.has(value.id)) continue;
    if (!isSnowgunVariantId(value.variantId) || !isPoint(value.point) ||
      typeof value.createdAt !== 'string' || !Number.isFinite(Date.parse(value.createdAt))) continue;
    usedIds.add(value.id);
    guns.push({
      id: value.id,
      variantId: value.variantId,
      point: value.point,
      elevM: typeof value.elevM === 'number' && Number.isFinite(value.elevM) ? value.elevM : null,
      hydrantId: typeof value.hydrantId === 'string' && hydrantIds.has(value.hydrantId)
        ? value.hydrantId : null,
      createdAt: value.createdAt,
    });
  }
  return reconcileSnowgunConnections(guns, nodes);
}
