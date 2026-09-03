import type { GameSave } from '../types';
import { saveGame } from '../gameSaveClient';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';

/** Persist the worker sidecar first so a GameSave never references newer guest state than exists on disk. */
export async function saveGameWithGuestCheckpoint(save: GameSave, runtime: GuestSimulationRuntime) {
  const revision = `${save.updatedAt}|${save.lastPlayedAt}`;
  const checkpoint = await runtime.persistBarrier(save.key, revision);
  if (!checkpoint.ok) return checkpoint;
  return saveGame(save);
}
