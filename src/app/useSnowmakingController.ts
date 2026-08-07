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

/** One presentation-facing façade over independently owned dam, pond, and node workflows. */
export function useSnowmakingController(options: SnowmakingControllerOptions) {
  const dam = useDamController(options.dam);
  const pond = usePondController(options.pond);
  const nodes = useSnowmakingNodeController(options.nodes);
  return { dam, pond, nodes,
    contributions: [dam.contribution, pond.contribution, nodes.contribution] as const };
}
