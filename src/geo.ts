import type { LatLonBounds } from './types/geo';

export const METERS_PER_DEGREE_LAT = 111320;

/**
 * Compute a true real-world-meters square's lat/lon bounds around a center
 * point (using the cos(lat) correction for longitude shrinkage). Shared by
 * the map picker's selection box and anything that needs the same bounds
 * without a Leaflet dependency (e.g. tests).
 */
export function boundsForSquareMeters(
  centerLat: number,
  centerLon: number,
  sizeMeters: number
): LatLonBounds {
  const latDelta = sizeMeters / 2 / METERS_PER_DEGREE_LAT;
  const lonDelta = sizeMeters / 2 / (METERS_PER_DEGREE_LAT * Math.cos((centerLat * Math.PI) / 180));

  return {
    south: centerLat - latDelta,
    north: centerLat + latDelta,
    west: centerLon - lonDelta,
    east: centerLon + lonDelta,
  };
}

const EARTH_RADIUS_M = 6371000;

/** Great-circle distance in meters between two [lng, lat] points (haversine). */
export function haversineMeters(a: [number, number], b: [number, number]): number {
  const toRad = Math.PI / 180;
  const dLat = (b[1] - a[1]) * toRad;
  const dLng = (b[0] - a[0]) * toRad;
  const sinLat = Math.sin(dLat / 2);
  const sinLng = Math.sin(dLng / 2);
  const h =
    sinLat * sinLat + Math.cos(a[1] * toRad) * Math.cos(b[1] * toRad) * sinLng * sinLng;
  return 2 * EARTH_RADIUS_M * Math.asin(Math.sqrt(h));
}

/**
 * The single projection every locally-prepared layer shares: a unit-square
 * coordinate (u,v ∈ [0,1], v measured from the NORTH edge downward, matching
 * the row-0-is-north grid convention) to lng/lat within `bounds`. Contours and
 * cover boundaries are baked in this unit space and placed through here;
 * `lngLatToUnit` is its exact inverse, used when sampling the DEM/cover grids.
 * Keeping both sides on one pair of functions is what guarantees the layers
 * stay registered to each other and to `bounds`.
 */
export function unitToLngLat(u: number, v: number, bounds: LatLonBounds): [number, number] {
  return [
    bounds.west + u * (bounds.east - bounds.west),
    bounds.north - v * (bounds.north - bounds.south),
  ];
}

export function lngLatToUnit(lng: number, lat: number, bounds: LatLonBounds): [number, number] {
  return [
    (lng - bounds.west) / (bounds.east - bounds.west),
    (bounds.north - lat) / (bounds.north - bounds.south),
  ];
}
