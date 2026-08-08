import { useEffect, useMemo, useReducer } from 'react';
import { analyzeSnowmakingSystem } from '../snowmakingHydraulics';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingLakeSource } from '../types/snowmaking';
import type { SavedDam, SavedPond } from '../types';
import { createSnowmakingAnalysisState, pumpAnalysisSetting,
  snowmakingAnalysisReducer } from './snowmakingAnalysisModel';

export function useSnowmakingAnalysis(input: {
  nodes: SavedSnowmakingNode[];
  pipes: SavedSnowmakingPipe[];
  guns: SavedSnowgun[];
  dams: SavedDam[];
  ponds: SavedPond[];
  lakes: SnowmakingLakeSource[];
}) {
  const { nodes, pipes, guns, dams, ponds, lakes } = input;
  const [state, dispatch] = useReducer(snowmakingAnalysisReducer, undefined,
    createSnowmakingAnalysisState);
  const pipeIds = useMemo(() => pipes.map((pipe) => pipe.id), [pipes]);
  const gunIds = useMemo(() => guns.map((gun) => gun.id), [guns]);
  const pumpIds = useMemo(() => nodes.filter((node) => node.kind === 'pump')
    .map((node) => node.id), [nodes]);

  useEffect(() => dispatch({ type: 'reconcile', pipeIds, gunIds, pumpIds }),
    [pipeIds, gunIds, pumpIds]);

  const sourceCapacitiesM3 = useMemo(() => Object.fromEntries(nodes.flatMap((node) => {
    if (node.kind !== 'intake' || !node.source) return [];
    const source = node.source;
    const capacity = source.kind === 'dam'
      ? dams.find((dam) => dam.id === source.damId)?.capacityM3
      : source.kind === 'pond'
        ? ponds.find((pond) => pond.id === source.pondId)?.capacityM3
        : lakes.find((lake) => lake.id === source.lakeId)?.capacityM3;
    return [[node.id, capacity ?? null]];
  })), [nodes, dams, ponds, lakes]);

  const check = () => dispatch({ type: 'checked', result: analyzeSnowmakingSystem({
    nodes, pipes, guns, selectedPipeIds: state.selectedPipeIds,
    selectedGunIds: state.selectedGunIds, wetBulbF: Number(state.wetBulbF),
    pumpSettings: Object.fromEntries(pumpIds.map((id) => [id,
      pumpAnalysisSetting(state.pumpSettings[id])])), sourceCapacitiesM3,
  }) });
  const gunStatuses = state.result?.ok && !state.stale
    ? Object.fromEntries(state.result.guns.map((gun) => [gun.gunId,
      gun.status === 'ready' ? 'ready' as const : 'failed' as const])) : undefined;

  return { state, dispatch, check, gunStatuses };
}
