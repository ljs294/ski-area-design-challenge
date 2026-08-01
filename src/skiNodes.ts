import { haversineMeters } from './geo';
import type { TrailStatus } from './types';

// Pure ski-node/path helpers: free-standing map pins ("nodes") and the
// footpaths that connect them to lifts, runs, or each other. Same layer as
// trails.ts/lifts.ts/roads.ts — no DOM, no fetch, no imports from src/app/**.
// Everything here is a pure function of stored geometry, cheap to unit test
// and safe to recompute on every load.

export const DEFAULT_PATH_WIDTH_M = 6;
export const MIN_PATH_WIDTH_M = 2;
export const MAX_PATH_WIDTH_M = 20;

// Mirrors the 0.05 m consecutive-point dedupe rule in sanitizeRoads.
export const POINT_DEDUPE_M = 0.05;

/** Where a run or path attaches to the rest of the mountain. */
export type AnchorRef =
  | { kind: 'lift'; liftId: string; end: 'top' | 'base'; point: [number, number] }
  | { kind: 'trail'; trailId: string; point: [number, number] }
  | { kind: 'path'; pathId: string; point: [number, number] }
  | { kind: 'node'; nodeId: string; point: [number, number] };

export interface SavedNode {
  id: string;
  name: string; // default "Node N"
  point: [number, number];
  elevM: number | null; // sampled; null when offline
  /** Optional: pins this node onto a run/lift/path rather than free-standing. */
  anchor?: AnchorRef;
  createdAt: string; // ISO
}

/** A durable topology point shared by trail segments, paths, and lift terminals. */
export interface SavedJunction {
  id: string;
  point: [number, number];
  elevM: number | null;
  /** Present when this junction is exactly a lift terminal. */
  liftTerminal?: { liftId: string; end: 'top' | 'base' };
  /** Retains otherwise unsupported legacy connectivity without enabling it for new tools. */
  legacyAnchor?: AnchorRef;
  createdAt: string;
}

export interface SavedPath {
  id: string;
  name: string; // default "Path N"
  points: [number, number][]; // >= 2 after dedupe
  /** Terrain elevations in meters, parallel to `points`; [] when unresolved. */
  pointElevM: number[];
  widthM: number;
  from: AnchorRef;
  to: AnchorRef;
  /** Schema-v5 durable endpoint references. Legacy paths are upgraded on hydration. */
  fromJunctionId?: string;
  toJunctionId?: string;
  lengthM: number; // ALWAYS recomputed on load from points
  status: TrailStatus; // 'planning' (dashed) or 'complete' (solid)
  /** Operating condition. Absent/false = open. */
  closed?: boolean;
  createdAt: string; // ISO
}

function isLngLat(p: unknown): p is [number, number] {
  return (
    Array.isArray(p) &&
    p.length === 2 &&
    typeof p[0] === 'number' &&
    typeof p[1] === 'number' &&
    Number.isFinite(p[0]) &&
    Number.isFinite(p[1])
  );
}

export function pathLengthM(points: [number, number][]): number {
  let length = 0;
  for (let i = 1; i < points.length; i++) length += haversineMeters(points[i - 1], points[i]);
  return length;
}

/**
 * Strict validator for the AnchorRef discriminated union. Any malformed shape
 * — wrong kind, missing/non-string id, non-finite point, missing or bad `end`
 * on a lift anchor — returns undefined rather than a partially-trusted value.
 */
export function sanitizeAnchor(raw: unknown): AnchorRef | undefined {
  if (typeof raw !== 'object' || raw === null) return undefined;
  const a = raw as Record<string, unknown>;
  if (!isLngLat(a.point)) return undefined;
  const point = a.point;
  switch (a.kind) {
    case 'lift':
      if (typeof a.liftId !== 'string') return undefined;
      if (a.end !== 'top' && a.end !== 'base') return undefined;
      return { kind: 'lift', liftId: a.liftId, end: a.end, point };
    case 'trail':
      if (typeof a.trailId !== 'string') return undefined;
      return { kind: 'trail', trailId: a.trailId, point };
    case 'path':
      if (typeof a.pathId !== 'string') return undefined;
      return { kind: 'path', pathId: a.pathId, point };
    case 'node':
      if (typeof a.nodeId !== 'string') return undefined;
      return { kind: 'node', nodeId: a.nodeId, point };
    default:
      return undefined;
  }
}

/** The id of the entity an anchor references — useful for reverse lookups. */
export function anchorTargetId(a: AnchorRef): string {
  switch (a.kind) {
    case 'lift':
      return a.liftId;
    case 'trail':
      return a.trailId;
    case 'path':
      return a.pathId;
    case 'node':
      return a.nodeId;
  }
}

/** Short human label for an anchor. Generic — it has no access to entity names. */
export function describeAnchor(a: AnchorRef): string {
  switch (a.kind) {
    case 'lift':
      return a.end === 'top' ? 'Lift top' : 'Lift base';
    case 'trail':
      return 'On a run';
    case 'path':
      return 'On a path';
    case 'node':
      return 'Node';
  }
}

/**
 * Hydration shield for `GameSave.nodes`: drops anything that isn't a valid
 * pin and recomputes nothing else — `point`/`elevM` are stored values, not
 * derived, so they pass through once validated.
 */
export function sanitizeNodes(raw: unknown[]): SavedNode[] {
  const out: SavedNode[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const n = item as Record<string, unknown>;
    if (typeof n.id !== 'string' || typeof n.name !== 'string') continue;
    if (!isLngLat(n.point)) continue;
    out.push({
      id: n.id,
      name: n.name,
      point: n.point,
      elevM: typeof n.elevM === 'number' && Number.isFinite(n.elevM) ? n.elevM : null,
      anchor: sanitizeAnchor(n.anchor),
      createdAt: typeof n.createdAt === 'string' ? n.createdAt : new Date().toISOString(),
    });
  }
  return out;
}

/**
 * Hydration shield for `GameSave.paths`: drops anything that isn't a valid
 * connector and recomputes cached length from the stored geometry so it can
 * never drift from it. A path with no declared ends has no purpose and is
 * dropped along with it.
 */
export function sanitizePaths(raw: unknown[]): SavedPath[] {
  const out: SavedPath[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const p = item as Record<string, unknown>;
    if (typeof p.id !== 'string' || typeof p.name !== 'string') continue;
    if (!Array.isArray(p.points)) continue;

    // Dedupe points and elevations together in one pass so pointElevM stays
    // aligned with the surviving points (dedupe changes the length).
    const rawElev = Array.isArray(p.pointElevM) ? p.pointElevM : null;
    const elevInputValid = rawElev !== null && rawElev.length === p.points.length &&
      rawElev.every((e) => typeof e === 'number' && Number.isFinite(e));
    const points: [number, number][] = [];
    const pointElevM: number[] = [];
    for (let i = 0; i < p.points.length; i++) {
      const point = p.points[i];
      if (!isLngLat(point)) { points.length = 0; pointElevM.length = 0; break; }
      const previous = points.at(-1);
      if (!previous || haversineMeters(previous, point) >= POINT_DEDUPE_M) {
        points.push(point);
        if (elevInputValid) pointElevM.push(rawElev![i] as number);
      }
    }
    if (points.length < 2) continue;

    const from = sanitizeAnchor(p.from);
    const to = sanitizeAnchor(p.to);
    if (!from || !to) continue;

    const widthM = typeof p.widthM === 'number' && Number.isFinite(p.widthM)
      ? Math.min(MAX_PATH_WIDTH_M, Math.max(MIN_PATH_WIDTH_M, p.widthM))
      : DEFAULT_PATH_WIDTH_M;
    const status: TrailStatus = p.status === 'planning' || p.status === 'complete' ? p.status : 'complete';

    out.push({
      id: p.id,
      name: p.name,
      points,
      pointElevM: elevInputValid && pointElevM.length === points.length ? pointElevM : [],
      widthM,
      from,
      to,
      fromJunctionId: typeof p.fromJunctionId === 'string' ? p.fromJunctionId : undefined,
      toJunctionId: typeof p.toJunctionId === 'string' ? p.toJunctionId : undefined,
      lengthM: pathLengthM(points),
      status,
      closed: p.closed === true,
      createdAt: typeof p.createdAt === 'string' ? p.createdAt : new Date().toISOString(),
    });
  }
  return out;
}

/** First "Node N" not already taken. */
export function nextNodeName(existing: SavedNode[]): string {
  const taken = new Set(existing.map((n) => n.name));
  for (let number = 1; ; number++) {
    const name = `Node ${number}`;
    if (!taken.has(name)) return name;
  }
}

/** First "Path N" not already taken. */
export function nextPathName(existing: SavedPath[]): string {
  const taken = new Set(existing.map((p) => p.name));
  for (let number = 1; ; number++) {
    const name = `Path ${number}`;
    if (!taken.has(name)) return name;
  }
}
