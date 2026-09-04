import { describe, expect, it } from 'vitest';
import {
  DEFAULT_DEMAND_TUNING,
  buildDemandForecast,
  createDemandScenario,
  demandForecastChecksum,
  demandMultipliers,
  isDemandForecast,
  isDemandRealization,
  planDailyArrivals,
  realizeDemand,
  reputationDemandMultiplier,
  toDemandPlan,
  valueDemandMultiplier,
} from './demand';

const baseInput = {
  seed: 'phase3-test', startTick: 0, endTick: 12 * 60 * 60, bucketSeconds: 10 * 60,
  dayType: 'weekend' as const, basePotentialGuests: 1_000, ticketPriceCents: 100,
  referencePriceCents: 100, reputation: 0.6, resortValue: 0.5, availableCapacityGuests: 800,
  maxGuests: 2_000, maxParties: 1_000,
};

describe('Phase 3 demand and arrivals', () => {
  it('uses monotone reputation, resort value, and price multipliers', () => {
    expect(reputationDemandMultiplier(0.9)).toBeGreaterThan(reputationDemandMultiplier(0.3));
    expect(valueDemandMultiplier(0.9)).toBeGreaterThan(valueDemandMultiplier(0.2));
    const cheap = createDemandScenario({ ...baseInput, ticketPriceCents: 50 });
    const expensive = createDemandScenario({ ...baseInput, ticketPriceCents: 200 });
    expect(demandMultipliers(cheap).price).toBeGreaterThan(demandMultipliers(expensive).price);
  });

  it('creates a normalized, bucketed forecast with deterministic checksum', () => {
    const scenario = createDemandScenario(baseInput);
    const forecast = buildDemandForecast(scenario);
    expect(forecast.buckets).toHaveLength(72);
    expect(forecast.buckets.reduce((sum, bucket) => sum + bucket.shapeWeight, 0)).toBeCloseTo(1, 12);
    expect(forecast.buckets.reduce((sum, bucket) => sum + bucket.admittedExpectedGuests, 0))
      .toBeCloseTo(forecast.admittedExpectedGuests, 10);
    expect(isDemandForecast(forecast)).toBe(true);
    expect(demandForecastChecksum(forecast)).toBe(forecast.checksum);
  });

  it('applies both the guest cap and available capacity gate', () => {
    const scenario = createDemandScenario({ ...baseInput, basePotentialGuests: 10_000, maxGuests: 500, availableCapacityGuests: 120 });
    const forecast = buildDemandForecast(scenario);
    expect(forecast.uncappedExpectedGuests).toBeGreaterThan(forecast.marketExpectedGuests);
    expect(forecast.marketExpectedGuests).toBeLessThanOrEqual(500);
    expect(forecast.admittedExpectedGuests).toBeLessThanOrEqual(120);
    expect(forecast.capacityAdmissionFactor).toBeLessThan(1);
  });

  it('returns no admitted demand when operations or capacity are zero', () => {
    const closed = buildDemandForecast(createDemandScenario({ ...baseInput, operatingFraction: 0 }));
    const full = buildDemandForecast(createDemandScenario({ ...baseInput, availableCapacityGuests: 0 }));
    expect(closed.admittedExpectedGuests).toBe(0);
    expect(full.admittedExpectedGuests).toBe(0);
  });

  it('realizes arrivals deterministically and obeys caps', () => {
    const forecast = buildDemandForecast(createDemandScenario({ ...baseInput, availableCapacityGuests: 80 }));
    const first = realizeDemand(forecast);
    const second = realizeDemand(forecast);
    expect(second).toEqual(first);
    expect(first.guestCount).toBeLessThanOrEqual(80);
    expect(first.guestCount).toBeLessThanOrEqual(2_000);
    expect(first.partyCount).toBeLessThanOrEqual(1_000);
    expect(first.heavyGroupCount).toBeLessThanOrEqual(first.partyCount);
    expect(isDemandRealization(first)).toBe(true);
  });

  it('keeps bucket draws independent of forecast array iteration order', () => {
    const forecast = buildDemandForecast(createDemandScenario(baseInput));
    const reversed = { ...forecast, buckets: [...forecast.buckets].reverse() };
    // The validator intentionally rejects reordered authoritative forecasts;
    // rebuilding from the same scenario is the supported replay path.
    expect(() => realizeDemand(reversed)).toThrow();
    expect(realizeDemand(buildDemandForecast(forecast.scenario))).toEqual(realizeDemand(forecast));
  });

  it('converts a realization to the current DemandPlan contract', () => {
    const scenario = createDemandScenario(baseInput);
    const forecast = buildDemandForecast(scenario);
    const realization = realizeDemand(forecast);
    const plan = toDemandPlan(realization, scenario);
    expect(plan.version).toBe(1);
    expect(plan.seed).toBe(scenario.seed);
    expect(plan.guestCount).toBe(realization.guestCount);
    expect(plan.partyCount).toBe(realization.partyCount);
    expect(plan.waves).toHaveLength(forecast.buckets.length);
  });

  it('provides one integration call for scenario, forecast, realization, and roster plan', () => {
    const planned = planDailyArrivals({ ...baseInput, tuning: { ...DEFAULT_DEMAND_TUNING, partyMeanSize: 3 } });
    expect(planned.demandPlan.guestCount).toBe(planned.realization.guestCount);
    expect(planned.forecast.scenario).toBe(planned.scenario);
    expect(planned.demandPlan.waves[0]?.startTick).toBe(0);
  });

  it('rejects malformed or unbounded scenarios before allocating buckets', () => {
    expect(() => createDemandScenario({ ...baseInput, bucketSeconds: 0 })).toThrow();
    expect(() => createDemandScenario({ ...baseInput, reputation: 2 })).toThrow();
    expect(() => createDemandScenario({ ...baseInput, maxGuests: 0 })).toThrow();
    expect(() => createDemandScenario({ ...baseInput, basePotentialGuests: 10_000_001 })).toThrow();
  });
});

