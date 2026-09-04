import { describe, expect, it } from 'vitest';
import { createDailyGuestRoster, createDefaultGuestSimulationNetwork } from './engine.ts';
import { createPrepaidTicketFinance, mergePrepaidTicketFinance } from './ticketFinance.ts';
import type { GuestPortal } from './contracts.ts';

const portal: GuestPortal = { version: 1, id: 'entrance', kind: 'guest-entrance', type: 'guest-entrance',
  semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: 'Entrance',
  capacityGuestsPerTick: 4, openFromTick: 0, openUntilTick: 10_000 };

describe('Phase 3 prepaid ticket finance', () => {
  it('recognizes one ordered integer-cent transaction per booked guest', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'tickets', guestCount: 12, portals: network.portals, endTick: 1_000 });
    const finance = createPrepaidTicketFinance({ dayId: 'day-1', recognizedTick: 0,
      ticketPriceCents: 12_500, guests: roster.guests });
    expect(finance.bookedCount).toBe(12);
    expect(finance.recognizedCount).toBe(12);
    expect(finance.ticketRevenueCents).toBe(150_000);
    expect(finance.reconciled).toBe(true);
    expect(new Set(finance.transactions.map((transaction) => transaction.id)).size).toBe(12);
    expect(finance.entries.map((entry) => entry.sequence)).toEqual([...Array(12).keys()]);
  });

  it('is deterministic, idempotent on merge, and rejects invalid prices', () => {
    const network = createDefaultGuestSimulationNetwork([portal]);
    const roster = createDailyGuestRoster({ seed: 'ticket-idempotence', guestCount: 3, portals: network.portals, endTick: 1_000 });
    const first = createPrepaidTicketFinance({ dayId: 'day-1', recognizedTick: 0, ticketPriceCents: 9_900, guests: roster.guests });
    expect(createPrepaidTicketFinance({ dayId: 'day-1', recognizedTick: 0,
      ticketPriceCents: 9_900, guests: roster.guests })).toEqual(first);
    expect(mergePrepaidTicketFinance(first, first)).toEqual(first);
    expect(() => createPrepaidTicketFinance({ dayId: 'day-1', recognizedTick: 0,
      ticketPriceCents: 0, guests: roster.guests })).toThrow(/positive integer cents/);
  });
});
