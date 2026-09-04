import type { LiftEdge, NetworkEdge, SkiNetwork, TrailEdge } from '../network';
import type { GuestSimulationEngineSnapshot, GuestSimulationNetwork } from '../guestSimulation/engine';
import type { GuestRenderPoint } from './guestLayers';
import type { PlacedGuestPortal } from './guestPortalPlacement';
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
export function guestNetworkFromSkiNetwork(network: SkiNetwork, portal: PlacedGuestPortal): GuestSimulationNetwork {
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
    portals: Object.freeze([portal]),
    portalConnections: Object.freeze([{ portalId: portal.id, nodeId: portal.nodeId }]),
  });
}

function edgePosition(edge: NetworkEdge | undefined, status: string): readonly [number, number] | null {
  if (!edge) return null;
  if (edge.kind === 'lift') return status === 'lift-ride' ? edge.path[1] : edge.path[0];
  return edge.path[Math.floor((edge.path.length - 1) / 2)] ?? null;
}

function edgeProgressPosition(edge: NetworkEdge | undefined, progressQ16: number | undefined): readonly [number, number] | null {
  if (!edge || progressQ16 === undefined || edge.path.length === 0) return null;
  if (edge.path.length === 1) return edge.path[0] ?? null;
  const scaled = Math.min(1, Math.max(0, progressQ16 / 65_535)) * (edge.path.length - 1);
  const index = Math.min(edge.path.length - 2, Math.floor(scaled));
  const fraction = scaled - index;
  const from = edge.path[index]!;
  const to = edge.path[index + 1]!;
  return [from[0] + (to[0] - from[0]) * fraction, from[1] + (to[1] - from[1]) * fraction];
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
  return snapshot.guests.flatMap((guest) => {
    if (guest.status === 'scheduled' || guest.status === 'departed') return [];
    const itinerary = itineraryByGuest.get(guest.id);
    const resource = guest.currentResourceId ? network.edgeById.get(guest.currentResourceId) : undefined;
    const liftEdge = itinerary ? liftEdgeByLift.get(itinerary.liftId) : undefined;
    const incident = activeIncidentByGuest.get(guest.id);
    const incidentEdge = incident ? network.edgeById.get(incident.edgeAnchor) : undefined;
    const position = edgeProgressPosition(incidentEdge, incident?.progressQ16)
      ?? edgePosition(resource ?? liftEdge, guest.status) ?? portal.lngLat;
    return [{ id: guest.id, lng: position[0], lat: position[1], status: guest.status }];
  });
}
