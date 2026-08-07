import { useMemo } from 'react';
import { analyzeLake } from '../lakeAnalysis';
import type { SnowmakingLakeSource } from '../types/snowmaking';
import type { TerrainRecord } from '../types/terrain';
import type { DamControllerOptions } from './useDamController';
import { useDamController } from './useDamController';
import type { PondControllerOptions } from './usePondController';
import { usePondController } from './usePondController';
import type { SnowmakingNodeControllerOptions } from './useSnowmakingNodeController';
import { useSnowmakingNodeController } from './useSnowmakingNodeController';

export interface SnowmakingControllerOptions {
  dam: DamControllerOptions;
  pond: PondControllerOptions;
  nodes: SnowmakingNodeControllerOptions;
}

/** Resolve persisted OSM feature IDs into live snowmaking sources. */
export function useSnowmakingLakeSources(record: TerrainRecord | null, ids: string[],
  depthOverrides: Record<string, number>, nameOverrides: Record<string, string>) {
  return useMemo<SnowmakingLakeSource[] | null>(() => {
    if (!record) return null;
    const selected = new Set(ids);
    return (record.vectorFeatures?.waterPolygons ?? [])
      .filter((feature) => selected.has(feature.id) && feature.rings[0]?.length >= 3)
      .map((feature) => {
        const lake = analyzeLake(feature, record, depthOverrides[feature.id], nameOverrides[feature.id]);
        return { id: feature.id, name: lake.name, boundary: feature.rings[0],
          surfaceElevationM: lake.surfaceElevationM, capacityM3: lake.volumeM3 };
      });
  }, [record, ids, depthOverrides, nameOverrides]);
}

/** One presentation-facing façade over independently owned dam, pond, and node workflows. */
export function useSnowmakingController(options: SnowmakingControllerOptions) {
  const dam = useDamController(options.dam);
  const pond = usePondController(options.pond);
  const nodes = useSnowmakingNodeController(options.nodes);
  return { dam, pond, nodes,
    contributions: [dam.contribution, pond.contribution, nodes.contribution] as const };
}
