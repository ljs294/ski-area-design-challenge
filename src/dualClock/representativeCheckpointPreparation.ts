import type { DualCheckpoint, MacroCohort } from './model';
import { validateDualCheckpoint } from './validation';

interface PreparationPorts {
  track(cohort: MacroCohort, count: number): void;
  reconcile(): void;
  checkpoint(): DualCheckpoint;
  restore(checkpoint: DualCheckpoint): void;
}

/** Offline-only exact representative allocation over coherent physical cohorts. */
export function prepareRepresentativeCheckpoint(
  state: DualCheckpoint,
  target: number,
  ports: PreparationPorts,
): DualCheckpoint {
  if (!state.clock.paused) throw new Error('Pause the simulation before preparing a representative checkpoint.');
  if (state.advance) throw new Error('Finish or clear the active advance before preparing a representative checkpoint.');
  if (!Number.isSafeInteger(target) || target <= 0 || target > 10_000) {
    throw new Error('Representative checkpoint target must be an integer from 1 to 10000.');
  }
  const cohorts = state.cohorts.filter(cohort => cohort.count > 0).sort((left, right) => left.id - right.id);
  const active = cohorts.reduce((sum, cohort) => sum + cohort.count, 0);
  if (active < target || active !== state.flow.active) {
    throw new Error('Representative checkpoint target exceeds coherent active cohort flow.');
  }
  const before = structuredClone(state);
  try {
    state.config.representativeLimit = target;
    state.clock.speed = 1;
    state.guests = [];
    state.selectedGuestId = null;
    state.autoTrack = false;
    state.trailQueues = {};
    state.presentationMode = 'individual';
    const quotas = cohorts.map(cohort => { const exact = cohort.count * target / active;
      return { cohort, count: Math.floor(exact), remainder: exact - Math.floor(exact) }; });
    let remaining = target - quotas.reduce((sum, quota) => sum + quota.count, 0);
    for (const quota of [...quotas].sort((left, right) => right.remainder - left.remainder || left.cohort.id - right.cohort.id)) {
      if (!remaining) break;
      if (quota.count < quota.cohort.count) { quota.count++; remaining--; }
    }
    if (remaining) throw new Error('Unable to allocate the requested representative population.');
    for (const quota of quotas) if (quota.count) ports.track(quota.cohort, quota.count);
    ports.reconcile();
    if (state.guests.filter(guest => guest.status !== 'departed').length !== target) {
      throw new Error('Representative checkpoint preparation did not reach the exact target.');
    }
    const checkpoint = ports.checkpoint();
    validateDualCheckpoint(checkpoint);
    return checkpoint;
  } catch (error) {
    ports.restore(before);
    throw error;
  }
}
