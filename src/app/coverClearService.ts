import type maplibregl from 'maplibre-gl';
import type { CoverGrid } from '../types/cover';
import type { SavedLift } from '../types/lifts';
import type { TerrainRecord } from '../types/terrain';
import { liftClearingRing, type CoverClearing } from '../coverEdit';
import { manifestWithUpdatedCover, validateTerrainCoverEdit } from '../terrainPackage';
import type { CoverEditAdapter } from './coverEditClient';
import type { TerrainDocument, TerrainSnapshot } from './terrainDocument';

interface CoverClearDependencies {
  map(): maplibregl.Map | null;
  terrain: TerrainDocument;
  adapter: CoverEditAdapter;
  now?(): string;
}

export interface CoverClearService {
  clear(clearings: CoverClearing[]): Promise<void>;
  clearLift(lift: SavedLift): Promise<void>;
}

/** Best-effort cover edits, serialized and revision-checked by TerrainDocument. */
export function createCoverClearService({
  map, terrain, adapter, now = () => new Date().toISOString(),
}: CoverClearDependencies): CoverClearService {
  const clearAgainst = async (
    { record, revision }: TerrainSnapshot,
    clearings: CoverClearing[],
  ): Promise<void> => {
    if (!map() || !record || !record.coverGrid || !record.bounds) return;
    try {
      const workerGrid = {
        ...record.coverGrid,
        bounds: { ...record.coverGrid.bounds },
        data: Uint8Array.from(record.coverGrid.data),
      } as unknown as CoverGrid;
      const hasVectorDisplay = !!record.coverDisplayGeometry && !!record.coverDisplayMetadata;
      const result = await adapter.run({
        grid: workerGrid,
        clearings,
        deriveDisplay: hasVectorDisplay,
      });
      if (result.changed === 0) return;
      const grid = {
        ...record.coverGrid,
        bounds: { ...record.coverGrid.bounds },
        data: result.gridData,
      } as unknown as CoverGrid;
      let upgraded = {
        ...record,
        coverGrid: grid,
        coverMetadata: result.coverMetadata,
        updatedAt: now(),
      } as unknown as TerrainRecord;

      if (hasVectorDisplay) {
        if (!result.displayGeometry || !result.displayMetadata) {
          throw new Error('Ground-cover worker returned no vector display geometry.');
        }
        upgraded = {
          ...upgraded,
          coverDisplayGeometry: result.displayGeometry,
          coverDisplayMetadata: result.displayMetadata,
        };
      }

      upgraded = { ...upgraded, packageManifest: manifestWithUpdatedCover(upgraded) };
      const validation = validateTerrainCoverEdit(upgraded);
      if (!validation.ok) {
        console.warn(
          'Cover-clear produced an invalid package; keeping the previous cover.',
          validation.errors.join(' '),
        );
        return;
      }
      const commit = terrain.commit({ expectedRevision: revision, record: upgraded, kind: 'cover' });
      if (!commit.ok) {
        console.warn('Cover-clear finished against a superseded terrain package; keeping the previous cover.');
      }
    } catch (error) {
      console.warn('Cover-clear failed; keeping the previous cover.', error);
    }
  };

  const clear = (clearings: CoverClearing[]): Promise<void> =>
    terrain.runCoverEdit((snapshot) => clearAgainst(snapshot, clearings));

  const clearLift = async (lift: SavedLift): Promise<void> => {
    const record = terrain.record;
    if (!record || !record.bounds) return;
    const ring = liftClearingRing(lift.points, record.bounds, lift.id);
    await clear([{ polygon: [ring] }]);
  };

  return { clear, clearLift };
}
