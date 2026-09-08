import type { GameSave } from '../types';
import { saveGame } from '../gameSaveClient';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';

/** Persist the worker sidecar first so a GameSave never references newer guest state than exists on disk. */
export async function saveGameWithGuestCheckpoint(save: GameSave, runtime: GuestSimulationRuntime) {
  const revision = `${save.updatedAt}|${save.lastPlayedAt}`;
  // Snapshotting the save and flushing the worker are separate async
  // operations.  Pass the snapshot's authoritative elapsed second into the
  // barrier so a running simulation cannot silently produce a mixed-time save.
  const expectedSecond = save.time?.clock.elapsedSimSecond;
  const checkpoint = await runtime.persistBarrier(save.key, revision, expectedSecond);
  if (!checkpoint.ok) return checkpoint;
  if (expectedSecond !== undefined && Math.abs(checkpoint.committedSecond - expectedSecond) > Number.EPSILON) {
    return { ok: false as const,
      error: 'The guest checkpoint and game clock do not share one committed timestamp; retry the save.' };
  }
  return saveGame(save);
}
