import type { LatLonBounds } from './elevation';

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
