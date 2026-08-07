import type { TerrainCommitRequest } from './terrainDocument';
import { TerrainDocument } from './terrainDocument';
import { TopologyTransaction } from './topologyDocument';

export type CommittedDocumentResult =
  | {
      ok: true;
      terrainRevision: number;
      topologyRevision: number;
      topologyChanged: boolean;
    }
  | { ok: false; reason: 'terrain-stale' | 'topology-stale' | 'topology-settled' };

export interface CommittedDocumentRequest {
  readonly terrain: TerrainDocument;
  readonly topology: TopologyTransaction;
  /** Omitted when the topology command does not grade terrain. */
  readonly terrainCommit?: TerrainCommitRequest;
}

/**
 * Commit terrain and topology as one synchronous document transition.
 *
 * Both revisions are validated before either document moves. Both authoritative
 * snapshots then move before either document publishes to React, map sources,
 * caches, or protocols, so every observer sees the same committed design.
 */
export function commitDocuments(request: CommittedDocumentRequest): CommittedDocumentResult {
  const terrainPreparation = request.terrainCommit
    ? request.terrain.prepareCommit(request.terrainCommit)
    : null;
  if (terrainPreparation && !terrainPreparation.ok) {
    request.topology.abort();
    return { ok: false, reason: 'terrain-stale' };
  }

  const topologyPreparation = request.topology.prepareCommit();
  if (!topologyPreparation.ok) {
    request.topology.abort();
    return {
      ok: false,
      reason: topologyPreparation.reason === 'stale' ? 'topology-stale' : 'topology-settled',
    };
  }

  // No user code or await occurs between preparation and application. A
  // rejected apply therefore signals a broken ownership invariant, not a
  // recoverable stale edit.
  if (terrainPreparation?.ok && !request.terrain.applyPrepared(terrainPreparation.prepared)) {
    request.topology.abort();
    throw new Error('Prepared terrain commit lost ownership before application');
  }
  if (!request.topology.applyPrepared(topologyPreparation.prepared)) {
    throw new Error('Prepared topology commit lost ownership before application');
  }

  if (terrainPreparation?.ok) request.terrain.publishPrepared(terrainPreparation.prepared);
  request.topology.publishPrepared(topologyPreparation.prepared);

  return {
    ok: true,
    terrainRevision: terrainPreparation?.ok
      ? terrainPreparation.prepared.revision
      : request.terrain.revision,
    topologyRevision: topologyPreparation.prepared.revision,
    topologyChanged: topologyPreparation.prepared.changed,
  };
}
