import { METERS_PER_DEGREE_LAT } from './geo';
import type { SavedRoad } from './types/roads';
import type { RoadFeature, RoadSurfaceClass } from './types/vectorFeatures';

export type RoadWidthSource = 'osm' | 'lanes' | 'default' | 'player-built';
export type RoadSource = 'osm' | 'player';

export interface RoadAnalysis {
  key: string;
  id: string;
  source: RoadSource;
  name: string;
  widthM: number;
  widthSource: RoadWidthSource;
  points: [number, number][];
  totalLanes: number;
  forwardLanes: number;
  backwardLanes: number;
  oneWay: boolean;
  highway?: string;
}

export interface RoadMarkingLine {
  kind: 'center' | 'divider';
  points: [number, number][];
}

const PAVED_SURFACES = new Set([
  'paved', 'asphalt', 'concrete', 'concrete:lanes', 'concrete:plates',
  'paving_stones', 'sett', 'cobblestone',
]);
const UNPAVED_SURFACES = new Set([
  'unpaved', 'compacted', 'fine_gravel', 'gravel', 'pebblestone', 'ground',
  'dirt', 'earth', 'mud', 'sand', 'grass',
]);
const NON_MOTOR_HIGHWAYS = new Set([
  'track', 'path', 'footway', 'cycleway', 'bridleway', 'steps', 'pedestrian',
]);
const MIN_ROAD_WIDTH_M = 1;
const MAX_ROAD_WIDTH_M = 50;
const MAX_LANES = 12;
const LANE_WIDTH_M = 3.5;

export function classifyRoadSurface(raw: string | undefined): RoadSurfaceClass {
  const value = raw?.trim().toLowerCase();
  if (!value) return 'unknown';
  if (PAVED_SURFACES.has(value)) return 'paved';
  if (UNPAVED_SURFACES.has(value)) return 'unpaved';
  return 'unknown';
}

export function parseRoadLaneCount(raw: string | undefined): number | undefined {
  if (!raw || !/^\d+$/.test(raw.trim())) return undefined;
  const value = Number(raw);
  return Number.isSafeInteger(value) && value >= 1 && value <= MAX_LANES ? value : undefined;
}

export function parseRoadOneWay(raw: string | undefined, highway?: string): boolean {
  const value = raw?.trim().toLowerCase();
  if (value === 'yes' || value === 'true' || value === '1' || value === '-1') return true;
  if (value === 'no' || value === 'false' || value === '0') return false;
  return highway === 'motorway' || highway === 'motorway_link';
}

function isMotorRoad(feature: RoadFeature): boolean {
  if (feature.highway) return !NON_MOTOR_HIGHWAYS.has(feature.highway);
  return feature.roadClass !== 'path';
}

export function isPavedRoad(feature: RoadFeature): boolean {
  if (!isMotorRoad(feature)) return false;
  return feature.surfaceClass !== 'unpaved';
}

function defaultWidthM(feature: RoadFeature): number {
  switch (feature.highway) {
    case 'motorway':
    case 'motorway_link': return 14;
    case 'trunk':
    case 'trunk_link':
    case 'primary':
    case 'primary_link':
    case 'secondary':
    case 'secondary_link': return 10.5;
    case 'service':
    case 'living_street': return 5;
    case 'tertiary':
    case 'tertiary_link':
    case 'residential':
    case 'unclassified':
    case 'road': return 7;
    default: return feature.roadClass === 'major' ? 10.5 : 7;
  }
}

function defaultLaneCount(feature: RoadFeature): number {
  if (feature.highway === 'motorway' || feature.highway === 'motorway_link') return 4;
  if (feature.highway === 'service' || feature.highway === 'living_street') return 1;
  return 2;
}

function validWidth(value: number | undefined): value is number {
  return Number.isFinite(value) && value! >= MIN_ROAD_WIDTH_M && value! <= MAX_ROAD_WIDTH_M;
}

function laneLayout(feature: RoadFeature): Pick<RoadAnalysis,
  'totalLanes' | 'forwardLanes' | 'backwardLanes' | 'oneWay'> {
  const oneWay = feature.oneWay === true;
  const directional = (feature.lanesForward ?? 0) + (feature.lanesBackward ?? 0);
  let total = feature.lanes ?? (directional || defaultLaneCount(feature));
  total = Math.max(1, Math.min(MAX_LANES, total));
  if (oneWay) return { totalLanes: total, forwardLanes: total, backwardLanes: 0, oneWay };

  if (directional > 0) {
    let forward = feature.lanesForward ?? Math.max(1, total - (feature.lanesBackward ?? 0));
    let backward = feature.lanesBackward ?? Math.max(1, total - forward);
    total = Math.max(total, forward + backward);
    if (forward + backward > MAX_LANES) {
      const scale = MAX_LANES / (forward + backward);
      forward = Math.max(1, Math.floor(forward * scale));
      backward = Math.max(1, MAX_LANES - forward);
      total = forward + backward;
    }
    return { totalLanes: total, forwardLanes: forward, backwardLanes: backward, oneWay };
  }

  if (total === 1) return { totalLanes: 1, forwardLanes: 1, backwardLanes: 0, oneWay };
  const backward = Math.floor(total / 2);
  return { totalLanes: total, forwardLanes: total - backward, backwardLanes: backward, oneWay };
}

function fallbackRoadName(feature: RoadFeature): string {
  const kind = feature.highway === 'service' ? 'service road'
    : feature.highway === 'living_street' ? 'living street'
      : feature.roadClass === 'major' ? 'major road' : 'road';
  return `Unnamed ${kind}`;
}

export function analyzeImportedRoad(feature: RoadFeature): RoadAnalysis | null {
  if (!isPavedRoad(feature)) return null;
  const lanes = laneLayout(feature);
  const sourceWidth = validWidth(feature.sourceWidthM) ? feature.sourceWidthM : null;
  const hasLaneMetadata = feature.lanes != null || feature.lanesForward != null ||
    feature.lanesBackward != null;
  const widthM = sourceWidth ?? (hasLaneMetadata ? lanes.totalLanes * LANE_WIDTH_M : defaultWidthM(feature));
  return {
    key: `osm:${feature.id}`,
    id: feature.id,
    source: 'osm',
    name: feature.name?.trim() || fallbackRoadName(feature),
    widthM,
    widthSource: sourceWidth != null ? 'osm' : hasLaneMetadata ? 'lanes' : 'default',
    points: feature.points,
    ...lanes,
    highway: feature.highway,
  };
}

export function analyzeBuiltRoad(road: SavedRoad): RoadAnalysis {
  return {
    key: `player:${road.id}`,
    id: road.id,
    source: 'player',
    name: road.name,
    widthM: road.widthM,
    widthSource: 'player-built',
    points: road.points,
    totalLanes: 2,
    forwardLanes: 1,
    backwardLanes: 1,
    oneWay: false,
  };
}

function offsetSegments(points: [number, number][], offsetM: number): [number, number][][] {
  if (Math.abs(offsetM) < 1e-9) return [points];
  const lines: [number, number][][] = [];
  for (let index = 1; index < points.length; index++) {
    const a = points[index - 1], b = points[index];
    const lat = (a[1] + b[1]) / 2;
    const metresLng = Math.max(1, METERS_PER_DEGREE_LAT * Math.cos(lat * Math.PI / 180));
    const dx = (b[0] - a[0]) * metresLng;
    const dy = (b[1] - a[1]) * METERS_PER_DEGREE_LAT;
    const length = Math.hypot(dx, dy);
    if (length < 0.05) continue;
    const ox = -dy / length * offsetM;
    const oy = dx / length * offsetM;
    lines.push([
      [a[0] + ox / metresLng, a[1] + oy / METERS_PER_DEGREE_LAT],
      [b[0] + ox / metresLng, b[1] + oy / METERS_PER_DEGREE_LAT],
    ]);
  }
  return lines;
}

export function roadMarkingLines(road: RoadAnalysis): RoadMarkingLine[] {
  const lines: RoadMarkingLine[] = [];
  const laneWidth = road.widthM / road.totalLanes;
  if (road.oneWay) {
    for (let lane = 1; lane < road.totalLanes; lane++) {
      const offset = -road.widthM / 2 + lane * laneWidth;
      for (const points of offsetSegments(road.points, offset)) lines.push({ kind: 'divider', points });
    }
    return lines;
  }

  const boundary = road.totalLanes === 1 ? 0
    : -road.widthM / 2 + road.backwardLanes * laneWidth;
  for (const points of offsetSegments(road.points, boundary)) lines.push({ kind: 'center', points });
  for (let lane = 1; lane < road.backwardLanes; lane++) {
    const offset = -road.widthM / 2 + lane * laneWidth;
    for (const points of offsetSegments(road.points, offset)) lines.push({ kind: 'divider', points });
  }
  for (let lane = 1; lane < road.forwardLanes; lane++) {
    const offset = boundary + lane * laneWidth;
    for (const points of offsetSegments(road.points, offset)) lines.push({ kind: 'divider', points });
  }
  return lines;
}
