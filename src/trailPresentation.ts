import { union, type MultiPolygon as ClippingMultiPolygon,
  type Pair as ClippingPoint, type Polygon as ClippingPolygon } from 'polygon-clipping';
import { makeFrame, simplifyRing, toLngLat, toMeters,
  type MetersFrame, type XY } from './network';
import type { SavedJunction, SavedTrailSegment } from './types/topology';
import type { SavedTrail } from './types/trails';
import { TRAIL_PRESENTATION_VERSION, type TrailJunctionResolution,
  type TrailPresentationInput, type TrailPresentationLabel,
  type TrailPresentationResult, type TrailPresentationRoute } from './types/trailPresentation';

type LngLat = [number, number];
type Ring = LngLat[];
type Polygon = Ring[];
type MultiPolygon = Polygon[];

interface MetricTrail {
  trail: SavedTrail;
  polygons: ClippingPolygon[];
}

interface RawRoute {
  featureId: string;
  segmentId: string;
  trail: SavedTrail;
  partIndex: number;
  segmentIndex: number;
  coordinates: LngLat[];
  metric: XY[];
  fromJunctionId: string | null;
  toJunctionId: string | null;
}

interface Incident {
  route: RawRoute;
  end: 'from' | 'to';
  away: XY;
}

interface MetricResolution {
  public: TrailJunctionResolution;
  point: XY;
  through: Set<string>;
  blendRadiusM: number;
}

const SNAP_M = 0.05;
const SIMPLIFY_M = 0.25;
const MIN_PRESENTATION_AREA_M2 = 4;
const OVERLAP_SAMPLE_M = 4;

const clamp = (value: number, min: number, max: number) =>
  Math.max(min, Math.min(max, value));

function metricPoint(frame: MetersFrame, point: LngLat): [number, number] {
  const metric = toMeters(frame, point);
  return [Math.round(metric.x / SNAP_M) * SNAP_M, Math.round(metric.y / SNAP_M) * SNAP_M];
}

function closeRing(ring: number[][]): ClippingPoint[] {
  const finite = ring.filter((point) => point.length >= 2 && point.every(Number.isFinite))
    .map((point) => [point[0], point[1]] as ClippingPoint);
  if (finite.length < 3) return [];
  const first = finite[0], last = finite.at(-1)!;
  if (first[0] !== last[0] || first[1] !== last[1]) finite.push([...first] as ClippingPoint);
  return finite.length >= 4 ? finite : [];
}

function signedArea(ring: ClippingPoint[]): number {
  let area = 0;
  for (let i = 1; i < ring.length; i++)
    area += ring[i - 1][0] * ring[i][1] - ring[i][0] * ring[i - 1][1];
  return area / 2;
}

function metricPolygon(frame: MetersFrame, polygon: Polygon): ClippingPolygon {
  return polygon.map((ring) => closeRing(ring.map((point) => metricPoint(frame, point))))
    .filter((ring) => ring.length >= 4);
}

function circle(point: XY, radiusM: number): ClippingPolygon {
  const ring: ClippingPoint[] = [];
  for (let i = 0; i <= 48; i++) {
    const angle = i / 48 * Math.PI * 2;
    ring.push([
      Math.round((point.x + Math.cos(angle) * radiusM) / SNAP_M) * SNAP_M,
      Math.round((point.y + Math.sin(angle) * radiusM) / SNAP_M) * SNAP_M,
    ] as ClippingPoint);
  }
  return [ring];
}

function cleanUnion(frame: MetersFrame, geometry: ClippingMultiPolygon): MultiPolygon {
  const polygons: MultiPolygon = [];
  for (const polygon of geometry) {
    if (!polygon[0] || Math.abs(signedArea(polygon[0])) < MIN_PRESENTATION_AREA_M2) continue;
    const rings: Polygon = [];
    for (let index = 0; index < polygon.length; index++) {
      if (index > 0 && Math.abs(signedArea(polygon[index])) < MIN_PRESENTATION_AREA_M2) continue;
      const simplified = simplifyRing(polygon[index] as LngLat[], SIMPLIFY_M);
      if (simplified.length < 4) continue;
      rings.push(simplified.map(([x, y]) => toLngLat(frame, { x, y })));
    }
    if (rings.length) polygons.push(rings);
  }
  return polygons;
}

function routeKey(trailId: string, partIndex: number, segmentIndex: number, segmentId: string): string {
  return `${trailId}:${partIndex}:${segmentIndex}:${segmentId}`;
}

function rawRoutes(trails: SavedTrail[], frame: MetersFrame): RawRoute[] {
  const routes: RawRoute[] = [];
  for (const trail of trails) trail.parts.forEach((part, partIndex) => {
    const segments: Array<SavedTrailSegment & { legacy?: boolean }> = part.segments?.length
      ? part.segments : [{ id: `${trail.id}:${partIndex}:legacy`, centerline: part.centerline,
          centerlineElevM: part.centerlineElevM, fromJunctionId: '', toJunctionId: '', legacy: true }];
    segments.forEach((segment, segmentIndex) => {
      if (segment.centerline.length < 2) return;
      routes.push({
        featureId: routeKey(trail.id, partIndex, segmentIndex, segment.id),
        segmentId: segment.id,
        trail,
        partIndex,
        segmentIndex,
        coordinates: segment.centerline,
        metric: segment.centerline.map((point) => toMeters(frame, point)),
        fromJunctionId: segment.legacy ? null : segment.fromJunctionId,
        toJunctionId: segment.legacy ? null : segment.toJunctionId,
      });
    });
  });
  return routes.sort((left, right) => left.featureId.localeCompare(right.featureId));
}

function unitAway(points: XY[], end: 'from' | 'to'): XY {
  const a = end === 'from' ? points[0] : points.at(-1)!;
  const b = end === 'from' ? points[1] : points.at(-2)!;
  const length = Math.hypot(b.x - a.x, b.y - a.y) || 1;
  return { x: (b.x - a.x) / length, y: (b.y - a.y) / length };
}

function deflection(left: Incident, right: Incident): number {
  const dot = clamp(left.away.x * right.away.x + left.away.y * right.away.y, -1, 1);
  return Math.abs(Math.PI - Math.acos(dot));
}

function sameRunContinuation(incoming: Incident, outgoing: Incident): boolean {
  return incoming.route.trail.id === outgoing.route.trail.id &&
    incoming.route.partIndex === outgoing.route.partIndex &&
    outgoing.route.segmentIndex === incoming.route.segmentIndex + 1;
}

function resolveJunctions(
  routes: RawRoute[], junctions: SavedJunction[], frame: MetersFrame,
): MetricResolution[] {
  const byJunction = new Map<string, Incident[]>();
  for (const route of routes) {
    if (route.fromJunctionId) {
      const incidents = byJunction.get(route.fromJunctionId) ?? [];
      incidents.push({ route, end: 'from', away: unitAway(route.metric, 'from') });
      byJunction.set(route.fromJunctionId, incidents);
    }
    if (route.toJunctionId) {
      const incidents = byJunction.get(route.toJunctionId) ?? [];
      incidents.push({ route, end: 'to', away: unitAway(route.metric, 'to') });
      byJunction.set(route.toJunctionId, incidents);
    }
  }

  const resolutions: MetricResolution[] = [];
  for (const junction of [...junctions].sort((a, b) => a.id.localeCompare(b.id))) {
    const incidents = byJunction.get(junction.id) ?? [];
    if (incidents.length < 2) continue;
    const incoming = incidents.filter((incident) => incident.end === 'to');
    const outgoing = incidents.filter((incident) => incident.end === 'from');
    const candidates = incoming.flatMap((left) => outgoing.map((right) => ({
      left,
      right,
      same: sameRunContinuation(left, right),
      angle: deflection(left, right),
      width: left.route.trail.brushWidthM + right.route.trail.brushWidthM,
      key: `${left.route.featureId}|${right.route.featureId}`,
    }))).filter((candidate) => candidate.left.route.featureId !== candidate.right.route.featureId)
      .sort((a, b) => Number(b.same) - Number(a.same) || a.angle - b.angle ||
        b.width - a.width || a.key.localeCompare(b.key));
    const winner = candidates[0] ?? null;
    const through = new Set(winner
      ? [winner.left.route.featureId, winner.right.route.featureId] : []);
    const widest = Math.max(...incidents.map((incident) => incident.route.trail.brushWidthM));
    const clearanceM = clamp(0.5 * widest, 6, 30);
    resolutions.push({
      point: toMeters(frame, junction.point),
      through,
      blendRadiusM: clamp(0.4 * widest, 4, 30),
      public: {
        junctionId: junction.id,
        throughSegmentIds: winner
          ? [winner.left.route.segmentId, winner.right.route.segmentId] : null,
        yieldingSegmentIds: incidents.filter((incident) => !through.has(incident.route.featureId))
          .map((incident) => incident.route.segmentId).sort(),
        clearanceM,
      },
    });
  }
  return resolutions;
}

function pointAtDistance(points: XY[], distanceM: number): XY {
  if (distanceM <= 0) return points[0];
  let remaining = distanceM;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1], b = points[i];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    if (remaining <= length) {
      const t = length ? remaining / length : 0;
      return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
    }
    remaining -= length;
  }
  return points.at(-1)!;
}

function polylineLength(points: XY[]): number {
  let length = 0;
  for (let i = 1; i < points.length; i++)
    length += Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
  return length;
}

function trimPolyline(points: XY[], fromM: number, toM: number): XY[] {
  const total = polylineLength(points);
  if (total <= fromM + toM + 0.5) return [];
  const result = [pointAtDistance(points, fromM)];
  let distance = 0;
  for (let i = 1; i < points.length - 1; i++) {
    distance += Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
    if (distance > fromM && distance < total - toM) result.push(points[i]);
  }
  result.push(pointAtDistance([...points].reverse(), toM));
  return result;
}

function pointInRing(point: XY, ring: ClippingPoint[]): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i], b = ring[j];
    if ((a[1] > point.y) !== (b[1] > point.y) &&
        point.x < (b[0] - a[0]) * (point.y - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
  }
  return inside;
}

function pointInPolygons(point: XY, polygons: ClippingPolygon[]): boolean {
  return polygons.some((polygon) => polygon[0] && pointInRing(point, polygon[0]) &&
    !polygon.slice(1).some((ring) => pointInRing(point, ring)));
}

function resample(points: XY[], spacingM: number): XY[] {
  const total = polylineLength(points);
  if (total <= spacingM) return points;
  const count = Math.max(2, Math.ceil(total / spacingM) + 1);
  return Array.from({ length: count }, (_, index) =>
    pointAtDistance(points, total * index / (count - 1)));
}

function protectedByThroughRoute(point: XY, route: RawRoute, resolutions: MetricResolution[]): boolean {
  return resolutions.some((resolution) => resolution.through.has(route.featureId) &&
    Math.hypot(point.x - resolution.point.x, point.y - resolution.point.y) <=
      resolution.public.clearanceM + resolution.blendRadiusM);
}

function visibleRouteParts(
  route: RawRoute,
  metricTrails: MetricTrail[],
  resolutions: MetricResolution[],
): XY[][] {
  const fromResolution = route.fromJunctionId
    ? resolutions.find((entry) => entry.public.junctionId === route.fromJunctionId) : null;
  const toResolution = route.toJunctionId
    ? resolutions.find((entry) => entry.public.junctionId === route.toJunctionId) : null;
  const trimmed = trimPolyline(route.metric,
    fromResolution && !fromResolution.through.has(route.featureId)
      ? fromResolution.public.clearanceM : 0,
    toResolution && !toResolution.through.has(route.featureId)
      ? toResolution.public.clearanceM : 0);
  if (trimmed.length < 2) return [];
  const sampled = resample(trimmed, OVERLAP_SAMPLE_M);
  const otherTrails = metricTrails.filter((entry) => entry.trail.id !== route.trail.id);
  const visible = sampled.map((point) => protectedByThroughRoute(point, route, resolutions) ||
    !otherTrails.some((entry) => pointInPolygons(point, entry.polygons)));
  const parts: XY[][] = [];
  let active: XY[] = [];
  sampled.forEach((point, index) => {
    if (visible[index]) active.push(point);
    if ((!visible[index] || index === sampled.length - 1) && active.length) {
      if (active.length >= 2) parts.push(active);
      active = [];
    }
  });
  return parts;
}

function routeMetadata(route: RawRoute, coordinates: LngLat[], suffix: string): TrailPresentationRoute {
  const trail = route.trail;
  const planned = trail.status === 'planning' ? ' · Planned' : '';
  const closed = trail.closed ? ' · Closed' : '';
  return {
    featureId: `${route.featureId}:${suffix}`,
    trailId: trail.id,
    name: trail.name,
    label: `${trail.name}${planned}${closed}`,
    difficulty: trail.difficulty,
    status: trail.status,
    closed: trail.closed === true,
    coordinates,
  };
}

/** Derived map presentation. Saved trail footprints are read, never rewritten. */
export function compileTrailPresentation(input: TrailPresentationInput): TrailPresentationResult {
  const trails = [...input.trails].sort((a, b) => a.id.localeCompare(b.id));
  const samples = trails.flatMap((trail) => trail.parts.flatMap((part) => part.polygon.flat()));
  if (!samples.length) return { version: TRAIL_PRESENTATION_VERSION,
    surface: [], routes: [], labels: [], junctions: [] };
  const frame = makeFrame(samples);
  const metricTrails: MetricTrail[] = trails.map((trail) => ({ trail,
    polygons: trail.parts.map((part) => metricPolygon(frame, part.polygon))
      .filter((polygon) => polygon.length) }));
  const routes = rawRoutes(trails, frame);
  const resolutions = resolveJunctions(routes, input.junctions, frame);

  const unionInputs: ClippingMultiPolygon[] = metricTrails.flatMap((entry) => entry.polygons)
    .map((polygon) => [polygon]);
  for (const resolution of resolutions)
    unionInputs.push([circle(resolution.point, resolution.blendRadiusM)]);
  let unioned: ClippingMultiPolygon = [];
  for (const geometry of unionInputs) unioned = unioned.length
    ? union(unioned, geometry) : geometry;

  const presentationRoutes: TrailPresentationRoute[] = [];
  const longestByTrail = new Map<string, { route: TrailPresentationRoute; lengthM: number }>();
  for (const route of routes) {
    const parts = visibleRouteParts(route, metricTrails, resolutions);
    parts.forEach((part, index) => {
      const coordinates = part.map((point) => toLngLat(frame, point));
      const presented = routeMetadata(route, coordinates, String(index));
      presentationRoutes.push(presented);
      const lengthM = polylineLength(part), current = longestByTrail.get(route.trail.id);
      if (!current || lengthM > current.lengthM ||
          (lengthM === current.lengthM && presented.featureId.localeCompare(current.route.featureId) < 0))
        longestByTrail.set(route.trail.id, { route: presented, lengthM });
    });
  }

  const labels = trails.map((trail): TrailPresentationLabel | null => {
    const longest = longestByTrail.get(trail.id);
    if (longest && longest.lengthM >= 40) {
      return { featureId: `label:${trail.id}`, trailId: trail.id, name: trail.name,
        label: longest.route.label, difficulty: trail.difficulty, status: trail.status,
        closed: trail.closed === true,
        geometry: { type: 'LineString', coordinates: longest.route.coordinates } };
    }
    const point = trail.parts[0]?.centerline[0] ?? trail.parts[0]?.polygon[0]?.[0];
    const source = routes.find((route) => route.trail.id === trail.id);
    if (!point || !source) return null;
    const metadata = routeMetadata(source, [point, point], 'label');
    return { featureId: `label:${trail.id}`, trailId: trail.id, name: trail.name,
      label: metadata.label, difficulty: trail.difficulty, status: trail.status,
      closed: trail.closed === true,
      geometry: { type: 'Point', coordinates: point } };
  }).filter((label): label is TrailPresentationLabel => label !== null);

  return {
    version: TRAIL_PRESENTATION_VERSION,
    surface: cleanUnion(frame, unioned),
    routes: presentationRoutes,
    labels,
    junctions: resolutions.map((resolution) => resolution.public),
  };
}
