import { useEffect, useRef } from 'react';
import type { SavedDam, SavedPond, SavedSnowmakingNode } from '../types/snowmaking';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import { addSnowmakingLayers, setSelectedSnowmakingNode, setSnowmakingData,
  SNOWMAKING_BUILT_LAYER_IDS, SNOWMAKING_HIT_LAYERS } from './snowmakingLayers';

export interface SnowmakingNodeControllerOptions {
  dams: readonly SavedDam[];
  ponds: readonly SavedPond[];
  nodes: readonly SavedSnowmakingNode[];
  selectedId: string | null;
  reconcileSources(dams: readonly SavedDam[], ponds: readonly SavedPond[]): void;
  rename(id: string, name: string): void;
  select(id: string): void;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

export interface SnowmakingNodeController {
  readonly contribution: ManagedMapContribution;
  select(id: string): void;
  rename(id: string, name: string): void;
}

export function useSnowmakingNodeController(
  options: SnowmakingNodeControllerOptions,
): SnowmakingNodeController {
  const optionsRef = useRef(options);
  const nodesRef = useRef(options.nodes);
  optionsRef.current = options;
  nodesRef.current = options.nodes;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'snowmaking', zOrder: MAP_Z_ORDER.snowmaking,
    hits: [{ id: 'snowmaking', priority: MAP_HIT_RANK.snowmaking,
      layerIds: SNOWMAKING_HIT_LAYERS, select: (id) => optionsRef.current.select(id) }],
    install: ({ map }) => addSnowmakingLayers(map),
    synchronizeData: ({ map }) => {
      setSnowmakingData(map, [...nodesRef.current]);
      setSelectedSnowmakingNode(map, optionsRef.current.selectedId);
    },
    visibility: (): MapVisibilityDescriptor[] => optionsRef.current.structuresVisible()
      ? [{ id: 'snowmaking-network', label: 'Snowmaking network',
        layerIds: SNOWMAKING_BUILT_LAYER_IDS, visible: true, section: 'Structures' }]
      : [],
    cleanup: () => {},
  };

  useEffect(() => {
    optionsRef.current.reconcileSources(options.dams, options.ponds);
  }, [options.dams, options.ponds]);

  useEffect(() => { optionsRef.current.synchronizeMap(); }, [options.nodes, options.selectedId]);

  return {
    contribution: contributionRef.current,
    select: (id) => optionsRef.current.select(id),
    rename: (id, name) => optionsRef.current.rename(id, name),
  };
}
