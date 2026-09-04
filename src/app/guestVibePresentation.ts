import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import type { GuestVibeCheckProps, GuestVibeEconomySummary, GuestVibeSentiment } from './GuestVibeCheck';

export type GuestVibePresentation = Pick<GuestVibeCheckProps, 'summary' | 'reasonAggregates' | 'topThoughts' | 'guests'>;

/** Attach controls without letting the presentation adapter own mutable game state. */
export function withGuestEconomyControls(view: GuestVibePresentation, nextDayTicketPriceCents: number,
  onNextDayTicketPriceChange: (ticketPriceCents: number) => void): GuestVibePresentation {
  if (!view.summary.economy) return view;
  return { ...view, summary: { ...view.summary, economy: { ...view.summary.economy,
    nextDayTicketPriceCents, onNextDayTicketPriceChange } } };
}

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

type UnknownRecord = Record<string, unknown>;

function record(value: unknown): UnknownRecord | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as UnknownRecord : null;
}

function finiteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function booleanValue(value: unknown): boolean | undefined {
  return typeof value === 'boolean' ? value : undefined;
}

function firstNumber(records: readonly (UnknownRecord | null)[], keys: readonly string[]): number | undefined {
  for (const source of records) {
    if (!source) continue;
    for (const key of keys) {
      const value = finiteNumber(source[key]);
      if (value !== undefined) return value;
    }
  }
  return undefined;
}

function firstRecord(records: readonly (UnknownRecord | null)[], keys: readonly string[]): UnknownRecord | null {
  for (const source of records) {
    if (!source) continue;
    for (const key of keys) {
      const value = record(source[key]);
      if (value) return value;
    }
  }
  return null;
}

function matrixScoreBps(source: UnknownRecord | null, layer: string): number | undefined {
  const matrix = source ? record(source[layer]) : null;
  const overall = matrix ? record(matrix.overall) : null;
  return finiteNumber(overall?.all);
}

/**
 * Read the optional Phase 3 projection without making React depend on the
 * worker's economic implementation types. During rollout the projection may
 * be exposed as `phase3`, `economy`, or as named top-level fields; accepting
 * all three lets old checkpoints keep rendering while the engine contract is
 * finalized. No values are invented when the projection is absent.
 */
function economyPresentation(snapshot: GuestSimulationEngineSnapshot): GuestVibeEconomySummary | undefined {
  const root = snapshot as unknown as UnknownRecord;
  const phase3 = record(root.phase3);
  const economy = record(root.economy);
  const market = record(root.market);
  const demand = record(root.demand);
  const phase3Economy = record(root.phase3Economy);
  const phase3NestedEconomy = phase3 ? record(phase3.economy) : null;
  const economySnapshot = record(root.economySnapshot);
  const economic = record(root.economic);
  const sources: readonly (UnknownRecord | null)[] = [root, phase3, economy, market, demand,
    phase3Economy, phase3NestedEconomy, economySnapshot, economic];
  const forecast = firstRecord(sources, ['demandForecast', 'forecast']);
  const realization = firstRecord(sources, ['demandRealization', 'realization', 'bookings']);
  const finance = firstRecord(sources, ['ticketFinance', 'finance']);
  const reputation = firstRecord(sources, ['reputation', 'reputationLedger', 'openingReputation']);
  const economyMetrics = firstRecord([phase3NestedEconomy, phase3Economy, phase3, economy, market, demand, root], ['metrics']);
  const nestedForecastSources: readonly (UnknownRecord | null)[] = [forecast, demand, phase3, economy, market,
    phase3Economy, phase3NestedEconomy, economySnapshot, economic, root];
  const nestedScenario = firstRecord(nestedForecastSources, ['scenario']);
  const expectedGuests = firstNumber([forecast, phase3, economy, market, demand, root],
    ['admittedExpectedGuests', 'expectedGuests', 'forecastGuests']);
  const bookedGuests = firstNumber([realization, finance, economyMetrics, phase3, economy, market, root],
    ['guestCount', 'bookedGuests', 'bookings', 'acceptedBookings', 'bookedCount']);
  const unmetDirect = firstNumber([realization, forecast, phase3, economy, market, root],
    ['unmetDemand', 'unmetGuests', 'unservedGuests']);
  const marketExpected = firstNumber([forecast], ['marketExpectedGuests']);
  const admittedExpected = firstNumber([forecast], ['admittedExpectedGuests']);
  const unmetDemand = unmetDirect ?? (marketExpected !== undefined && admittedExpected !== undefined
    ? Math.max(0, marketExpected - admittedExpected) : undefined);
  const ticketPriceCents = firstNumber([finance, nestedScenario, forecast, phase3, economy, root],
    ['ticketPriceCents', 'dayTicketPriceCents']);
  const ticketRevenueCents = firstNumber([finance, economyMetrics, phase3NestedEconomy, phase3Economy, phase3, economy, root],
    ['ticketRevenueCents', 'revenueCents']);
  const reputationScoreBps = firstNumber([reputation], ['scoreBps']);
  const reputationMaximumBps = firstNumber([reputation], ['maximumScoreBps']);
  const reputationScore = firstNumber([reputation, phase3, economy, root], ['reputation', 'score']);
  const profileHypeBps = matrixScoreBps(reputation, 'hype');
  const profileLegacyBps = matrixScoreBps(reputation, 'legacy');
  const hype = firstNumber([phase3, economy, market, root], ['hype', 'shortTermHype'])
    ?? (profileHypeBps === undefined ? undefined : (profileHypeBps - 5_000) / 5_000);
  const arrivedGuests = firstNumber([phase3, economy, economyMetrics, root], ['arrivedGuests', 'arrivedCount']);
  const explicitReconciliation = [finance, realization, forecast, economyMetrics, phase3NestedEconomy,
    phase3Economy, phase3, economy, root]
    .map((source) => source ? booleanValue(source.reconciled) : undefined)
    .filter((value): value is boolean => value !== undefined);
  const hasProjection = Boolean(forecast || realization || finance || reputation || phase3 || economy
    || market || demand || phase3Economy || economySnapshot || economic
    || expectedGuests !== undefined || bookedGuests !== undefined || ticketPriceCents !== undefined);
  if (!hasProjection) return undefined;
  const normalizedReputation = reputationScoreBps !== undefined && reputationMaximumBps !== undefined && reputationMaximumBps > 0
    ? reputationScoreBps / reputationMaximumBps : reputationScore
      ?? (profileHypeBps !== undefined && profileLegacyBps !== undefined
        ? (profileHypeBps * 0.35 + profileLegacyBps * 0.65) / 10_000
        : profileHypeBps !== undefined ? profileHypeBps / 10_000
          : profileLegacyBps !== undefined ? profileLegacyBps / 10_000 : undefined);
  const metrics = snapshot.metrics;
  return {
    ...(ticketPriceCents === undefined ? {} : { ticketPriceCents }),
    ...(expectedGuests === undefined ? {} : { expectedGuests }),
    ...(bookedGuests === undefined ? {} : { bookedGuests }),
    arrivedGuests: arrivedGuests ?? metrics.arrived,
    ...(unmetDemand === undefined ? {} : { unmetDemand }),
    ...(ticketRevenueCents === undefined ? {} : { ticketRevenueCents }),
    ...(normalizedReputation === undefined ? {} : { reputation: normalizedReputation }),
    ...(hype === undefined ? {} : { hype }),
    ...(explicitReconciliation.length === 0 ? {} : { reconciled: explicitReconciliation.every(Boolean) }),
  };
}

/** Convert one coherent worker snapshot into the bounded dashboard view model. */
export function guestVibePresentation(snapshot: GuestSimulationEngineSnapshot | null,
  selectedGuestId?: string | null): GuestVibePresentation {
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
    safetyRate: snapshot.safety.metrics.safetyRate, economy: economyPresentation(snapshot) }, reasonAggregates, topThoughts, guests };
}
