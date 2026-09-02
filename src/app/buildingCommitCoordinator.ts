import type { BuildingTransaction } from '../buildingDocument';
import type { TerrainCommitRequest } from './terrainDocument';
import { TerrainDocument } from './terrainDocument';
import type { SnowmakingNetworkTransaction } from './snowmakingNetworkDocument';

export type BuildingCommitResult =
  | { ok: true; terrainRevision: number; buildingRevision: number; snowmakingRevision: number }
  | { ok: false; reason: 'terrain-stale' | 'building-stale' | 'building-settled' |
      'snowmaking-stale' | 'snowmaking-settled' };

export interface BuildingCommitDocumentsRequest {
  terrain: TerrainDocument;
  building: BuildingTransaction;
  snowmaking: SnowmakingNetworkTransaction;
  terrainCommit?: TerrainCommitRequest;
}

/**
 * Atomically advance the optional terrain grade, the building collection, and
 * the reciprocal snowmaking network edit. Every revision is checked before
 * any authoritative snapshot moves; every snapshot moves before observers run.
 */
export function commitBuildingDocuments(
  request: BuildingCommitDocumentsRequest,
): BuildingCommitResult {
  const terrainPreparation = request.terrainCommit
    ? request.terrain.prepareCommit(request.terrainCommit)
    : null;
  if (terrainPreparation && !terrainPreparation.ok) {
    request.building.abort();
    request.snowmaking.abort();
    return { ok: false, reason: 'terrain-stale' };
  }

  const buildingPreparation = request.building.prepareCommit();
  if (!buildingPreparation.ok) {
    request.building.abort();
    request.snowmaking.abort();
    return { ok: false, reason: buildingPreparation.reason === 'stale'
      ? 'building-stale' : 'building-settled' };
  }

  const snowmakingPreparation = request.snowmaking.prepareCommit();
  if (!snowmakingPreparation.ok) {
    request.building.abort();
    request.snowmaking.abort();
    return { ok: false, reason: snowmakingPreparation.reason === 'stale'
      ? 'snowmaking-stale' : 'snowmaking-settled' };
  }

  if (terrainPreparation?.ok && !request.terrain.applyPrepared(terrainPreparation.prepared)) {
    throw new Error('Prepared building terrain commit lost ownership before application');
  }
  if (!request.building.applyPrepared(buildingPreparation.prepared)) {
    throw new Error('Prepared building commit lost ownership before application');
  }
  if (!request.snowmaking.applyPrepared(snowmakingPreparation.prepared)) {
    throw new Error('Prepared snowmaking commit lost ownership before application');
  }

  if (terrainPreparation?.ok) request.terrain.publishPrepared(terrainPreparation.prepared);
  request.building.publishPrepared(buildingPreparation.prepared);
  request.snowmaking.publishPrepared(snowmakingPreparation.prepared);

  return {
    ok: true,
    terrainRevision: terrainPreparation?.ok
      ? terrainPreparation.prepared.revision : request.terrain.revision,
    buildingRevision: buildingPreparation.prepared.revision,
    snowmakingRevision: snowmakingPreparation.prepared.revision,
  };
}
