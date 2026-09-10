import type { GameSave } from '../types';
import { saveGame } from '../gameSaveClient';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';
import { projectDualClock } from '../dualClock/clock';
import { encodeSnowGrid } from '../snow';
import { buildSkiNetwork } from '../network';
import { resortRevision } from '../dualClock/revision';

/** Persist the worker sidecar first so a GameSave never references newer guest state than exists on disk. */
export async function saveGameWithGuestCheckpoint(save: GameSave, runtime: GuestSimulationRuntime) {
  if (save.schemaVersion === 17) {
    if (!runtime.dualCheckpoint) return { ok: false as const, error: 'The dual-clock save barrier is unavailable.' };
    try {
      const network = buildSkiNetwork(save.trails, save.lifts, { nodes: save.nodes ?? [], paths: save.paths ?? [], junctions: save.junctions ?? [] });
      const checkpoint = await runtime.dualCheckpoint();
      if (checkpoint.resortRevision !== resortRevision(network.edges, save.trails)) {
        return { ok: false as const, error: 'The resort changed during the simulation save barrier. Retry the save.' };
      }
      save.dualClock = checkpoint;
      save.time = { schemaVersion: 3, configVersion: 1, clock: projectDualClock(checkpoint.clock) };
      if (save.weatherRun) save.weatherRun = { ...save.weatherRun, cursorHour: Math.max(0,
        Math.floor((Date.parse(checkpoint.clock.at) - Date.parse(save.weatherRun.localStartAt)) / 3600000)) };
      if (checkpoint.snow) save.snow = encodeSnowGrid({ ...checkpoint.snow,
        depthM: Float32Array.from(checkpoint.snow.depthM), surface: Uint8Array.from(checkpoint.snow.surface) });
      return await saveGame(save);
    } catch (error) { return { ok: false as const, error: error instanceof Error ? error.message : 'Simulation checkpoint failed.' }; }
  }
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
