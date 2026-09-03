import { describe, expect, it } from 'vitest';
import { createDefaultGuestSimulationNetwork } from '../guestSimulation/engine';
import type { GuestPortal } from '../guestSimulation/contracts';
import { GuestSimulationWorkerEngine } from './guestSimulationWorkerEngine';
import { createConditionSnapshot } from '../guestSimulation/conditions';

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

  it('applies condition revisions at their tick and replays them through a checkpoint', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const conditions = createConditionSnapshot({ revision: 1, tick: 300, edges: network.edges.map((edge) => ({
      edgeId: edge.id, baseDifficulty: edge.targetRating ?? 0.1, grooming: 0.2,
      snowQuality: 0.3, coverage: 0.8, occupancy: { guests: 8, capacity: 10 },
    })) });
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const advanced = runtime.handle({ type: 'advance', requestId: 'conditions', sequence: 1, toTick: 600,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3, conditionSnapshot: conditions });
    expect(advanced.type).toBe('advanced');
    if (advanced.type !== 'advanced') return;
    expect(advanced.snapshot.conditionSnapshot.checksum).toBe(conditions.checksum);
    expect(advanced.snapshot.thoughtAggregation.reconciled).toBe(true);
    const checkpoint = runtime.handle({ type: 'checkpoint', requestId: 'condition-checkpoint', sequence: 2 });
    expect(checkpoint.type).toBe('checkpoint');
    if (checkpoint.type !== 'checkpoint') return;
    const restored = new GuestSimulationWorkerEngine();
    const ready = restored.handle({ type: 'restore', requestId: 'condition-restore', sequence: 0,
      bytes: checkpoint.bytes, expectedTopologyRevision: 3 });
    expect(ready.type).toBe('ready');
    if (ready.type === 'ready') expect(ready.snapshot.checksum).toBe(advanced.snapshot.checksum);
  });

  it('rejects a future condition without mutating the authoritative tick', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const network = createDefaultGuestSimulationNetwork([portal]);
    const future = createConditionSnapshot({ revision: 1, tick: 500, edges: network.edges.map((edge) => ({
      edgeId: edge.id, baseDifficulty: edge.targetRating ?? 0.1, grooming: 0.5,
      snowQuality: 0.75, coverage: 1, occupancy: { guests: 0, capacity: 10 },
    })) });
    expect(runtime.handle({ type: 'advance', requestId: 'bad-future', sequence: 1, toTick: 100,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3, conditionSnapshot: future }))
      .toMatchObject({ type: 'error', code: 'invalid-request' });
    const snapshot = runtime.handle({ type: 'snapshot', requestId: 'after-bad-future', sequence: 2 });
    if (snapshot.type === 'snapshot') expect(snapshot.snapshot.tick).toBe(0);
  });
});
