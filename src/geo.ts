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

export interface WorldPoint {
  x: number;
  y: number;
}

/**
 * Project a lon/lat point into the same world-space square the renderer
 * works in (0..mapSize on each axis). Matches the grid convention used
 * throughout elevation.ts/contours.ts: row 0 = north edge, col 0 = west
 * edge, so y=0 is north and x=0 is west here too — vector features line up
 * with the elevation grid exactly as long as both are projected from the
 * same `bounds`. A linear equirectangular approximation, which is fine at
 * the few-km scale these selections are (same simplification
 * boundsForSquareMeters already makes).
 */
export function lonLatToWorld(lon: number, lat: number, bounds: LatLonBounds, mapSize: number): WorldPoint {
  const x = ((lon - bounds.west) / (bounds.east - bounds.west)) * mapSize;
  const y = ((bounds.north - lat) / (bounds.north - bounds.south)) * mapSize;
  return { x, y };
}
