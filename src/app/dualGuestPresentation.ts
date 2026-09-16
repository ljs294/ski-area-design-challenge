import type { DualPublication } from '../dualClock/model';
import type { GuestVibePresentation } from './guestVibePresentation';

export function dualGuestPresentation(publication: DualPublication | null): GuestVibePresentation {
  const flow = publication?.flow;
  const guests = publication?.guests ?? [];
  const selected = publication?.selected;
  const visible = selected && !guests.some(g => g.id === selected.id) ? [...guests, selected] : guests;
  return { summary: { guestCount: flow?.admitted ?? 0, activeGuestCount: flow?.active ?? 0,
    positiveThoughtCount: guests.filter(g => g.satisfaction >= 0.7).length,
    neutralThoughtCount: guests.filter(g => g.satisfaction >= 0.4 && g.satisfaction < 0.7).length,
    negativeThoughtCount: guests.filter(g => g.satisfaction < 0.4).length,
    amenityRevenueCents: flow?.amenityRevenueCents ?? 0,
    economy: { arrivedGuests: flow?.admitted ?? 0, ticketRevenueCents: flow?.ticketRevenueCents ?? 0, reconciled: true } },
    reasonAggregates: [], topThoughts: guests.slice(0, 4).map(g => ({ text: g.thought, reasonCode: g.status,
      sentiment: g.satisfaction >= 0.7 ? 'positive' : g.satisfaction >= 0.4 ? 'neutral' : 'negative' })),
    guests: visible.map(g => ({ ...g, label: `Guest ${g.id.replace('representative-', '')}`, latestThought: g.thought,
      sentiment: g.satisfaction >= 0.7 ? 'positive' : g.satisfaction >= 0.4 ? 'neutral' : 'negative' })) };
}
