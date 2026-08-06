import type { GameSave, TerrainRecord } from '../types';
import type { TerrainSaveResponse } from '../ipcContract';

/**
 * What "unsaved" means for a resort session.
 *
 * Terrain edits — regrading under a trail/road/dam/pond, and felling ground
 * cover — used to hit the disk the instant a build was confirmed, so they
 * survived an exit that discarded everything they belonged to. They are now
 * held in memory like every other design change and written only when the
 * player saves, which is the rule the rest of the app already states in
 * resumeCheckpoint.ts.
 */

export interface TerrainDirty {
  /** sampleHeights / contours changed: only a full package write covers it. */
  elevation: boolean;
  /** coverGrid / display geometry changed. */
  cover: boolean;
}

export const TERRAIN_CLEAN: TerrainDirty = { elevation: false, cover: false };

export function withTerrainEdit(dirty: TerrainDirty, kind: 'elevation' | 'cover'): TerrainDirty {
  if (dirty[kind]) return dirty;
  return { ...dirty, [kind]: true };
}

export function terrainHasEdits(dirty: TerrainDirty): boolean {
  return dirty.elevation || dirty.cover;
}

export interface TerrainWriters {
  saveTerrain(record: TerrainRecord): Promise<TerrainSaveResponse>;
  saveTerrainCover(record: TerrainRecord): Promise<TerrainSaveResponse>;
}

/**
 * Write the pending terrain edits with the narrowest write that covers them.
 * A cover-only edit on a package that carries vector display geometry takes the
 * partial cover write (desktop leaves the immutable elevation assets alone);
 * anything touching elevation — or a raster-only v4 package, whose cover lives
 * in the full record — takes the whole-package write.
 */
export async function flushTerrainEdits(
  record: TerrainRecord,
  dirty: TerrainDirty,
  writers: TerrainWriters
): Promise<TerrainSaveResponse> {
  if (!terrainHasEdits(dirty)) return { ok: true, key: record.key };
  const coverOnly = !dirty.elevation
    && !!record.coverDisplayGeometry && !!record.coverDisplayMetadata;
  return coverOnly ? writers.saveTerrainCover(record) : writers.saveTerrain(record);
}

/**
 * The parts of a GameSave a player can actually edit. Camera and 3D are left
 * out on purpose: panning is not a change, and the exit checkpoint persists the
 * pose on its own.
 */
export type DesignSnapshot = Pick<GameSave,
  | 'name'
  | 'site'
  | 'lifts'
  | 'trails'
  | 'roads'
  | 'dams'
  | 'ponds'
  | 'nodes'
  | 'paths'
  | 'junctions'
  | 'snowmakingNodes'
  | 'lakeDepthOverrides'
  | 'lakeNameOverrides'
  | 'streamWidthOverrides'>;

export function designOf(save: DesignSnapshot): DesignSnapshot {
  return {
    name: save.name,
    site: save.site,
    lifts: save.lifts,
    trails: save.trails,
    roads: save.roads,
    dams: save.dams,
    ponds: save.ponds,
    nodes: save.nodes,
    paths: save.paths,
    junctions: save.junctions,
    snowmakingNodes: save.snowmakingNodes,
    lakeDepthOverrides: save.lakeDepthOverrides,
    lakeNameOverrides: save.lakeNameOverrides,
    streamWidthOverrides: save.streamWidthOverrides,
  };
}

const DESIGN_COLLECTIONS = [
  'site', 'lifts', 'trails', 'roads', 'dams', 'ponds', 'nodes', 'paths', 'junctions',
  'snowmakingNodes',
  'lakeDepthOverrides', 'lakeNameOverrides', 'streamWidthOverrides',
] as const;

/**
 * Has the live design diverged from the last explicitly saved one?
 *
 * Collections compare by *reference*: every editor mutation replaces the array
 * or record it touches, and snapshot() stores those exact references into the
 * save, so a reference match is proof nothing was edited. It also means a
 * camera-only checkpoint — which spreads the persisted save — stays clean.
 */
export function designHasEdits(saved: DesignSnapshot, live: DesignSnapshot): boolean {
  if (saved.name !== live.name) return true;
  return DESIGN_COLLECTIONS.some((field) => saved[field] !== live[field]);
}
