import type { SavedSnowmakingNode } from './types/snowmaking';
import {
  allocateSnowmakingNode,
  detachSnowmakingNode,
  type SnowmakingNetworkState,
} from './snowmakingNetwork';

export const OWNED_PUMP_HOUSE_RATING = Object.freeze({
  horsepowerHp: 1000 as const,
  efficiency: 0.85 as const,
});

export interface OwnedSnowmakingPumpInput {
  id: string;
  ownerBuildingId: string;
  name: string;
  point: [number, number];
  elevM: number | null;
  createdAt: string;
}

export interface OwnedSnowmakingPumpCreation {
  state: SnowmakingNetworkState;
  node: SavedSnowmakingNode;
}

export function createOwnedSnowmakingPump(
  state: SnowmakingNetworkState,
  input: OwnedSnowmakingPumpInput,
): OwnedSnowmakingPumpCreation | null {
  if (!input.id || !input.ownerBuildingId || state.nodes.some((node) =>
    node.id === input.id || node.ownerBuildingId === input.ownerBuildingId)) return null;
  const allocated = allocateSnowmakingNode(state, {
    id: input.id, name: input.name, kind: 'pump', point: [...input.point],
    elevM: input.elevM, createdAt: input.createdAt,
  });
  const node: SavedSnowmakingNode = {
    ...allocated.node,
    ownerBuildingId: input.ownerBuildingId,
    pumpRating: { ...OWNED_PUMP_HOUSE_RATING },
  };
  return { node, state: { ...allocated.state,
    nodes: allocated.state.nodes.map((candidate) => candidate.id === node.id ? node : candidate) } };
}

export const createBuildingOwnedPump = createOwnedSnowmakingPump;

export function isOwnedSnowmakingPump(
  node: SavedSnowmakingNode | undefined,
  ownerBuildingId?: string,
): node is SavedSnowmakingNode & { kind: 'pump'; ownerBuildingId: string } {
  return !!node && node.kind === 'pump' && typeof node.ownerBuildingId === 'string' &&
    node.ownerBuildingId.length > 0 &&
    (ownerBuildingId === undefined || node.ownerBuildingId === ownerBuildingId);
}

export interface SnowmakingNodeInspectorCapabilities {
  canRename: boolean;
  canRemove: boolean;
  ownerBuildingId: string | null;
}

export function snowmakingNodeInspectorCapabilities(
  node: SavedSnowmakingNode | undefined,
): SnowmakingNodeInspectorCapabilities {
  if (!node) return { canRename: false, canRemove: false, ownerBuildingId: null };
  if (isOwnedSnowmakingPump(node)) {
    return { canRename: false, canRemove: false, ownerBuildingId: node.ownerBuildingId };
  }
  return { canRename: node.kind !== 'junction',
    canRemove: node.kind === 'pump' || node.kind === 'hydrant', ownerBuildingId: null };
}

export function canRenameSnowmakingNode(node: SavedSnowmakingNode | undefined): boolean {
  return snowmakingNodeInspectorCapabilities(node).canRename;
}

export function canRemoveSnowmakingNode(node: SavedSnowmakingNode | undefined): boolean {
  return snowmakingNodeInspectorCapabilities(node).canRemove;
}

export interface OwnedSnowmakingPumpRemoval {
  state: SnowmakingNetworkState;
  node: SavedSnowmakingNode;
  connectedPipeIds: string[];
}

export function removeOwnedSnowmakingPump(
  state: SnowmakingNetworkState,
  nodeId: string,
  createId?: () => string,
): OwnedSnowmakingPumpRemoval | null {
  const node = state.nodes.find((candidate) => candidate.id === nodeId);
  if (!isOwnedSnowmakingPump(node)) return null;
  const connectedPipeIds = state.pipes.filter((pipe) => pipe.vertices.some((vertex) =>
    vertex.nodeId === nodeId)).map((pipe) => pipe.id);
  return { node, connectedPipeIds, state: {
    ...state,
    nodes: state.nodes.filter((candidate) => candidate.id !== nodeId),
    pipes: detachSnowmakingNode(state.pipes, nodeId, createId),
  } };
}

export function removeBuildingOwnedPump(
  state: SnowmakingNetworkState,
  buildingId: string,
  createId?: () => string,
): OwnedSnowmakingPumpRemoval | null {
  const node = state.nodes.find((candidate) => isOwnedSnowmakingPump(candidate, buildingId));
  return node ? removeOwnedSnowmakingPump(state, node.id, createId) : null;
}

export function renameOwnedSnowmakingPump(
  state: SnowmakingNetworkState,
  buildingId: string,
  name: string,
): SnowmakingNetworkState | null {
  const node = state.nodes.find((candidate) => isOwnedSnowmakingPump(candidate, buildingId));
  const trimmed = name.trim();
  if (!node || !trimmed) return null;
  if (node.name === trimmed) return state;
  return { ...state, nodes: state.nodes.map((candidate) => candidate.id === node.id
    ? { ...candidate, name: trimmed } : candidate) };
}
