import type { NetworkEdge } from '../network';
import type { SavedTrail } from '../types/trails';

/** Deterministic design identity shared by orchestration and the save barrier. */
export function resortRevision(edges: readonly NetworkEdge[], trails: readonly SavedTrail[]): number {
  const text = JSON.stringify([edges, trails]);
  let hash = 2166136261;
  for (let i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619);
  return hash >>> 0;
}
