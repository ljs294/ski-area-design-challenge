import { afterEach, describe, expect, it, vi } from 'vitest';
import { createDefaultGuestSimulationNetwork } from '../guestSimulation/engine';
import type { GuestPortal } from '../guestSimulation/contracts';
import { GuestSimulationWorkerEngine } from './guestSimulationWorkerEngine';
import { createConditionSnapshot } from '../guestSimulation/conditions';
import { planDailyArrivals } from '../guestSimulation/demand';

const portal: GuestPortal = { version: 1, id: 'portal-1', kind: 'guest-entrance', type: 'guest-entrance',
  semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: 'Entrance',
  capacityGuestsPerTick: 10, openFromTick: 0, openUntilTick: 20_000 };

function initialize(runtime: GuestSimulationWorkerEngine) {
  return runtime.handle({ type: 'initialize', requestId: 'initialize', sequence: 0, runId: 'run-1', seed: 'seed-1',
    guestCount: 20, network: createDefaultGuestSimulationNetwork([portal]), startTick: 0, endTick: 10_000,
    environmentRevision: 2, topologyRevision: 3 });
}

describe('guest simulation worker engine', () => {
  afterEach(() => vi.restoreAllMocks());
  it('uses the deterministic Phase 3 realization as the roster authority', () => {
    const runtime = new GuestSimulationWorkerEngine();
    const demand = { dayType: 'weekend' as const, basePotentialGuests: 700, ticketPriceCents: 100,
      referencePriceCents: 100, reputation: 0.85, resortValue: 0.75, availableCapacityGuests: 320,
      maxGuests: 500, maxParties: 500, bucketSeconds: 600, outcomeWeight: 7 };
    const expected = planDailyArrivals({ seed: 'demand-seed', startTick: 0, endTick: 9_600,
      ...demand });
    const response = runtime.handle({ type: 'initialize', requestId: 'demand', sequence: 0, runId: 'demand-run',
      seed: 'demand-seed', network: createDefaultGuestSimulationNetwork([portal]), startTick: 0, endTick: 9_600,
      environmentRevision: 2, topologyRevision: 3, demand });
    expect(response.type).toBe('ready');
    if (response.type === 'ready') {
      expect(response.snapshot.metrics.population).toBe(expected.realization.guestCount);
      expect(response.snapshot.demandPlan.guestCount).toBe(expected.realization.guestCount);
      expect(response.snapshot.demandPlan.seed).toBe('demand-seed');
      expect(response.snapshot.demandPlan.waves.length).toBe(expected.demandPlan.waves.length);
      expect(response.snapshot.phase3.demandForecast?.checksum).toBe(expected.forecast.checksum);
      expect(response.snapshot.phase3.demandRealization?.checksum).toBe(expected.realization.checksum);
      expect(response.snapshot.phase3.economy.metrics.ticketCount).toBe(expected.realization.guestCount);
      expect(response.snapshot.phase3.economy.metrics.ticketRevenueCents)
        .toBe(expected.realization.guestCount * demand.ticketPriceCents);
      expect(response.snapshot.phase3.weeklyEstimate).toMatchObject({
        outcomeWeight: 7,
        ticketCount: expected.realization.guestCount * 7,
        ticketRevenueCents: expected.realization.guestCount * demand.ticketPriceCents * 7,
      });
      expect(response.snapshot.phase3.reconciled).toBe(true);
      const checkpoint = runtime.handle({ type: 'checkpoint', requestId: 'demand-checkpoint', sequence: 1 });
      expect(checkpoint.type).toBe('checkpoint');
      if (checkpoint.type === 'checkpoint') {
        const restored = new GuestSimulationWorkerEngine().handle({ type: 'restore', requestId: 'demand-restore',
          sequence: 0, bytes: checkpoint.bytes, expectedTopologyRevision: 3 });
        if (restored.type === 'error') throw new Error(restored.message);
        expect(restored.type).toBe('ready');
        if (restored.type === 'ready') expect(restored.snapshot.checksum).toBe(checkpoint.snapshot.checksum);
      }
    }
  });

  it('allows a Phase 3 scenario to produce zero guests without a sentinel roster record', () => {
    const runtime = new GuestSimulationWorkerEngine();
    const response = runtime.handle({ type: 'initialize', requestId: 'empty-demand', sequence: 0, runId: 'empty-run',
      seed: 'empty-seed', network: createDefaultGuestSimulationNetwork([portal]), startTick: 0, endTick: 9_600,
      environmentRevision: 2, topologyRevision: 3, demand: { dayType: 'weekday', basePotentialGuests: 0,
        ticketPriceCents: 100, referencePriceCents: 100, reputation: 0.5, resortValue: 0.5,
        availableCapacityGuests: 500, maxGuests: 500, maxParties: 500, bucketSeconds: 600 } });
    expect(response.type).toBe('ready');
    if (response.type === 'ready') {
      expect(response.snapshot.metrics.population).toBe(0);
      expect(response.snapshot.guests).toHaveLength(0);
      expect(response.snapshot.parties).toHaveLength(0);
    }
    const advanced = runtime.handle({ type: 'advance', requestId: 'empty-advance', sequence: 1, toTick: 600,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    expect(advanced.type).toBe('advanced');
    if (advanced.type === 'advanced') expect(advanced.snapshot.metrics.population).toBe(0);
  });

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

  it('serves deterministic compact advances with a bounded transferable frame and no rich snapshot', () => {
    // This checks the compact-frame contract, not host CPU throughput.
    vi.spyOn(performance, 'now').mockReturnValue(0);
    const first = new GuestSimulationWorkerEngine();
    const second = new GuestSimulationWorkerEngine();
    initialize(first); initialize(second);
    const request = (runtime: GuestSimulationWorkerEngine, requestId: string, sequence: number) => runtime.handle({
      type: 'advance', requestId, sequence, targetSecond: 120, maxCpuMs: 8,
      topologyRevision: 3, operationsRevision: 0, weatherRevision: 0,
    });
    const left = request(first, 'compact-left', 1);
    const right = request(second, 'compact-right', 1);
    expect(left.type).toBe('advanced');
    expect(right.type).toBe('advanced');
    if (left.type !== 'advanced' || !('renderFrame' in left)
      || right.type !== 'advanced' || !('renderFrame' in right)) return;
    expect(left).not.toHaveProperty('snapshot');
    expect(left.performance.eventsProcessed).toBeGreaterThan(0);
    expect(left.committedSecond).toBe(60);
    expect(left.backlogSeconds).toBe(60);
    const frameBytes = left.renderFrame.ids.byteLength + left.renderFrame.edgeIndices.byteLength
      + left.renderFrame.progress.byteLength + left.renderFrame.statusFlags.byteLength;
    expect(left.renderFrame.byteLength).toBe(frameBytes);
    expect(frameBytes).toBeLessThanOrEqual(left.renderFrame.ids.length * 24);
    expect(Array.from(left.renderFrame.ids)).toEqual(Array.from(right.renderFrame.ids));
    expect(Array.from(left.renderFrame.edgeIndices)).toEqual(Array.from(right.renderFrame.edgeIndices));
    expect(Array.from(left.renderFrame.progress)).toEqual(Array.from(right.renderFrame.progress));
    expect(Array.from(left.renderFrame.statusFlags)).toEqual(Array.from(right.renderFrame.statusFlags));
  });

  it('reports explicit stale topology, operations, and weather revisions', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const stale = runtime.handle({ type: 'advance', requestId: 'stale-composite', sequence: 1,
      targetSecond: 10, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 1, weatherRevision: 0 });
    expect(stale).toMatchObject({ type: 'error', code: 'stale-revision' });
    expect((stale as { readonly message?: string }).message).toContain('operations 1/0');
  });

  it('atomically replaces topology at the committed second and reports waiting guests', () => {
    const runtime = new GuestSimulationWorkerEngine();
    const initialized = initialize(runtime);
    if (initialized.type !== 'ready') throw new Error('expected initialized worker');
    const activeTick = Math.min(9_999, initialized.snapshot.guests[0]!.arrivalTick + 120);
    const advanced = runtime.handle({ type: 'advance', requestId: 'before-topology', sequence: 1, toTick: activeTick,
      expectedEnvironmentRevision: 2, expectedTopologyRevision: 3 });
    expect(advanced.type).toBe('advanced');
    const network = createDefaultGuestSimulationNetwork([portal]);
    const noRuns = { ...network, edges: network.edges.filter((edge) => edge.kind !== 'descent') };
    const updated = runtime.handle({ type: 'topology-update', requestId: 'topology-update', sequence: 2,
      network: noRuns, topologyRevision: 4 });
    expect(updated).toMatchObject({ type: 'topology-updated', committedSecond: activeTick });
    if (updated.type === 'topology-updated') {
      expect(updated.migration.waitingGuestIds.length).toBeGreaterThan(0);
      expect(updated.renderFrame.byteLength).toBe(updated.renderFrame.ids.length * 16);
      expect(updated).not.toHaveProperty('snapshot');
    }
    const snapshot = runtime.handle({ type: 'snapshot', requestId: 'after-topology', sequence: 3 });
    expect(snapshot.type).toBe('snapshot');
    if (snapshot.type === 'snapshot') {
      expect(snapshot.snapshot.tick).toBe(activeTick);
      expect(snapshot.snapshot.topologyRevision).toBe(4);
      expect(snapshot.snapshot.guests.some((guest) => guest.status === 'waiting-for-route'
        && guest.routeStateReason === 'no-valid-route')).toBe(true);
    }
  });

  it('queues hot condition revisions and applies them only at their effective second', () => {
    // Keep the effective-second boundary independent of parallel test load.
    vi.spyOn(performance, 'now').mockReturnValue(0);
    const runtime = new GuestSimulationWorkerEngine();
    const ready = initialize(runtime);
    if (ready.type !== 'ready') throw new Error('worker did not initialize');
    const network = createDefaultGuestSimulationNetwork([portal]);
    const conditions = createConditionSnapshot({ revision: 1, tick: 300, edges: network.edges.map((edge) => ({
      edgeId: edge.id, baseDifficulty: edge.targetRating ?? 0.1, grooming: 0.1,
      snowQuality: 0.15, coverage: 0.5, occupancy: { guests: 80, capacity: 10 },
    })) });
    const queued = runtime.handle({ type: 'updateEnvironment', requestId: 'hot-condition', sequence: 1,
      effectiveSecond: 300, topologyRevision: 3, operationsRevision: 1, weatherRevision: 1,
      conditionSnapshot: conditions });
    expect(queued).toMatchObject({ type: 'environment-updated', committedSecond: 0,
      effectiveSecond: 300, operationsRevision: 1, weatherRevision: 1 });
    const before = runtime.handle({ type: 'advance', requestId: 'before-hot-condition', sequence: 2,
      targetSecond: 240, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 1, weatherRevision: 1 });
    expect(before).toMatchObject({ type: 'advanced', committedSecond: 60 });
    for (let sequence = 3; sequence <= 12; sequence += 1) {
      runtime.handle({ type: 'advance', requestId: `hot-${sequence}`, sequence, targetSecond: 600,
        maxCpuMs: 8, topologyRevision: 3, operationsRevision: 1, weatherRevision: 1 });
    }
    const snapshot = runtime.handle({ type: 'snapshot', requestId: 'after-hot-condition', sequence: 13 });
    expect(snapshot).toMatchObject({ type: 'snapshot' });
    if (snapshot.type === 'snapshot') {
      expect(snapshot.snapshot.tick).toBe(600);
      expect(snapshot.snapshot.conditionSnapshot.tick).toBe(300);
      expect(snapshot.snapshot.conditionSnapshot.revision).toBe(1);
      expect(snapshot.snapshot.conditionSnapshot.checksum).toBe(conditions.checksum);
    }
  });

  it('rejects stale hot updates and old advances after a revision has been queued', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const queued = runtime.handle({ type: 'updateEnvironment', requestId: 'hot-revision', sequence: 1,
      effectiveSecond: 30, topologyRevision: 3, operationsRevision: 2, weatherRevision: 4 });
    expect(queued).toMatchObject({ type: 'environment-updated' });
    expect(runtime.handle({ type: 'updateEnvironment', requestId: 'old-hot-revision', sequence: 2,
      effectiveSecond: 60, topologyRevision: 3, operationsRevision: 1, weatherRevision: 4 }))
      .toMatchObject({ type: 'error', code: 'stale-revision' });
    expect(runtime.handle({ type: 'advance', requestId: 'old-advance', sequence: 3,
      targetSecond: 10, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 }))
      .toMatchObject({ type: 'error', code: 'stale-revision' });
  });

  it('does not call the rich snapshot path during a compact advance', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const engine = (runtime as unknown as { readonly engine: { snapshot: () => unknown } }).engine;
    const snapshot = vi.spyOn(engine, 'snapshot');
    const response = runtime.handle({ type: 'advance', requestId: 'compact-no-snapshot', sequence: 1,
      targetSecond: 60, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
    expect(response.type).toBe('advanced');
    expect(snapshot).not.toHaveBeenCalled();
  });

  it('acknowledges only integer compact time and carries the remainder as backlog', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    const compact = runtime.handle({ type: 'advance', requestId: 'fractional', sequence: 1,
      targetSecond: 12.5, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
    expect(compact.type).toBe('advanced');
    if (compact.type === 'advanced' && 'renderFrame' in compact) {
      expect(Number.isSafeInteger(compact.committedSecond)).toBe(true);
      expect(compact.committedSecond).toBe(12);
      expect(compact.backlogSeconds).toBe(0.5);
      const inspection = runtime.handle({ type: 'inspectGuest', requestId: 'inspect-fractional', sequence: 2, guestId: 'guest-000001' });
      expect(inspection.type).toBe('guest');
      if (inspection.type === 'guest') expect(inspection.committedSecond).toBe(12);
      const completed = runtime.handle({ type: 'advance', requestId: 'complete-fractional', sequence: 3,
        targetSecond: 13, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
      expect(completed.type).toBe('advanced');
      if (completed.type === 'advanced' && 'renderFrame' in completed) {
        expect(completed.committedSecond).toBe(13);
        expect(completed.backlogSeconds).toBe(0);
      }
    }
  });

  it('projects increasing progress for a guest while a movement event is pending', () => {
    const runtime = new GuestSimulationWorkerEngine();
    initialize(runtime);
    let previous: Float32Array | null = null;
    let increased = false;
    // Arrival waves are seeded, so scan a bounded deterministic horizon until
    // at least one connector/lift/descent traversal spans two slices.
    for (let step = 1; step <= 160 && !increased; step += 1) {
      const response = runtime.handle({ type: 'advance', requestId: `progress-${step}`, sequence: step,
        targetSecond: step * 60, maxCpuMs: 8, topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
      if (response.type !== 'advanced' || !('renderFrame' in response)) continue;
      if (previous && response.renderFrame.progress.some((value, index) => value > (previous?.[index] ?? value) + 0.0001)) {
        increased = true;
      }
      previous = response.renderFrame.progress;
    }
    expect(increased).toBe(true);
  });
});
