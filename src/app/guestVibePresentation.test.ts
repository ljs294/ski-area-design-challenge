import { describe, expect, it } from 'vitest';
import { createDefaultGuestSimulation } from '../guestSimulation/engine';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import { guestVibePresentation } from './guestVibePresentation';

describe('guestVibePresentation Phase 3 projection', () => {
  it('projects demand, finance, reputation, and reconciliation without recomputing them', () => {
    const snapshot = createDefaultGuestSimulation({ seed: 'vibe-phase3', guestCount: 2 }).snapshot();
    const withEconomy = {
      ...snapshot,
      phase3: {
        demandForecast: { admittedExpectedGuests: 1_200, marketExpectedGuests: 1_350 },
        demandRealization: { guestCount: 1_000 },
        ticketFinance: { ticketPriceCents: 2_500, ticketRevenueCents: 2_500_000, reconciled: true },
        reputation: { scoreBps: 7_400, maximumScoreBps: 10_000 },
        hype: -0.15,
        reconciled: true,
      },
    } as unknown as GuestSimulationEngineSnapshot;

    const result = guestVibePresentation(withEconomy);
    expect(result.summary.economy).toMatchObject({
      expectedGuests: 1_200,
      bookedGuests: 1_000,
      unmetDemand: 150,
      ticketPriceCents: 2_500,
      ticketRevenueCents: 2_500_000,
      reputation: 0.74,
      hype: -0.15,
      reconciled: true,
    });
  });

  it('does not expose a fabricated economic panel for a pre-Phase-3 snapshot', () => {
    const snapshot = createDefaultGuestSimulation({ seed: 'vibe-foundation', guestCount: 1 }).snapshot();
    const { phase3: _phase3, ...prePhase3Snapshot } = snapshot;
    expect(guestVibePresentation(prePhase3Snapshot as GuestSimulationEngineSnapshot).summary.economy).toBeUndefined();
  });

  it('reads the engine-facing phase3Economy projection and its nested metrics', () => {
    const snapshot = createDefaultGuestSimulation({ seed: 'vibe-economy-snapshot', guestCount: 2 }).snapshot();
    const withEconomy = {
      ...snapshot,
      phase3: {
        economy: {
          openingReputation: {
            hype: { overall: { all: 8_000 } },
            legacy: { overall: { all: 6_000 } },
          },
          ticketFinance: {
            ticketPriceCents: 3_000,
            bookedCount: 7,
            reconciled: true,
          },
          metrics: { ticketRevenueCents: 21_000, reconciled: true },
        },
        bookedGuests: 7,
        reconciled: true,
      },
    } as unknown as GuestSimulationEngineSnapshot;

    expect(guestVibePresentation(withEconomy).summary.economy).toMatchObject({
      bookedGuests: 7,
      ticketPriceCents: 3_000,
      ticketRevenueCents: 21_000,
      reputation: 0.67,
      hype: 0.6,
      reconciled: true,
    });
  });
});
