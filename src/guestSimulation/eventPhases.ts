import type {
  EventCalendar,
  EventGenerationToken,
  EventIdentityPart,
  ScheduledCalendarEvent,
} from './eventCalendar';
import type { SimulatedSecond } from './contracts.ts';

/**
 * Same-second guest work is deliberately staged.  The order is part of the
 * simulation contract: a capacity dispatch at a tick must be visible before
 * bookings and arrivals at that same tick are evaluated.
 */
export const GUEST_EVENT_PHASE_ORDER = Object.freeze([
  'environment-revisions',
  'due-travel-service-completions',
  'cancellation-incidents',
  'capacity-dispatch',
  'bookings-arrivals',
  'thresholds-departures-route-failures',
  'decisions-enqueue',
  'append-outputs-metrics-snapshot',
] as const);

export type GuestEventPhase = (typeof GUEST_EVENT_PHASE_ORDER)[number];

/** Named constants avoid repeating phase spelling at scheduling call sites. */
export const GUEST_EVENT_PHASE = Object.freeze({
  environmentRevisions: GUEST_EVENT_PHASE_ORDER[0],
  dueTravelServiceCompletions: GUEST_EVENT_PHASE_ORDER[1],
  cancellationIncidents: GUEST_EVENT_PHASE_ORDER[2],
  capacityDispatch: GUEST_EVENT_PHASE_ORDER[3],
  bookingsArrivals: GUEST_EVENT_PHASE_ORDER[4],
  thresholdsDeparturesRouteFailures: GUEST_EVENT_PHASE_ORDER[5],
  decisionsEnqueue: GUEST_EVENT_PHASE_ORDER[6],
  appendOutputsMetricsSnapshot: GUEST_EVENT_PHASE_ORDER[7],
} as const);

export const GUEST_EVENT_PHASES = GUEST_EVENT_PHASE_ORDER;
export const EVENT_PHASE_ORDER = GUEST_EVENT_PHASE_ORDER;

const PHASE_RANK: Readonly<Record<GuestEventPhase, number>> = Object.freeze(
  Object.fromEntries(GUEST_EVENT_PHASE_ORDER.map((phase, index) => [phase, index])) as Record<GuestEventPhase, number>,
);

export function isGuestEventPhase(value: unknown): value is GuestEventPhase {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(PHASE_RANK, value);
}

export function guestEventPhaseRank(phase: GuestEventPhase): number {
  if (!isGuestEventPhase(phase)) throw new RangeError(`Unknown guest event phase: ${String(phase)}`);
  return PHASE_RANK[phase];
}

export const phaseRank = guestEventPhaseRank;

export interface GuestEventScheduleInput<T = unknown> {
  readonly dueTick: SimulatedSecond;
  readonly phase: GuestEventPhase;
  readonly ownerId: EventIdentityPart;
  readonly guestId: EventIdentityPart;
  readonly payload: T;
  readonly generation?: EventGenerationToken;
  readonly generationToken?: EventGenerationToken;
}

/** The only dimensions permitted before insertionSequence for guest events. */
export interface GuestEventOrderKey {
  readonly dueTick: SimulatedSecond;
  readonly phase: GuestEventPhase;
  readonly ownerId: string;
  readonly guestId: string;
  readonly insertionSequence: number;
}

function stableIdentityPart(value: EventIdentityPart): string {
  return typeof value === 'bigint' ? `${value}n` : String(value);
}

/**
 * Return the canonical named order key.  The helper intentionally has no
 * generic event-key argument: guestId is the key, and insertionSequence is
 * the final tie-breaker.
 */
export function guestEventOrderKey(
  input: Omit<GuestEventScheduleInput<unknown>, 'payload' | 'generation' | 'generationToken'>,
  insertionSequence: number,
): GuestEventOrderKey {
  if (!Number.isSafeInteger(insertionSequence) || insertionSequence < 0) {
    throw new RangeError('insertionSequence must be a non-negative safe integer');
  }
  return {
    dueTick: input.dueTick,
    phase: input.phase,
    ownerId: stableIdentityPart(input.ownerId),
    guestId: stableIdentityPart(input.guestId),
    insertionSequence,
  };
}

/**
 * Schedule a guest event using the fixed total key
 * (dueTick, phase, ownerId, guestId, insertionSequence).
 */
export function scheduleGuestEvent<T>(
  calendar: EventCalendar<T>,
  input: GuestEventScheduleInput<T>,
): ScheduledCalendarEvent<T> {
  if (!isGuestEventPhase(input.phase)) throw new RangeError(`Unknown guest event phase: ${String(input.phase)}`);
  return calendar.schedule({
    tick: input.dueTick,
    priority: guestEventPhaseRank(input.phase),
    entityId: input.ownerId,
    key: input.guestId,
    payload: input.payload,
    phase: input.phase,
    generation: input.generationToken ?? input.generation,
  });
}

export const scheduleGuestSimulationEvent = scheduleGuestEvent;
