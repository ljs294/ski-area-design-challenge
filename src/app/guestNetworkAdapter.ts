import type { LiftEdge, SkiNetwork, TrailEdge } from '../network';
import type { GuestSimulationEngineSnapshot, GuestSimulationNetwork } from '../guestSimulation/engine';
import type { GuestRenderPoint } from './guestLayers';
import type { PlacedGuestPortal } from './guestPortalPlacement';

export interface GuestOperatingWindow {
  readonly startTick: number;
  readonly endTick: number;
}
import { LIFT_TYPE_CATALOG } from '../lifts';

function carrierSeats(edge: LiftEdge): number {
  const rule = LIFT_TYPE_CATALOG[edge.liftTypeId].capacity;
  if (rule.kind === 'tram-cycle') return rule.cabinSize;
  const match = edge.liftTypeId.match(/(?:double|triple|quad|six-pack|eight-pack|gondola-(\d+))$/);
  if (match?.[1]) return Number(match[1]);
  if (edge.liftTypeId.endsWith('double')) return 2;
  if (edge.liftTypeId.endsWith('triple')) return 3;
  if (edge.liftTypeId.endsWith('quad')) return 4;
  if (edge.liftTypeId.endsWith('six-pack')) return 6;
  if (edge.liftTypeId.endsWith('eight-pack')) return 8;
  return 1;
}

function nodeKind(node: SkiNetwork['nodes'][number]): GuestSimulationNetwork['nodes'][number]['kind'] {
  if (node.liftBases.length) return 'lift-base';
  if (node.liftTops.length) return 'lift-top';
  return 'junction';
}

function targetRating(edge: TrailEdge): number {
  switch (edge.difficulty) {
    case 'green': return 0.2;
    case 'blue': return 0.45;
    case 'black': return 0.7;
    case 'red': return 0.92;
  }
}

/** Convert the authoritative resort topology into the small worker-domain graph. */
export function guestNetworkFromSkiNetwork(network: SkiNetwork, portal: PlacedGuestPortal,
  operatingWindow?: GuestOperatingWindow): GuestSimulationNetwork {
  if (operatingWindow && (!Number.isSafeInteger(operatingWindow.startTick)
    || !Number.isSafeInteger(operatingWindow.endTick) || operatingWindow.startTick < 0
    || operatingWindow.endTick <= operatingWindow.startTick)) {
    throw new RangeError('guest operating window must be a non-empty safe-integer interval');
  }
  // A placed entrance is durable infrastructure. Its persisted interval may
  // come from an older build that treated ticks as week-relative, so project
  // it onto the current absolute operating day before it crosses the worker.
  const activePortal = operatingWindow ? Object.freeze({ ...portal,
    openFromTick: operatingWindow.startTick, openUntilTick: operatingWindow.endTick }) : portal;
  const edges = network.edges.map((edge) => {
    const common = { id: edge.id, fromNodeId: edge.from, toNodeId: edge.to,
      travelSeconds: Math.max(1, Math.round(edge.travelTimeS)), closed: !edge.open };
    if (edge.kind === 'lift') return { ...common, kind: 'lift' as const, liftId: edge.liftId,
      capacitySeats: carrierSeats(edge) };
    if (edge.kind === 'trail') return { ...common, kind: 'descent' as const, targetRating: targetRating(edge) };
    return { ...common, kind: 'connector' as const };
  });
  const liftEdges = network.edges.filter((edge): edge is LiftEdge => edge.kind === 'lift');
  return Object.freeze({
    nodes: Object.freeze(network.nodes.map((node) => Object.freeze({ id: node.id, kind: nodeKind(node) }))),
    edges: Object.freeze(edges),
    lifts: Object.freeze(liftEdges.map((edge) => { const seats = carrierSeats(edge); return Object.freeze({ id: edge.liftId,
      baseNodeId: edge.from, topNodeId: edge.to, edgeId: edge.id, capacitySeats: seats,
      dispatchIntervalSeconds: Math.max(1, Math.round(seats * 3_600 / edge.capacityPph)),
      rideSeconds: Math.max(1, Math.round(edge.rideTimeS)) }); })),
    portals: Object.freeze([activePortal]),
    portalConnections: Object.freeze([{ portalId: activePortal.id, nodeId: activePortal.nodeId }]),
  });
}

type LngLat = readonly [number, number];

function clampUnit(value: number): number { return Math.min(1, Math.max(0, value)); }

/** Distance-weighted projection avoids speed changes when map vertices are unevenly spaced. */
function pathProgressPosition(path: readonly LngLat[], progress: number): LngLat | null {
  if (path.length === 0) return null;
  if (path.length === 1) return path[0] ?? null;
  const lengths: number[] = [];
  let total = 0;
  for (let index = 1; index < path.length; index += 1) {
    const from = path[index - 1]!, to = path[index]!;
    const latitudeScale = Math.cos(((from[1] + to[1]) / 2) * Math.PI / 180);
    const length = Math.hypot((to[0] - from[0]) * latitudeScale, to[1] - from[1]);
    lengths.push(length); total += length;
  }
  if (total <= Number.EPSILON) return path[0] ?? null;
  let remaining = clampUnit(progress) * total;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index]!;
    if (remaining <= length || index === lengths.length - 1) {
      const from = path[index]!, to = path[index + 1]!;
      const fraction = length <= Number.EPSILON ? 0 : clampUnit(remaining / length);
      return [from[0] + (to[0] - from[0]) * fraction, from[1] + (to[1] - from[1]) * fraction];
    }
    remaining -= length;
  }
  return path[path.length - 1] ?? null;
}

function movementProgress(currentTick: number, dueTick: number | undefined, durationSeconds: number): number | null {
  if (dueTick === undefined || durationSeconds <= 0) return null;
  return clampUnit((currentTick - (dueTick - durationSeconds)) / durationSeconds);
}

function joinedConnectorPath(network: SkiNetwork, edgeIds: readonly string[], portal: LngLat,
  liftEdge: LiftEdge | undefined): readonly LngLat[] {
  const points: LngLat[] = [portal];
  for (const id of edgeIds) {
    const edge = network.edgeById.get(id);
    if (!edge) continue;
    for (const point of edge.path) {
      const previous = points[points.length - 1];
      if (!previous || previous[0] !== point[0] || previous[1] !== point[1]) points.push(point);
    }
  }
  const base = liftEdge?.path[0];
  const previous = points[points.length - 1];
  if (base && (!previous || previous[0] !== base[0] || previous[1] !== base[1])) points.push(base);
  return points;
}

/** Low-cadence authoritative positions; the renderer may interpolate between published frames. */
export function guestRenderPoints(
  snapshot: GuestSimulationEngineSnapshot,
  network: SkiNetwork,
  portal: PlacedGuestPortal,
): readonly GuestRenderPoint[] {
  const itineraryByGuest = new Map(snapshot.itineraries.map((itinerary) => [itinerary.guestId, itinerary]));
  const liftEdgeByLift = new Map(network.edges.filter((edge): edge is LiftEdge => edge.kind === 'lift')
    .map((edge) => [edge.liftId, edge]));
  const activeIncidentByGuest = new Map(snapshot.safety.guestIncidents
    .filter((incident) => incident.status !== 'resolved' && incident.status !== 'failed'
      && incident.status !== 'unreachable' && incident.status !== 'cancelled')
    .map((incident) => [incident.guestId, incident]));
  const movementEventByGuest = new Map<string, GuestSimulationEngineSnapshot['pendingEvents'][number]>();
  for (const event of snapshot.pendingEvents) {
    if (event.payload.kind !== 'reach-lift' && event.payload.kind !== 'ride-complete'
      && event.payload.kind !== 'descent-complete' && event.payload.kind !== 'injury') continue;
    const previous = movementEventByGuest.get(event.guestId);
    if (!previous || event.tick < previous.tick) movementEventByGuest.set(event.guestId, event);
  }
  return snapshot.guests.flatMap((guest) => {
    if (guest.status === 'scheduled' || guest.status === 'departed') return [];
    const itinerary = itineraryByGuest.get(guest.id);
    const resource = guest.currentResourceId ? network.edgeById.get(guest.currentResourceId) : undefined;
    const liftEdge = itinerary ? liftEdgeByLift.get(itinerary.liftId) : undefined;
    const incident = activeIncidentByGuest.get(guest.id);
    const incidentEdge = incident ? network.edgeById.get(incident.edgeAnchor) : undefined;
    const movement = movementEventByGuest.get(guest.id);
    let position: LngLat | null = incident && incidentEdge
      ? pathProgressPosition(incidentEdge.path, incident.progressQ16 / 65_535) : null;
    if (!position && itinerary && guest.status === 'travelling-to-lift') {
      const progress = movement?.payload.kind === 'reach-lift'
        ? movementProgress(snapshot.tick, movement.tick, itinerary.travelToLiftSeconds) : null;
      position = pathProgressPosition(joinedConnectorPath(network, itinerary.connectorEdgeIds,
        portal.lngLat, liftEdge), progress ?? 0);
    }
    if (!position && itinerary && guest.status === 'lift-ride' && liftEdge) {
      const progress = movement?.payload.kind === 'ride-complete'
        ? movementProgress(snapshot.tick, movement.tick, itinerary.rideSeconds) : null;
      position = pathProgressPosition(liftEdge.path, progress ?? 0);
    }
    if (!position && itinerary && guest.status === 'skiing' && resource) {
      const progress = movement?.payload.kind === 'descent-complete'
        ? movementProgress(snapshot.tick, movement.tick, itinerary.descentSeconds)
        : movement?.payload.kind === 'injury'
          ? clampUnit((snapshot.tick - movement.payload.incident.entryTick) / itinerary.descentSeconds) : null;
      position = pathProgressPosition(resource.path, progress ?? 0);
    }
    if (!position && itinerary && (guest.status === 'appraising' || guest.status === 'choosing')) {
      const descent = network.edgeById.get(itinerary.descentEdgeId);
      position = descent?.path[descent.path.length - 1] ?? null;
    }
    if (!position && liftEdge && (guest.status === 'lift-queue' || guest.status === 'travelling-to-lift')) {
      position = liftEdge.path[0] ?? null;
    }
    position ??= resource?.path[0] ?? portal.lngLat;
    return [{ id: guest.id, lng: position[0], lat: position[1], status: guest.status }];
  });
}
