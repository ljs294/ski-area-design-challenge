import { describe, expect, it } from 'vitest';
import { createDefaultGuestSimulationNetwork } from '../guestSimulation/engine';
import type { GuestPortal } from '../guestSimulation/contracts';
import { GuestSimulationWorkerEngine } from './guestSimulationWorkerEngine';

const portal: GuestPortal = { version: 1, id: 'portal-1', kind: 'guest-entrance', type: 'guest-entrance',
  semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: 'Entrance',
  capacityGuestsPerTick: 10, openFromTick: 0, openUntilTick: 20_000 };

function initialize(runtime: GuestSimulationWorkerEngine) {
  return runtime.handle({ type: 'initialize', requestId: 'initialize', sequence: 0, runId: 'run-1', seed: 'seed-1',
    guestCount: 20, network: createDefaultGuestSimulationNetwork([portal]), startTick: 0, endTick: 10_000,
    environmentRevision: 2, topologyRevision: 3 });
}

describe('guest simulation worker engine', () => {
  it('rejects stale sequence and composite revisions', () => {
    const runtime = new GuestSimulationWorkerEngine();
    expect(initialize(runtime).type).toBe('ready');
    expect(runtime.handle({ type: 'snapshot', requestId: 'stale', sequence: 0 })).toMatchObject({ code: 'stale-sequence' });
    expect(runtime.handle({ type: 'advance', requestId: 'revision', sequence: 1, toTick: 100,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 99 })).toMatchObject({ code: 'stale-revision' });
  });

  it('produces the same hash for one large advance and many small advances', () => {
    const large = new GuestSimulationWorkerEngine();
    const small = new GuestSimulationWorkerEngine();
    initialize(large); initialize(small);
    const largeResult = large.handle({ type: 'advance', requestId: 'large', sequence: 1, toTick: 600,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    let smallResult = initialize(new GuestSimulationWorkerEngine());
    for (let tick = 60, sequence = 1; tick <= 600; tick += 60, sequence += 1) {
      smallResult = small.handle({ type: 'advance', requestId: `small-${tick}`, sequence, toTick: tick,
        expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    }
    expect(largeResult.type).toBe('advanced');
    expect(smallResult.type).toBe('advanced');
    if (largeResult.type === 'advanced' && smallResult.type === 'advanced') {
      expect(smallResult.snapshot.checksum).toBe(largeResult.snapshot.checksum);
    }
  });

  it('checkpoints and restores the live worker without changing continuation', () => {
    const uninterrupted = new GuestSimulationWorkerEngine();
    initialize(uninterrupted);
    uninterrupted.handle({ type: 'advance', requestId: 'before-save', sequence: 1, toTick: 400,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    const checkpoint = uninterrupted.handle({ type: 'checkpoint', requestId: 'checkpoint', sequence: 2 });
    expect(checkpoint.type).toBe('checkpoint');
    if (checkpoint.type !== 'checkpoint') return;
    const expected = uninterrupted.handle({ type: 'advance', requestId: 'after-save', sequence: 3, toTick: 900,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    const restored = new GuestSimulationWorkerEngine();
    const ready = restored.handle({ type: 'restore', requestId: 'restore', sequence: 0, bytes: checkpoint.bytes,
      expectedTopologyRevision: 3 });
    expect(ready.type).toBe('ready');
    const actual = restored.handle({ type: 'advance', requestId: 'after-load', sequence: 1, toTick: 900,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    if (expected.type === 'advanced' && actual.type === 'advanced') expect(actual.snapshot.checksum).toBe(expected.snapshot.checksum);
  });
});
