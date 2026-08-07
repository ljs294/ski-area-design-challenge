import type {
  SavedDam,
  SavedPond,
  SavedSnowmakingNode,
  SnowmakingLakeSource,
  SnowmakingNodeKind,
  SnowmakingSourceRef,
} from './types/snowmaking';

// Pure snowmaking pipe-network helpers: free-standing network nodes
// (intakes, pumps, junctions, hydrants) and their reconciliation against the
// resort's water sources (dams/ponds). Same layer as skiNodes.ts/lifts.ts/
// roads.ts — no DOM, no fetch, no imports from src/app/**. Everything here is
// a pure function of stored geometry, cheap to unit test and safe to
// recompute on every load.

export const SNOWMAKING_NODE_LABELS: Record<SnowmakingNodeKind, string> = {
  intake: 'Intake',
  pump: 'Pump',
  junction: 'Junction',
  hydrant: 'Hydrant',
};

const SNOWMAKING_NODE_KINDS = new Set<SnowmakingNodeKind>(['intake', 'pump', 'junction', 'hydrant']);

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

/** `crypto.randomUUID` is gated to secure contexts (fails under packaged file://). */
function newNodeId(): string {
  try {
    return crypto.randomUUID();
  } catch {
    return 'snow-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  }
}

/**
 * Mean of a ring's distinct vertices. `SavedPond.boundary` (and
 * `SavedDam.pondRings[0]`) are always *closed* rings — first vertex repeated
 * as the last — so naively averaging every point would double-weight vertex
 * 0 and skew the center toward it. Strip the closing duplicate before
 * averaging so a closed square centers exactly, not lopsided.
 */
export function ringCenter(ring: [number, number][]): [number, number] {
  if (ring.length === 0) return [0, 0];
  const first = ring[0];
  const last = ring[ring.length - 1];
  const open = ring.length > 1 && first[0] === last[0] && first[1] === last[1]
    ? ring.slice(0, -1)
    : ring;
  const n = open.length || 1;
  let sumX = 0, sumY = 0;
  for (const [x, y] of open) { sumX += x; sumY += y; }
  return [sumX / n, sumY / n];
}

export function intakeNameFor(sourceName: string): string {
  return `${sourceName} Intake`;
}

/**
 * Strict validator for the SnowmakingSourceRef discriminated union. Any
 * malformed shape — wrong kind, missing/non-string id — returns undefined
 * rather than a partially-trusted value.
 */
function sanitizeSource(raw: unknown): SnowmakingSourceRef | undefined {
  if (typeof raw !== 'object' || raw === null) return undefined;
  const s = raw as Record<string, unknown>;
  switch (s.kind) {
    case 'dam':
      return typeof s.damId === 'string' ? { kind: 'dam', damId: s.damId } : undefined;
    case 'pond':
      return typeof s.pondId === 'string' ? { kind: 'pond', pondId: s.pondId } : undefined;
    case 'lake':
      return typeof s.lakeId === 'string' ? { kind: 'lake', lakeId: s.lakeId } : undefined;
    default:
      return undefined;
  }
}

/**
 * Hydration shield for `GameSave.snowmakingNodes`: drops anything that isn't
 * a valid pin. `id`/`name`/`point`/`kind`/`createdAt` are load-bearing, so a
 * row missing or misshaping any of them is dropped entirely.
 *
 * A malformed `source`, by contrast, only drops the `source` field and keeps
 * the node. This is deliberate, not merely lenient: `reconcileSnowmakingNodes`
 * deletes any node whose `source` fails to resolve to a current dam/pond, but
 * leaves source-less nodes alone forever (rule 4 below — they're the
 * forward-compatible slot for future hand-placed nodes). So the choice is
 * between "corrupted source -> node vanishes on the next reconcile" and
 * "corrupted source -> node survives as an ordinary hand-placed-style node".
 * The latter is safer for recovering from bad save data, and today nothing
 * hand-places nodes yet, so there's no real downside to treating an orphaned
 * intake as if a person had dropped it there themselves.
 */
export function sanitizeSnowmakingNodes(raw: unknown[]): SavedSnowmakingNode[] {
  const out: SavedSnowmakingNode[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const n = item as Record<string, unknown>;
    if (typeof n.id !== 'string' || typeof n.name !== 'string') continue;
    if (typeof n.createdAt !== 'string') continue;
    if (!isLngLat(n.point)) continue;
    if (typeof n.kind !== 'string' || !SNOWMAKING_NODE_KINDS.has(n.kind as SnowmakingNodeKind)) continue;
    out.push({
      id: n.id,
      name: n.name,
      kind: n.kind as SnowmakingNodeKind,
      point: n.point,
      elevM: typeof n.elevM === 'number' && Number.isFinite(n.elevM) ? n.elevM : null,
      source: sanitizeSource(n.source),
      createdAt: n.createdAt,
    });
  }
  return out;
}

/**
 * Reconciles the persisted node list against the resort's current water
 * sources (every dam, plus every pond whose capacity counts toward
 * snowmaking — mirrors `snowmakingWaterCapacityM3` in app/resortStats.ts).
 *
 *  1. A node whose `source` still resolves to a currently-desired source is
 *     kept as-is, including its current `name` — renames are never resynced
 *     from the source, only set at creation.
 *  2. A node whose `source` no longer resolves (source deleted, or a pond's
 *     `isSnowmaking` flag turned off) is dropped.
 *  3. Every desired source with no existing matching node gets a freshly
 *     created intake node.
 *  4. Nodes with no `source` field at all pass through untouched — reserved
 *     for future hand-placed nodes.
 *
 * Reference stability: the app diffs design state by array reference
 * (`designHasEdits` in app/unsavedChanges.ts) to decide whether there are
 * unsaved changes, so an already-consistent input must come back as the
 * exact same array reference, not a new array with equal contents. Kept
 * node objects are reused as-is too — only additions allocate new objects.
 */
export function reconcileSnowmakingNodes(
  nodes: SavedSnowmakingNode[],
  dams: SavedDam[],
  ponds: SavedPond[],
  lakes?: SnowmakingLakeSource[],
): SavedSnowmakingNode[] {
  interface DesiredSource {
    key: string;
    source: SnowmakingSourceRef;
    sourceName: string;
    point: [number, number];
    elevM: number | null;
  }

  const desired: DesiredSource[] = [];
  for (const dam of dams) {
    desired.push({
      key: `dam:${dam.id}`,
      source: { kind: 'dam', damId: dam.id },
      sourceName: dam.name,
      point: ringCenter(dam.pondRings[0] ?? []),
      elevM: dam.crestElevationM,
    });
  }
  for (const pond of ponds) {
    if (pond.isSnowmaking === false) continue;
    desired.push({
      key: `pond:${pond.id}`,
      source: { kind: 'pond', pondId: pond.id },
      sourceName: pond.name,
      point: ringCenter(pond.boundary),
      elevM: pond.topElevationM,
    });
  }
  for (const lake of lakes ?? []) {
    desired.push({
      key: `lake:${lake.id}`,
      source: { kind: 'lake', lakeId: lake.id },
      sourceName: lake.name,
      point: ringCenter(lake.boundary),
      elevM: lake.surfaceElevationM,
    });
  }
  const desiredByKey = new Map(desired.map((d) => [d.key, d]));

  const matchedKeys = new Set<string>();
  const kept: SavedSnowmakingNode[] = [];
  let anyDropped = false;
  for (const node of nodes) {
    if (!node.source) { kept.push(node); continue; }
    // Terrain-backed lakes cannot be resolved during initial save hydration;
    // retain them until the terrain package is available to the controller.
    if (node.source.kind === 'lake' && lakes === undefined) { kept.push(node); continue; }
    const key = node.source.kind === 'dam' ? `dam:${node.source.damId}`
      : node.source.kind === 'pond' ? `pond:${node.source.pondId}` : `lake:${node.source.lakeId}`;
    if (desiredByKey.has(key)) {
      kept.push(node);
      matchedKeys.add(key);
    } else {
      anyDropped = true;
    }
  }

  const additions: SavedSnowmakingNode[] = [];
  for (const d of desired) {
    if (matchedKeys.has(d.key)) continue;
    additions.push({
      id: newNodeId(),
      name: intakeNameFor(d.sourceName),
      kind: 'intake',
      point: d.point,
      elevM: d.elevM,
      source: d.source,
      createdAt: new Date().toISOString(),
    });
  }

  if (!anyDropped && additions.length === 0) return nodes;
  return [...kept, ...additions];
}
