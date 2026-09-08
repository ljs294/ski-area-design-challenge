import { describe, expect, it } from 'vitest';
import { createDailyGuestRoster, createDefaultGuestSimulationNetwork, createGuestSimulationEngine } from './engine';
import { replayStateFromGuestSimulationEngine, restoreGuestSimulationEngine } from './enginePersistence';
import { createFacility } from './facilities';

const portal = { version: 1 as const, id: 'entrance', kind: 'guest-entrance' as const, type: 'guest-entrance' as const,
  semantics: 'guest-entrance' as const, direction: 'inbound' as const, accepts: 'guests' as const, label: 'Entrance',
  capacityGuestsPerTick: 50, openFromTick: 0, openUntilTick: 43_200 };

describe('remaining guest phase integration', () => {
  it('publishes amenities, access, lodging, memory, and compact scaling state together', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'phase-5-7', guestCount: 12, portals: network.portals, endTick: 3_600 });
    const snapshot = createGuestSimulationEngine({ network, roster }).advanceTo(3_600);
    expect(snapshot.phase5to7.amenities.tick).toBe(snapshot.tick);
    expect(snapshot.phase5to7.amenities.metrics.requests).toBeGreaterThan(0);
    expect(snapshot.phase5to7.access.ledger.conservation.occupants).toBe(snapshot.metrics.population);
    expect(snapshot.phase5to7.lodging.properties).toHaveLength(1);
    expect(snapshot.phase5to7.repeatVisitors.records).toHaveLength(snapshot.metrics.departed);
    expect(snapshot.phase5to7.scaling.publicationBytes).toBe(snapshot.metrics.population * 19);
    expect(snapshot.phase5to7.scaling.publicationChecksum).toBeGreaterThan(0);
  });

  it('restores a checkpoint with identical remaining-phase state', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'phase-5-7-replay', guestCount: 8, portals: network.portals, endTick: 3_600 });
    const facility = createFacility({ id: 'inventory-cafe', label: 'Inventory cafe', kind: 'cafe', operating: true,
      quality: 0.8, comfort: 0.8, schedule: { openFromTick: 0, openUntilTick: 3_600 },
      entrances: [{ id: 'door', nodeId: network.nodes[0]!.id, accessSeconds: 0 }], services: [{ id: 'snack',
        label: 'Snack', kind: 'meal', priceCents: 0, serviceSeconds: 1, capacity: 8, queueCapacity: 20,
        quality: 0.8, comfort: 0.8, restores: { hunger: 0.3 }, inventory: { enabled: true, itemId: 'snack',
          capacityUnits: 100, availableUnits: 100 } }] });
    const engine = createGuestSimulationEngine({ network, roster, phase5to7: { facilities: [facility] } });
    engine.advanceTo(3_600);
    const expected = engine.snapshot();
    const restored = restoreGuestSimulationEngine(replayStateFromGuestSimulationEngine(engine)).snapshot();
    expect(restored.checksum).toBe(expected.checksum);
    expect(restored.phase5to7).toEqual(expected.phase5to7);
  });
});
