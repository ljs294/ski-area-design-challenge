import { trailsFromLift, type LiftEdge, type SkiNetwork } from '../network';
import type { SavedRoad } from '../types/roads';
import type { PlacedGuestPortal } from './guestPortalPlacement';

export type GuestConnectivityState = 'missing-entrance' | 'missing-node' |
  'no-operating-lift' | 'no-open-descent' | 'reachable';

export interface GuestConnectivity {
  readonly state: GuestConnectivityState;
  readonly reachable: boolean;
  readonly message: string;
  readonly portal: PlacedGuestPortal | null;
  readonly connectedLiftId: string | null;
  readonly connectedLiftName: string | null;
  readonly reachableRunCount: number;
  readonly connectionPath: readonly [number, number][];
  readonly roadAccessLabel: string;
}

function nearestRoadName(roads: readonly SavedRoad[], portal: PlacedGuestPortal): string | null {
  let best: { name: string; distance: number } | null = null;
  for (const road of roads) for (const point of road.points) {
    const distance = (point[0] - portal.lngLat[0]) ** 2 + (point[1] - portal.lngLat[1]) ** 2;
    if (!best || distance < best.distance) best = { name: road.name, distance };
  }
  return best?.name ?? null;
}

/** Player-facing reachability derived from the same authoritative ski graph used by guests. */
export function analyzeGuestConnectivity(network: SkiNetwork, portal: PlacedGuestPortal | null,
  roads: readonly SavedRoad[] = []): GuestConnectivity {
  const roadName = portal && nearestRoadName(roads.filter((road) => road.points.length >= 2), portal);
  const roadAccessLabel = roadName ? roadName : 'Virtual edge-of-map access';
  const result = (state: GuestConnectivityState, message: string, edge?: LiftEdge,
    reachableRunCount = 0): GuestConnectivity => Object.freeze({ state, reachable: state === 'reachable', message,
      portal, connectedLiftId: edge?.liftId ?? null, connectedLiftName: edge?.liftName ?? null,
      reachableRunCount, connectionPath: Object.freeze(edge ? [...edge.path] : []), roadAccessLabel });
  if (!portal) return result('missing-entrance', 'Resort unreachable: place a Guest Entrance at an operating lift base.');
  const node = network.nodeById.get(portal.nodeId);
  if (!node) return result('missing-node', 'Resort unreachable: the Guest Entrance no longer matches the ski network.');
  const liftEdges = node.liftBases.map((id) => network.edgeById.get(network.liftEdgeIds.get(id) ?? ''))
    .filter((edge): edge is LiftEdge => edge?.kind === 'lift' && edge.from === node.id)
    .sort((left, right) => left.liftName.localeCompare(right.liftName) || left.liftId.localeCompare(right.liftId));
  const openLifts = liftEdges.filter((edge) => edge.open);
  if (!openLifts.length) return result('no-operating-lift',
    'Resort unreachable: no operating lift is connected to the Guest Entrance.', liftEdges[0]);
  for (const edge of openLifts) {
    const runs = trailsFromLift(network, edge.liftId, { openOnly: true })?.reachable.length ?? 0;
    if (runs > 0) return result('reachable',
      `${edge.liftName} connects the entrance to ${runs} open run${runs === 1 ? '' : 's'}.`, edge, runs);
  }
  return result('no-open-descent',
    `Resort unreachable: ${openLifts[0]!.liftName} has no reachable open descent.`, openLifts[0]);
}
