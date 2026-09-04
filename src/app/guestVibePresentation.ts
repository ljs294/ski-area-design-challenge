import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import type { GuestVibeCheckProps, GuestVibeSentiment } from './GuestVibeCheck';

const REASON_LABELS: Readonly<Record<string, string>> = Object.freeze({
  arrival: 'Arrivals', queueing: 'Lift queues', riding: 'Lift rides', skiing: 'Ski runs', waiting: 'Waiting',
  leaving: 'Departures', 'positive-experience': 'Great experiences', 'low-satisfaction': 'Low satisfaction',
  'expectation-shortfall': 'Missed expectations', crowding: 'Crowding', 'long-wait': 'Long waits',
  'poor-conditions': 'Poor snow conditions', 'terrain-mismatch': 'Terrain mismatch',
  'safety-concern': 'Safety concerns', 'value-concern': 'Poor value', injury: 'Injuries',
});

function latestThoughtByGuest(snapshot: GuestSimulationEngineSnapshot) {
  const result = new Map<string, GuestSimulationEngineSnapshot['thoughtEvents'][number]>();
  for (const thought of snapshot.thoughtEvents) result.set(thought.guestId, thought);
  return result;
}

/** Convert one coherent worker snapshot into the bounded dashboard view model. */
export function guestVibePresentation(snapshot: GuestSimulationEngineSnapshot | null,
  selectedGuestId?: string | null): Pick<GuestVibeCheckProps,
  'summary' | 'reasonAggregates' | 'topThoughts' | 'guests'> {
  if (!snapshot) return { summary: { guestCount: 0, activeGuestCount: 0, positiveThoughtCount: 0,
    neutralThoughtCount: 0, negativeThoughtCount: 0 }, reasonAggregates: [], topThoughts: [], guests: [] };
  const aggregate = snapshot.thoughtAggregation;
  const latest = latestThoughtByGuest(snapshot);
  const reasonAggregates = aggregate.byReason.slice().sort((a, b) => b.count - a.count || a.reasonCode.localeCompare(b.reasonCode))
    .map((reason) => ({ reasonCode: reason.reasonCode, label: REASON_LABELS[reason.reasonCode] ?? reason.reasonCode,
      count: reason.count, sentiment: reason.sentiment }));
  const topThoughts = reasonAggregates.slice(0, 4).map((reason) => {
    const representative = snapshot.thoughtEvents.slice().reverse()
      .find((thought) => thought.reasonCode === reason.reasonCode);
    return { text: representative?.text ?? reason.label, reasonCode: reason.reasonCode,
      sentiment: reason.sentiment, count: reason.count };
  });
  const activeGuests: typeof snapshot.guests[number][] = [];
  for (const guest of snapshot.guests) {
    if (guest.status !== 'scheduled' && guest.status !== 'departed') activeGuests.push(guest);
    if (activeGuests.length === 12) break;
  }
  const fallbackGuests = activeGuests;
  if (fallbackGuests.length === 0) for (const guest of snapshot.guests) {
    if (guest.status !== 'departed' && fallbackGuests.push(guest) >= 12) break;
  }
  const selected = selectedGuestId ? snapshot.guests.find((guest) => guest.id === selectedGuestId) : undefined;
  const candidates = selected && !fallbackGuests.some((guest) => guest.id === selected.id)
    ? [...fallbackGuests.slice(0, 11), selected] : fallbackGuests;
  const guests = candidates.map((guest) => {
    const thought = latest.get(guest.id);
    return { id: guest.id, label: `Guest ${guest.ordinal + 1}`, status: guest.status,
      satisfaction: guest.satisfaction, sentiment: thought?.sentiment as GuestVibeSentiment | undefined,
      latestThought: thought?.text };
  });
  return { summary: { guestCount: snapshot.metrics.population, activeGuestCount: snapshot.metrics.active,
    positiveThoughtCount: aggregate.positiveEvents, neutralThoughtCount: aggregate.neutralEvents,
    negativeThoughtCount: aggregate.negativeEvents, activeIncidentCount: snapshot.safety.metrics.activeIncidents,
    resolvedIncidentCount: snapshot.safety.metrics.resolvedIncidents,
    patrolQueueCount: snapshot.safety.patrol.metrics.queuedCount,
    safetyRate: snapshot.safety.metrics.safetyRate }, reasonAggregates, topThoughts, guests };
}
