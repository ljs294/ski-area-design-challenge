// Fetches real-world map features (roads, buildings, hydrography, named peaks,
// and land cover) from OpenStreetMap via the Overpass API for a terrain's exact
// ingest bounds. Raw lon/lat geometry is persisted in TerrainRecord so the
// supported MapLibre renderer can consume it without legacy world projection.
import type { LatLonBounds } from './types/geo';
import type {
  RoadClass,
  WaterLineClass,
  OsmLandCoverClass,
  RoadFeature,
  WaterLineFeature,
  WaterPolygonFeature,
  LandCoverFeature,
  PeakFeature,
  BuildingFeature,
  VectorFeatureSet,
} from './types/vectorFeatures';
import { parseOsmWidthM } from './streamAnalysis';
import { classifyRoadSurface, parseRoadLaneCount, parseRoadOneWay } from './roadAnalysis';
import { OVERPASS_ENDPOINTS } from './overpassConfig';

export { OVERPASS_ENDPOINTS } from './overpassConfig';

/** Whether persisted map context contains the normalized metadata required by
 * the current offline 3D-building renderer. Empty building coverage is valid. */
export function has3DBuildingContext(features: VectorFeatureSet | undefined): boolean {
  return !!features?.buildings && features.buildings.every((building) =>
    Number.isFinite(building.heightM) && building.heightM! > 0 &&
    Number.isFinite(building.minHeightM) && building.minHeightM! >= 0);
}

// Overpass is a shared community resource, not a paid API — a descriptive
// User-Agent and a short mirror list (not aggressive retries) is the
// expected etiquette. Keep this centralized list aligned with OpenStreetMap's
// public-instance registry; Private.coffee is the documented successor to the
// former overpass.kumi.systems service.
// Denser areas (e.g. New England road networks) can meaningfully exceed 25s
// server-side even for a small few-km bbox — 45s gives Overpass enough
// headroom before the client gives up on an endpoint and tries the next.
const OVERPASS_SERVER_TIMEOUT_S = 45;
const QUERY_TIMEOUT_MS = 60_000;

export interface OverpassEndpointFailure {
  endpoint: string;
  elapsedMs: number;
  kind: 'http' | 'timeout' | 'network' | 'invalid-response';
  status?: number;
  message: string;
}

/** Every configured provider failed. Structured diagnostics prevent terrain
 * preparation from silently discarding the entire local map context. */
export class MapContextProviderError extends Error {
  readonly failures: readonly OverpassEndpointFailure[];

  constructor(failures: readonly OverpassEndpointFailure[]) {
    super(`Map context providers failed: ${failures.map((failure) =>
      `${new URL(failure.endpoint).host} (${failure.message}, ` +
      `${(failure.elapsedMs / 1000).toFixed(1)}s)`).join('; ')}`);
    this.name = 'MapContextProviderError';
    this.failures = failures.map((failure) => ({ ...failure }));
  }
}

function bboxParam(bounds: LatLonBounds): string {
  return `${bounds.south},${bounds.west},${bounds.north},${bounds.east}`;
}

function buildQuery(bounds: LatLonBounds): string {
  const bbox = bboxParam(bounds);
  return (
    `[out:json][timeout:${OVERPASS_SERVER_TIMEOUT_S}];(` +
    `way[highway](${bbox});` +
    `way[building](${bbox});` +
    `relation[building][type=multipolygon](${bbox});` +
    `way[natural=water](${bbox});` +
    `relation[natural=water][type=multipolygon](${bbox});` +
    `way[waterway=riverbank](${bbox});` +
    `way[waterway~"^(river|stream|canal)$"](${bbox});` +
    `node[natural=peak](${bbox});` +
    `way[natural~"^(wood|scrub|grassland|bare_rock|scree)$"](${bbox});` +
    `way[landuse~"^(forest|meadow)$"](${bbox});` +
    `);out geom;`
  );
}

interface OverpassLatLon {
  lat: number;
  lon: number;
}

interface OverpassMember {
  type: string;
  ref: number;
  role: string;
  geometry?: OverpassLatLon[];
}

interface OverpassElement {
  type: 'node' | 'way' | 'relation';
  id: number;
  tags?: Record<string, string>;
  lat?: number;
  lon?: number;
  geometry?: (OverpassLatLon | null)[];
  members?: OverpassMember[];
}

interface OverpassResponse {
  elements: OverpassElement[];
}

async function fetchOverpass(bounds: LatLonBounds, signal?: AbortSignal): Promise<OverpassResponse> {
  const query = buildQuery(bounds);
  const failures: OverpassEndpointFailure[] = [];

  for (const endpoint of OVERPASS_ENDPOINTS) {
    if (signal?.aborted) throw new DOMException('Map context download cancelled', 'AbortError');
    const controller = new AbortController();
    let timedOut = false;
    const timeout = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, QUERY_TIMEOUT_MS);
    const cancel = () => controller.abort();
    signal?.addEventListener('abort', cancel, { once: true });
    const startedAt = performance.now();
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: {
          'Content-Type': 'text/plain',
        },
        body: query,
        referrerPolicy: 'origin-when-cross-origin',
        signal: controller.signal,
      });
      if (!response.ok) {
        failures.push({ endpoint, elapsedMs: performance.now() - startedAt,
          kind: 'http', status: response.status,
          message: `HTTP ${response.status}${response.statusText ? ` ${response.statusText}` : ''}` });
        continue;
      }
      try {
        const value = await response.json() as Partial<OverpassResponse>;
        if (!Array.isArray(value.elements)) throw new Error('Response has no elements array');
        return { elements: value.elements };
      } catch (error) {
        failures.push({ endpoint, elapsedMs: performance.now() - startedAt,
          kind: 'invalid-response',
          message: error instanceof Error ? error.message : 'Response was not valid JSON' });
      }
    } catch (error) {
      if (signal?.aborted) throw new DOMException('Map context download cancelled', 'AbortError');
      failures.push({ endpoint, elapsedMs: performance.now() - startedAt,
        kind: timedOut ? 'timeout' : 'network',
        message: timedOut ? `Timed out after ${QUERY_TIMEOUT_MS / 1000} seconds`
          : error instanceof Error ? error.message : 'Network request failed' });
    } finally {
      clearTimeout(timeout);
      signal?.removeEventListener('abort', cancel);
    }
  }

  throw new MapContextProviderError(failures);
}

const MAJOR_HIGHWAY = new Set(['motorway', 'motorway_link', 'trunk', 'trunk_link',
  'primary', 'primary_link', 'secondary', 'secondary_link']);
const MINOR_HIGHWAY = new Set(['tertiary', 'tertiary_link', 'residential', 'unclassified',
  'service', 'living_street', 'road']);

function classifyRoad(highway: string): RoadClass {
  if (MAJOR_HIGHWAY.has(highway)) return 'major';
  if (MINOR_HIGHWAY.has(highway)) return 'minor';
  return 'path';
}

function classifyLandCover(tags: Record<string, string>): OsmLandCoverClass | null {
  if (tags.natural === 'wood' || tags.landuse === 'forest') return 'forest';
  if (tags.natural === 'scrub') return 'scrub';
  if (tags.natural === 'grassland' || tags.landuse === 'meadow') return 'grass';
  if (tags.natural === 'bare_rock' || tags.natural === 'scree') return 'rock';
  return null;
}

const DEFAULT_BUILDING_HEIGHT_M = 6;
const BUILDING_LEVEL_HEIGHT_M = 3;

function parseBuildingLevels(raw: string | undefined): number | null {
  if (!raw) return null;
  const value = Number(raw.trim().replace(',', '.'));
  return Number.isFinite(value) && value > 0 && value <= 200 ? value : null;
}

/** Normalize raw OSM tags similarly to a vector tile's `render_height`.
 * Explicit metric/imperial heights win, followed by storey counts. */
function buildingDimensions(tags: Record<string, string>): {
  heightM: number;
  minHeightM: number;
} {
  const levels = parseBuildingLevels(tags['building:levels']);
  const minLevels = parseBuildingLevels(tags['building:min_level']);
  const heightM = parseOsmWidthM(tags.height)
    ?? (levels == null ? DEFAULT_BUILDING_HEIGHT_M : levels * BUILDING_LEVEL_HEIGHT_M);
  const requestedBase = parseOsmWidthM(tags.min_height)
    ?? (minLevels == null ? 0 : minLevels * BUILDING_LEVEL_HEIGHT_M);
  return { heightM, minHeightM: Math.min(Math.max(0, requestedBase), heightM - 0.25) };
}

function toLonLat(points: OverpassLatLon[]): [number, number][] {
  return points.map((p) => [p.lon, p.lat]);
}

const RING_CLOSE_EPS = 1e-7;
function pointsMatch(a: [number, number], b: [number, number]): boolean {
  return Math.abs(a[0] - b[0]) < RING_CLOSE_EPS && Math.abs(a[1] - b[1]) < RING_CLOSE_EPS;
}

/**
 * Stitches multipolygon-relation way fragments end-to-end into closed
 * rings — large lakes are frequently split across several "outer" member
 * ways rather than one simple closed way. Fragments that can't be matched
 * to anything are kept as their own (possibly open) ring rather than
 * dropped: an imperfect shape beats silently losing a lake.
 */
function assembleRings(fragments: [number, number][][]): [number, number][][] {
  const remaining = fragments.map((f) => f.slice());
  const rings: [number, number][][] = [];

  while (remaining.length > 0) {
    let chain = remaining.shift()!;
    let extended = true;
    while (extended && chain.length > 0 && !pointsMatch(chain[0], chain[chain.length - 1])) {
      extended = false;
      for (let i = 0; i < remaining.length; i++) {
        const frag = remaining[i];
        const chainEnd = chain[chain.length - 1];
        if (pointsMatch(frag[0], chainEnd)) {
          chain = chain.concat(frag.slice(1));
          remaining.splice(i, 1);
          extended = true;
          break;
        }
        if (pointsMatch(frag[frag.length - 1], chainEnd)) {
          chain = chain.concat(frag.slice(0, -1).reverse());
          remaining.splice(i, 1);
          extended = true;
          break;
        }
      }
    }
    rings.push(chain);
  }

  return rings;
}

/**
 * Fetch and classify every supported vector feature family for a terrain's
 * ingest bounds. Raw lon/lat only — see hydrateVectorFeatures below for the
 * world-space projection step run at hydrate time.
 */
export async function fetchVectorFeatures(
  bounds: LatLonBounds,
  signal?: AbortSignal,
): Promise<VectorFeatureSet> {
  const data = await fetchOverpass(bounds, signal);

  const roads: RoadFeature[] = [];
  const waterLines: WaterLineFeature[] = [];
  const waterPolygons: WaterPolygonFeature[] = [];
  const landCover: LandCoverFeature[] = [];
  const peaks: PeakFeature[] = [];
  const buildings: BuildingFeature[] = [];

  for (const el of data.elements) {
    const tags = el.tags ?? {};

    if (el.type === 'node' && tags.natural === 'peak' && el.lat != null && el.lon != null) {
      peaks.push({
        id: `node/${el.id}`,
        name: tags.name ?? 'Unnamed Peak',
        elevationMeters: tags.ele ? Number(tags.ele) : undefined,
        lon: el.lon,
        lat: el.lat,
      });
      continue;
    }

    if (el.type !== 'way' || !el.geometry) continue;
    const points = el.geometry.filter((p): p is OverpassLatLon => p !== null);
    if (points.length < 2) continue;
    const lonLat = toLonLat(points);

    if (tags.building && tags.building !== 'no') {
      if (lonLat.length >= 3) {
        buildings.push({ id: `way/${el.id}`, name: tags.name,
          ...buildingDimensions(tags), rings: [lonLat] });
      }
      continue;
    }

    if (tags.highway) {
      const sourceWidthM = parseOsmWidthM(tags.width);
      const lanes = parseRoadLaneCount(tags.lanes);
      const lanesForward = parseRoadLaneCount(tags['lanes:forward']);
      const lanesBackward = parseRoadLaneCount(tags['lanes:backward']);
      roads.push({
        id: `way/${el.id}`,
        name: tags.name,
        roadClass: classifyRoad(tags.highway),
        highway: tags.highway,
        surfaceClass: classifyRoadSurface(tags.surface),
        ...(sourceWidthM == null ? {} : { sourceWidthM }),
        ...(lanes == null ? {} : { lanes }),
        ...(lanesForward == null ? {} : { lanesForward }),
        ...(lanesBackward == null ? {} : { lanesBackward }),
        oneWay: parseRoadOneWay(tags.oneway, tags.highway),
        points: lonLat,
      });
      continue;
    }

    if (tags.waterway === 'riverbank' || tags.natural === 'water') {
      waterPolygons.push({ id: `way/${el.id}`, name: tags.name, rings: [lonLat] });
      continue;
    }

    if (tags.waterway === 'river' || tags.waterway === 'stream' || tags.waterway === 'canal') {
      const waterClass: WaterLineClass = tags.waterway === 'stream' ? 'stream' : 'river';
      const sourceWidthM = parseOsmWidthM(tags.width);
      waterLines.push({ id: `way/${el.id}`, name: tags.name, waterClass,
        ...(sourceWidthM == null ? {} : { sourceWidthM }), points: lonLat });
      continue;
    }

    const landClass = classifyLandCover(tags);
    if (landClass) {
      landCover.push({ id: `way/${el.id}`, landCoverClass: landClass, rings: [lonLat] });
    }
  }

  for (const el of data.elements) {
    if (el.type !== 'relation' || !el.members) continue;
    const tags = el.tags ?? {};
    const isWater = tags.natural === 'water';
    const isBuilding = !!tags.building && tags.building !== 'no';
    if (!isWater && !isBuilding) continue;

    const outerFragments = el.members
      .filter((m) => m.role === 'outer' && m.geometry)
      .map((m) => toLonLat(m.geometry!));
    const innerFragments = el.members
      .filter((m) => m.role === 'inner' && m.geometry)
      .map((m) => toLonLat(m.geometry!));
    const outerRings = assembleRings(outerFragments);
    const innerRings = assembleRings(innerFragments);
    if (outerRings.length === 0) continue;

    outerRings.forEach((outer, i) => {
      const polygon = { id: `relation/${el.id}/${i}`, name: tags.name,
        ...(isBuilding ? buildingDimensions(tags) : {}),
        rings: [outer, ...innerRings] };
      if (isBuilding) buildings.push(polygon);
      else waterPolygons.push(polygon);
    });
  }

  return { roads, waterLines, waterPolygons, landCover, peaks, buildings };
}
