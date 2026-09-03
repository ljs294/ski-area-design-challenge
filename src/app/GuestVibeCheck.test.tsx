import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { GuestVibeCheck, type GuestVibeCheckProps } from './GuestVibeCheck';

const baseProps: GuestVibeCheckProps = {
  summary: { guestCount: 1_000, activeGuestCount: 760, positiveThoughtCount: 44,
    neutralThoughtCount: 12, negativeThoughtCount: 5 },
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
});
