import { haversineMeters } from '../geo';
import type { NetworkNode, SkiNetwork } from '../network';
import type { GuestPortal } from '../guestSimulation/contracts';
import { buildingFootprint, type BuildingRectangle } from '../buildingGeometry';
import type { BuildingRenderRecord } from './buildingLayers';

export interface PlacedGuestPortal extends GuestPortal {
  readonly nodeId: string;
  readonly lngLat: readonly [number, number];
}

/** The entrance is a presentation building, not a saved player building. */
export const GUEST_ENTRANCE_DIMENSIONS = Object.freeze({
  lengthM: 10,
  widthM: 6,
  eaveHeightM: 3.5,
});

const GUEST_ENTRANCE_RECTANGLE = Object.freeze({
  lengthM: GUEST_ENTRANCE_DIMENSIONS.lengthM,
  widthM: GUEST_ENTRANCE_DIMENSIONS.widthM,
  bearingDeg: 0,
});

/**
 * Project the durable portal onto the existing building renderer. The record
 * is intentionally transient: callers must keep it out of the building
 * document and the save payload.
 */
export function guestEntranceBuilding(portal: PlacedGuestPortal): BuildingRenderRecord {
  return {
    id: `guest-entrance-building:${portal.id}`,
    name: portal.label,
    center: portal.lngLat,
    bearingDeg: 0,
    dimensions: GUEST_ENTRANCE_DIMENSIONS,
  };
}

/** North-aligned 10 m × 6 m footprint shared by the SVG and dashboard maps. */
export function guestEntranceFootprint(portal: PlacedGuestPortal): [number, number][] {
  return buildingFootprint({
    ...GUEST_ENTRANCE_RECTANGLE,
    center: [...portal.lngLat] as [number, number],
  } satisfies BuildingRectangle);
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
    // Placement is durable resort infrastructure, not a one-week reservation.
    // The runtime projects this onto each day's exact operating window.
    openFromTick: 0, openUntilTick: Number.MAX_SAFE_INTEGER,
    nodeId: nearest.id, lngLat: Object.freeze([...nearest.lngLat] as [number, number]),
  }), error: null };
}
