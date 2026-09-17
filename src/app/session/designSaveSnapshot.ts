import type { SavedSiteBox } from '../../types/gameSave';
import type { DesignCameraState, DesignSaveDraft } from '../../types/designSave';
import { DESIGN_SAVE_FORMAT, DESIGN_SAVE_SCHEMA_VERSION } from '../../types/designSave';
import type { TerrainRecord } from '../../types/terrain';
import type { SavedLift } from '../../types/lifts';
import type { SavedTrail } from '../../types/trails';
import type { SavedJunction, SavedNode, SavedPath } from '../../types/topology';
import type { DesignPersistenceSnapshot } from './designSession';

export interface DesignSaveMetadata {
  name: string;
  createdAt: string;
  updatedAt: string;
  site: SavedSiteBox | null;
  camera: DesignCameraState;
}

/** Capture the session boundary without simulation, weather, renderer, or
 * React state. DesignSaveRepository takes the one storage-owned clone before
 * beginning asynchronous publication. */
export function designSaveDraft(
  snapshot: DesignPersistenceSnapshot,
  metadata: DesignSaveMetadata,
): DesignSaveDraft {
  if (!snapshot.terrain) throw new Error('A terrain package is required before saving the design.');
  return {
    format: DESIGN_SAVE_FORMAT,
    schemaVersion: DESIGN_SAVE_SCHEMA_VERSION,
    key: snapshot.identity.id,
    name: metadata.name,
    createdAt: metadata.createdAt,
    updatedAt: metadata.updatedAt,
    site: metadata.site,
    camera: metadata.camera,
    revisions: { design: snapshot.revision, terrain: snapshot.terrainRevision,
      topology: snapshot.topologyRevision, lifts: snapshot.liftRevision },
    terrainRecord: snapshot.terrain as unknown as TerrainRecord,
    lifts: snapshot.lifts as unknown as SavedLift[],
    trails: snapshot.trails as unknown as SavedTrail[],
    nodes: snapshot.nodes as unknown as SavedNode[],
    paths: snapshot.paths as unknown as SavedPath[],
    junctions: snapshot.junctions as unknown as SavedJunction[],
  };
}
