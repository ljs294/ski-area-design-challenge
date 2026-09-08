import { describe, expect, it } from 'vitest';
import { createDailyGuestRoster, createDefaultGuestSimulationNetwork } from './engine.ts';
import { createPrepaidTicketFinance } from './ticketFinance.ts';
import {
  PHASE_3_ECONOMY_FORMULAS,
  blendedReputationScore,
  closePhase3Economy,
  createPhase3Economy,
  createReputationProfile,
  createSyntheticPeerBaseline,
  isPhase3EconomySnapshot,
  phase3EconomySnapshotChecksum,
  recordVisitOutcome,
  type Phase3EconomySnapshot,
} from './phase3Economy.ts';

function empty(dayId = 'day-1'): Phase3EconomySnapshot {
  return createPhase3Economy({ dayId, maximumVisitOutcomes: 8 });
}

function outcome(dayId: string, guestId: string, segment: 'budget' | 'standard' | 'premium' | 'luxury', satisfaction: number, tick = 100) {
  return { dayId, guestId, segment, satisfaction, tick,
    dimensionScores: { terrain: satisfaction, comfort: satisfaction, value: satisfaction, service: satisfaction, safety: satisfaction } } as const;
}

describe('Phase 3 economy/reputation day boundary', () => {
  it('creates frozen opening reputation and display-only peer comparison data', () => {
    const peer = createSyntheticPeerBaseline('economy-peer-seed');
    const economy = createPhase3Economy({ dayId: 'day-1', syntheticPeerBaseline: peer });
    expect(economy.openingReputation.hype.overall.all).toBe(5_000);
    expect(economy.openingReputation.legacy.overall.all).toBe(5_000);
    expect(peer.displayOnly).toBe(true);
    expect(createSyntheticPeerBaseline('economy-peer-seed')).toEqual(peer);
    expect(isPhase3EconomySnapshot(economy)).toBe(true);
    expect(Object.isFrozen(economy)).toBe(true);
  });

  it('records exactly one outcome per guest/day and leaves opening state unchanged', () => {
    const before = empty();
    const first = recordVisitOutcome(before, outcome('day-1', 'guest-1', 'premium', 0.9));
    expect(first.openingReputation).toBe(before.openingReputation);
    expect(first.visitOutcomes).toHaveLength(1);
    expect(recordVisitOutcome(first, outcome('day-1', 'guest-1', 'premium', 0.9))).toBe(first);
    expect(() => recordVisitOutcome(first, outcome('day-1', 'guest-1', 'premium', 0.1))).toThrow(/different data|guest\/day/i);
    expect(() => recordVisitOutcome(first, outcome('day-2', 'guest-2', 'standard', 0.9))).toThrow(/match/i);
  });

  it('maps experience and safety signals into dimension reason deltas', () => {
    const first = recordVisitOutcome(empty(), {
      ...outcome('day-1', 'guest-1', 'standard', 0.9),
      signals: [
        { kind: 'experience', eventId: 'experience:guest-1:1', guestId: 'guest-1', tick: 200, satisfaction: 1, reasonCode: 'terrain-mismatch' },
        { kind: 'safety', eventId: 'safety:guest-1:1', guestId: 'guest-1', tick: 200, severity: 'major', outcome: 'resolved', responseSeconds: 600 },
      ],
    });
    const record = first.visitOutcomes[0]!;
    expect(record.deltaByDimensionBps.terrain).toBeGreaterThan(0);
    expect(record.deltaByDimensionBps.safety).toBeLessThan(record.deltaByDimensionBps.service);
    expect(first.metrics.signalCount).toBe(2);
    expect(() => recordVisitOutcome(first, {
      ...outcome('day-1', 'guest-2', 'standard', 0.9),
      signals: [{ kind: 'experience', eventId: 'experience:guest-1:1', guestId: 'guest-2', tick: 200, satisfaction: 1 }],
    })).toThrow(/signal guest|already used/i);
  });

  it('uses fast hype and slow legacy updates at close, with no same-day feedback', () => {
    const before = empty();
    const openScore = blendedReputationScore(before.openingReputation, 'standard');
    const withOutcome = recordVisitOutcome(before, outcome('day-1', 'guest-1', 'standard', 1));
    expect(blendedReputationScore(withOutcome.openingReputation, 'standard')).toBe(openScore);
    const closed = closePhase3Economy(withOutcome, { closeId: 'close:day-1', closedTick: 86_400 });
    const next = closed.closing!.nextDayReputation;
    const hypeChange = next.hype.overall.standard - before.openingReputation.hype.overall.standard;
    const legacyChange = next.legacy.overall.standard - before.openingReputation.legacy.overall.standard;
    expect(hypeChange).toBeGreaterThan(legacyChange);
    expect(hypeChange).toBe(Math.round(PHASE_3_ECONOMY_FORMULAS.hypeLearningRate * 100));
    expect(legacyChange).toBe(Math.round(PHASE_3_ECONOMY_FORMULAS.legacyLearningRate * 100));
    expect(closed.openingReputation).toBe(before.openingReputation);
    expect(() => recordVisitOutcome(closed, outcome('day-1', 'guest-2', 'standard', 0.8))).toThrow(/after day close/i);
  });

  it('updates all and only observed segments, and leaves unobserved segments unchanged', () => {
    const before = empty();
    const withOutcomes = recordVisitOutcome(recordVisitOutcome(before,
      outcome('day-1', 'guest-budget', 'budget', 0)), outcome('day-1', 'guest-luxury', 'luxury', 1));
    const closed = closePhase3Economy(withOutcomes, { closeId: 'close:day-1', closedTick: 86_400 });
    const next = closed.closing!.nextDayReputation;
    expect(next.hype.overall.budget).toBeLessThan(before.openingReputation.hype.overall.budget);
    expect(next.hype.overall.luxury).toBeGreaterThan(before.openingReputation.hype.overall.luxury);
    expect(next.hype.overall.standard).toBe(before.openingReputation.hype.overall.standard);
    expect(next.legacy.overall.premium).toBe(before.openingReputation.legacy.overall.premium);
  });

  it('closes idempotently and rejects a conflicting close request', () => {
    const closed = closePhase3Economy(empty(), { closeId: 'close:day-1', closedTick: 86_400 });
    expect(closePhase3Economy(closed, { closeId: 'close:day-1', closedTick: 86_400 })).toBe(closed);
    expect(() => closePhase3Economy(closed, { closeId: 'close:other', closedTick: 86_400 })).toThrow(/already closed/i);
    expect(() => closePhase3Economy(closed, { closeId: 'close:day-1', closedTick: 86_401 })).toThrow(/already closed/i);
  });

  it('includes ticket revenue by segment without allowing it to alter same-day reputation', () => {
    const network = createDefaultGuestSimulationNetwork([{
      version: 1, id: 'entrance', kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance',
      direction: 'inbound', accepts: 'guests', label: 'Entrance', capacityGuestsPerTick: 4, openFromTick: 0, openUntilTick: 10_000,
    }]);
    const roster = createDailyGuestRoster({ seed: 'economy-ticket', guestCount: 4, portals: network.portals, endTick: 1_000 });
    const finance = createPrepaidTicketFinance({ dayId: 'day-1', recognizedTick: 0, ticketPriceCents: 10_000, guests: roster.guests });
    const economy = createPhase3Economy({ dayId: 'day-1', ticketFinance: finance });
    expect(economy.metrics.ticketCount).toBe(4);
    expect(economy.metrics.ticketRevenueCents).toBe(40_000);
    expect(Object.values(economy.metrics.ticketRevenueBySegment).reduce((sum, value) => sum + value, 0)).toBe(40_000);
    const closed = closePhase3Economy(economy, { closeId: 'close:day-1', closedTick: 86_400 });
    expect(closed.closing!.ticketFinanceChecksum).toBe(finance.checksum);
  });

  it('round-trips through JSON and validates deterministic checksums', () => {
    const withOutcome = recordVisitOutcome(empty(), outcome('day-1', 'guest-1', 'standard', 0.7));
    const closed = closePhase3Economy(withOutcome, { closeId: 'close:day-1', closedTick: 86_400 });
    const restored = JSON.parse(JSON.stringify(closed)) as Phase3EconomySnapshot;
    expect(isPhase3EconomySnapshot(restored)).toBe(true);
    expect(phase3EconomySnapshotChecksum(restored)).toBe(restored.checksum);
    expect(restored).toEqual(closed);
  });

  it('allows custom opening layers while keeping score bounds', () => {
    const profile = createReputationProfile({ hype: { overall: { premium: 8_500 } }, legacy: { overall: { premium: 2_500 } } });
    const economy = createPhase3Economy({ dayId: 'day-custom', openingReputation: profile });
    expect(economy.openingReputation.hype.overall.premium).toBe(8_500);
    expect(economy.openingReputation.legacy.overall.premium).toBe(2_500);
    expect(blendedReputationScore(profile, 'premium')).toBe(0.46);
    expect(() => createReputationProfile({ hype: { overall: { all: 10_001 } } })).toThrow(/score bounds/i);
  });
});
