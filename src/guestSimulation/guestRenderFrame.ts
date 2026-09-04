import type { GuestState } from './contracts.ts';
import type { GuestSimulationEngineSnapshot } from './engineSupport.ts';

/** Four 32-bit columns: ID, edge index, progress, and state flags. */
export const GUEST_RENDER_BYTES_PER_GUEST = 16 as const;

export interface GuestRenderFrame {
  readonly ids: Uint32Array;
  readonly guestIds: Uint32Array;
  readonly edgeIndices: Int32Array;
  readonly progress: Float32Array;
  readonly statusFlags: Uint32Array;
  readonly bytesPerGuest: typeof GUEST_RENDER_BYTES_PER_GUEST;
  readonly byteLength: number;
}

/**
 * The small engine projection needed to build a render frame.  Keeping this
 * separate from GuestSimulationEngineSnapshot is important: compact worker
 * advances must not materialize economy, queue, party, or persistence state
 * just to draw guests.
 */
export type GuestRenderFrameProjection = {
  readonly tick: GuestSimulationEngineSnapshot['tick'];
  readonly guests: GuestSimulationEngineSnapshot['guests'];
  readonly network: GuestSimulationEngineSnapshot['network'];
  readonly itineraries: GuestSimulationEngineSnapshot['itineraries'];
  readonly pendingEvents: GuestSimulationEngineSnapshot['pendingEvents'];
  readonly safety: Pick<GuestSimulationEngineSnapshot['safety'], 'guestIncidents'>;
};

/** Stable bit values; they are intentionally independent of string statuses. */
export const GUEST_RENDER_STATUS_FLAGS = Object.freeze({
  scheduled: 1,
  arriving: 2,
  waitingForRoute: 131_072,
  choosing: 4,
  travellingToLift: 8,
  liftQueue: 16,
  liftRide: 32,
  skiing: 64,
  appraising: 128,
  departing: 256,
  departed: 512,
  facilityQueue: 1_024,
  facilityService: 2_048,
  regrouping: 4_096,
  incident: 8_192,
  patrolResponse: 16_384,
  lodging: 32_768,
  roadTravel: 65_536,
} as const);

function statusFlag(guest: GuestState): number {
  switch (guest.status) {
    case 'waiting-for-route': return GUEST_RENDER_STATUS_FLAGS.waitingForRoute;
    case 'travelling-to-lift': return GUEST_RENDER_STATUS_FLAGS.travellingToLift;
    case 'lift-queue': return GUEST_RENDER_STATUS_FLAGS.liftQueue;
    case 'lift-ride': return GUEST_RENDER_STATUS_FLAGS.liftRide;
    case 'facility-queue': return GUEST_RENDER_STATUS_FLAGS.facilityQueue;
    case 'facility-service': return GUEST_RENDER_STATUS_FLAGS.facilityService;
    case 'patrol-response': return GUEST_RENDER_STATUS_FLAGS.patrolResponse;
    case 'road-travel': return GUEST_RENDER_STATUS_FLAGS.roadTravel;
    default: return GUEST_RENDER_STATUS_FLAGS[guest.status];
  }
}

/**
 * Return the currently occupied route edge when one is observable from the
 * engine snapshot.  The Phase 1 engine does not yet expose sub-edge distance,
 * so progress is a deterministic state projection (0, or 1 after departure).
 */
interface GuestMovementProjection {
  readonly edgeIndex: number;
  readonly progress: number;
}

/** Resolve a global connector-route progress onto the active route segment. */
export function guestMovementProjection(
  snapshot: GuestRenderFrameProjection,
  guest: GuestState,
  movement: GuestRenderFrameProjection['pendingEvents'][number] | undefined,
  itinerary: GuestRenderFrameProjection['itineraries'][number] | undefined,
  edgeIndex: ReadonlyMap<string, number>,
): GuestMovementProjection | null {
  if (guest.status !== 'travelling-to-lift' || movement?.payload.kind !== 'reach-lift' || !itinerary) return null;
  const route = itinerary.connectorEdgeIds;
  if (route.length === 0) return null;
  const byId = new Map(snapshot.network.edges.map((edge) => [edge.id, edge]));
  const durations = route.map((id) => Math.max(0, byId.get(id)?.travelSeconds ?? 0));
  const measuredTotal = durations.reduce((sum, value) => sum + value, 0);
  const total = measuredTotal > 0 ? measuredTotal : Math.max(0, itinerary.travelToLiftSeconds);
  if (total <= 0) return { edgeIndex: edgeIndex.get(route[0] ?? '') ?? -1, progress: 0 };
  const elapsed = clampUnit((snapshot.tick - (movement.tick - total)) / total) * total;
  let remaining = elapsed;
  for (let index = 0; index < route.length; index += 1) {
    const duration = durations[index] ?? 0;
    // A zero-duration edge is only selected when every remaining edge is also
    // zero; otherwise it contributes no visible interval.
    if (duration > 0 && (remaining < duration || index === route.length - 1)) {
      return { edgeIndex: edgeIndex.get(route[index] ?? '') ?? -1,
        progress: clampUnit(remaining / duration) };
    }
    remaining = Math.max(0, remaining - duration);
  }
  const last = route.at(-1) ?? '';
  return { edgeIndex: edgeIndex.get(last) ?? -1, progress: 1 };
}

function edgeForGuest(guest: GuestState,
  edgeIndex: ReadonlyMap<string, number>, liftEdgeIndex: ReadonlyMap<string, number>,
  itinerary: GuestSimulationEngineSnapshot['itineraries'][number] | undefined,
  movementProjection: GuestMovementProjection | null): number {
  // While traversing connectors the engine keeps currentResourceId at the
  // destination lift for routing purposes; publish the first connector edge
  // so the renderer can associate the normalized route progress with it.
  if (guest.status === 'travelling-to-lift' && itinerary) {
    return movementProjection?.edgeIndex ?? edgeIndex.get(itinerary.connectorEdgeIds[0] ?? '') ?? -1;
  }
  const current = guest.currentResourceId;
  if (current) {
    const direct = edgeIndex.get(current);
    if (direct !== undefined) return direct;
    const lift = liftEdgeIndex.get(current);
    if (lift !== undefined) return lift;
  }
  if (!itinerary) return -1;
  if (guest.status === 'lift-queue' || guest.status === 'lift-ride') return edgeIndex.get(itinerary.liftEdgeId) ?? -1;
  if (guest.status === 'skiing' || guest.status === 'appraising') return edgeIndex.get(itinerary.descentEdgeId) ?? -1;
  return -1;
}

function clampUnit(value: number): number { return Math.min(1, Math.max(0, value)); }

function movementProgress(currentSecond: number, dueSecond: number | undefined, durationSeconds: number): number | null {
  if (dueSecond === undefined || durationSeconds <= 0) return null;
  return clampUnit((currentSecond - (dueSecond - durationSeconds)) / durationSeconds);
}

function movementEventByGuest(snapshot: GuestRenderFrameProjection): ReadonlyMap<string, GuestRenderFrameProjection['pendingEvents'][number]> {
  const events = new Map<string, GuestRenderFrameProjection['pendingEvents'][number]>();
  for (const event of snapshot.pendingEvents) {
    if (event.payload.kind !== 'reach-lift' && event.payload.kind !== 'ride-complete'
      && event.payload.kind !== 'descent-complete' && event.payload.kind !== 'injury') continue;
    const previous = events.get(event.guestId);
    if (!previous || event.tick < previous.tick
      || (event.tick === previous.tick && event.insertionSequence < previous.insertionSequence)) events.set(event.guestId, event);
  }
  return events;
}

function progressForGuest(snapshot: GuestRenderFrameProjection, guest: GuestState,
  movement: GuestRenderFrameProjection['pendingEvents'][number] | undefined,
  itinerary: GuestRenderFrameProjection['itineraries'][number] | undefined,
  movementProjection: GuestMovementProjection | null,
  incident: GuestRenderFrameProjection['safety']['guestIncidents'][number] | undefined): number {
  if (guest.status === 'departed') return 1;
  if (movementProjection) return movementProjection.progress;
  if (!itinerary || !movement) {
    return incident ? clampUnit(incident.progressQ16 / 65_535) : 0;
  }
  if (movement.payload.kind === 'reach-lift' && guest.status === 'travelling-to-lift') {
    return movementProgress(snapshot.tick, movement.tick, itinerary.travelToLiftSeconds) ?? 0;
  }
  if (movement.payload.kind === 'ride-complete' && guest.status === 'lift-ride') {
    return movementProgress(snapshot.tick, movement.tick, itinerary.rideSeconds) ?? 0;
  }
  if (movement.payload.kind === 'injury' && (guest.status === 'skiing' || guest.status === 'patrol-response')) {
    return clampUnit((snapshot.tick - movement.payload.incident.entryTick) / itinerary.descentSeconds);
  }
  if (movement.payload.kind === 'descent-complete' && guest.status === 'skiing') {
    return movementProgress(snapshot.tick, movement.tick, itinerary.descentSeconds) ?? 0;
  }
  return 0;
}

/** Build the transferable, bounded render projection for one authoritative snapshot. */
export function buildGuestRenderFrame(snapshot: GuestRenderFrameProjection): GuestRenderFrame {
  const guests = snapshot.guests;
  const edgeIndex = new Map(snapshot.network.edges.map((edge, index) => [edge.id, index]));
  const liftEdgeIndex = new Map(snapshot.network.lifts.map((lift) => [lift.id, edgeIndex.get(lift.edgeId) ?? -1]));
  const itineraryByGuest = new Map(snapshot.itineraries.map((itinerary) => [itinerary.guestId, itinerary]));
  const activeIncidentByGuest = new Map(snapshot.safety.guestIncidents
    .filter((incident) => incident.status !== 'resolved' && incident.status !== 'failed'
      && incident.status !== 'unreachable' && incident.status !== 'cancelled')
    .map((incident) => [incident.guestId, incident]));
  const movements = movementEventByGuest(snapshot);
  const ids = new Uint32Array(guests.length);
  const edgeIndices = new Int32Array(guests.length);
  const progress = new Float32Array(guests.length);
  const statusFlags = new Uint32Array(guests.length);
  for (let index = 0; index < guests.length; index += 1) {
    const guest = guests[index]!;
    // Roster ordinals are unique and deterministic for a run, unlike a hash
    // of the string ID which could collide at the 10,000 guest cap.
    ids[index] = guest.ordinal + 1;
    const itinerary = itineraryByGuest.get(guest.id);
    const movement = movements.get(guest.id);
    const projection = guestMovementProjection(snapshot, guest, movement, itinerary, edgeIndex);
    edgeIndices[index] = edgeForGuest(guest, edgeIndex, liftEdgeIndex, itinerary, projection);
    progress[index] = progressForGuest(snapshot, guest, movement, itinerary, projection, activeIncidentByGuest.get(guest.id));
    statusFlags[index] = statusFlag(guest);
  }
  const byteLength = ids.byteLength + edgeIndices.byteLength + progress.byteLength + statusFlags.byteLength;
  if (byteLength !== guests.length * GUEST_RENDER_BYTES_PER_GUEST) {
    throw new Error('guest render frame exceeded its fixed byte budget');
  }
  return { ids, guestIds: ids, edgeIndices, progress, statusFlags,
    bytesPerGuest: GUEST_RENDER_BYTES_PER_GUEST, byteLength };
}
