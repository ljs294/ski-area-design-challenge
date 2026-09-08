import { describe, expect, it } from 'vitest';
import { GUEST_SIMULATION_CONTRACT_VERSION, type GuestPortal } from './contracts.ts';
import { createDailyGuestRoster, createDefaultGuestSimulationNetwork, createGuestSimulationEngine } from './engine.ts';

const portal: GuestPortal = { version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'phase4-entrance',
  kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance', direction: 'inbound',
  accepts: 'guests', label: 'Phase 4 entrance', capacityGuestsPerTick: 50, openFromTick: 0, openUntilTick: 10_000 };

describe('Phase 4 engine integration', () => {
  it('runs traversal injuries through patrol with exclusive outcomes and conserved responders', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'phase4-engine-integration', guestCount: 300,
      portals: network.portals, startTick: 0, endTick: 2_400 });
    const engine = createGuestSimulationEngine({ network, roster, runId: 'phase4-engine' });
    const snapshot = engine.advanceTo(3_600);
    expect(snapshot.safety.metrics.traversalsStarted).toBeGreaterThan(0);
    expect(snapshot.safety.metrics.incidentCount).toBeGreaterThan(0);
    expect(snapshot.safety.traversals.every((traversal) => traversal.outcome === 'normal'
      || traversal.outcome === 'injury')).toBe(true);
    expect(snapshot.safety.guestIncidents).toHaveLength(snapshot.safety.metrics.incidentCount);
    expect(new Set(snapshot.safety.guestIncidents.map((incident) => incident.id)).size)
      .toBe(snapshot.safety.guestIncidents.length);
    expect(snapshot.safety.patrol.metrics.responderCapacityConserved).toBe(true);
    expect(snapshot.safety.patrol.metrics.assignedResponderUnits)
      .toBeLessThanOrEqual(snapshot.safety.patrol.metrics.responderCapacityUnits);
    expect(snapshot.safety.metrics.activeIncidents).toBe(0);
    expect(snapshot.thoughtAggregation.byReason.find((reason) => reason.reasonCode === 'injury')?.count)
      .toBeGreaterThanOrEqual(snapshot.safety.metrics.incidentCount);
  });

  it('is invariant to one large advance versus chunked worker-style advances', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'phase4-chunking', guestCount: 120,
      portals: network.portals, startTick: 0, endTick: 1_200 });
    const large = createGuestSimulationEngine({ network, roster, runId: 'phase4-chunking' });
    const chunked = createGuestSimulationEngine({ network, roster, runId: 'phase4-chunking' });
    const expected = large.advanceTo(1_800);
    for (let tick = 60; tick <= 1_800; tick += 60) chunked.advanceTo(tick);
    const actual = chunked.snapshot();
    expect(actual.safety).toEqual(expected.safety);
    expect(actual.checksum).toBe(expected.checksum);
  });
});
