import { describe, expect, it } from 'vitest';
import { createDefaultGuestSimulationNetwork, createDailyGuestRoster, createGuestSimulationEngine } from './engine';
import type { GuestPortal } from './contracts';
import { createGuestSimulationNetwork } from './engineSupport';

const portal: GuestPortal = { version: 1, id: 'portal-1', kind: 'guest-entrance', type: 'guest-entrance',
  semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: 'Entrance',
  capacityGuestsPerTick: 100, openFromTick: 0, openUntilTick: 20_000 };

function setup() {
  const network = createDefaultGuestSimulationNetwork([portal]);
  const roster = createDailyGuestRoster({ seed: 'topology-migration', guestCount: 40,
    portals: network.portals, startTick: 0, endTick: 10_000 });
  return { network, engine: createGuestSimulationEngine({ network, roster }) };
}

describe('guest topology migration', () => {
  it('preserves committed time and releases affected movement when a route is removed', () => {
    const { network, engine } = setup();
    engine.advanceTo(1_000);
    const before = engine.snapshot();
    const affected = before.guests.filter((guest) => guest.status === 'travelling-to-lift'
      || guest.status === 'lift-queue' || guest.status === 'lift-ride' || guest.status === 'skiing');
    expect(affected.length).toBeGreaterThan(0);
    const noRuns = createGuestSimulationNetwork({ ...network, edges: network.edges.filter((edge) => edge.kind !== 'descent') });
    const migration = engine.replaceTopology(noRuns, 2);
    expect(engine.currentTick).toBe(1_000);
    expect(migration.reroutedGuestIds.length + migration.waitingGuestIds.length).toBeGreaterThan(0);
    const after = engine.snapshot();
    for (const guest of after.guests.filter((candidate) => migration.waitingGuestIds.includes(candidate.id))) {
      expect(guest.status).toBe('waiting-for-route');
      expect(guest.routeStateReason).toBe('no-valid-route');
      expect(guest.currentResourceId).toBeNull();
    }
    expect(after.metrics.liftSeatsConserved).toBe(true);
  });

  it('retries waiting guests when a valid topology is restored without rebuilding identities', () => {
    const { network, engine } = setup();
    engine.advanceTo(1_000);
    const originalIds = engine.snapshot().guests.map((guest) => guest.id);
    const noRuns = createGuestSimulationNetwork({ ...network, edges: network.edges.filter((edge) => edge.kind !== 'descent') });
    const removed = engine.replaceTopology(noRuns, 2);
    expect(removed.waitingGuestIds.length).toBeGreaterThan(0);
    const restored = engine.replaceTopology(network, 3);
    expect(restored.reroutedGuestIds.length).toBeGreaterThan(0);
    expect(engine.currentTick).toBe(1_000);
    expect(engine.snapshot().guests.map((guest) => guest.id)).toEqual(originalIds);
    for (const guest of engine.snapshot().guests.filter((candidate) => restored.reroutedGuestIds.includes(candidate.id))) {
      expect(guest.status).toBe('travelling-to-lift');
      expect(guest.routeStateReason).toBeUndefined();
      expect(engine.getItinerary(guest.id)).toBeDefined();
    }
  });

  it('treats the already-applied topology revision as an idempotent no-op', () => {
    const { network, engine } = setup();
    engine.advanceTo(100);
    const before = engine.snapshot();
    const migration = engine.replaceTopology(network, engine.topologyRevision);
    expect(migration).toMatchObject({ preservedGuestIds: [], reroutedGuestIds: [], waitingGuestIds: [] });
    expect(engine.snapshot().checksum).toBe(before.checksum);
  });
});
