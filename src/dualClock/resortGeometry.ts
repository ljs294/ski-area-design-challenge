import type { NetworkEdge } from '../network';
import type { SnowGrid } from '../types/snow';
import type { TransitRoute, ResortSimulationInput } from './model';
import { prepareFootprint, prepareRoute, prepareTrailCoverage, type PreparedRoute, type TrailFootprint } from './geometry';

export interface PreparedResortGeometry {
  edges: Map<string, NetworkEdge>;
  outgoing: Map<string, NetworkEdge[]>;
  routes: Map<string, PreparedRoute>;
  footprints: Map<string, TrailFootprint>;
  trailCoverage: Map<string, TrailFootprint>;
}

export function journeyEdge(edges: ReadonlyMap<string, NetworkEdge>, transitRoutes: readonly TransitRoute[], id: string): NetworkEdge | TransitRoute | undefined {
  return edges.get(id) ?? transitRoutes.find(route => route.id === id);
}

export function resortGeometryKey(resort: ResortSimulationInput, snow: SnowGrid | null): string {
  return JSON.stringify({ edges: resort.edges.map(edge => ({ id: edge.id, kind: edge.kind, from: edge.from,
    to: edge.to, path: edge.path, trailId: edge.kind === 'trail' ? edge.trailId : undefined,
    partIndex: edge.kind === 'trail' ? edge.partIndex : undefined })),
  trails: resort.trails.map(trail => ({ id: trail.id, brushWidthM: trail.brushWidthM,
    parts: trail.parts.map(part => ({ polygon: part.polygon, centerline: part.centerline })) })),
  snow: snow ? { width: snow.width, height: snow.height, bounds: snow.bounds } : null });
}

export function prepareResortGeometry(resort: ResortSimulationInput, snow: SnowGrid | null,
  transitRoutes: readonly TransitRoute[]): PreparedResortGeometry {
  const edges = new Map(resort.edges.map(edge => [edge.id, edge]));
  const outgoing = new Map<string, NetworkEdge[]>();
  const routes = new Map<string, PreparedRoute>(), footprints = new Map<string, TrailFootprint>();
  const trailCoverage = new Map<string, TrailFootprint>(), trails = new Map(resort.trails.map(trail => [trail.id, trail]));
  for (const edge of resort.edges) {
    const list = outgoing.get(edge.from) ?? []; list.push(edge); outgoing.set(edge.from, list);
    const trail = edge.kind === 'trail' ? trails.get(edge.trailId) : undefined;
    routes.set(edge.id, prepareRoute(edge, trail));
    if (snow) { const fp = prepareFootprint(edge, trail, snow); if (fp) footprints.set(edge.id, fp); }
    if (snow && trail && !trailCoverage.has(trail.id)) {
      const coverage = prepareTrailCoverage(edge, trail, snow); if (coverage) trailCoverage.set(trail.id, coverage);
    }
  }
  for (const list of outgoing.values()) list.sort((a, b) => a.id.localeCompare(b.id));
  for (const transit of transitRoutes) {
    routes.set(transit.id, { lanes: transit.lanes, distances: Float64Array.from(transit.distances), length: transit.distances.at(-1) ?? 0 });
    if (transit.footprint) footprints.set(transit.id, { ...transit.footprint, edgeId: transit.id, trailId: transit.trailId,
      lengthM: transit.lengthM, indices: Uint32Array.from(transit.footprint.indices), areas: Float64Array.from(transit.footprint.areas) });
  }
  return { edges, outgoing, routes, footprints, trailCoverage };
}
