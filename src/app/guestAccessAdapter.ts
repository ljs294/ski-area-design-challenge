import type { AccessGraphInput, AccessRoadLike } from '../guestSimulation/access';
import type { SavedRoad } from '../types/roads';
import type { PlacedGuestPortal } from './guestPortalPlacement';

function distanceSquared(a: readonly [number, number], b: readonly [number, number]): number {
  return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2;
}

/** Map saved road centerlines into the Phase 6 access contract. */
export function guestAccessFromRoads(roads: readonly SavedRoad[], portal: PlacedGuestPortal): AccessGraphInput | undefined {
  const usable = roads.filter((road) => road.points.length >= 2).slice().sort((a, b) => a.id.localeCompare(b.id));
  if (usable.length === 0) return undefined;
  let closestRoad = usable[0]!;
  let closestIndex = 0;
  let closestDistance = Number.POSITIVE_INFINITY;
  for (const road of usable) for (let index = 0; index < road.points.length; index += 1) {
    const distance = distanceSquared(road.points[index]!, portal.lngLat);
    if (distance < closestDistance) { closestDistance = distance; closestRoad = road; closestIndex = index; }
  }
  const originIndex = closestIndex < closestRoad.points.length / 2 ? closestRoad.points.length - 1 : 0;
  const accessRoads: AccessRoadLike[] = usable.map((road) => ({ id: road.id, points: road.points,
    travelSeconds: Math.max(1, Math.round(road.lengthM / Math.max(1, road.points.length - 1) / 8)), capacityVehicles: 60 }));
  return { roads: accessRoads,
    edgeOfMapNodes: [{ id: `edge:${closestRoad.id}`, coordinate: closestRoad.points[originIndex] }],
    parkingAreas: [{ id: 'base-parking', capacityVehicles: 5_000, roadId: closestRoad.id, pointIndex: closestIndex }],
    dropOffZones: [{ id: 'base-dropoff', capacityVehiclesPerTick: 20, roadId: closestRoad.id, pointIndex: closestIndex }],
    portals: [{ portal, roadId: closestRoad.id, pointIndex: closestIndex }] };
}
