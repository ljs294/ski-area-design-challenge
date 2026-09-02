import type { LatLonBounds } from './types/geo';
import type { SavedBuilding } from './types/buildings';
import { normalizeBearingDeg } from './buildingUnits';

export type BuildingPoint = [number, number];

export interface BuildingRectangle {
  center: BuildingPoint;
  lengthM: number;
  widthM: number;
  bearingDeg: number;
}

const EARTH_METERS_PER_DEGREE_LAT = 111_132;
const EARTH_METERS_PER_DEGREE_LNG = 111_320;
const COLLISION_EPSILON_M = 1e-7;

function rectangleOf(value: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>): BuildingRectangle {
  if ('dimensions' in value) {
    return {
      center: value.center,
      bearingDeg: value.bearingDeg,
      lengthM: value.dimensions.lengthM,
      widthM: value.dimensions.widthM,
    };
  }
  return value;
}

function metersPerDegreeLng(latitude: number): number {
  return Math.max(1, EARTH_METERS_PER_DEGREE_LNG * Math.cos(latitude * Math.PI / 180));
}

/** Convert an east/north metric offset around a geographic anchor to lng/lat. */
export function offsetLngLat(center: BuildingPoint, eastM: number, northM: number): BuildingPoint {
  return [
    center[0] + eastM / metersPerDegreeLng(center[1]),
    center[1] + northM / EARTH_METERS_PER_DEGREE_LAT,
  ];
}

/** Convert a local rectangle coordinate to east/north metres.
 * Local x follows the long axis; local y is the right side of that axis. */
export function rotateBuildingOffset(
  localLongM: number,
  localRightM: number,
  bearingDeg: number,
): BuildingPoint {
  const radians = normalizeBearingDeg(bearingDeg) * Math.PI / 180;
  return [
    localLongM * Math.sin(radians) + localRightM * Math.cos(radians),
    localLongM * Math.cos(radians) - localRightM * Math.sin(radians),
  ];
}

/** Corners use the stable south-west, north-west, north-east, south-east order
 * used by the map's existing local building geometry helpers. */
export function rectangleCornersMeters(
  value: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
): BuildingPoint[] {
  const rectangle = rectangleOf(value);
  const halfLength = rectangle.lengthM / 2;
  const halfWidth = rectangle.widthM / 2;
  return [
    rotateBuildingOffset(-halfLength, -halfWidth, rectangle.bearingDeg),
    rotateBuildingOffset(halfLength, -halfWidth, rectangle.bearingDeg),
    rotateBuildingOffset(halfLength, halfWidth, rectangle.bearingDeg),
    rotateBuildingOffset(-halfLength, halfWidth, rectangle.bearingDeg),
  ];
}

/** Geographic corners of a player's oriented building footprint. */
export function buildingFootprint(
  value: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
): BuildingPoint[] {
  const rectangle = rectangleOf(value);
  return rectangleCornersMeters(rectangle).map(([eastM, northM]) =>
    offsetLngLat(rectangle.center, eastM, northM));
}

export const buildingFootprintCorners = buildingFootprint;
export const orientedBuildingFootprint = buildingFootprint;

function unitScale(latitude: number): [number, number] {
  return [metersPerDegreeLng(latitude), EARTH_METERS_PER_DEGREE_LAT];
}

function toLocalMeters(point: BuildingPoint, center: BuildingPoint): BuildingPoint {
  const [eastScale, northScale] = unitScale(center[1]);
  return [(point[0] - center[0]) * eastScale, (point[1] - center[1]) * northScale];
}

function projectedRange(points: BuildingPoint[], axis: BuildingPoint): [number, number] {
  let min = Infinity;
  let max = -Infinity;
  for (const point of points) {
    const projection = point[0] * axis[0] + point[1] * axis[1];
    min = Math.min(min, projection);
    max = Math.max(max, projection);
  }
  return [min, max];
}

function polygonAxes(points: BuildingPoint[]): BuildingPoint[] {
  const axes: BuildingPoint[] = [];
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    const dx = b[0] - a[0];
    const dy = b[1] - a[1];
    const length = Math.hypot(dx, dy);
    if (length > 0) axes.push([-dy / length, dx / length]);
  }
  return axes;
}

/** Positive-area overlap for two convex building footprints. Edge touching is allowed. */
export function buildingFootprintsOverlap(
  a: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
  b: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
): boolean {
  const first = rectangleOf(a);
  const second = rectangleOf(b);
  const firstPoints = rectangleCornersMeters(first);
  const secondPoints = rectangleCornersMeters({ ...second,
    center: [0, 0],
  });
  const relativeSecondCenter = toLocalMeters(second.center, first.center);
  const secondRelative = secondPoints.map(([east, north]) => [
    east + relativeSecondCenter[0], north + relativeSecondCenter[1],
  ] as BuildingPoint);
  const axes = [...polygonAxes(firstPoints), ...polygonAxes(secondRelative)];
  for (const axis of axes) {
    const [firstMin, firstMax] = projectedRange(firstPoints, axis);
    const [secondMin, secondMax] = projectedRange(secondRelative, axis);
    if (firstMax <= secondMin + COLLISION_EPSILON_M || secondMax <= firstMin + COLLISION_EPSILON_M) {
      return false;
    }
  }
  return true;
}

export const orientedRectanglesOverlap = buildingFootprintsOverlap;
export const buildingFootprintsCollide = buildingFootprintsOverlap;
export const orientedRectangleOverlap = buildingFootprintsOverlap;
export const rectangleOverlaps = buildingFootprintsOverlap;

function boundsOf(value: LatLonBounds | [[number, number], [number, number]]): LatLonBounds {
  if ('south' in value) return value;
  const west = Math.min(value[0][0], value[1][0]);
  const east = Math.max(value[0][0], value[1][0]);
  const south = Math.min(value[0][1], value[1][1]);
  const north = Math.max(value[0][1], value[1][1]);
  return { west, east, south, north };
}

/** True only when every rotated footprint corner remains inside the prepared site bounds. */
export function isBuildingFootprintInsideBounds(
  value: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
  bounds: LatLonBounds | [[number, number], [number, number]],
): boolean {
  const site = boundsOf(bounds);
  return buildingFootprint(value).every(([lng, lat]) =>
    lng >= site.west && lng <= site.east && lat >= site.south && lat <= site.north);
}

export const footprintInsideBounds = isBuildingFootprintInsideBounds;
export const isFootprintInsideTerrain = isBuildingFootprintInsideBounds;

export function buildingFootprintAreaM2(
  value: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
): number {
  const rectangle = rectangleOf(value);
  return Math.max(0, rectangle.lengthM) * Math.max(0, rectangle.widthM);
}

/** Player building collision policy intentionally ignores imported OSM buildings. */
export function hasBuildingCollision(
  candidate: BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>,
  existing: readonly (BuildingRectangle | Pick<SavedBuilding, 'center' | 'bearingDeg' | 'dimensions'>)[],
): boolean {
  return existing.some((building) => buildingFootprintsOverlap(candidate, building));
}
