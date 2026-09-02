import { useCallback, useEffect, useMemo, useReducer, useRef } from 'react';
import { deriveSnowmakingAnalysisGroups, snowmakingSourceKey,
  type SnowmakingSourceResource } from '../snowmakingHydraulics';
import { deriveSnowmakingRoutingForest, prepareSnowmakingRoutingTopology } from '../snowmakingRouting';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingLakeSource } from '../types/snowmaking';
import type { SavedDam, SavedPond } from '../types';
import { SnowmakingAnalysisAdapter } from './snowmakingAnalysisClient';
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
  const adapterRef = useRef<SnowmakingAnalysisAdapter | null>(null);
  if (!adapterRef.current) adapterRef.current = new SnowmakingAnalysisAdapter();

  const gunIds = useMemo(() => guns.map((gun) => gun.id), [guns]);
  const intakeNodeIds = useMemo(() => nodes.filter((node) => node.kind === 'intake')
    .map((node) => node.id), [nodes]);
  const pumpIds = useMemo(() => nodes.filter((node) => node.kind === 'pump')
    .map((node) => node.id), [nodes]);
  const groups = useMemo(() => deriveSnowmakingAnalysisGroups({ nodes, pipes, guns }),
    [nodes, pipes, guns]);

  useEffect(() => dispatch({ type: 'reconcile', gunIds, intakeNodeIds, pumpIds }),
    [gunIds, intakeNodeIds, pumpIds]);

  useEffect(() => {
    dispatch({ type: 'clear-result' });
  }, [nodes, pipes, guns, dams, ponds, lakes]);

  const selectedGunSet = useMemo(() => new Set(state.selectedGunIds), [state.selectedGunIds]);
  const relevantGroups = useMemo(() => groups.filter((group) =>
    group.gunIds.some((id) => selectedGunSet.has(id))), [groups, selectedGunSet]);
  const relevantIntakeIds = useMemo(() => relevantGroups.flatMap((group) => group.intakeNodeIds),
    [relevantGroups]);
  const automaticIntakeIds = useMemo(() => relevantGroups.flatMap((group) =>
    group.intakeNodeIds.length === 1 ? group.intakeNodeIds : []), [relevantGroups]);
  useEffect(() => dispatch({ type: 'auto-intakes', ids: automaticIntakeIds,
    relevantIds: relevantIntakeIds }), [automaticIntakeIds, relevantIntakeIds]);

  const sourceResourcesByIntakeId = useMemo(() => Object.fromEntries(nodes.flatMap((node) => {
    if (node.kind !== 'intake') return [];
    const source = node.source;
    const entity = source?.kind === 'dam' ? dams.find((dam) => dam.id === source.damId)
      : source?.kind === 'pond' ? ponds.find((pond) => pond.id === source.pondId)
        : source?.kind === 'lake' ? lakes.find((lake) => lake.id === source.lakeId) : undefined;
    const resource: SnowmakingSourceResource = {
      sourceKey: snowmakingSourceKey(source, node.id),
      name: entity?.name ?? node.name,
      capacityM3: entity?.capacityM3 ?? null,
    };
    return [[node.id, resource]];
  })), [nodes, dams, ponds, lakes]);
  const analysisPumpSettings = useMemo(() => Object.fromEntries(
    pumpIds.map((id) => [id,
      pumpAnalysisSetting(state.pumpSettings[id], nodes.find((node) => node.id === id))]),
  ), [pumpIds, state.pumpSettings, nodes]);
  const routingTopology = useMemo(() => prepareSnowmakingRoutingTopology({ nodes, pipes }),
    [nodes, pipes]);
  const routing = useMemo(() => deriveSnowmakingRoutingForest({ nodes, pipes, guns,
    selectedGunIds: state.selectedGunIds,
    selectedIntakeNodeIds: state.selectedIntakeNodeIds,
    pumpSettings: analysisPumpSettings, topology: routingTopology,
  }), [nodes, pipes, guns, state.selectedGunIds, state.selectedIntakeNodeIds,
    analysisPumpSettings, routingTopology]);

  // Any edit to the current snapshot invalidates work that was started from
  // the previous snapshot. It does not start a replacement request; analysis
  // is deliberately an explicit user action now.
  useEffect(() => {
    const adapter = adapterRef.current!;
    return () => { adapter.cancel(); };
  }, [nodes, pipes, guns, sourceResourcesByIntakeId, state.selectedGunIds,
    state.selectedIntakeNodeIds, state.wetBulbF, analysisPumpSettings]);

  const analyze = useCallback(() => {
    if (state.selectedGunIds.length === 0 || state.calculating) return;
    const adapter = adapterRef.current!;
    dispatch({ type: 'calculation-started' });
    adapter.run({
      nodes, pipes, guns,
      selectedGunIds: [...state.selectedGunIds],
      selectedIntakeNodeIds: [...state.selectedIntakeNodeIds],
      wetBulbF: Number(state.wetBulbF),
      pumpSettings: analysisPumpSettings,
      sourceResourcesByIntakeId,
    }, {
      onResult: (result) => dispatch({ type: 'analyzed', result }),
      onError: (message) => dispatch({ type: 'analysis-error', message }),
    });
  }, [state.selectedGunIds, state.calculating, state.selectedIntakeNodeIds, state.wetBulbF,
    nodes, pipes, guns, analysisPumpSettings, sourceResourcesByIntakeId]);

  useEffect(() => () => adapterRef.current?.dispose(), []);

  const gunStatuses = useMemo(() => !state.stale && state.result ? Object.fromEntries(
    state.result.systems.flatMap((system) => system.guns.map((gun) => [gun.gunId,
      gun.status === 'ready' ? 'ready' as const : 'failed' as const]))) : undefined,
  [state.stale, state.result]);

  return { state, dispatch, analyze, groups, relevantGroups, routing, gunStatuses,
    sourceResourcesByIntakeId };
}
