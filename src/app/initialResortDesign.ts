import { sanitizeDams } from '../damAnalysis';
import { sanitizeLakeDepthOverrides, sanitizeLakeNameOverrides,
  sanitizeSnowmakingLakeIds } from '../lakeAnalysis';
import { sanitizeLifts } from '../lifts';
import { sanitizePonds } from '../pondAnalysis';
import { sanitizeRoads } from '../roads';
import { sanitizeNodes, sanitizePaths } from '../skiNodes';
import { reconcileSnowmakingNodes, sanitizeSnowmakingNodes } from '../snowmakingNodes';
import { hydrateSnowmakingNetwork } from '../snowmakingNetwork';
import { sanitizeStreamWidthOverrides } from '../streamAnalysis';
import { hydrateTopology } from '../topology';
import { sanitizeTrails } from '../trails';
import type { GameSave } from '../types/gameSave';

/** One authoritative, sanitized projection of every persisted design collection. */
export function initialResortDesign(save?: GameSave | null) {
  const lifts = sanitizeLifts(save?.lifts ?? []);
  const nodes = sanitizeNodes(save?.nodes ?? []);
  const topology = hydrateTopology(
    sanitizeTrails(save?.trails ?? []),
    sanitizePaths(save?.paths ?? []),
    lifts,
    save?.junctions ?? [],
    nodes,
  );
  const dams = sanitizeDams(save?.dams ?? []);
  const ponds = sanitizePonds(save?.ponds ?? []);
  const snowmaking = hydrateSnowmakingNetwork(reconcileSnowmakingNodes(
    sanitizeSnowmakingNodes(save?.snowmakingNodes ?? []), dams, ponds),
  save?.snowmakingPipes ?? [], save?.snowmakingNodeNextNumbers, save?.snowguns ?? []);
  return {
    lifts,
    trails: topology.trails,
    roads: sanitizeRoads(save?.roads ?? []),
    dams,
    ponds,
    snowmakingNodes: snowmaking.nodes,
    snowmakingPipes: snowmaking.pipes,
    snowguns: snowmaking.guns,
    snowmakingNodeNextNumbers: snowmaking.nextNumbers,
    nodes,
    paths: topology.paths,
    junctions: topology.junctions,
    streamWidthOverrides: sanitizeStreamWidthOverrides(save?.streamWidthOverrides),
    lakeDepthOverrides: sanitizeLakeDepthOverrides(save?.lakeDepthOverrides),
    lakeNameOverrides: sanitizeLakeNameOverrides(save?.lakeNameOverrides),
    snowmakingLakeIds: sanitizeSnowmakingLakeIds(save?.snowmakingLakeIds),
  };
}
