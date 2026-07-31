import { METERS_PER_DEGREE_LAT, haversineMeters } from './geo';
import { fixedGripCapacityPph, fixedGripDerived, liftStats } from './lifts';
import type { AnchorRef, SavedNode, SavedPath } from './skiNodes';
import { difficultyForSlopes, trailAreaM2, trailStats } from './trails';
import type {
  ChairSize,
  LiftClass,
  LiftStatus,
  SavedLift,
  SavedTrail,
  TrailDifficulty,
  TrailStatus,
} from './types';

/**
 * The ski-area network graph: painted runs and placed lifts turned into a
 * connected, directed node/edge map a pathfinder can walk.
 *
 * DOCTRINE
 * - Pure and DOM-free, like trails.ts / lifts.ts / roads.ts. No imports from
 *   src/app/**: the app layer imports from here, never the reverse.
 * - DERIVED, NEVER PERSISTED. The graph is rebuilt from (trails, lifts) on
 *   every load and every geometry edit — the same rule that has sanitizeTrails
 *   recompute cached stats from geometry so they can never drift. Because it
 *   never round-trips through JSON it is free to hold Maps.
 * - TOPOLOGY IN METERS, STATS IN LNG/LAT. The local equirectangular frame
 *   (MetersFrame) decides *"does this cross? is this within tolerance?"* only.
 *   Every reported length/slope/vertical comes back from trailStats, which uses
 *   haversineMeters. That split is deliberate: haversine assumes R=6371000
 *   (~111195 m/deg) while METERS_PER_DEGREE_LAT is 111320, a 0.11% disagreement
 *   that is meaningless for a 30 m snap test but would silently make segment
 *   lengths disagree with SavedTrail.lengthM if the flat frame measured them.
 *
 * WHY PROXIMITY SNAPPING EXISTS
 * A trail centerline is a medial axis (trailPaintEngine.skeletonDiameter), so
 * two runs whose paint visibly merges usually have centerlines that only come
 * *close* — they rarely intersect. Detecting true crossings alone would leave
 * the graph disconnected and defeat the whole purpose. See snapDistance().
 */

// ---------------------------------------------------------------------------
// Public types
// ---------------------------------------------------------------------------

export type NodeId = string; // 'n:0', 'n:1', … assigned after a deterministic sort
export type EdgeId = string; // 't:<trailId>:<part>:<seg>' | 't:…:<seg>:r' | 'x:<pathId>:<seg>' | 'x:…:<seg>:r' | 'l:<liftId>'

/** Local equirectangular meters. TOPOLOGY ONLY — never used for stats. */
export interface MetersFrame {
  lng0: number;
  lat0: number;
  mPerLng: number;
  mPerLat: number;
}

/**
 * Provenance label, by priority lift-base > lift-top > crossing > junction >
 * trail-head > trail-out. Informational only — `liftBases`, `liftTops`,
 * `trailIds`, `incoming` and `outgoing` are the authoritative facts.
 */
export type NetworkNodeKind =
  | 'lift-base'
  | 'lift-top'
  | 'user-node'
  | 'crossing'
  | 'junction'
  | 'trail-head'
  | 'trail-out';

export interface NetworkNode {
  id: NodeId;
  /** Cluster centroid, back-projected to [lng, lat]. */
  lngLat: [number, number];
  /** Mean of the resolved member elevations; null when none resolved. */
  elevM: number | null;
  kind: NetworkNodeKind;
  /** Lift ids whose BOTTOM terminal clustered here (sorted). */
  liftBases: string[];
  /** Lift ids whose TOP terminal clustered here (sorted). */
  liftTops: string[];
  /** Distinct parent trail ids incident here (sorted). */
  trailIds: string[];
  /** SavedNode ids clustered here (sorted). */
  nodeIds: string[];
  /** Distinct parent path ids incident here (sorted). */
  pathIds: string[];
  outgoing: EdgeId[];
  incoming: EdgeId[];
}

export type EdgeCondition = 'open' | 'closed';

interface NetworkEdgeBase {
  id: EdgeId;
  from: NodeId;
  to: NodeId;
  /** Slope length, from trailStats / SavedLift.lengthM. */
  lengthM: number;
  /** Uniform cost surface for the future pathfinder, seconds. */
  travelTimeS: number;
  condition: EdgeCondition;
  /** condition === 'open' && status === 'complete'. */
  open: boolean;
}

export interface TrailEdge extends NetworkEdgeBase {
  kind: 'trail';
  trailId: string;
  trailName: string;
  partIndex: number;
  /** Ordinal along the part, in stored centerline order. */
  segmentIndex: number;
  /**
   * True [lng, lat] geometry, from → to. Deliberately NOT snapped onto the node
   * positions: `node.lngLat` is a cluster centroid up to ~snapDistance/2 away,
   * and moving the path would displace real painted geometry and corrupt the
   * slope of the first/last station pair. Invariant: path[0] lies within
   * snapDistance of node(from).lngLat.
   */
  path: [number, number][];
  /** Parallel to `path`; empty when the parent part's elevations are unresolved. */
  elevM: number[];
  /** Recomputed for THIS segment, not inherited (except when unresolved). */
  difficulty: TrailDifficulty;
  avgSlopeDeg: number;
  maxSlopeDeg: number;
  /** Unsigned max − min along the segment, from trailStats. */
  verticalM: number | null;
  /** Signed elev[0] − elev[last]. NEGATIVE means the segment climbs. */
  netVerticalM: number | null;
  /** Prorated share of the parent PART's footprint area. */
  areaM2: number;
  brushWidthM: number;
  status: TrailStatus;
  planned: boolean;
  elevationResolved: boolean;
  /** Near-flat, or climbing against the parent's downhill order. */
  traverse: boolean;
}

export interface LiftEdge extends NetworkEdgeBase {
  kind: 'lift';
  liftId: string;
  liftName: string;
  liftClass: LiftClass;
  chairSize: ChairSize;
  /** [base, top] in edge direction. */
  path: [[number, number], [number, number]];
  verticalM: number | null;
  rideTimeS: number;
  headwayS: number;
  carrierSpacingM: number;
  carriersOnLine: number;
  capacityPph: number;
  /** PLACEHOLDER. Always 0 — there is no simulation yet. See withLiftQueues(). */
  peopleWaiting: number;
  /** PLACEHOLDER. peopleWaiting / capacityPph * 3600. Always 0 today. */
  waitTimeS: number;
  status: LiftStatus;
  planned: boolean;
  /** Both endpointElevM resolved, so `path` really is [bottom, top]. */
  orientationResolved: boolean;
  /** Distinct parent trail ids leaving this lift's TOP node (sorted). */
  servesTrailIds: string[];
}

export interface PathEdge extends NetworkEdgeBase {
  kind: 'path';
  pathId: string;
  pathName: string;
  /** Ordinal along the path, in stored point order. */
  segmentIndex: number;
  /** True [lng, lat] geometry, from → to. Same node-centroid caveat as TrailEdge.path. */
  path: [number, number][];
  /** Parallel to `path`; empty when the path's elevations are unresolved. */
  elevM: number[];
  /** Recomputed for THIS segment. A path has no stored grade to fall back on,
   *  so an unresolved segment reads 'green' rather than inheriting anything. */
  difficulty: TrailDifficulty;
  avgSlopeDeg: number;
  maxSlopeDeg: number;
  /** Unsigned max − min along the segment, from trailStats. */
  verticalM: number | null;
  /** Signed elev[0] − elev[last]. NEGATIVE means the segment climbs. */
  netVerticalM: number | null;
  widthM: number;
  status: TrailStatus;
  planned: boolean;
  elevationResolved: boolean;
  /** Near-flat, or climbing against the stored from→to order. */
  traverse: boolean;
}

export type NetworkEdge = TrailEdge | LiftEdge | PathEdge;

export interface NetworkDiagnostics {
  crossingCount: number;
  tJunctionCount: number;
  endpointMergeCount: number;
  liftSnapCount: number;
  /** Lift terminals that had to split a trail mid-run to attach. */
  liftSplitCount: number;
  /** Split points collapsed by minSegmentM instead of becoming an edge. */
  mergedSplitCount: number;
  /** '<trailId>:<partIndex>' entries dropped as degenerate or too short. */
  droppedPartIds: string[];
  unresolvedElevationTrailIds: string[];
  unresolvedLiftIds: string[];
  /** Trails no lift top can reach by descending. */
  orphanTrailIds: string[];
  /** Lifts whose top node has no outgoing trail edge. */
  isolatedLiftIds: string[];
  /** Weakly-connected components; 1 means the whole mountain is connected. */
  componentCount: number;
  traverseEdgeCount: number;
  /** Trails with no `anchor` field at all (legacy/pre-feature). Informational only. */
  unanchoredTrailIds: string[];
  /** Trail `anchor` present but its target is missing, drifted beyond
   *  anchorResolveM, or self-referential. */
  unresolvedAnchorTrailIds: string[];
  /** Same as unresolvedAnchorTrailIds, for a path's `from`/`to`. */
  unresolvedAnchorPathIds: string[];
  /** Anchor resolved, but farther than maxClusterRadiusM from its own end —
   *  the union still happened (uncapped), but the node will sit visibly off
   *  the geometry it was declared against. */
  overreachingAnchorIds: string[];
  /** Paths whose `from` and `to` both resolved into the same cluster. */
  degeneratePathIds: string[];
}

export interface SkiNetwork {
  nodes: NetworkNode[];
  /** Trail edges, then path edges, then lift edges. */
  edges: NetworkEdge[];
  nodeById: Map<NodeId, NetworkNode>;
  edgeById: Map<EdgeId, NetworkEdge>;
  /** trailId → its segment edge ids in downhill order (reverse twins excluded). */
  trailEdgeIds: Map<string, EdgeId[]>;
  /** pathId → its segment edge ids in stored order (reverse twins excluded). */
  pathEdgeIds: Map<string, EdgeId[]>;
  liftEdgeIds: Map<string, EdgeId>;
  frame: MetersFrame;
  diagnostics: NetworkDiagnostics;
}

export interface BuildNetworkOptions {
  /** Multiplier on the brush-derived snap distance. */
  snapFactor?: number;
  minSnapM?: number;
  maxSnapM?: number;
  liftSnapM?: number;
  minSegmentM?: number;
  /** Keep 'planning' runs and lifts in the topology as closed edges. */
  includePlanning?: boolean;
  /** Emit a reverse twin edge for near-flat segments. Off by default: the
   *  network is a one-way vector map, but a dead-flat connector is genuinely
   *  skated both ways, so the simulation can turn this on without a rewrite. */
  bidirectionalTraverses?: boolean;
  /** Free-standing map pins to register as nodes. */
  nodes?: SavedNode[];
  /** Footpaths connecting nodes/lifts/runs. */
  paths?: SavedPath[];
  /** Max metres a cached AnchorRef.point may drift from the target's CURRENT
   *  geometry before resolution gives up. Not a topology tolerance — this is
   *  "did the point survive an edit". */
  anchorResolveM?: number;
}

export const NETWORK_DEFAULTS = {
  snapFactor: 1,
  minSnapM: 6,
  maxSnapM: 60,
  liftSnapM: 40,
  minSegmentM: 8,
  coincidentM: 1,
  maxClusterRadiusM: 60,
  anchorResolveM: 50,
} as const;

/** Mirrors the station dedupe rule in roads.ts. */
const STATION_DEDUPE_M = 0.05;
/** Dimensionless, applied to |sin θ| so the test is scale-free. */
const EPS_PARALLEL = 1e-9;
/** Grade below which a segment counts as flat rather than a descent. */
const FLAT_GRADE = Math.tan((0.5 * Math.PI) / 180);
/** Absolute floor on the flat test, so DEM noise never reads as a pitch. */
const FLAT_FLOOR_M = 0.5;

/**
 * PLACEHOLDER descent speeds along slope length, used only to give edges a
 * travelTimeS cost surface. Not a physics model — the simulation replaces them.
 */
export const SKI_SPEED_MPS: Record<TrailDifficulty, number> = {
  green: 6,
  blue: 9,
  black: 11,
  red: 12,
};
export const TRAVERSE_SPEED_MPS = 2;

// ---------------------------------------------------------------------------
// Geometry primitives (local meters frame)
// ---------------------------------------------------------------------------

export interface XY {
  x: number;
  y: number;
}

interface BBox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export function makeFrame(samples: [number, number][]): MetersFrame {
  let sumLng = 0;
  let sumLat = 0;
  let n = 0;
  for (const p of samples) {
    if (!Number.isFinite(p[0]) || !Number.isFinite(p[1])) continue;
    sumLng += p[0];
    sumLat += p[1];
    n++;
  }
  const lng0 = n > 0 ? sumLng / n : 0;
  const lat0 = n > 0 ? sumLat / n : 0;
  return {
    lng0,
    lat0,
    mPerLng: METERS_PER_DEGREE_LAT * Math.cos((lat0 * Math.PI) / 180),
    mPerLat: METERS_PER_DEGREE_LAT,
  };
}

export function toMeters(frame: MetersFrame, p: [number, number]): XY {
  return { x: (p[0] - frame.lng0) * frame.mPerLng, y: (p[1] - frame.lat0) * frame.mPerLat };
}

export function toLngLat(frame: MetersFrame, p: XY): [number, number] {
  return [
    frame.lng0 + (frame.mPerLng !== 0 ? p.x / frame.mPerLng : 0),
    frame.lat0 + p.y / frame.mPerLat,
  ];
}

/**
 * Parametric intersection of two 2-D segments. Null when parallel, collinear,
 * or non-overlapping. The parallel test compares |cross| against |r|·|s|, i.e.
 * |sin θ| < EPS_PARALLEL — dimensionless, so it behaves identically for 2 m and
 * 200 m segments. An absolute epsilon on the determinant would misjudge both.
 */
export function segmentIntersection(
  p0: XY,
  p1: XY,
  q0: XY,
  q1: XY
): { t: number; u: number } | null {
  const rx = p1.x - p0.x;
  const ry = p1.y - p0.y;
  const sx = q1.x - q0.x;
  const sy = q1.y - q0.y;
  const denom = rx * sy - ry * sx;
  const lr = Math.hypot(rx, ry);
  const ls = Math.hypot(sx, sy);
  if (Math.abs(denom) <= EPS_PARALLEL * lr * ls) return null;
  const qpx = q0.x - p0.x;
  const qpy = q0.y - p0.y;
  const t = (qpx * sy - qpy * sx) / denom;
  const u = (qpx * ry - qpy * rx) / denom;
  if (t < 0 || t > 1 || u < 0 || u > 1) return null;
  return { t, u };
}

/** Distance from `p` to segment ab, plus the clamped parameter of the foot. */
export function pointSegmentDistance(p: XY, a: XY, b: XY): { d: number; u: number } {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len2 = dx * dx + dy * dy;
  let u = len2 > 0 ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2 : 0;
  u = u < 0 ? 0 : u > 1 ? 1 : u;
  return { d: Math.hypot(p.x - (a.x + u * dx), p.y - (a.y + u * dy)), u };
}

function bboxOf(points: XY[]): BBox {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of points) {
    if (p.x < minX) minX = p.x;
    if (p.y < minY) minY = p.y;
    if (p.x > maxX) maxX = p.x;
    if (p.y > maxY) maxY = p.y;
  }
  return { minX, minY, maxX, maxY };
}

function bboxOverlaps(a: BBox, b: BBox, pad: number): boolean {
  return (
    a.minX - pad <= b.maxX &&
    a.maxX + pad >= b.minX &&
    a.minY - pad <= b.maxY &&
    a.maxY + pad >= b.minY
  );
}

function segBBoxOverlaps(
  a0: XY,
  a1: XY,
  b0: XY,
  b1: XY,
  pad: number
): boolean {
  return (
    Math.min(a0.x, a1.x) - pad <= Math.max(b0.x, b1.x) &&
    Math.max(a0.x, a1.x) + pad >= Math.min(b0.x, b1.x) &&
    Math.min(a0.y, a1.y) - pad <= Math.max(b0.y, b1.y) &&
    Math.max(a0.y, a1.y) + pad >= Math.min(b0.y, b1.y)
  );
}

// ---------------------------------------------------------------------------
// Union-find with a cluster-diameter cap
// ---------------------------------------------------------------------------

/**
 * Anti-chaining is the whole point of the cap. Naive union-find at a 30 m
 * tolerance chains: five run ends spaced 30 m along a ridge would collapse into
 * one node 120 m across. Refusing any union that pushes the merged bounding box
 * past `maxRadiusM` keeps clusters physically plausible while still merging
 * genuine 3- and 4-way junctions.
 */
class ClusterSet {
  private parent: number[];
  private rank: number[];
  private box: BBox[];

  constructor(points: XY[]) {
    this.parent = points.map((_, i) => i);
    this.rank = points.map(() => 0);
    this.box = points.map((p) => ({ minX: p.x, minY: p.y, maxX: p.x, maxY: p.y }));
  }

  find(i: number): number {
    let root = i;
    while (this.parent[root] !== root) root = this.parent[root];
    while (this.parent[i] !== root) {
      const next = this.parent[i];
      this.parent[i] = root;
      i = next;
    }
    return root;
  }

  /** Merge unconditionally. Used for geometrically forced merges. */
  union(a: number, b: number): boolean {
    return this.link(this.find(a), this.find(b));
  }

  /** Merge only when the result stays inside `maxRadiusM`. */
  unionCapped(a: number, b: number, maxRadiusM: number): boolean {
    const ra = this.find(a);
    const rb = this.find(b);
    if (ra === rb) return false;
    const merged = this.mergedBox(ra, rb);
    const diag = Math.hypot(merged.maxX - merged.minX, merged.maxY - merged.minY);
    if (diag > maxRadiusM) return false;
    return this.link(ra, rb);
  }

  private mergedBox(ra: number, rb: number): BBox {
    const a = this.box[ra];
    const b = this.box[rb];
    return {
      minX: Math.min(a.minX, b.minX),
      minY: Math.min(a.minY, b.minY),
      maxX: Math.max(a.maxX, b.maxX),
      maxY: Math.max(a.maxY, b.maxY),
    };
  }

  private link(ra: number, rb: number): boolean {
    if (ra === rb) return false;
    const merged = this.mergedBox(ra, rb);
    let root = ra;
    let child = rb;
    if (this.rank[ra] < this.rank[rb]) {
      root = rb;
      child = ra;
    } else if (this.rank[ra] === this.rank[rb]) {
      this.rank[ra]++;
    }
    this.parent[child] = root;
    this.box[root] = merged;
    return true;
  }
}

// ---------------------------------------------------------------------------
// Internal working structures
// ---------------------------------------------------------------------------

interface Polyline {
  key: string; // '<trailId>:<partIndex>' | 'path:<pathId>'
  entityKind: 'trail' | 'path';
  entityId: string; // trailId or pathId
  /** Trail-only; present when entityKind === 'trail'. */
  trailId?: string;
  partIndex?: number;
  trail?: SavedTrail;
  /** Path-only; present when entityKind === 'path'. */
  path?: SavedPath;
  lngLat: [number, number][];
  m: XY[];
  /** Empty when this part's elevations are unresolved. */
  elevM: number[];
  /** Cumulative HORIZONTAL chainage, cum[0] = 0. */
  cum: number[];
  totalM: number;
  /** This part's own footprint area. Always 0 for a path. */
  areaM2: number;
  brushWidthM: number;
  bbox: BBox;
}

interface Site {
  key: string;
  m: XY;
  lngLat: [number, number];
  elevM: number | null;
  /** Set for sites that sit on a trail/path centerline. */
  polylineIndex: number;
  /** Chainage along that polyline; -1 for lift terminals and user nodes. */
  s: number;
  liftBase?: string;
  liftTop?: string;
  /** Set for the site of a registered SavedNode. */
  userNode?: string;
  fromCrossing: boolean;
}

// ---------------------------------------------------------------------------
// Polyline construction
// ---------------------------------------------------------------------------

function locate(pl: Polyline, s: number): { i: number; t: number } {
  const last = pl.lngLat.length - 2;
  if (s <= 0) return { i: 0, t: 0 };
  if (s >= pl.totalM) return { i: last, t: 1 };
  let lo = 0;
  let hi = pl.cum.length - 1;
  while (lo < hi - 1) {
    const mid = (lo + hi) >> 1;
    if (pl.cum[mid] <= s) lo = mid;
    else hi = mid;
  }
  const segLen = pl.cum[lo + 1] - pl.cum[lo];
  return { i: lo, t: segLen > 0 ? (s - pl.cum[lo]) / segLen : 0 };
}

function pointAt(pl: Polyline, s: number): [number, number] {
  const { i, t } = locate(pl, s);
  const a = pl.lngLat[i];
  const b = pl.lngLat[i + 1];
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
}

function elevAt(pl: Polyline, s: number): number | null {
  if (pl.elevM.length !== pl.lngLat.length) return null;
  const { i, t } = locate(pl, s);
  return pl.elevM[i] + (pl.elevM[i + 1] - pl.elevM[i]) * t;
}

function sliceBetween(
  pl: Polyline,
  a: number,
  b: number
): { lngLat: [number, number][]; elevM: number[] } {
  const resolved = pl.elevM.length === pl.lngLat.length;
  const lngLat: [number, number][] = [pointAt(pl, a)];
  const elevM: number[] = resolved ? [elevAt(pl, a) as number] : [];
  const end = locate(pl, b).i;
  for (let k = locate(pl, a).i + 1; k <= end; k++) {
    if (pl.cum[k] > a + 1e-6 && pl.cum[k] < b - 1e-6) {
      lngLat.push(pl.lngLat[k]);
      if (resolved) elevM.push(pl.elevM[k]);
    }
  }
  lngLat.push(pointAt(pl, b));
  if (resolved) elevM.push(elevAt(pl, b) as number);
  return { lngLat, elevM };
}

function buildPolylines(
  frame: MetersFrame,
  trails: SavedTrail[],
  minSegmentM: number,
  droppedPartIds: string[],
  unresolvedElevationTrailIds: Set<string>
): Polyline[] {
  const out: Polyline[] = [];
  for (const trail of trails) {
    trail.parts.forEach((part, partIndex) => {
      const key = `${trail.id}:${partIndex}`;
      const lngLat: [number, number][] = [];
      const elevSrc: number[] = [];
      const hasElev =
        part.centerlineElevM.length === part.centerline.length &&
        part.centerlineElevM.every((e) => Number.isFinite(e));
      for (let i = 0; i < part.centerline.length; i++) {
        const p = part.centerline[i];
        if (!Number.isFinite(p[0]) || !Number.isFinite(p[1])) continue;
        const prev = lngLat[lngLat.length - 1];
        if (prev && haversineMeters(prev, p) < STATION_DEDUPE_M) continue;
        lngLat.push([p[0], p[1]]);
        if (hasElev) elevSrc.push(part.centerlineElevM[i]);
      }
      if (!hasElev) unresolvedElevationTrailIds.add(trail.id);
      if (lngLat.length < 2) {
        droppedPartIds.push(key);
        return;
      }
      const cum = [0];
      for (let i = 1; i < lngLat.length; i++) {
        cum.push(cum[i - 1] + haversineMeters(lngLat[i - 1], lngLat[i]));
      }
      const totalM = cum[cum.length - 1];
      if (totalM < minSegmentM) {
        droppedPartIds.push(key);
        return;
      }
      const m = lngLat.map((p) => toMeters(frame, p));
      out.push({
        key,
        entityKind: 'trail',
        entityId: trail.id,
        trailId: trail.id,
        partIndex,
        trail,
        lngLat,
        m,
        elevM: hasElev ? elevSrc : [],
        cum,
        totalM,
        areaM2: trailAreaM2([part]),
        brushWidthM: trail.brushWidthM,
        bbox: bboxOf(m),
      });
    });
  }
  return out;
}

/**
 * Path counterpart of buildPolylines. A path has one polyline per SavedPath
 * (no parts), zero footprint area, and its stored widthM stands in for
 * brushWidthM — that is what feeds snapDistance, so a path participates in
 * crossing/T-junction/endpoint-merge detection exactly like a trail part.
 */
function buildPathPolylines(
  frame: MetersFrame,
  paths: SavedPath[],
  minSegmentM: number
): Polyline[] {
  const out: Polyline[] = [];
  for (const path of paths) {
    const key = `path:${path.id}`;
    const lngLat: [number, number][] = [];
    const elevSrc: number[] = [];
    const hasElev =
      path.pointElevM.length === path.points.length &&
      path.pointElevM.every((e) => Number.isFinite(e));
    for (let i = 0; i < path.points.length; i++) {
      const p = path.points[i];
      if (!Number.isFinite(p[0]) || !Number.isFinite(p[1])) continue;
      const prev = lngLat[lngLat.length - 1];
      if (prev && haversineMeters(prev, p) < STATION_DEDUPE_M) continue;
      lngLat.push([p[0], p[1]]);
      if (hasElev) elevSrc.push(path.pointElevM[i]);
    }
    if (lngLat.length < 2) continue;
    const cum = [0];
    for (let i = 1; i < lngLat.length; i++) {
      cum.push(cum[i - 1] + haversineMeters(lngLat[i - 1], lngLat[i]));
    }
    const totalM = cum[cum.length - 1];
    if (totalM < minSegmentM) continue;
    const m = lngLat.map((p) => toMeters(frame, p));
    out.push({
      key,
      entityKind: 'path',
      entityId: path.id,
      path,
      lngLat,
      m,
      elevM: hasElev ? elevSrc : [],
      cum,
      totalM,
      areaM2: 0,
      brushWidthM: path.widthM,
      bbox: bboxOf(m),
    });
  }
  return out;
}

// ---------------------------------------------------------------------------
// Build
// ---------------------------------------------------------------------------

export function buildSkiNetwork(
  trails: SavedTrail[],
  lifts: SavedLift[],
  options?: BuildNetworkOptions
): SkiNetwork {
  const opt = {
    snapFactor: options?.snapFactor ?? NETWORK_DEFAULTS.snapFactor,
    minSnapM: options?.minSnapM ?? NETWORK_DEFAULTS.minSnapM,
    maxSnapM: options?.maxSnapM ?? NETWORK_DEFAULTS.maxSnapM,
    liftSnapM: options?.liftSnapM ?? NETWORK_DEFAULTS.liftSnapM,
    minSegmentM: options?.minSegmentM ?? NETWORK_DEFAULTS.minSegmentM,
    includePlanning: options?.includePlanning ?? true,
    bidirectionalTraverses: options?.bidirectionalTraverses ?? false,
    anchorResolveM: options?.anchorResolveM ?? NETWORK_DEFAULTS.anchorResolveM,
  };

  const diagnostics: NetworkDiagnostics = {
    crossingCount: 0,
    tJunctionCount: 0,
    endpointMergeCount: 0,
    liftSnapCount: 0,
    liftSplitCount: 0,
    mergedSplitCount: 0,
    droppedPartIds: [],
    unresolvedElevationTrailIds: [],
    unresolvedLiftIds: [],
    orphanTrailIds: [],
    isolatedLiftIds: [],
    componentCount: 0,
    traverseEdgeCount: 0,
    unanchoredTrailIds: [],
    unresolvedAnchorTrailIds: [],
    unresolvedAnchorPathIds: [],
    overreachingAnchorIds: [],
    degeneratePathIds: [],
  };

  // First wins on a duplicate id, so the graph never carries two of anything.
  const trailList: SavedTrail[] = [];
  const seenTrail = new Set<string>();
  for (const t of [...trails].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    if (seenTrail.has(t.id)) continue;
    if (!opt.includePlanning && t.status === 'planning') continue;
    seenTrail.add(t.id);
    trailList.push(t);
  }
  const liftList: SavedLift[] = [];
  const seenLift = new Set<string>();
  for (const l of [...lifts].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    if (seenLift.has(l.id)) continue;
    if (!opt.includePlanning && l.status === 'planning') continue;
    seenLift.add(l.id);
    liftList.push(l);
  }
  const nodeList: SavedNode[] = [];
  const seenNode = new Set<string>();
  for (const n of [...(options?.nodes ?? [])].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    if (seenNode.has(n.id)) continue;
    seenNode.add(n.id);
    nodeList.push(n);
  }
  const pathList: SavedPath[] = [];
  const seenPath = new Set<string>();
  for (const p of [...(options?.paths ?? [])].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    if (seenPath.has(p.id)) continue;
    if (!opt.includePlanning && p.status === 'planning') continue;
    seenPath.add(p.id);
    pathList.push(p);
  }

  const frameSamples: [number, number][] = [];
  for (const t of trailList) for (const p of t.parts) for (const c of p.centerline) frameSamples.push(c);
  for (const l of liftList) frameSamples.push(l.points[0], l.points[1]);
  for (const n of nodeList) frameSamples.push(n.point);
  for (const p of pathList) for (const pt of p.points) frameSamples.push(pt);
  const frame = makeFrame(frameSamples);

  const unresolvedElev = new Set<string>();
  const trailPolylines = buildPolylines(
    frame,
    trailList,
    opt.minSegmentM,
    diagnostics.droppedPartIds,
    unresolvedElev
  );
  diagnostics.unresolvedElevationTrailIds = [...unresolvedElev].sort();
  const pathPolylines = buildPathPolylines(frame, pathList, opt.minSegmentM);
  const polylines: Polyline[] = [...trailPolylines, ...pathPolylines];
  const polylineIndexByKey = new Map<string, number>();
  polylines.forEach((pl, idx) => polylineIndexByKey.set(pl.key, idx));
  const polylinesByEntity = new Map<string, number[]>();
  polylines.forEach((pl, idx) => {
    const k = `${pl.entityKind}:${pl.entityId}`;
    const arr = polylinesByEntity.get(k);
    if (arr) arr.push(idx);
    else polylinesByEntity.set(k, [idx]);
  });

  const snapDistance = (a: Polyline, b: Polyline): number => {
    // A medial axis lies at most W/2 from its own footprint edge, so two
    // footprints of widths Wa and Wb touch exactly when their centerlines are
    // (Wa + Wb) / 2 apart. This is that threshold, not a fudge factor.
    const raw = ((a.brushWidthM + b.brushWidthM) / 2) * opt.snapFactor;
    return Math.min(opt.maxSnapM, Math.max(opt.minSnapM, raw));
  };

  // -- sites & links --------------------------------------------------------

  const sites = new Map<string, Site>();
  const links: { a: string; b: string }[] = [];

  const trailSiteKey = (pi: number, s: number) => `p${pi}@${s.toFixed(3)}`;

  function addTrailSite(pi: number, s: number, fromCrossing: boolean): string {
    const pl = polylines[pi];
    const clamped = Math.min(pl.totalM, Math.max(0, s));
    const key = trailSiteKey(pi, clamped);
    const existing = sites.get(key);
    if (existing) {
      if (fromCrossing) existing.fromCrossing = true;
      return key;
    }
    const lngLat = pointAt(pl, clamped);
    sites.set(key, {
      key,
      m: toMeters(frame, lngLat),
      lngLat,
      elevM: elevAt(pl, clamped),
      polylineIndex: pi,
      s: clamped,
      fromCrossing,
      });
    return key;
  }

  function addLiftSite(
    liftId: string,
    end: 'base' | 'top',
    p: [number, number],
    elevM: number | null
  ): string {
    const key = end === 'base' ? `lb:${liftId}` : `lt:${liftId}`;
    sites.set(key, {
      key,
      m: toMeters(frame, p),
      lngLat: p,
      elevM,
      polylineIndex: -1,
      s: -1,
      liftBase: end === 'base' ? liftId : undefined,
      liftTop: end === 'top' ? liftId : undefined,
      fromCrossing: false,
    });
    return key;
  }

  function addUserNodeSite(node: SavedNode): string {
    const key = `nd:${node.id}`;
    sites.set(key, {
      key,
      m: toMeters(frame, node.point),
      lngLat: node.point,
      elevM: node.elevM,
      polylineIndex: -1,
      s: -1,
      userNode: node.id,
      fromCrossing: false,
    });
    return key;
  }

  // Every polyline always owns its two endpoints.
  for (let i = 0; i < polylines.length; i++) {
    addTrailSite(i, 0, false);
    addTrailSite(i, polylines[i].totalM, false);
  }

  // Pass 1 — true X crossings between centerlines.
  //
  // NOTE: trail centerlines are NEVER intersected with lift lines. Skiers cross
  // under lifts; only lift TERMINALS attach to the trail network (pass 4).
  for (let i = 0; i < polylines.length; i++) {
    for (let j = i + 1; j < polylines.length; j++) {
      const a = polylines[i];
      const b = polylines[j];
      // Same part never self-tests: skeletonDiameter returns a simple path, so
      // any apparent self-crossing is a smoothing artifact. Splitting there
      // would fabricate a loop letting the pathfinder short-circuit the run.
      if (a.key === b.key) continue;
      const pad = snapDistance(a, b);
      if (!bboxOverlaps(a.bbox, b.bbox, pad)) continue;
      for (let ai = 0; ai < a.m.length - 1; ai++) {
        for (let bi = 0; bi < b.m.length - 1; bi++) {
          if (!segBBoxOverlaps(a.m[ai], a.m[ai + 1], b.m[bi], b.m[bi + 1], 0)) continue;
          const hit = segmentIntersection(a.m[ai], a.m[ai + 1], b.m[bi], b.m[bi + 1]);
          if (!hit) continue;
          const sa = a.cum[ai] + hit.t * (a.cum[ai + 1] - a.cum[ai]);
          const sb = b.cum[bi] + hit.u * (b.cum[bi + 1] - b.cum[bi]);
          links.push({ a: addTrailSite(i, sa, true), b: addTrailSite(j, sb, true) });
          diagnostics.crossingCount++;
        }
      }
    }
  }

  // Pass 2 — endpoint → interior (the T-junction that keeps the graph connected).
  for (let i = 0; i < polylines.length; i++) {
    const a = polylines[i];
    for (const endS of [0, a.totalM]) {
      const p = toMeters(frame, pointAt(a, endS));
      for (let j = 0; j < polylines.length; j++) {
        if (i === j) continue;
        const b = polylines[j];
        if (a.key === b.key) continue;
        const tol = snapDistance(a, b);
        if (!bboxOverlaps({ minX: p.x, minY: p.y, maxX: p.x, maxY: p.y }, b.bbox, tol)) continue;
        // Only the SINGLE closest foot per (endpoint, target) pair — otherwise
        // one endpoint near a switchback splits the target three times.
        let best: { d: number; s: number } | null = null;
        for (let bi = 0; bi < b.m.length - 1; bi++) {
          const { d, u } = pointSegmentDistance(p, b.m[bi], b.m[bi + 1]);
          if (d > tol) continue;
          const s = b.cum[bi] + u * (b.cum[bi + 1] - b.cum[bi]);
          if (!best || d < best.d - 1e-9 || (Math.abs(d - best.d) <= 1e-9 && s < best.s)) {
            best = { d, s };
          }
        }
        if (!best) continue;
        // A foot landing near b's own end simply collapses into b's endpoint
        // site later, which is exactly the endpoint↔endpoint merge.
        links.push({ a: addTrailSite(i, endS, false), b: addTrailSite(j, best.s, false) });
        diagnostics.tJunctionCount++;
      }
    }
  }

  // Pass 3 — endpoint ↔ endpoint.
  const endpointRefs: { pi: number; s: number; m: XY }[] = [];
  for (let i = 0; i < polylines.length; i++) {
    endpointRefs.push({ pi: i, s: 0, m: polylines[i].m[0] });
    endpointRefs.push({ pi: i, s: polylines[i].totalM, m: polylines[i].m[polylines[i].m.length - 1] });
  }
  for (let i = 0; i < endpointRefs.length; i++) {
    for (let j = i + 1; j < endpointRefs.length; j++) {
      const ea = endpointRefs[i];
      const eb = endpointRefs[j];
      // The two ends of one run are never joined — a run is not a loop. Ends of
      // different PARTS of the same trail are: a run painted as two blobs that
      // touch is genuinely one run.
      if (ea.pi === eb.pi) continue;
      const tol = snapDistance(polylines[ea.pi], polylines[eb.pi]);
      if (Math.hypot(ea.m.x - eb.m.x, ea.m.y - eb.m.y) > tol) continue;
      links.push({ a: addTrailSite(ea.pi, ea.s, false), b: addTrailSite(eb.pi, eb.s, false) });
      diagnostics.endpointMergeCount++;
    }
  }

  // Pass 4 — lift terminals. Runs last so a terminal can land on a junction
  // that passes 1-3 already created.
  interface LiftPlan {
    lift: SavedLift;
    baseKey: string;
    topKey: string;
    basePoint: [number, number];
    topPoint: [number, number];
    baseElevM: number | null;
    topElevM: number | null;
    orientationResolved: boolean;
  }
  const liftPlans: LiftPlan[] = [];
  for (const lift of liftList) {
    const stats = liftStats(lift.points, lift.endpointElevM);
    const orientationResolved = stats.topIndex !== null;
    if (!orientationResolved) diagnostics.unresolvedLiftIds.push(lift.id);
    const flip = stats.topIndex === 0;
    const basePoint = flip ? lift.points[1] : lift.points[0];
    const topPoint = flip ? lift.points[0] : lift.points[1];
    const baseElevM = flip ? lift.endpointElevM[1] : lift.endpointElevM[0];
    const topElevM = flip ? lift.endpointElevM[0] : lift.endpointElevM[1];
    const baseKey = addLiftSite(lift.id, 'base', basePoint, baseElevM);
    const topKey = addLiftSite(lift.id, 'top', topPoint, topElevM);
    liftPlans.push({
      lift,
      baseKey,
      topKey,
      basePoint,
      topPoint,
      baseElevM,
      topElevM,
      orientationResolved,
    });

    // Preference ladder: the matching trail end first (a top terminal wants run
    // HEADS, a base wants run FEET), then any nearby site, then force a split.
    const attach = (
      terminalKey: string,
      point: [number, number],
      prefer: 'head' | 'foot'
    ): void => {
      const pm = toMeters(frame, point);
      let bestEnd: { key: string; d: number } | null = null;
      let bestAny: { key: string; d: number } | null = null;
      for (const site of [...sites.values()].sort((x, y) => (x.key < y.key ? -1 : 1))) {
        if (site.polylineIndex < 0) continue;
        const d = Math.hypot(site.m.x - pm.x, site.m.y - pm.y);
        if (d > opt.liftSnapM) continue;
        const pl = polylines[site.polylineIndex];
        const isHead = site.s <= 1e-6;
        const isFoot = site.s >= pl.totalM - 1e-6;
        const matches = prefer === 'head' ? isHead : isFoot;
        if (matches && (!bestEnd || d < bestEnd.d)) bestEnd = { key: site.key, d };
        if (!bestAny || d < bestAny.d) bestAny = { key: site.key, d };
      }
      const chosen = bestEnd ?? bestAny;
      if (chosen) {
        links.push({ a: terminalKey, b: chosen.key });
        diagnostics.liftSnapCount++;
        return;
      }
      // Nothing to share — but a run may still pass close by. Split it so the
      // skier can unload onto a mid-mountain traverse.
      let bestSplit: { pi: number; s: number; d: number } | null = null;
      for (let i = 0; i < polylines.length; i++) {
        const pl = polylines[i];
        if (!bboxOverlaps({ minX: pm.x, minY: pm.y, maxX: pm.x, maxY: pm.y }, pl.bbox, opt.liftSnapM)) {
          continue;
        }
        for (let si = 0; si < pl.m.length - 1; si++) {
          const { d, u } = pointSegmentDistance(pm, pl.m[si], pl.m[si + 1]);
          if (d > opt.liftSnapM) continue;
          const s = pl.cum[si] + u * (pl.cum[si + 1] - pl.cum[si]);
          if (!bestSplit || d < bestSplit.d - 1e-9) bestSplit = { pi: i, s, d };
        }
      }
      if (bestSplit) {
        links.push({ a: terminalKey, b: addTrailSite(bestSplit.pi, bestSplit.s, false) });
        diagnostics.liftSnapCount++;
        diagnostics.liftSplitCount++;
      }
    };
    attach(topKey, topPoint, 'head');
    attach(baseKey, basePoint, 'foot');
  }

  // Pass 5 — terminal ↔ terminal, so two lifts meeting at a mid-mountain
  // transfer share one node. Runs after the lift loop because `attach` only
  // considers trail sites; without this, terminals would only merge when they
  // happened to fall inside the 1 m coincident radius.
  const terminalRefs = liftPlans.flatMap((p) => [
    { key: p.baseKey, liftId: p.lift.id, m: toMeters(frame, p.basePoint) },
    { key: p.topKey, liftId: p.lift.id, m: toMeters(frame, p.topPoint) },
  ]);
  for (let i = 0; i < terminalRefs.length; i++) {
    for (let j = i + 1; j < terminalRefs.length; j++) {
      const a = terminalRefs[i];
      const b = terminalRefs[j];
      // Never collapse a lift onto itself, however short it was drawn.
      if (a.liftId === b.liftId) continue;
      if (Math.hypot(a.m.x - b.m.x, a.m.y - b.m.y) > opt.liftSnapM) continue;
      links.push({ a: a.key, b: b.key });
      diagnostics.liftSnapCount++;
    }
  }

  // -- user-declared node sites ---------------------------------------------
  //
  // Registered before anchor resolution so a `kind: 'node'` anchor has
  // something to resolve to, and so every SavedNode becomes a NetworkNode
  // even when nothing ever attaches to it (zero incident edges).
  for (const node of nodeList) addUserNodeSite(node);

  // Pass 6 — explicit anchors. Runs after every geometric inference pass (so
  // an anchor can land on a junction one of those passes already created) and
  // before the split-collapse/clustering stage below.
  //
  // Unlike every other union in this file, an anchor union is pushed into
  // `forcedLinks` and therefore unions UNCAPPED, even when the two ends are
  // farther apart than maxClusterRadiusM. The cap exists to stop many
  // independent PROXIMITY HEURISTICS from chaining along a ridge; a single
  // targeted user assertion between two named entities cannot chain that way,
  // and silently refusing a user's declared connection in a design tool would
  // be wrong. When a resolved anchor lands far enough that the cap would have
  // refused it, `overreachingAnchorIds` records it so the UI can warn that
  // the node will sit visibly off the geometry.
  const forcedLinks: { a: string; b: string }[] = [];
  const unanchoredTrailIdSet = new Set<string>();
  const unresolvedAnchorTrailIdSet = new Set<string>();
  const unresolvedAnchorPathIdSet = new Set<string>();
  const overreachingAnchorIdSet = new Set<string>();

  // Resolves an AnchorRef to a site key against CURRENT geometry — never to
  // another entity's anchor, so A→B→A circular anchors can never recurse.
  function resolveAnchorSiteKey(
    anchor: AnchorRef,
    self: { kind: 'trail' | 'path' | 'node'; id: string }
  ): { key: string } | 'self' | null {
    if (anchor.kind === 'trail' && self.kind === 'trail' && anchor.trailId === self.id) return 'self';
    if (anchor.kind === 'path' && self.kind === 'path' && anchor.pathId === self.id) return 'self';
    if (anchor.kind === 'node' && self.kind === 'node' && anchor.nodeId === self.id) return 'self';
    if (anchor.kind === 'lift') {
      const key = anchor.end === 'top' ? `lt:${anchor.liftId}` : `lb:${anchor.liftId}`;
      return sites.has(key) ? { key } : null;
    }
    if (anchor.kind === 'node') {
      const key = `nd:${anchor.nodeId}`;
      return sites.has(key) ? { key } : null;
    }
    const entityKey = anchor.kind === 'trail' ? `trail:${anchor.trailId}` : `path:${anchor.pathId}`;
    const candidates = polylinesByEntity.get(entityKey) ?? [];
    if (candidates.length === 0) return null;
    const pm = toMeters(frame, anchor.point);
    let best: { pi: number; s: number; d: number } | null = null;
    for (const pi of candidates) {
      const cpl = polylines[pi];
      for (let si = 0; si < cpl.m.length - 1; si++) {
        const { d, u } = pointSegmentDistance(pm, cpl.m[si], cpl.m[si + 1]);
        if (!best || d < best.d - 1e-9) {
          const s = cpl.cum[si] + u * (cpl.cum[si + 1] - cpl.cum[si]);
          best = { pi, s, d };
        }
      }
    }
    if (!best || best.d > opt.anchorResolveM) return null;
    return { key: addTrailSite(best.pi, best.s, false) };
  }

  function linkAnchor(ownKey: string, resolved: { key: string }, ownerId: string): void {
    forcedLinks.push({ a: ownKey, b: resolved.key });
    const ownSite = sites.get(ownKey);
    const resolvedSite = sites.get(resolved.key);
    if (ownSite && resolvedSite) {
      const distM = Math.hypot(ownSite.m.x - resolvedSite.m.x, ownSite.m.y - resolvedSite.m.y);
      if (distM > NETWORK_DEFAULTS.maxClusterRadiusM) overreachingAnchorIdSet.add(ownerId);
    }
  }

  for (const trail of trailList) {
    if (!trail.anchor) {
      unanchoredTrailIdSet.add(trail.id);
      continue;
    }
    const ownPi = polylineIndexByKey.get(`${trail.id}:0`);
    if (ownPi === undefined) {
      unresolvedAnchorTrailIdSet.add(trail.id);
      continue;
    }
    const ownKey = trailSiteKey(ownPi, 0);
    const resolved = resolveAnchorSiteKey(trail.anchor, { kind: 'trail', id: trail.id });
    if (resolved === 'self' || resolved === null) {
      unresolvedAnchorTrailIdSet.add(trail.id);
      continue;
    }
    linkAnchor(ownKey, resolved, trail.id);
  }

  for (const p of pathList) {
    const ownPi = polylineIndexByKey.get(`path:${p.id}`);
    if (ownPi === undefined) {
      unresolvedAnchorPathIdSet.add(p.id);
      continue;
    }
    const ownPl = polylines[ownPi];
    const headKey = trailSiteKey(ownPi, 0);
    const footKey = trailSiteKey(ownPi, ownPl.totalM);
    let unresolved = false;

    const fromResolved = resolveAnchorSiteKey(p.from, { kind: 'path', id: p.id });
    if (fromResolved === 'self' || fromResolved === null) unresolved = true;
    else linkAnchor(headKey, fromResolved, p.id);

    const toResolved = resolveAnchorSiteKey(p.to, { kind: 'path', id: p.id });
    if (toResolved === 'self' || toResolved === null) unresolved = true;
    else linkAnchor(footKey, toResolved, p.id);

    if (unresolved) unresolvedAnchorPathIdSet.add(p.id);
  }

  // A node dropped ON a run, lift or path records what it sits on. Honour that
  // here, or the node would become an isolated island in the graph — visibly a
  // junction on the map, but connected to nothing.
  for (const node of nodeList) {
    if (!node.anchor) continue;
    const resolved = resolveAnchorSiteKey(node.anchor, { kind: 'node', id: node.id });
    if (resolved === 'self' || resolved === null) continue;
    linkAnchor(`nd:${node.id}`, resolved, node.id);
  }

  diagnostics.unanchoredTrailIds = [...unanchoredTrailIdSet].sort();
  diagnostics.unresolvedAnchorTrailIds = [...unresolvedAnchorTrailIdSet].sort();
  diagnostics.unresolvedAnchorPathIds = [...unresolvedAnchorPathIdSet].sort();
  diagnostics.overreachingAnchorIds = [...overreachingAnchorIdSet].sort();

  // -- collapse sub-minSegmentM splits -------------------------------------
  //
  // This is what kills the whole zero-length-edge bug class at the source: a
  // degenerate split becomes a MERGE, never an edge. It also turns "run A
  // overshoots run B by 2 m" into a clean junction instead of a 2 m stub.
  const keptByPolyline: string[][] = polylines.map(() => []);
  for (let i = 0; i < polylines.length; i++) {
    const own = [...sites.values()]
      .filter((s) => s.polylineIndex === i)
      .sort((a, b) => a.s - b.s || (a.key < b.key ? -1 : 1));
    if (own.length < 2) continue;
    const first = own[0];
    const last = own[own.length - 1];
    const kept: Site[] = [first];
    for (let k = 1; k < own.length - 1; k++) {
      if (own[k].s - kept[kept.length - 1].s >= opt.minSegmentM) {
        kept.push(own[k]);
      } else {
        forcedLinks.push({ a: own[k].key, b: kept[kept.length - 1].key });
        diagnostics.mergedSplitCount++;
      }
    }
    while (kept.length > 1 && last.s - kept[kept.length - 1].s < opt.minSegmentM) {
      const dropped = kept.pop() as Site;
      forcedLinks.push({ a: dropped.key, b: last.key });
      diagnostics.mergedSplitCount++;
    }
    kept.push(last);
    keptByPolyline[i] = kept.map((s) => s.key);
  }

  // -- clustering -----------------------------------------------------------

  const siteKeys = [...sites.keys()].sort();
  const siteIndex = new Map<string, number>();
  siteKeys.forEach((k, i) => siteIndex.set(k, i));
  const siteList = siteKeys.map((k) => sites.get(k) as Site);
  const clusters = new ClusterSet(siteList.map((s) => s.m));

  // Forced merges first and uncapped — they are geometrically required.
  for (const l of forcedLinks) {
    const a = siteIndex.get(l.a);
    const b = siteIndex.get(l.b);
    if (a !== undefined && b !== undefined) clusters.union(a, b);
  }

  // Then everything else, nearest first, subject to the diameter cap. Sorting
  // by distance is what makes the greedy result order-independent.
  const candidates: { a: number; b: number; d: number }[] = [];
  const pushCandidate = (a: number, b: number) => {
    const d = Math.hypot(siteList[a].m.x - siteList[b].m.x, siteList[a].m.y - siteList[b].m.y);
    candidates.push(a < b ? { a, b, d } : { a: b, b: a, d });
  };
  for (const l of links) {
    const a = siteIndex.get(l.a);
    const b = siteIndex.get(l.b);
    if (a !== undefined && b !== undefined && a !== b) pushCandidate(a, b);
  }
  for (let i = 0; i < siteList.length; i++) {
    for (let j = i + 1; j < siteList.length; j++) {
      const d = Math.hypot(siteList[i].m.x - siteList[j].m.x, siteList[i].m.y - siteList[j].m.y);
      if (d <= NETWORK_DEFAULTS.coincidentM) candidates.push({ a: i, b: j, d });
    }
  }
  candidates.sort(
    (x, y) => x.d - y.d || x.a - y.a || x.b - y.b
  );
  for (const c of candidates) clusters.unionCapped(c.a, c.b, NETWORK_DEFAULTS.maxClusterRadiusM);

  // -- nodes ----------------------------------------------------------------

  interface Cluster {
    members: number[];
    sum: XY;
    elevSum: number;
    elevCount: number;
    liftBases: Set<string>;
    liftTops: Set<string>;
    trailIds: Set<string>;
    nodeIds: Set<string>;
    pathIds: Set<string>;
    fromCrossing: boolean;
    headOnly: boolean;
  }
  const byRoot = new Map<number, Cluster>();
  for (let i = 0; i < siteList.length; i++) {
    const root = clusters.find(i);
    let c = byRoot.get(root);
    if (!c) {
      c = {
        members: [],
        sum: { x: 0, y: 0 },
        elevSum: 0,
        elevCount: 0,
        liftBases: new Set(),
        liftTops: new Set(),
        trailIds: new Set(),
        nodeIds: new Set(),
        pathIds: new Set(),
        fromCrossing: false,
        headOnly: true,
      };
      byRoot.set(root, c);
    }
    const site = siteList[i];
    c.members.push(i);
    c.sum.x += site.m.x;
    c.sum.y += site.m.y;
    if (site.elevM != null && Number.isFinite(site.elevM)) {
      c.elevSum += site.elevM;
      c.elevCount++;
    }
    if (site.liftBase) c.liftBases.add(site.liftBase);
    if (site.liftTop) c.liftTops.add(site.liftTop);
    if (site.userNode) c.nodeIds.add(site.userNode);
    if (site.polylineIndex >= 0) {
      const pl = polylines[site.polylineIndex];
      if (pl.entityKind === 'trail') c.trailIds.add(pl.entityId);
      else c.pathIds.add(pl.entityId);
      if (site.s > 1e-6) c.headOnly = false;
    }
    if (site.fromCrossing) c.fromCrossing = true;
  }

  const clusterEntries = [...byRoot.entries()].map(([root, c]) => {
    const n = c.members.length;
    const centroid: XY = { x: c.sum.x / n, y: c.sum.y / n };
    return { root, c, centroid };
  });
  // North → south, then west → east. Independent of Map iteration order.
  clusterEntries.sort(
    (a, b) =>
      Math.round(b.centroid.y * 100) - Math.round(a.centroid.y * 100) ||
      Math.round(a.centroid.x * 100) - Math.round(b.centroid.x * 100)
  );

  const nodes: NetworkNode[] = [];
  const nodeById = new Map<NodeId, NetworkNode>();
  const rootToNode = new Map<number, NodeId>();
  clusterEntries.forEach((entry, i) => {
    const { c, centroid } = entry;
    const id: NodeId = `n:${i}`;
    let kind: NetworkNodeKind;
    if (c.liftBases.size > 0) kind = 'lift-base';
    else if (c.liftTops.size > 0) kind = 'lift-top';
    else if (c.nodeIds.size > 0) kind = 'user-node';
    else if (c.fromCrossing) kind = 'crossing';
    else if (c.members.length > 1) kind = 'junction';
    else if (c.headOnly) kind = 'trail-head';
    else kind = 'trail-out';
    const node: NetworkNode = {
      id,
      lngLat: toLngLat(frame, centroid),
      elevM: c.elevCount > 0 ? c.elevSum / c.elevCount : null,
      kind,
      liftBases: [...c.liftBases].sort(),
      liftTops: [...c.liftTops].sort(),
      trailIds: [...c.trailIds].sort(),
      nodeIds: [...c.nodeIds].sort(),
      pathIds: [...c.pathIds].sort(),
      outgoing: [],
      incoming: [],
    };
    nodes.push(node);
    nodeById.set(id, node);
    rootToNode.set(entry.root, id);
  });

  const nodeForSite = (key: string): NodeId => {
    const i = siteIndex.get(key) as number;
    return rootToNode.get(clusters.find(i)) as NodeId;
  };

  // A path is degenerate when its two declared ends resolved into the same
  // cluster — still a legal connector (e.g. a loop back to its own junction),
  // just flagged so the UI can call it out. Edges still build normally.
  const degeneratePathIdSet = new Set<string>();
  for (const p of pathList) {
    const pi = polylineIndexByKey.get(`path:${p.id}`);
    if (pi === undefined) continue;
    const pl = polylines[pi];
    const headKey = trailSiteKey(pi, 0);
    const footKey = trailSiteKey(pi, pl.totalM);
    if (nodeForSite(headKey) === nodeForSite(footKey)) degeneratePathIdSet.add(p.id);
  }
  diagnostics.degeneratePathIds = [...degeneratePathIdSet].sort();

  // -- trail & path edges ----------------------------------------------------

  const edges: NetworkEdge[] = [];
  const trailEdgeIds = new Map<string, EdgeId[]>();
  const pathEdgeIds = new Map<string, EdgeId[]>();

  for (let i = 0; i < polylines.length; i++) {
    const pl = polylines[i];
    const kept = keptByPolyline[i];
    if (kept.length < 2) continue;

    if (pl.entityKind === 'trail') {
    const trail = pl.trail as SavedTrail;
    const trailId = pl.trailId as string;
    const partIndex = pl.partIndex as number;
    const condition: EdgeCondition = trail.closed ? 'closed' : 'open';
    const planned = trail.status === 'planning';
    let segmentIndex = 0;
    for (let k = 0; k < kept.length - 1; k++) {
      const aSite = sites.get(kept[k]) as Site;
      const bSite = sites.get(kept[k + 1]) as Site;
      const spanM = bSite.s - aSite.s;
      if (spanM <= 0) continue;
      const { lngLat, elevM } = sliceBetween(pl, aSite.s, bSite.s);
      if (lngLat.length < 2) continue;
      const stats = trailStats(lngLat, elevM);
      const elevationResolved = elevM.length === lngLat.length && elevM.length > 0;
      // Unresolved elevations make trailStats report zero slope, and zero slope
      // through difficultyForSlopes would claim a false 'green'. Inheriting the
      // parent's grade is the only honest answer there.
      const difficulty = elevationResolved
        ? difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg)
        : trail.difficulty;
      const netVerticalM = elevationResolved ? elevM[0] - elevM[elevM.length - 1] : null;
      const flatTolM = Math.max(FLAT_FLOOR_M, FLAT_GRADE * spanM);
      const nearFlat = netVerticalM != null && Math.abs(netVerticalM) <= flatTolM;
      const climbs = netVerticalM != null && netVerticalM < -flatTolM;
      const traverse = nearFlat || climbs;
      const speed = traverse ? TRAVERSE_SPEED_MPS : SKI_SPEED_MPS[difficulty];
      // Chainage fractions sum to exactly 1, so Σ segment.areaM2 conserves the
      // part's footprint area. Proration by length is the right model here:
      // runs are painted at a constant brush diameter.
      const areaM2 = pl.totalM > 0 ? (pl.areaM2 * spanM) / pl.totalM : 0;
      const id: EdgeId = `t:${trailId}:${partIndex}:${segmentIndex}`;
      const from = nodeForSite(aSite.key);
      const to = nodeForSite(bSite.key);
      const base = {
        trailId,
        trailName: trail.name,
        partIndex,
        segmentIndex,
        difficulty,
        avgSlopeDeg: stats.avgSlopeDeg,
        maxSlopeDeg: stats.maxSlopeDeg,
        verticalM: stats.verticalM,
        areaM2,
        brushWidthM: pl.brushWidthM,
        status: trail.status,
        planned,
        elevationResolved,
        condition,
        open: condition === 'open' && trail.status === 'complete',
        lengthM: stats.lengthM,
        travelTimeS: speed > 0 ? stats.lengthM / speed : 0,
      };
      edges.push({
        kind: 'trail',
        id,
        from,
        to,
        path: lngLat,
        elevM,
        netVerticalM,
        traverse,
        ...base,
      });
      if (traverse) diagnostics.traverseEdgeCount++;
      const list = trailEdgeIds.get(trailId) ?? [];
      list.push(id);
      trailEdgeIds.set(trailId, list);

      if (opt.bidirectionalTraverses && nearFlat) {
        const twinId: EdgeId = `${id}:r`;
        edges.push({
          kind: 'trail',
          id: twinId,
          from: to,
          to: from,
          path: [...lngLat].reverse(),
          elevM: [...elevM].reverse(),
          netVerticalM: netVerticalM != null ? -netVerticalM : null,
          traverse: true,
          ...base,
        });
        diagnostics.traverseEdgeCount++;
      }
      segmentIndex++;
    }
    } else {
      const savedPath = pl.path as SavedPath;
      const condition: EdgeCondition = savedPath.closed ? 'closed' : 'open';
      const planned = savedPath.status === 'planning';
      let segmentIndex = 0;
      for (let k = 0; k < kept.length - 1; k++) {
        const aSite = sites.get(kept[k]) as Site;
        const bSite = sites.get(kept[k + 1]) as Site;
        const spanM = bSite.s - aSite.s;
        if (spanM <= 0) continue;
        const { lngLat, elevM } = sliceBetween(pl, aSite.s, bSite.s);
        if (lngLat.length < 2) continue;
        const stats = trailStats(lngLat, elevM);
        const elevationResolved = elevM.length === lngLat.length && elevM.length > 0;
        // A path has no stored difficulty to fall back on; unresolved terrain
        // reads as the mellowest band rather than fabricating a grade.
        const difficulty: TrailDifficulty = elevationResolved
          ? difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg)
          : 'green';
        const netVerticalM = elevationResolved ? elevM[0] - elevM[elevM.length - 1] : null;
        const flatTolM = Math.max(FLAT_FLOOR_M, FLAT_GRADE * spanM);
        const nearFlat = netVerticalM != null && Math.abs(netVerticalM) <= flatTolM;
        const climbs = netVerticalM != null && netVerticalM < -flatTolM;
        const traverse = nearFlat || climbs;
        const speed = traverse ? TRAVERSE_SPEED_MPS : SKI_SPEED_MPS[difficulty];
        const id: EdgeId = `x:${savedPath.id}:${segmentIndex}`;
        const from = nodeForSite(aSite.key);
        const to = nodeForSite(bSite.key);
        const base = {
          pathId: savedPath.id,
          pathName: savedPath.name,
          segmentIndex,
          difficulty,
          avgSlopeDeg: stats.avgSlopeDeg,
          maxSlopeDeg: stats.maxSlopeDeg,
          verticalM: stats.verticalM,
          widthM: savedPath.widthM,
          status: savedPath.status,
          planned,
          elevationResolved,
          condition,
          open: condition === 'open' && savedPath.status === 'complete',
          lengthM: stats.lengthM,
          travelTimeS: speed > 0 ? stats.lengthM / speed : 0,
        };
        edges.push({
          kind: 'path',
          id,
          from,
          to,
          path: lngLat,
          elevM,
          netVerticalM,
          traverse,
          ...base,
        });
        if (traverse) diagnostics.traverseEdgeCount++;
        const list = pathEdgeIds.get(savedPath.id) ?? [];
        list.push(id);
        pathEdgeIds.set(savedPath.id, list);

        if (opt.bidirectionalTraverses && nearFlat) {
          const twinId: EdgeId = `${id}:r`;
          edges.push({
            kind: 'path',
            id: twinId,
            from: to,
            to: from,
            path: [...lngLat].reverse(),
            elevM: [...elevM].reverse(),
            netVerticalM: netVerticalM != null ? -netVerticalM : null,
            traverse: true,
            ...base,
          });
          diagnostics.traverseEdgeCount++;
        }
        segmentIndex++;
      }
    }
  }

  // -- lift edges -----------------------------------------------------------

  const liftEdgeIds = new Map<string, EdgeId>();
  for (const plan of liftPlans) {
    const { lift } = plan;
    const derived = fixedGripDerived(lift.lengthM);
    const condition: EdgeCondition = lift.closed ? 'closed' : 'open';
    const id: EdgeId = `l:${lift.id}`;
    const edge: LiftEdge = {
      kind: 'lift',
      id,
      from: nodeForSite(plan.baseKey),
      to: nodeForSite(plan.topKey),
      liftId: lift.id,
      liftName: lift.name,
      liftClass: lift.liftClass,
      chairSize: lift.chairSize,
      path: [plan.basePoint, plan.topPoint],
      lengthM: lift.lengthM,
      verticalM: lift.verticalM,
      rideTimeS: derived.rideTimeS,
      headwayS: derived.headwayS,
      carrierSpacingM: derived.carrierSpacingM,
      carriersOnLine: derived.carriersOnLine,
      capacityPph: fixedGripCapacityPph(lift.chairSize),
      peopleWaiting: 0,
      waitTimeS: 0,
      travelTimeS: derived.rideTimeS,
      condition,
      open: condition === 'open' && lift.status === 'complete',
      status: lift.status,
      planned: lift.status === 'planning',
      orientationResolved: plan.orientationResolved,
      servesTrailIds: [],
    };
    edges.push(edge);
    liftEdgeIds.set(lift.id, id);
  }

  // -- adjacency ------------------------------------------------------------

  const edgeById = new Map<EdgeId, NetworkEdge>();
  for (const e of edges) edgeById.set(e.id, e);
  for (const e of edges) {
    nodeById.get(e.from)?.outgoing.push(e.id);
    nodeById.get(e.to)?.incoming.push(e.id);
  }
  for (const n of nodes) {
    n.outgoing.sort();
    n.incoming.sort();
  }
  for (const e of edges) {
    if (e.kind !== 'lift') continue;
    const top = nodeById.get(e.to);
    const served = new Set<string>();
    for (const eid of top?.outgoing ?? []) {
      const out = edgeById.get(eid);
      if (out && out.kind === 'trail') served.add(out.trailId);
    }
    e.servesTrailIds = [...served].sort();
  }

  const network: SkiNetwork = {
    nodes,
    edges,
    nodeById,
    edgeById,
    trailEdgeIds,
    pathEdgeIds,
    liftEdgeIds,
    frame,
    diagnostics,
  };

  // -- diagnostics that need the finished graph -----------------------------

  const liftTopNodes = edges
    .filter((e): e is LiftEdge => e.kind === 'lift')
    .map((e) => e.to);
  const servedTrailIds = new Set<string>();
  if (liftTopNodes.length > 0) {
    const reach = reachableFrom(network, liftTopNodes, {
      edgeFilter: (e) => e.kind === 'trail',
    });
    for (const eid of reach.edgeIds) {
      const e = edgeById.get(eid);
      if (e && e.kind === 'trail') servedTrailIds.add(e.trailId);
    }
  }
  diagnostics.orphanTrailIds = [...trailEdgeIds.keys()].filter((id) => !servedTrailIds.has(id)).sort();
  diagnostics.isolatedLiftIds = edges
    .filter((e): e is LiftEdge => e.kind === 'lift' && e.servesTrailIds.length === 0)
    .map((e) => e.liftId)
    .sort();
  diagnostics.componentCount = countComponents(network);

  return network;
}

// ---------------------------------------------------------------------------
// Traversal & queries
// ---------------------------------------------------------------------------

export interface ReachOptions {
  direction?: 'forward' | 'reverse';
  edgeFilter?: (edge: NetworkEdge) => boolean;
  maxEdges?: number;
}

/**
 * The single traversal primitive: BFS over the directed adjacency, O(V+E).
 * trailsFromLift, liftsFromTrail, the orphan/component diagnostics and a future
 * Dijkstra all sit on this same adjacency unchanged.
 *
 * Note it records EVERY edge examined from a popped node, not only edges whose
 * head is newly visited. Two runs between the same pair of junctions is a real
 * and legal case; visiting-node bookkeeping alone would silently drop one.
 */
export function reachableFrom(
  network: SkiNetwork,
  startNodeIds: NodeId[],
  options?: ReachOptions
): { nodeIds: NodeId[]; edgeIds: EdgeId[] } {
  const direction = options?.direction ?? 'forward';
  const filter = options?.edgeFilter;
  const maxEdges = options?.maxEdges ?? Infinity;
  const visited = new Set<NodeId>();
  const seenEdges = new Set<EdgeId>();
  const nodeIds: NodeId[] = [];
  const edgeIds: EdgeId[] = [];
  const queue: NodeId[] = [];
  for (const id of startNodeIds) {
    if (!network.nodeById.has(id) || visited.has(id)) continue;
    visited.add(id);
    nodeIds.push(id);
    queue.push(id);
  }
  for (let head = 0; head < queue.length; head++) {
    const node = network.nodeById.get(queue[head]);
    if (!node) continue;
    const list = direction === 'forward' ? node.outgoing : node.incoming;
    for (const eid of list) {
      const edge = network.edgeById.get(eid);
      if (!edge) continue;
      if (filter && !filter(edge)) continue;
      if (!seenEdges.has(eid)) {
        seenEdges.add(eid);
        edgeIds.push(eid);
        if (edgeIds.length >= maxEdges) return { nodeIds, edgeIds };
      }
      const next = direction === 'forward' ? edge.to : edge.from;
      if (!visited.has(next)) {
        visited.add(next);
        nodeIds.push(next);
        queue.push(next);
      }
    }
  }
  return { nodeIds, edgeIds };
}

function countComponents(network: SkiNetwork): number {
  const seen = new Set<NodeId>();
  let count = 0;
  for (const node of network.nodes) {
    if (seen.has(node.id)) continue;
    count++;
    const queue = [node.id];
    seen.add(node.id);
    for (let head = 0; head < queue.length; head++) {
      const n = network.nodeById.get(queue[head]);
      if (!n) continue;
      for (const eid of [...n.outgoing, ...n.incoming]) {
        const edge = network.edgeById.get(eid);
        if (!edge) continue;
        for (const next of [edge.from, edge.to]) {
          if (seen.has(next)) continue;
          seen.add(next);
          queue.push(next);
        }
      }
    }
  }
  return count;
}

export interface TrailsFromLift {
  liftId: string;
  /** Trails whose first segment leaves this lift's TOP node directly. */
  direct: string[];
  /** Every trail reachable by descending from the top (optionally riding on). */
  reachable: string[];
  reachableEdgeIds: EdgeId[];
}

/**
 * `openOnly` defaults to FALSE: this is a design tool first, and a planner wants
 * to see planned and closed runs a lift would serve. The simulation passes true.
 */
export function trailsFromLift(
  network: SkiNetwork,
  liftId: string,
  options?: { includeLifts?: boolean; openOnly?: boolean }
): TrailsFromLift | null {
  const edgeId = network.liftEdgeIds.get(liftId);
  if (!edgeId) return null;
  const liftEdge = network.edgeById.get(edgeId) as LiftEdge | undefined;
  if (!liftEdge) return null;
  const includeLifts = options?.includeLifts ?? false;
  const openOnly = options?.openOnly ?? false;
  const filter = (e: NetworkEdge) =>
    (!openOnly || e.open) && (includeLifts || e.kind === 'trail');

  const topNode = network.nodeById.get(liftEdge.to);
  const direct = new Set<string>();
  for (const eid of topNode?.outgoing ?? []) {
    const e = network.edgeById.get(eid);
    if (e && e.kind === 'trail' && filter(e)) direct.add(e.trailId);
  }
  const reach = reachableFrom(network, [liftEdge.to], { edgeFilter: filter });
  const reachable = new Set<string>();
  for (const eid of reach.edgeIds) {
    const e = network.edgeById.get(eid);
    if (e && e.kind === 'trail') reachable.add(e.trailId);
  }
  return {
    liftId,
    direct: [...direct].sort(),
    reachable: [...reachable].sort(),
    reachableEdgeIds: reach.edgeIds,
  };
}

export interface LiftsFromTrail {
  trailId: string;
  /** Lifts whose top node IS the `from` of one of this trail's segments. */
  direct: string[];
  /** Lifts that can reach any segment of this trail. */
  reachable: string[];
}

export function liftsFromTrail(
  network: SkiNetwork,
  trailId: string,
  options?: { openOnly?: boolean }
): LiftsFromTrail | null {
  const edges = edgesForTrail(network, trailId);
  if (edges.length === 0) return null;
  const openOnly = options?.openOnly ?? false;
  const filter = (e: NetworkEdge) => !openOnly || e.open;

  const direct = new Set<string>();
  const starts = new Set<NodeId>();
  for (const e of edges) {
    starts.add(e.from);
    starts.add(e.to);
    for (const liftId of network.nodeById.get(e.from)?.liftTops ?? []) direct.add(liftId);
  }
  const reach = reachableFrom(network, [...starts], { direction: 'reverse', edgeFilter: filter });
  const reachable = new Set<string>();
  for (const nid of reach.nodeIds) {
    for (const liftId of network.nodeById.get(nid)?.liftTops ?? []) reachable.add(liftId);
  }
  return { trailId, direct: [...direct].sort(), reachable: [...reachable].sort() };
}

export function edgesForTrail(network: SkiNetwork, trailId: string): TrailEdge[] {
  const ids = network.trailEdgeIds.get(trailId) ?? [];
  const out: TrailEdge[] = [];
  for (const id of ids) {
    const e = network.edgeById.get(id);
    if (e && e.kind === 'trail') out.push(e);
  }
  return out;
}

export function trailEdgesAt(network: SkiNetwork, nodeId: NodeId): TrailEdge[] {
  const node = network.nodeById.get(nodeId);
  if (!node) return [];
  const out: TrailEdge[] = [];
  for (const id of [...node.outgoing, ...node.incoming]) {
    const e = network.edgeById.get(id);
    if (e && e.kind === 'trail') out.push(e);
  }
  return out;
}

/**
 * Pure O(lifts) queue refresh — the simulation tick's hook. peopleWaiting has no
 * source in GameSave; keeping it behind a shallow clone means the tick loop
 * never re-runs intersection detection just to update a queue.
 */
export function withLiftQueues(
  network: SkiNetwork,
  queues: Record<string, { peopleWaiting?: number }>
): SkiNetwork {
  let changed = false;
  const edges = network.edges.map((e) => {
    if (e.kind !== 'lift') return e;
    const raw = queues[e.liftId]?.peopleWaiting;
    const peopleWaiting = typeof raw === 'number' && Number.isFinite(raw) && raw > 0 ? raw : 0;
    const waitTimeS = e.capacityPph > 0 ? (peopleWaiting / e.capacityPph) * 3600 : 0;
    if (peopleWaiting === e.peopleWaiting && waitTimeS === e.waitTimeS) return e;
    changed = true;
    return { ...e, peopleWaiting, waitTimeS, travelTimeS: e.rideTimeS + waitTimeS };
  });
  if (!changed) return network;
  const edgeById = new Map<EdgeId, NetworkEdge>();
  for (const e of edges) edgeById.set(e.id, e);
  return { ...network, edges, edgeById };
}

export interface NetworkSummary {
  nodeCount: number;
  trailEdgeCount: number;
  liftEdgeCount: number;
  trailLengthM: number;
  openTrailLengthM: number;
  totalTrailAreaM2: number;
  byDifficulty: Record<TrailDifficulty, { count: number; lengthM: number; areaM2: number }>;
}

export function networkSummary(network: SkiNetwork): NetworkSummary {
  const byDifficulty: NetworkSummary['byDifficulty'] = {
    green: { count: 0, lengthM: 0, areaM2: 0 },
    blue: { count: 0, lengthM: 0, areaM2: 0 },
    black: { count: 0, lengthM: 0, areaM2: 0 },
    red: { count: 0, lengthM: 0, areaM2: 0 },
  };
  let trailEdgeCount = 0;
  let liftEdgeCount = 0;
  let trailLengthM = 0;
  let openTrailLengthM = 0;
  let totalTrailAreaM2 = 0;
  for (const e of network.edges) {
    if (e.kind === 'lift') {
      liftEdgeCount++;
      continue;
    }
    if (e.kind !== 'trail') continue; // a path is a connector, not graded terrain
    if (e.id.endsWith(':r')) continue; // reverse twins are not extra terrain
    trailEdgeCount++;
    trailLengthM += e.lengthM;
    if (e.open) openTrailLengthM += e.lengthM;
    totalTrailAreaM2 += e.areaM2;
    const bucket = byDifficulty[e.difficulty];
    bucket.count++;
    bucket.lengthM += e.lengthM;
    bucket.areaM2 += e.areaM2;
  }
  return {
    nodeCount: network.nodes.length,
    trailEdgeCount,
    liftEdgeCount,
    trailLengthM,
    openTrailLengthM,
    totalTrailAreaM2,
    byDifficulty,
  };
}
