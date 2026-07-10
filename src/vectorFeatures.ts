// Fetches real-world map features (roads, hydrography, named peaks, land
// cover) from OpenStreetMap via the Overpass API for a terrain's exact
// ingest bounds, and projects/tile-indexes them into the renderer's
// world-space at hydrate time. Same "store raw, derive display form on
// load" split as elevation.ts/bicubicUpscale.ts: raw lon/lat geometry is
// what gets persisted (types.ts's VectorFeatureSet), everything in this
// file below fetchVectorFeatures is a pure, re-derivable projection step.
import type { LatLonBounds } from './elevation';
import { lonLatToWorld, type WorldPoint } from './geo';
import { buildTileIndex, type TileIndex, type Segment } from './tileIndex';
import { thinLabelsBySpacing, type WorldLabel } from './labels';
import { TILES_PER_AXIS } from './contours';
import type {
  RoadClass,
  WaterLineClass,
  LandCoverClass,
  RoadFeature,
  WaterLineFeature,
  WaterPolygonFeature,
  LandCoverFeature,
  PeakFeature,
  VectorFeatureSet,
} from './types';

// Overpass is a shared community resource, not a paid API — a descriptive
// User-Agent and a short mirror list (not aggressive retries) is the
// expected etiquette. overpass-api.de is the primary public instance;
// kumi.systems is a well-known independent mirror used as a fallback if the
// primary is overloaded or down.
const OVERPASS_ENDPOINTS = [
  'https://overpass-api.de/api/interpreter',
  'https://overpass.kumi.systems/api/interpreter',
];

// Denser areas (e.g. New England road networks) can meaningfully exceed 25s
// server-side even for a small few-km bbox — 45s gives Overpass enough
// headroom before the client gives up on an endpoint and tries the next.
const OVERPASS_SERVER_TIMEOUT_S = 45;
const QUERY_TIMEOUT_MS = 50_000;

function bboxParam(bounds: LatLonBounds): string {
  return `${bounds.south},${bounds.west},${bounds.north},${bounds.east}`;
}

function buildQuery(bounds: LatLonBounds): string {
  const bbox = bboxParam(bounds);
  return (
    `[out:json][timeout:${OVERPASS_SERVER_TIMEOUT_S}];(` +
    `way[highway](${bbox});` +
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

async function fetchOverpass(bounds: LatLonBounds): Promise<OverpassResponse> {
  const query = buildQuery(bounds);
  let lastError: unknown;

  for (const endpoint of OVERPASS_ENDPOINTS) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), QUERY_TIMEOUT_MS);
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: {
          'Content-Type': 'text/plain',
          'User-Agent': 'ski-area-design-challenge (mountain terrain planner, non-commercial)',
        },
        body: query,
        signal: controller.signal,
      });
      if (!response.ok) throw new Error(`Overpass returned ${response.status}`);
      return (await response.json()) as OverpassResponse;
    } catch (e) {
      lastError = e;
    } finally {
      clearTimeout(timeout);
    }
  }

  throw lastError instanceof Error ? lastError : new Error('Overpass fetch failed');
}

const MAJOR_HIGHWAY = new Set(['motorway', 'trunk', 'primary', 'secondary']);
const MINOR_HIGHWAY = new Set(['tertiary', 'residential', 'unclassified', 'service']);

function classifyRoad(highway: string): RoadClass {
  if (MAJOR_HIGHWAY.has(highway)) return 'major';
  if (MINOR_HIGHWAY.has(highway)) return 'minor';
  return 'path';
}

function classifyLandCover(tags: Record<string, string>): LandCoverClass | null {
  if (tags.natural === 'wood' || tags.landuse === 'forest') return 'forest';
  if (tags.natural === 'scrub') return 'scrub';
  if (tags.natural === 'grassland' || tags.landuse === 'meadow') return 'grass';
  if (tags.natural === 'bare_rock' || tags.natural === 'scree') return 'rock';
  return null;
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
export async function fetchVectorFeatures(bounds: LatLonBounds): Promise<VectorFeatureSet> {
  const data = await fetchOverpass(bounds);

  const roads: RoadFeature[] = [];
  const waterLines: WaterLineFeature[] = [];
  const waterPolygons: WaterPolygonFeature[] = [];
  const landCover: LandCoverFeature[] = [];
  const peaks: PeakFeature[] = [];

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

    if (tags.highway) {
      roads.push({ id: `way/${el.id}`, name: tags.name, roadClass: classifyRoad(tags.highway), points: lonLat });
      continue;
    }

    if (tags.waterway === 'riverbank' || tags.natural === 'water') {
      waterPolygons.push({ id: `way/${el.id}`, name: tags.name, rings: [lonLat] });
      continue;
    }

    if (tags.waterway === 'river' || tags.waterway === 'stream' || tags.waterway === 'canal') {
      const waterClass: WaterLineClass = tags.waterway === 'stream' ? 'stream' : 'river';
      waterLines.push({ id: `way/${el.id}`, name: tags.name, waterClass, points: lonLat });
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
    if (tags.natural !== 'water') continue;

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
      waterPolygons.push({ id: `relation/${el.id}/${i}`, name: tags.name, rings: [outer, ...innerRings] });
    });
  }

  return { roads, waterLines, waterPolygons, landCover, peaks };
}

// ---------------------------------------------------------------------
// Hydration — projects raw lon/lat features into the renderer's world
// space and builds the same kind of viewport tile index contours.ts uses,
// so roads/rivers stay fast to pan/zoom at any feature density.
// ---------------------------------------------------------------------

export interface RoadSegment extends Segment {
  roadClass: RoadClass;
}

export interface WaterLineSegment extends Segment {
  waterClass: WaterLineClass;
}

export interface ProjectedPolygon {
  rings: WorldPoint[][];
}

export interface ProjectedLandCoverPolygon extends ProjectedPolygon {
  landCoverClass: LandCoverClass;
}

export interface ProjectedPeak {
  x: number;
  y: number;
  name: string;
  elevationMeters?: number;
}

export interface HydratedVectorFeatures {
  roadIndex: TileIndex<RoadSegment>;
  waterLineIndex: TileIndex<WaterLineSegment>;
  waterPolygons: ProjectedPolygon[];
  landCover: ProjectedLandCoverPolygon[];
  peaks: ProjectedPeak[];
  roadLabels: WorldLabel[];
  waterLabels: WorldLabel[];
}

const ROAD_LABEL_MIN_SPACING = 320;
const WATER_LABEL_MIN_SPACING = 320;

function projectRing(ring: [number, number][], bounds: LatLonBounds, mapSize: number): WorldPoint[] {
  return ring.map(([lon, lat]) => lonLatToWorld(lon, lat, bounds, mapSize));
}

function projectPolylineToSegments<M extends object>(
  points: [number, number][],
  bounds: LatLonBounds,
  mapSize: number,
  meta: M
): (Segment & M)[] {
  const projected = projectRing(points, bounds, mapSize);
  const segments: (Segment & M)[] = [];
  for (let i = 0; i < projected.length - 1; i++) {
    segments.push({ x1: projected[i].x, y1: projected[i].y, x2: projected[i + 1].x, y2: projected[i + 1].y, ...meta });
  }
  return segments;
}

/** Places a label at the midpoint of a projected line, angled along the
 * line's local direction there (matches the contour label convention). */
function labelForLine(projected: WorldPoint[], text: string): WorldLabel {
  const midIdx = Math.floor(projected.length / 2);
  const mid = projected[midIdx];
  const next = projected[Math.min(projected.length - 1, midIdx + 1)];
  let angle = Math.atan2(next.y - mid.y, next.x - mid.x);
  if (angle > Math.PI / 2 || angle < -Math.PI / 2) angle += Math.PI;
  return { x: mid.x, y: mid.y, angle, text };
}

function polygonCentroid(ring: WorldPoint[]): WorldPoint {
  let sx = 0;
  let sy = 0;
  for (const p of ring) {
    sx += p.x;
    sy += p.y;
  }
  return { x: sx / ring.length, y: sy / ring.length };
}

export function hydrateVectorFeatures(
  features: VectorFeatureSet | undefined,
  bounds: LatLonBounds,
  mapSize: number,
  tilesPerAxis: number = TILES_PER_AXIS
): HydratedVectorFeatures {
  if (!features) {
    return {
      roadIndex: buildTileIndex<RoadSegment>([], mapSize, tilesPerAxis),
      waterLineIndex: buildTileIndex<WaterLineSegment>([], mapSize, tilesPerAxis),
      waterPolygons: [],
      landCover: [],
      peaks: [],
      roadLabels: [],
      waterLabels: [],
    };
  }

  const roadSegments: RoadSegment[] = [];
  const roadLabels: WorldLabel[] = [];
  for (const road of features.roads) {
    roadSegments.push(...projectPolylineToSegments(road.points, bounds, mapSize, { roadClass: road.roadClass }));
    if (road.name) {
      roadLabels.push(labelForLine(projectRing(road.points, bounds, mapSize), road.name));
    }
  }

  const waterLineSegments: WaterLineSegment[] = [];
  const waterLabels: WorldLabel[] = [];
  for (const line of features.waterLines) {
    waterLineSegments.push(...projectPolylineToSegments(line.points, bounds, mapSize, { waterClass: line.waterClass }));
    if (line.name) {
      waterLabels.push(labelForLine(projectRing(line.points, bounds, mapSize), line.name));
    }
  }

  const waterPolygons: ProjectedPolygon[] = features.waterPolygons.map((poly) => ({
    rings: poly.rings.map((ring) => projectRing(ring, bounds, mapSize)),
  }));
  for (const poly of features.waterPolygons) {
    if (!poly.name || poly.rings.length === 0) continue;
    const centroid = polygonCentroid(projectRing(poly.rings[0], bounds, mapSize));
    waterLabels.push({ x: centroid.x, y: centroid.y, angle: 0, text: poly.name });
  }

  const landCover: ProjectedLandCoverPolygon[] = features.landCover.map((poly) => ({
    landCoverClass: poly.landCoverClass,
    rings: poly.rings.map((ring) => projectRing(ring, bounds, mapSize)),
  }));

  const peaks: ProjectedPeak[] = features.peaks.map((peak) => {
    const p = lonLatToWorld(peak.lon, peak.lat, bounds, mapSize);
    return { x: p.x, y: p.y, name: peak.name, elevationMeters: peak.elevationMeters };
  });

  return {
    roadIndex: buildTileIndex(roadSegments, mapSize, tilesPerAxis),
    waterLineIndex: buildTileIndex(waterLineSegments, mapSize, tilesPerAxis),
    waterPolygons,
    landCover,
    peaks,
    roadLabels: thinLabelsBySpacing(roadLabels, ROAD_LABEL_MIN_SPACING),
    waterLabels: thinLabelsBySpacing(waterLabels, WATER_LABEL_MIN_SPACING),
  };
}
