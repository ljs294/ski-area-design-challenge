import { haversineMeters } from '../geo';
import type { NetworkNode, SkiNetwork } from '../network';
import type { GuestPortal } from '../guestSimulation/contracts';

export interface PlacedGuestPortal extends GuestPortal {
  readonly nodeId: string;
  readonly lngLat: readonly [number, number];
}

export interface GuestPortalPlacementResult {
  readonly portal: PlacedGuestPortal | null;
  readonly error: string | null;
}

function eligible(node: NetworkNode): boolean {
  return node.liftBases.length > 0;
}

/** Snap a user click to an actual ski-network node; disconnected portals are rejected. */
export function placeGuestPortal(
  network: SkiNetwork,
  lngLat: readonly [number, number],
  id = 'guest-portal-1',
  maximumSnapM = 75,
): GuestPortalPlacementResult {
  if (!Number.isFinite(maximumSnapM) || maximumSnapM <= 0) throw new RangeError('maximumSnapM must be positive');
  let nearest: NetworkNode | null = null;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (const node of network.nodes) {
    if (!eligible(node)) continue;
    const distance = haversineMeters([lngLat[0], lngLat[1]], node.lngLat);
    if (distance < nearestDistance || (distance === nearestDistance && node.id < (nearest?.id ?? ''))) {
      nearest = node;
      nearestDistance = distance;
    }
  }
  if (!nearest || nearestDistance > maximumSnapM) {
    return { portal: null, error: 'Place the Guest Entrance within 75 m of an operating lift base.' };
  }
  return { portal: Object.freeze({
    version: 1, id, kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance',
    direction: 'inbound', accepts: 'guests', label: 'Guest Entrance', capacityGuestsPerTick: 12,
    openFromTick: 0, openUntilTick: 7 * 24 * 60 * 60,
    nodeId: nearest.id, lngLat: Object.freeze([...nearest.lngLat] as [number, number]),
  }), error: null };
}
