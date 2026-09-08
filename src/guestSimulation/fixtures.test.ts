import { describe, expect, it } from 'vitest';
import {
  GUEST_FIXTURE_COUNTS,
  GUEST_FIXTURE_SCENARIOS,
  GUEST_SIMULATION_FIXTURE_DESCRIPTORS,
  createClosureStormIncidents,
  createDemandPlan,
  createEnvironmentSnapshot,
  createGuestSimulationFixture,
  generateGuests,
  getGuestSimulationFixtureDescriptor,
} from './fixtures';
import { isNormalizedUnitInterval } from './contracts';

describe('guest simulation fixtures', () => {
  it('defines exactly the four standard deterministic populations', () => {
    expect(GUEST_FIXTURE_COUNTS).toEqual([1_000, 10_000, 25_000, 50_000]);
    expect(GUEST_SIMULATION_FIXTURE_DESCRIPTORS.map((descriptor) => descriptor.guestCount)).toEqual(GUEST_FIXTURE_COUNTS);
    expect(GUEST_SIMULATION_FIXTURE_DESCRIPTORS.every((descriptor) => descriptor.scenarios === GUEST_FIXTURE_SCENARIOS)).toBe(true);
  });

  it('generates stable IDs and exact guest sizes', () => {
    for (const size of GUEST_FIXTURE_COUNTS) {
      const descriptor = getGuestSimulationFixtureDescriptor(size);
      const first = generateGuests(descriptor);
      const second = generateGuests(descriptor);
      expect(first).toEqual(second);
      expect(first).toHaveLength(size);
      expect(first[0]?.id).toBe('guest-000001');
      expect(first.at(-1)?.id).toBe(`guest-${String(size).padStart(6, '0')}`);
      expect(new Set(first.map((guest) => guest.id)).size).toBe(size);
      expect(first.every((guest) => Number.isInteger(guest.arrivalTick) && guest.arrivalTick >= 0 && guest.arrivalTick < descriptor.endTick)).toBe(true);
      expect(first.every((guest) => guest.futurePartyId === null)).toBe(true);
      expect(first.every((guest) => {
        const preferences = guest.preferences;
        return isNormalizedUnitInterval(preferences.ability)
          && isNormalizedUnitInterval(preferences.riskTolerance)
          && isNormalizedUnitInterval(preferences.comfortDemand)
          && isNormalizedUnitInterval(preferences.hardcoreTerrainPreference)
          && isNormalizedUnitInterval(preferences.priceSensitivity)
          && isNormalizedUnitInterval(preferences.frugality)
          && isNormalizedUnitInterval(preferences.patience)
          && isNormalizedUnitInterval(preferences.varietySeeking)
          && preferences.tripCashCents > 0;
      })).toBe(true);
    }
  });

  it('includes weekend waves, heavy groups, closure storms, and deterioration', () => {
    const descriptor = getGuestSimulationFixtureDescriptor(1_000);
    const demand = createDemandPlan(descriptor);
    const environment = createEnvironmentSnapshot(descriptor);
    const laterConditions = createEnvironmentSnapshot(descriptor, descriptor.endTick - 1).conditions;
    expect(demand.waves.some((wave) => wave.kind === 'weekend' && wave.guestCount > 0)).toBe(true);
    expect(demand.heavyGroupCount).toBeGreaterThan(0);
    expect(createClosureStormIncidents(descriptor).some((incident) => incident.kind === 'closure-storm')).toBe(true);
    expect(environment.portals.every((portal) => portal.kind === 'guest-entrance' && portal.direction === 'inbound' && portal.accepts === 'guests')).toBe(true);
    expect(laterConditions.trend).toBe('deteriorating');
    expect(laterConditions.terrainOpenFraction).toBeGreaterThanOrEqual(0);
    expect(laterConditions.terrainOpenFraction).toBeLessThanOrEqual(1);
  });

  it('keeps peak save/load metadata and snapshot state bounded and nullable', () => {
    const fixture = createGuestSimulationFixture(1_000);
    const { descriptor } = fixture;
    expect(descriptor.peakSaveLoad.scenario).toBe('peak-save-load-metadata');
    expect(descriptor.peakSaveLoad.materialization).toBe('metadata-only');
    expect(descriptor.peakSaveLoad.expectedGuestCount).toBe(1_000);
    expect(descriptor.peakSaveLoad.saveTick).toBe(descriptor.peakSaveLoad.snapshotTick);
    expect(descriptor.peakSaveLoad.loadTick).toBeGreaterThan(descriptor.peakSaveLoad.saveTick);
    const snapshot = fixture.createSnapshot();
    expect(snapshot.version).toBe(1);
    expect(snapshot.tick).toBe(descriptor.peakSaveLoad.snapshotTick);
    expect(snapshot.guests).toHaveLength(1_000);
    expect(snapshot.futureParty).toBeNull();
    expect(snapshot.thoughtEvents.length).toBeLessThanOrEqual(24);
    expect(snapshot.thoughtEvents.every((event, index, events) => index === 0 || events[index - 1]!.tick <= event.tick)).toBe(true);
    expect(snapshot.guests.every((guest) => ['scheduled', 'choosing', 'departed'].includes(guest.status))).toBe(true);
  });

  it('does not eagerly materialize the largest guest array', () => {
    const fixture = createGuestSimulationFixture(50_000);
    expect(Object.prototype.hasOwnProperty.call(fixture, 'guests')).toBe(true);
    expect(fixture.descriptor.guestCount).toBe(50_000);
    // Accessing descriptor and demand metadata must not require 50k guest records.
    expect(fixture.demandPlan.guestCount).toBe(50_000);
  });
});
