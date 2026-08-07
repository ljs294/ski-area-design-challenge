import { useMemo, useRef, useState } from 'react';
import { analyzeLake } from '../lakeAnalysis';
import type { SnowmakingLakeSource } from '../types/snowmaking';
import type { SnowmakingNetworkState } from '../snowmakingNetwork';
import type { TerrainRecord } from '../types/terrain';
import type { DamControllerOptions } from './useDamController';
import { useDamController } from './useDamController';
import type { PondControllerOptions } from './usePondController';
import { usePondController } from './usePondController';
import type { SnowmakingNetworkControllerOptions } from './useSnowmakingNetworkController';
import { useSnowmakingNetworkController } from './useSnowmakingNetworkController';
import { SnowmakingNetworkDocument, snowmakingNetworkProjection } from './snowmakingNetworkDocument';

export interface SnowmakingControllerOptions {
  dam: DamControllerOptions;
  pond: PondControllerOptions;
  network: SnowmakingNetworkControllerOptions;
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
  const network = useSnowmakingNetworkController(options.network);
  return { dam, pond, network,
    contributions: [dam.contribution, pond.contribution, network.contribution] as const };
}

/** React projection plus the synchronous document used by save/capture paths. */
export function useCommittedSnowmakingNetwork(initial: SnowmakingNetworkState) {
  const [nodes, setNodes] = useState(initial.nodes);
  const [pipes, setPipes] = useState(initial.pipes);
  const [nextNumbers, setNextNumbers] = useState(initial.nextNumbers);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
  const [selectedPipeId, setSelectedPipeId] = useState<string | null>(null);
  const committedRef = useRef<SnowmakingNetworkState>({ nodes, pipes, nextNumbers });
  const documentRef = useRef<SnowmakingNetworkDocument | null>(null);
  if (!documentRef.current) documentRef.current = new SnowmakingNetworkDocument(
    committedRef.current,
    ({ snapshot, changed }) => {
      const projection = snowmakingNetworkProjection(snapshot);
      committedRef.current = projection;
      if (changed.nodes) setNodes(projection.nodes);
      if (changed.pipes) setPipes(projection.pipes);
      if (changed.nextNumbers) setNextNumbers(projection.nextNumbers);
    },
  );
  return { nodes, pipes, nextNumbers, selectedNodeId, selectedPipeId,
    setSelectedNodeId, setSelectedPipeId, committedRef, document: documentRef.current };
}
