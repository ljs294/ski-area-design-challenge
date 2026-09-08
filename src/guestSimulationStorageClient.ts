import { desktop } from './desktopBridge';
import type {
  GuestSimulationCheckpointLoadResponse,
  GuestSimulationCheckpointSaveResponse,
} from './ipcContract';

/** Phase 1C intentionally supports Electron only; the web renderer has no guest persistence. */
export function saveGuestSimulationCheckpoint(
  saveKey: string,
  gameSaveUpdatedAt: string,
  bytes: Uint8Array,
): Promise<GuestSimulationCheckpointSaveResponse> {
  if (!desktop) return Promise.resolve({ ok: false, error: 'Guest simulation persistence requires the desktop app.' });
  return desktop.guestSimulation.saveCheckpoint(saveKey, gameSaveUpdatedAt, bytes);
}

export function loadGuestSimulationCheckpoint(saveKey: string, gameSaveUpdatedAt: string): Promise<GuestSimulationCheckpointLoadResponse> {
  if (!desktop) return Promise.resolve({ status: 'missing' });
  return desktop.guestSimulation.loadCheckpoint(saveKey, gameSaveUpdatedAt);
}
