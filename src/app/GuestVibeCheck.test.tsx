import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { GuestVibeCheck, type GuestVibeCheckProps } from './GuestVibeCheck';

const baseProps: GuestVibeCheckProps = {
  summary: { guestCount: 1_000, activeGuestCount: 760, positiveThoughtCount: 44,
    neutralThoughtCount: 12, negativeThoughtCount: 5, activeIncidentCount: 2,
    resolvedIncidentCount: 7, patrolQueueCount: 1, safetyRate: 0.987 },
  reasonAggregates: [
    { reasonCode: 'short_queue', label: 'Short queue', count: 44, sentiment: 'positive' },
    { reasonCode: 'closed_trail', label: 'Closed trail', count: 5, sentiment: 'negative' },
  ],
  topThoughts: [{ text: 'That lift is flying!', reasonCode: 'short_queue', sentiment: 'positive', count: 18 }],
  guests: [
    { id: 'guest-001', label: 'Guest 001', status: 'skiing', satisfaction: 0.92, sentiment: 'positive', latestThought: 'That lift is flying!' },
    { id: 'guest-002', label: 'Guest 002', status: 'lift-queue', satisfaction: 0.51, sentiment: 'neutral' },
  ],
  selectedGuestId: 'guest-001',
  onSelectGuest: vi.fn(),
  onClearSelectedGuest: vi.fn(),
};

function render(overrides: Partial<GuestVibeCheckProps> = {}): string {
  return renderToStaticMarkup(<GuestVibeCheck {...baseProps} {...overrides} />);
}

describe('GuestVibeCheck', () => {
  it('renders aggregate counts, sentiment, coded reasons, and top thoughts', () => {
    const html = render();
    expect(html).toContain('Guest vibe check');
    expect(html).toContain('1,000 guests');
    expect(html).toContain('Positive');
    expect(html).toContain('Short queue');
    expect(html).toContain('short_queue');
    expect(html).toContain('That lift is flying!');
    expect(html).toContain('Guest safety summary');
    expect(html).toContain('Patrol queue');
    expect(html).toContain('99%');
  });

  it('bounds the guest list and exposes controlled selection state accessibly', () => {
    const html = render({ maxGuests: 1 });
    expect(html).toContain('Guest 001');
    expect(html).not.toContain('Guest 002');
    expect(html).toContain('Showing 1 of 2 guests.');
    expect(html).toContain('aria-pressed="true"');
    expect(html).toContain('aria-label="Guests available for inspection"');
    expect(html).toContain('Clear guest selection');
  });

  it('keeps empty aggregate and guest states understandable', () => {
    const html = render({ reasonAggregates: [], topThoughts: [], guests: [], selectedGuestId: null });
    expect(html).toContain('No coded thoughts have arrived yet.');
    expect(html).toContain('No representative thoughts yet.');
    expect(html).toContain('No guests are available in this snapshot.');
  });

  it('renders the Phase 3 market, ticket, and reconciliation readout in the existing panel', () => {
    const html = render({ summary: { ...baseProps.summary, economy: {
      ticketPriceCents: 2_500, expectedGuests: 1_240, bookedGuests: 1_000, arrivedGuests: 760,
      unmetDemand: 240, ticketRevenueCents: 2_500_000, reputation: 0.74, hype: -0.12, reconciled: true,
    } } });
    expect(html).toContain('Market and finance');
    expect(html).toContain('1,240');
    expect(html).toContain('$25.00');
    expect(html).toContain('$25,000.00');
    expect(html).toContain('Reconciled');
    expect(html).toContain('Ticket price is locked for this active day');
  });
});
