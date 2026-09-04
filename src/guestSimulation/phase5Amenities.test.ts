import { describe, expect, it } from 'vitest';
import type { Guest, GuestPreferences } from './contracts.ts';
import { createFacility, type FacilityContract } from './facilities.ts';
import {
  createPhase5Amenities,
  createPhase5AmenitiesFromSnapshot,
  isPhase5AmenitiesSnapshot,
  phase5AmenitiesSnapshotChecksum,
} from './phase5Amenities.ts';

const preferences: GuestPreferences = {
  experience: 'intermediate', abilityBand: 'intermediate', ability: 0.5, ageBand: 'adult', wantsLessons: false,
  budgetCents: 5_000, economicSegment: 'standard', tripCashCents: 5_000, riskTolerance: 0.5,
  comfortDemand: 0.7, hardcoreTerrainPreference: 0.4, priceSensitivity: 0.3, frugality: 0.2,
  patience: 0.7, varietySeeking: 0.5,
};

function guest(id: string, walletCents = 5_000): Guest {
  return { id, partyId: `party-${id}`, ordinal: 0, arrivalTick: 0, plannedDepartureTick: 2_000,
    portalId: 'entrance', preferences: { ...preferences, budgetCents: walletCents, tripCashCents: walletCents }, futurePartyId: null };
}

function facility(): FacilityContract {
  return createFacility({ id: 'base-cafe', label: 'Base Cafe', kind: 'cafe', operating: true, quality: 0.85, comfort: 0.8,
    entrances: [{ id: 'base-door', nodeId: 'base-node', accessSeconds: 30 }], schedule: { openFromTick: 0, openUntilTick: 10_000 },
    services: [{ id: 'meal', label: 'Hot meal', kind: 'meal', priceCents: 800, serviceSeconds: 120, capacity: 1, queueCapacity: 1,
      quality: 0.9, comfort: 0.9, restores: { hunger: 0.9, warmth: 0.25 }, inventory: { enabled: true, itemId: 'meal', capacityUnits: 3, availableUnits: 3 } },
    { id: 'restroom', label: 'Restroom', kind: 'restroom', priceCents: 0, serviceSeconds: 30, capacity: 1, queueCapacity: 4,
      quality: 0.7, comfort: 0.5, restores: { restroom: 1 } }] });
}

describe('Phase 5 amenities and comfort', () => {
  it('chooses an affordable meal for a wealthy hungry guest', () => {
    const runtime = createPhase5Amenities({ facilities: [facility()], guests: [{ guest: guest('wealthy'), needs: { hunger: 0.95 } }] });
    const choice = runtime.chooseForGuest('wealthy');
    expect(choice?.candidate.serviceId).toBe('meal');
    const result = runtime.chooseAndRequest('wealthy', 'order:wealthy:meal');
    expect(result?.ok).toBe(true);
    expect(runtime.guest('wealthy')?.walletCents).toBe(4_200);
    const completed = runtime.advanceTo(120);
    expect(completed.requests[0]?.status).toBe('completed');
    expect(completed.guests[0]?.needs.hunger).toBeLessThan(0.2);
  });

  it('declines a poor guest without changing cash or inventory', () => {
    const runtime = createPhase5Amenities({ facilities: [facility()], guests: [{ guest: guest('poor', 100), needs: { hunger: 0.95 } }] });
    const before = runtime.guest('poor')!;
    const result = runtime.requestService({ requestId: 'order:poor:meal', guestId: 'poor', facilityId: 'base-cafe', serviceId: 'meal' });
    expect(result.ok).toBe(false);
    expect((result as { reason: string }).reason).toBe('unaffordable');
    expect(runtime.guest('poor')?.walletCents).toBe(before.walletCents);
    expect(runtime.snapshot().facilities[0]!.services[0]!.inventory?.availableUnits).toBe(3);
  });

  it('conserves wallet and enabled inventory through completion and JSON restore', () => {
    const runtime = createPhase5Amenities({ facilities: [facility()], guests: [{ guest: guest('buyer'), needs: { hunger: 1 } }] });
    expect(runtime.requestService({ requestId: 'order:buyer', guestId: 'buyer', facilityId: 'base-cafe', serviceId: 'meal' }).ok).toBe(true);
    const snapshot = runtime.advanceTo(120);
    expect(snapshot.metrics.revenueCents).toBe(800);
    expect(snapshot.facilities[0]!.services[0]!.inventory?.availableUnits).toBe(2);
    expect(snapshot.guests[0]!.inventory.meal).toBe(1);
    expect(snapshot.guests[0]!.walletCents + snapshot.metrics.revenueCents).toBe(5_000);
    const restored = JSON.parse(JSON.stringify(snapshot));
    expect(isPhase5AmenitiesSnapshot(restored)).toBe(true);
    expect(phase5AmenitiesSnapshotChecksum(restored)).toBe(snapshot.checksum);
    expect(createPhase5AmenitiesFromSnapshot(restored).snapshot()).toEqual(snapshot);
  });

  it('enforces service capacity and FIFO queue order', () => {
    const runtime = createPhase5Amenities({ facilities: [facility()], guests: [
      { guest: guest('guest-a'), needs: { hunger: 1 } }, { guest: guest('guest-b'), needs: { hunger: 1 } },
      { guest: guest('guest-c'), needs: { hunger: 1 } },
    ] });
    expect(runtime.requestService({ requestId: 'order-a', guestId: 'guest-a', facilityId: 'base-cafe', serviceId: 'meal' }).ok).toBe(true);
    expect(runtime.requestService({ requestId: 'order-b', guestId: 'guest-b', facilityId: 'base-cafe', serviceId: 'meal' }).ok).toBe(true);
    const third = runtime.requestService({ requestId: 'order-c', guestId: 'guest-c', facilityId: 'base-cafe', serviceId: 'meal' });
    expect(third.ok).toBe(false);
    expect((third as { reason: string }).reason).toBe('queue-full');
    const first = runtime.advanceTo(120);
    expect(first.requests.find((request) => request.requestId === 'order-a')?.status).toBe('completed');
    expect(first.requests.find((request) => request.requestId === 'order-b')?.status).toBe('service');
    const second = runtime.advanceTo(240);
    expect(second.requests.find((request) => request.requestId === 'order-b')?.status).toBe('completed');
  });

  it('produces the same checked snapshot when advanced in chunks', () => {
    const options = { facilities: [facility()], guests: [
      { guest: guest('chunk-a'), needs: { hunger: 0.8, thirst: 0.3 } },
      { guest: guest('chunk-b'), needs: { hunger: 0.8 } },
    ] } as const;
    const large = createPhase5Amenities(options);
    const chunked = createPhase5Amenities(options);
    for (const runtime of [large, chunked]) {
      runtime.requestService({ requestId: 'order:a', guestId: 'chunk-a', facilityId: 'base-cafe', serviceId: 'meal' });
      runtime.requestService({ requestId: 'order:b', guestId: 'chunk-b', facilityId: 'base-cafe', serviceId: 'meal' });
    }
    const expected = large.advanceTo(500);
    for (const tick of [30, 60, 120, 240, 500]) chunked.advanceTo(tick);
    expect(chunked.snapshot()).toEqual(expected);
    expect(chunked.snapshot().checksum).toBe(expected.checksum);
  });
});
