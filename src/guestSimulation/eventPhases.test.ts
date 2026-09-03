import { describe, expect, it } from 'vitest';
import { EventCalendar } from './eventCalendar';
import {
  GUEST_EVENT_PHASE,
  GUEST_EVENT_PHASE_ORDER,
  guestEventOrderKey,
  guestEventPhaseRank,
  scheduleGuestEvent,
} from './eventPhases';

describe('guest simulation same-second event phases', () => {
  it('exports the canonical eight-phase order and contiguous ranks', () => {
    expect(GUEST_EVENT_PHASE_ORDER).toEqual([
      'environment-revisions',
      'due-travel-service-completions',
      'cancellation-incidents',
      'capacity-dispatch',
      'bookings-arrivals',
      'thresholds-departures-route-failures',
      'decisions-enqueue',
      'append-outputs-metrics-snapshot',
    ]);
    expect(GUEST_EVENT_PHASE_ORDER.map(guestEventPhaseRank)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
    expect(GUEST_EVENT_PHASE.capacityDispatch).toBe('capacity-dispatch');
  });

  it('dispatches capacity before bookings/arrivals at the same second', () => {
    const calendar = new EventCalendar<string>();
    // Deliberately make the arrival owner sort first alphabetically: phase
    // rank must still be the first tie-breaker after dueTick.
    scheduleGuestEvent(calendar, {
      dueTick: 30, phase: GUEST_EVENT_PHASE.bookingsArrivals,
      ownerId: 'a-booking', guestId: 'guest-1', payload: 'arrival',
    });
    scheduleGuestEvent(calendar, {
      dueTick: 30, phase: GUEST_EVENT_PHASE.capacityDispatch,
      ownerId: 'z-lift', guestId: 'guest-1', payload: 'capacity',
    });
    const events = calendar.advanceTo(30);
    expect(events.map((event) => event.payload)).toEqual(['capacity', 'arrival']);
    expect(events.map((event) => event.phase)).toEqual(['capacity-dispatch', 'bookings-arrivals']);
  });

  it('uses insertion sequence immediately after owner and guest identity', () => {
    const calendar = new EventCalendar<string>();
    scheduleGuestEvent(calendar, {
      dueTick: 4, phase: GUEST_EVENT_PHASE.decisionsEnqueue,
      ownerId: 'owner', guestId: 'guest', payload: 'first',
    });
    scheduleGuestEvent(calendar, {
      dueTick: 4, phase: GUEST_EVENT_PHASE.decisionsEnqueue,
      ownerId: 'owner', guestId: 'guest', payload: 'second',
    });
    expect(calendar.advanceTo(4).map((event) => event.payload)).toEqual(['first', 'second']);
    expect(guestEventOrderKey({
      dueTick: 4, phase: GUEST_EVENT_PHASE.decisionsEnqueue,
      ownerId: 'owner', guestId: 'guest',
    }, 12)).toEqual({
      dueTick: 4,
      phase: 'decisions-enqueue',
      ownerId: 'owner',
      guestId: 'guest',
      insertionSequence: 12,
    });
  });
});
