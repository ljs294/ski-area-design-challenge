import type {
  GuestId,
  GuestSimulationEnvironmentSnapshot,
  GuestState,
  PartyState,
  SimulatedSecond,
  ThoughtEvent,
} from './contracts.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import type {
  GuestLift,
  GuestSimulationEngineSnapshot,
} from './engineSupport.ts';
import type { ExperienceThoughtReasonCode } from './experience.ts';

export interface MutableGuest extends GuestState {
  status: GuestState['status'];
  currentPortalId: string | null;
  currentResourceId: string | null;
  satisfaction: number;
  pendingDeparture: boolean;
  decisionOrdinal: number;
  queueJoinedTick: SimulatedSecond | null;
  lastQueueWaitSeconds: number;
  traversalOrdinal: number;
  routeStateReason?: string;
}

export interface MutableParty extends PartyState {
  status: PartyState['status'];
}

export interface MutableLiftLedger {
  lift: GuestLift;
  readonly queue: GuestId[];
  readonly partyOrder: string[];
  partyCursor: number;
  readonly inTransit: Set<GuestId>;
  dispatches: number;
  completedRides: number;
}

export function environmentAt(
  base: GuestSimulationEnvironmentSnapshot,
  tickValue: SimulatedSecond,
): GuestSimulationEnvironmentSnapshot {
  return Object.freeze({
    ...base,
    tick: tickValue,
    conditions: Object.freeze({ ...base.conditions, tick: tickValue }),
    operating: base.operating,
  });
}

export function immutableGuest(guest: MutableGuest): GuestState {
  const {
    pendingDeparture: _pendingDeparture,
    decisionOrdinal: _decisionOrdinal,
    queueJoinedTick: _queueJoinedTick,
    lastQueueWaitSeconds: _lastQueueWaitSeconds,
    traversalOrdinal: _traversalOrdinal,
    ...state
  } = guest;
  return Object.freeze({ ...state });
}

export function defaultThoughtReason(
  kind: ThoughtEvent['kind'],
  sentiment: ThoughtEvent['sentiment'],
): ExperienceThoughtReasonCode {
  if (sentiment === 'positive') return 'positive-experience';
  if (kind === 'arrived') return 'arrival';
  if (kind === 'concerned') return 'terrain-mismatch';
  return kind;
}

export function checksumProjection(
  snapshot: Omit<GuestSimulationEngineSnapshot, 'checksum'>,
): string {
  return eventCalendarChecksum(snapshot);
}
