import { saveDesign } from '../../src/designSaveClient';
import type { SavedSiteBox } from '../../src/types/gameSave';
import type { DesignCameraState } from '../../src/types/designSave';
import { designSaveDraft } from '../../src/app/session/designSaveSnapshot';
import type { DesignPersistenceSnapshot } from '../../src/app/session/designSession';

/** Owns the standalone game's one-way selection-to-Three.js save handoff. */
export async function saveInitialDesignFork(snapshot: DesignPersistenceSnapshot, name: string,
  site: SavedSiteBox | null, camera: DesignCameraState): Promise<string> {
  const now = new Date().toISOString();
  const result = await saveDesign(designSaveDraft(snapshot,
    { name, createdAt: now, updatedAt: now, site, camera }));
  if (!result.ok) throw new Error(`Could not save the design fork: ${result.error}`);
  return result.receipt.key;
}
