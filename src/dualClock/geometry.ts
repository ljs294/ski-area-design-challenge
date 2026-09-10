import polygonClipping from 'polygon-clipping';
import type { NetworkEdge } from '../network';
import type { SnowGrid } from '../types/snow';
import type { SavedTrail } from '../types/trails';

type Point = readonly [number, number];
export interface PreparedRoute { lanes: Point[][]; distances: Float64Array; length: number }
export interface TrailFootprint { edgeId: string; trailId: string; lengthM: number; slopeDeg: number;
  indices: Uint32Array; areas: Float64Array; areaM2: number }
export function stableHash(value: string): number {
  let hash = 2166136261;
  for (let i = 0; i < value.length; i++) hash = Math.imul(hash ^ value.charCodeAt(i), 16777619);
  return hash >>> 0;
}
function inRing(p: Point, ring: readonly Point[]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i], b = ring[j];
    if ((a[1] > p[1]) !== (b[1] > p[1]) && p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
  }
  return inside;
}
export function insidePolygon(p: Point, rings: readonly (readonly Point[])[]): boolean {
  return !!rings.length && inRing(p, rings[0]) && !rings.slice(1).some(ring => inRing(p, ring));
}
// Route validation uses a local metre frame. Keeping this tolerance in metres
// avoids treating tiny geographic segments as parallel or cancelling their
// area at longitudes such as -121.
const BOUNDARY_EPSILON = 1e-9;
function onRing(p: Point, ring: readonly Point[]): boolean {
  for (let index = 1; index < ring.length; index++) {
    const a = ring[index - 1]!, b = ring[index]!;
    const dx = b[0] - a[0], dy = b[1] - a[1], lengthSq = dx * dx + dy * dy;
    if (!lengthSq) continue;
    const progress = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / lengthSq;
    if (progress < -BOUNDARY_EPSILON || progress > 1 + BOUNDARY_EPSILON) continue;
    const nearestX = a[0] + dx * progress, nearestY = a[1] + dy * progress;
    if (Math.hypot(p[0] - nearestX, p[1] - nearestY) <= BOUNDARY_EPSILON) return true;
  }
  return false;
}
/** Trail centerlines normally meet the outside ring at their terminals. */
function insideOrOnOuterBoundary(p: Point, rings: readonly (readonly Point[])[]): boolean {
  return insidePolygon(p, rings) || (!!rings[0] && onRing(p, rings[0]) && !rings.slice(1).some(ring => onRing(p, ring)));
}

function finitePoint(point: Point | undefined): point is Point {
  return !!point && Number.isFinite(point[0]) && Number.isFinite(point[1]);
}

function authoredRingArea(ring: readonly Point[]): number {
  const origin = ring[0] ?? [0, 0];
  let sum = 0;
  for (let index = 1; index < ring.length; index += 1) {
    const previous = ring[index - 1]!, current = ring[index]!;
    const previousX = previous[0] - origin[0], previousY = previous[1] - origin[1];
    const currentX = current[0] - origin[0], currentY = current[1] - origin[1];
    sum += previousX * currentY - currentX * previousY;
  }
  return Math.abs(sum) / 2;
}

function validRing(ring: readonly Point[]): boolean {
  return ring.length >= 4 && ring.every(finitePoint)
    && ring[0]![0] === ring[ring.length - 1]![0]
    && ring[0]![1] === ring[ring.length - 1]![1]
    && authoredRingArea(ring) > BOUNDARY_EPSILON;
}

function validPolygon(rings: readonly (readonly Point[])[]): boolean {
  if (!rings.length || !validRing(rings[0]!)) return false;
  for (const ring of rings.slice(1)) {
    if (!validRing(ring) || !insidePolygon(ring[0]!, [rings[0]!]) || onRing(ring[0]!, rings[0]!)) return false;
  }
  return true;
}

function segmentParameter(point: Point, start: Point, end: Point): number {
  const dx = end[0] - start[0], dy = end[1] - start[1];
  return Math.abs(dx) >= Math.abs(dy)
    ? (Math.abs(dx) <= Number.EPSILON ? 0 : (point[0] - start[0]) / dx)
    : (Math.abs(dy) <= Number.EPSILON ? 0 : (point[1] - start[1]) / dy);
}

/** Return all parameters where two segments touch, including collinear overlap. */
function segmentTouches(a: Point, b: Point, c: Point, d: Point): number[] {
  const r: Point = [b[0] - a[0], b[1] - a[1]], s: Point = [d[0] - c[0], d[1] - c[1]];
  const denominator = r[0] * s[1] - r[1] * s[0];
  const offset: Point = [c[0] - a[0], c[1] - a[1]];
  if (Math.abs(denominator) > BOUNDARY_EPSILON) {
    const t = (offset[0] * s[1] - offset[1] * s[0]) / denominator;
    const u = (offset[0] * r[1] - offset[1] * r[0]) / denominator;
    return t >= -BOUNDARY_EPSILON && t <= 1 + BOUNDARY_EPSILON
      && u >= -BOUNDARY_EPSILON && u <= 1 + BOUNDARY_EPSILON ? [Math.max(0, Math.min(1, t))] : [];
  }
  if (Math.abs(offset[0] * r[1] - offset[1] * r[0]) > BOUNDARY_EPSILON) return [];
  const parameters = [segmentParameter(c, a, b), segmentParameter(d, a, b)]
    .filter(value => value >= -BOUNDARY_EPSILON && value <= 1 + BOUNDARY_EPSILON)
    .map(value => Math.max(0, Math.min(1, value)));
  return parameters;
}

/**
 * Validate the authored segment in place. A route must remain inside the
 * saved footprint; there is deliberately no alternate route or hole-avoidance
 * fallback because that would move a guest away from the network geometry.
 */
function segmentInside(a: Point, b: Point, rings: readonly (readonly Point[])[]): boolean {
  if (!finitePoint(a) || !finitePoint(b) || !insideOrOnOuterBoundary(a, rings)
    || !insideOrOnOuterBoundary(b, rings) || (a[0] === b[0] && a[1] === b[1])) return false;
  const touches: number[] = [0, 1];
  for (let ringIndex = 0; ringIndex < rings.length; ringIndex += 1) {
    const ring = rings[ringIndex]!;
    for (let index = 1; index < ring.length; index += 1) {
      const values = segmentTouches(a, b, ring[index - 1]!, ring[index]!);
      for (const value of values) {
        // A centerline may terminate on the outer boundary, but may not run
        // along or touch a boundary in the middle of a segment. Holes are
        // never valid endpoints, so every hole touch is rejected.
        if (value > BOUNDARY_EPSILON && value < 1 - BOUNDARY_EPSILON) return false;
        if (ringIndex > 0 && value >= -BOUNDARY_EPSILON && value <= 1 + BOUNDARY_EPSILON) return false;
        touches.push(value);
      }
    }
  }
  touches.sort((left, right) => left - right);
  for (let index = 1; index < touches.length; index += 1) {
    const start = touches[index - 1]!, end = touches[index]!;
    if (end - start <= BOUNDARY_EPSILON) continue;
    const middle = start + (end - start) / 2;
    const point: Point = [a[0] + (b[0] - a[0]) * middle, a[1] + (b[1] - a[1]) * middle];
    if (!insidePolygon(point, rings)) return false;
  }
  return true;
}

function emptyRoute(): PreparedRoute {
  return { lanes: [[]], distances: new Float64Array(0), length: 0 };
}

function dedupeCenterline(path: readonly Point[]): Point[] {
  const result: Point[] = [];
  for (const point of path) {
    const previous = result.at(-1);
    if (!previous || previous[0] !== point[0] || previous[1] !== point[1]) result.push(point);
  }
  return result;
}

function localMeters(point: Point, origin: Point, scaleX: number, scaleY: number): Point {
  return [(point[0] - origin[0]) * scaleX, (point[1] - origin[1]) * scaleY];
}

/** Polygon work happens once per geometry revision, never per rendered guest. */
export function prepareRoute(edge: NetworkEdge, trail?: SavedTrail): PreparedRoute {
  const hasTrail = edge.kind === 'trail' && trail !== undefined;
  const part = hasTrail && Array.isArray(trail?.parts) ? trail.parts[edge.partIndex] : undefined;
  const polygon = hasTrail ? part?.polygon : undefined;
  const rawCenterline = edge.path as readonly Point[];
  // Check every saved footprint coordinate before translating to the local
  // metre frame. A malformed legacy vertex must hide the route cleanly rather
  // than throwing while mapping `null`/`undefined` through localMeters.
  const finitePolygon = Array.isArray(polygon)
    && polygon.every(ring => Array.isArray(ring) && ring.every(finitePoint));
  if (!Array.isArray(rawCenterline) || rawCenterline.length < 2 || !rawCenterline.every(finitePoint)
    || (hasTrail && !finitePolygon)) return emptyRoute();
  const scaleX = 111320 * Math.cos((rawCenterline[0]?.[1] ?? 0) * Math.PI / 180), scaleY = 111320;
  const centerline = dedupeCenterline(rawCenterline);
  const origin = centerline[0] ?? [0, 0];
  const validationPolygon = polygon?.map(ring => ring.map(point => localMeters(point, origin, scaleX, scaleY)));
  const validationCenterline = centerline.map(point => localMeters(point, origin, scaleX, scaleY));
  if (centerline.length < 2 || !centerline.every(finitePoint)
    || (validationPolygon && (!validPolygon(validationPolygon)
      || validationCenterline.some((point, index) => index > 0 && !segmentInside(validationCenterline[index - 1]!, point, validationPolygon))))) return emptyRoute();
  const points: Point[] = centerline.map(point => [point[0], point[1]]);
  const distances: number[] = [0];
  let total = 0;
  for (let index = 1; index < points.length; index += 1) {
    const a = points[index - 1]!, p = points[index]!;
    total += Math.hypot((p[0] - a[0]) * scaleX, (p[1] - a[1]) * scaleY);
    distances.push(total);
  }
  return total > Number.EPSILON ? { lanes: [points], distances: Float64Array.from(distances), length: total } : emptyRoute();
}
export function routePosition(route: PreparedRoute, progress: number, identity: string): Point {
  return routeLanePosition(route, progress, route.lanes.length ? stableHash(identity) % route.lanes.length : 0);
}
export function routeLanePosition(route: PreparedRoute, progress: number, lane: number): Point {
  const points = route.lanes[lane] ?? route.lanes[0] ?? [];
  if (!points.length || !route.distances.length) return [0, 0];
  if (points.length === 1 || route.length <= Number.EPSILON) return points[0]!;
  const distance = Math.max(0, Math.min(1, progress)) * route.length;
  let low = 1, high = route.distances.length - 1;
  while (low < high) { const mid = (low + high) >>> 1; if (route.distances[mid] < distance) low = mid + 1; else high = mid; }
  const from = points[Math.max(0, low - 1)]!, to = points[Math.min(points.length - 1, low)]!;
  const fromDistance = route.distances[Math.max(0, low - 1)] ?? 0;
  const segmentDistance = (route.distances[low] ?? fromDistance) - fromDistance;
  const fraction = segmentDistance <= Number.EPSILON ? 0 : Math.max(0, Math.min(1, (distance - fromDistance) / segmentDistance));
  return [from[0] + (to[0] - from[0]) * fraction, from[1] + (to[1] - from[1]) * fraction];
}
function ringArea(ring: number[][]): number {
  let sum = 0;
  for (let i = 1; i < ring.length; i++) sum += ring[i - 1][0] * ring[i][1] - ring[i][0] * ring[i - 1][1];
  return Math.abs(sum) / 2;
}
export function snowCellArea(grid: SnowGrid): number {
  return (grid.bounds.east - grid.bounds.west) * 111320 * Math.cos((grid.bounds.north + grid.bounds.south) / 2 * Math.PI / 180)
    * (grid.bounds.north - grid.bounds.south) * 111320 / (grid.width * grid.height);
}
export function prepareFootprint(edge: NetworkEdge, trail: SavedTrail | undefined, grid: SnowGrid): TrailFootprint | null {
  if (edge.kind !== 'trail' || !trail?.parts[edge.partIndex]) return null;
  const { bounds: b, width, height } = grid, dx = (b.east - b.west) / width, dy = (b.north - b.south) / height;
  // Translate before shoelace area calculation to avoid geographic-coordinate cancellation.
  const polygon = trail.parts[edge.partIndex].polygon.map(ring => ring.map(p => [(p[0] - b.west) / dx, (b.north - p[1]) / dy] as [number, number]));
  const outer = polygon[0];
  if (!outer?.length) return null;
  const cellArea = snowCellArea(grid), indices: number[] = [], areas: number[] = [];
  const xs = outer.map(p => p[0]), ys = outer.map(p => p[1]);
  const x0 = Math.max(0, Math.floor(Math.min(...xs))), x1 = Math.min(width - 1, Math.floor(Math.max(...xs)));
  const y0 = Math.max(0, Math.floor(Math.min(...ys))), y1 = Math.min(height - 1, Math.floor(Math.max(...ys)));
  let areaM2 = 0;
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    const clipped = polygonClipping.intersection(polygon, [[[x, y], [x + 1, y], [x + 1, y + 1], [x, y + 1], [x, y]]]);
    const area = clipped.reduce((sum, part) => sum + ringArea(part[0]) - part.slice(1).reduce((holes, ring) => holes + ringArea(ring), 0), 0) * cellArea;
    if (area > 1e-6) { indices.push(y * width + x); areas.push(area); areaM2 += area; }
  }
  return { edgeId: edge.id, trailId: edge.trailId, lengthM: edge.lengthM, slopeDeg: edge.avgSlopeDeg,
    indices: Uint32Array.from(indices), areas: Float64Array.from(areas), areaM2 };
}

/** Union before sampling, so split edges and overlapping parts do not inflate warning area. */
export function prepareTrailCoverage(edge: NetworkEdge, trail: SavedTrail, grid: SnowGrid): TrailFootprint | null {
  if (edge.kind !== 'trail' || !trail.parts.length) return null;
  const union = polygonClipping.union(trail.parts[0].polygon, ...trail.parts.slice(1).map(part => part.polygon));
  const cells = new Map<number, number>(); let areaM2 = 0;
  for (const polygon of union) {
    const footprint = prepareFootprint({ ...edge, partIndex: 0 }, { ...trail, parts: [{ ...trail.parts[0], polygon }] }, grid);
    if (!footprint) continue;
    areaM2 += footprint.areaM2;
    for (let i = 0; i < footprint.indices.length; i++) cells.set(footprint.indices[i], (cells.get(footprint.indices[i]) ?? 0) + footprint.areas[i]);
  }
  return { edgeId: edge.id, trailId: trail.id, lengthM: trail.lengthM, slopeDeg: trail.avgSlopeDeg,
    indices: Uint32Array.from(cells.keys()), areas: Float64Array.from(cells.values()), areaM2 };
}
