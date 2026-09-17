import { saveDesign } from '../../designSaveClient';
import type { SavedSiteBox } from '../../types/gameSave';
import type { DesignCameraState } from '../../types/designSave';
import { designSaveDraft } from './designSaveSnapshot';
import type { DesignPersistenceSnapshot } from './designSession';

/** The selection host's one-way persistence handoff into Three.js gameplay. */
export async function saveInitialDesignFork(snapshot: DesignPersistenceSnapshot, name: string,
  site: SavedSiteBox | null, camera: DesignCameraState): Promise<string> {
  const now = new Date().toISOString();
  const result = await saveDesign(designSaveDraft(snapshot,
    { name, createdAt: now, updatedAt: now, site, camera }));
  if (!result.ok) throw new Error(`Could not save the design fork: ${result.error}`);
  return result.receipt.key;
}
