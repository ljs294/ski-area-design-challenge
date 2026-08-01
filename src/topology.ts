import { haversineMeters } from './geo';
import { makeFrame, pointSegmentDistance, toMeters } from './network';
import { describeAnchor, sanitizeAnchor, type AnchorRef, type SavedJunction, type SavedNode,
  type SavedPath } from './skiNodes';
import type { SavedLift, SavedTrail, SavedTrailPart, SavedTrailSegment } from './types';

const SAME_POINT_M = 0.25;

function isPoint(value: unknown): value is [number, number] {
  return Array.isArray(value) && value.length === 2 && value.every((n) =>
    typeof n === 'number' && Number.isFinite(n));
}

export function sanitizeJunctions(raw: unknown[]): SavedJunction[] {
  const out: SavedJunction[] = [];
  const ids = new Set<string>();
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const j = item as Record<string, unknown>;
    if (typeof j.id !== 'string' || ids.has(j.id) || !isPoint(j.point)) continue;
    const lt = typeof j.liftTerminal === 'object' && j.liftTerminal !== null
      ? j.liftTerminal as Record<string, unknown> : null;
    const liftTerminal = lt && typeof lt.liftId === 'string' && (lt.end === 'top' || lt.end === 'base')
      ? { liftId: lt.liftId, end: lt.end } as const : undefined;
    ids.add(j.id);
    out.push({ id: j.id, point: j.point, elevM: typeof j.elevM === 'number' && Number.isFinite(j.elevM)
      ? j.elevM : null, liftTerminal,
      legacyAnchor: sanitizeAnchor(j.legacyAnchor),
      createdAt: typeof j.createdAt === 'string' ? j.createdAt : new Date().toISOString() });
  }
  return out;
}

/** Add missing endpoint junctions when loading legacy trails or partially-valid saves. */
export function hydrateJunctions(trails: SavedTrail[], raw: unknown[]): SavedJunction[] {
  const out = sanitizeJunctions(raw);
  const byId = new Map(out.map((j) => [j.id, j]));
  for (const trail of trails) for (const part of trail.parts) for (const segment of part.segments ?? []) {
    const endpoints: Array<[string, [number, number], number | null]> = [
      [segment.fromJunctionId, segment.centerline[0], segment.centerlineElevM[0] ?? null],
      [segment.toJunctionId, segment.centerline.at(-1)!, segment.centerlineElevM.at(-1) ?? null],
    ];
    for (const [id, point, elevM] of endpoints) if (!byId.has(id)) {
      const junction: SavedJunction = { id, point, elevM, createdAt: trail.createdAt };
      byId.set(id, junction); out.push(junction);
    }
  }
  return out;
}

export interface TopologyEdit {
  trails: SavedTrail[];
  junctions: SavedJunction[];
  junction: SavedJunction;
}

function rebuildPart(part: SavedTrailPart, segments: SavedTrailSegment[]): SavedTrailPart {
  const centerline = segments.flatMap((s, i) => i === 0 ? s.centerline : s.centerline.slice(1));
  const resolved = segments.every((s) => s.centerlineElevM.length === s.centerline.length);
  const centerlineElevM = resolved
    ? segments.flatMap((s, i) => i === 0 ? s.centerlineElevM : s.centerlineElevM.slice(1)) : [];
  return { ...part, segments, centerline, centerlineElevM };
}

/** Split the exact saved segment selected by a projected trail anchor. */
export function splitTrailAt(
  trails: SavedTrail[], junctions: SavedJunction[], trailId: string, requested: [number, number],
  idFactory: () => string, createdAt = new Date().toISOString(),
): TopologyEdit | null {
  const trailIndex = trails.findIndex((t) => t.id === trailId);
  if (trailIndex < 0) return null;
  const trail = trails[trailIndex];
  const frame = makeFrame([requested]);
  const pm = toMeters(frame, requested);
  let best: { partIndex: number; segmentIndex: number; edgeIndex: number; u: number; d: number } | null = null;
  trail.parts.forEach((part, partIndex) => (part.segments ?? []).forEach((segment, segmentIndex) => {
    for (let edgeIndex = 0; edgeIndex < segment.centerline.length - 1; edgeIndex++) {
      const hit = pointSegmentDistance(pm, toMeters(frame, segment.centerline[edgeIndex]),
        toMeters(frame, segment.centerline[edgeIndex + 1]));
      if (!best || hit.d < best.d) best = { partIndex, segmentIndex, edgeIndex, u: hit.u, d: hit.d };
    }
  }));
  if (!best || (best as { d: number }).d > 1) return null;
  const hit = best as { partIndex: number; segmentIndex: number; edgeIndex: number; u: number; d: number };
  const part = trail.parts[hit.partIndex];
  const segments = [...(part.segments ?? [])];
  const segment = segments[hit.segmentIndex];
  const a = segment.centerline[hit.edgeIndex];
  const b = segment.centerline[hit.edgeIndex + 1];
  const point: [number, number] = [a[0] + (b[0] - a[0]) * hit.u, a[1] + (b[1] - a[1]) * hit.u];
  if (haversineMeters(point, segment.centerline[0]) <= SAME_POINT_M) {
    const junction = junctions.find((j) => j.id === segment.fromJunctionId);
    return junction ? { trails, junctions, junction } : null;
  }
  if (haversineMeters(point, segment.centerline.at(-1)!) <= SAME_POINT_M) {
    const junction = junctions.find((j) => j.id === segment.toJunctionId);
    return junction ? { trails, junctions, junction } : null;
  }
  const resolved = segment.centerlineElevM.length === segment.centerline.length;
  const elevation = resolved ? segment.centerlineElevM[hit.edgeIndex] +
    (segment.centerlineElevM[hit.edgeIndex + 1] - segment.centerlineElevM[hit.edgeIndex]) * hit.u : null;
  const junction: SavedJunction = { id: idFactory(), point, elevM: elevation, createdAt };
  const leftLine = [...segment.centerline.slice(0, hit.edgeIndex + 1), point];
  const rightLine = [point, ...segment.centerline.slice(hit.edgeIndex + 1)];
  const leftElev = resolved ? [...segment.centerlineElevM.slice(0, hit.edgeIndex + 1), elevation!] : [];
  const rightElev = resolved ? [elevation!, ...segment.centerlineElevM.slice(hit.edgeIndex + 1)] : [];
  const left: SavedTrailSegment = { ...segment, centerline: leftLine, centerlineElevM: leftElev,
    toJunctionId: junction.id };
  const right: SavedTrailSegment = { id: idFactory(), centerline: rightLine, centerlineElevM: rightElev,
    fromJunctionId: junction.id, toJunctionId: segment.toJunctionId };
  segments.splice(hit.segmentIndex, 1, left, right);
  const nextTrail = { ...trail, parts: trail.parts.map((p, i) => i === hit.partIndex ? rebuildPart(p, segments) : p) };
  return { trails: trails.map((t, i) => i === trailIndex ? nextTrail : t),
    junctions: [...junctions, junction], junction };
}

export function liftJunction(
  lifts: SavedLift[], junctions: SavedJunction[], liftId: string, end: 'top' | 'base',
  point: [number, number], idFactory: () => string,
): { junctions: SavedJunction[]; junction: SavedJunction } {
  const existing = junctions.find((j) => j.liftTerminal?.liftId === liftId && j.liftTerminal.end === end);
  if (existing) return { junctions, junction: existing };
  const lift = lifts.find((l) => l.id === liftId);
  const index: 0 | 1 | null = lift
    ? (haversineMeters(lift.points[0], point) <= haversineMeters(lift.points[1], point) ? 0 : 1) : null;
  const junction: SavedJunction = { id: idFactory(), point,
    elevM: index !== null ? lift!.endpointElevM[index] : null, liftTerminal: { liftId, end },
    createdAt: new Date().toISOString() };
  return { junctions: [...junctions, junction], junction };
}

export function withTopologyPart(part: SavedTrailPart, fromJunctionId: string, toJunctionId: string,
  segmentId: string): SavedTrailPart {
  const segment: SavedTrailSegment = { id: segmentId, centerline: part.centerline,
    centerlineElevM: part.centerlineElevM, fromJunctionId, toJunctionId };
  return { ...part, segments: [segment] };
}

/** Cross-entity schema-v1-v4 migration. Explicit anchors become durable shared junctions. */
export function hydrateTopology(trailsInput: SavedTrail[], pathsInput: SavedPath[], lifts: SavedLift[],
  rawJunctions: unknown[], nodes: SavedNode[] = []): { trails: SavedTrail[]; paths: SavedPath[]; junctions: SavedJunction[] } {
  let trails = trailsInput;
  let junctions = hydrateJunctions(trails, rawJunctions);
  const used = new Set([...junctions.map((j) => j.id), ...trails.flatMap((trail) => trail.parts.flatMap((part) =>
    (part.segments ?? []).map((segment) => segment.id)))]);
  let counter = 0;
  const nextId = () => { let id: string; do id = `migration:${++counter}`; while (used.has(id)); used.add(id); return id; };

  const materialize = (anchor: AnchorRef): SavedJunction | null => {
    if (anchor.kind === 'trail') {
      const edit = splitTrailAt(trails, junctions, anchor.trailId, anchor.point, nextId);
      if (!edit) return null;
      trails = edit.trails; junctions = edit.junctions; return edit.junction;
    }
    if (anchor.kind === 'lift') {
      const edit = liftJunction(lifts, junctions, anchor.liftId, anchor.end, anchor.point, nextId);
      junctions = edit.junctions; return edit.junction;
    }
    const existing = junctions.find((junction) => haversineMeters(junction.point, anchor.point) <= SAME_POINT_M);
    if (existing) return existing;
    const junction: SavedJunction = { id: nextId(), point: anchor.point, elevM: null,
      legacyAnchor: anchor, createdAt: new Date().toISOString() };
    junctions = [...junctions, junction]; return junction;
  };

  // A legacy trail anchor describes its first station. Replace that generated
  // endpoint id with the target's shared junction id.
  for (const original of trailsInput) {
    if (!original.anchor) continue;
    const junction = materialize(original.anchor);
    if (!junction) continue;
    trails = trails.map((trail) => {
      if (trail.id !== original.id || !trail.parts[0]?.segments?.[0]) return trail;
      const part = trail.parts[0];
      const segments = [...part.segments!];
      segments[0] = { ...segments[0], fromJunctionId: junction.id };
      return { ...trail, parts: [rebuildPart(part, segments), ...trail.parts.slice(1)] };
    });
  }

  const paths = pathsInput.map((path) => {
    if (path.fromJunctionId && path.toJunctionId) return path;
    const from = materialize(path.from);
    const to = materialize(path.to);
    return { ...path, fromJunctionId: from?.id, toJunctionId: to?.id };
  });
  for (const node of nodes) if (node.anchor) materialize(node.anchor);
  const referenced = new Set(trails.flatMap((trail) => trail.parts.flatMap((part) =>
    (part.segments ?? []).flatMap((segment) => [segment.fromJunctionId, segment.toJunctionId]))));
  for (const path of paths) {
    if (path.fromJunctionId) referenced.add(path.fromJunctionId);
    if (path.toJunctionId) referenced.add(path.toJunctionId);
  }
  junctions = junctions.filter((junction) => referenced.has(junction.id) || junction.liftTerminal || junction.legacyAnchor);
  return { trails, paths, junctions };
}

/** Everything `describeAnchorDetail` needs to turn ids into names and numbers. */
export interface AnchorWorld {
  trails: SavedTrail[];
  lifts: SavedLift[];
  junctions: SavedJunction[];
  nodes?: SavedNode[];
  paths?: SavedPath[];
}

export interface AnchorDescription {
  /** The named thing the anchor attaches to — "Summit Express top", "Run 1". */
  label: string;
  /** Where on it: segment ordinal and graph nodes. Null when there is no more to say. */
  detail: string | null;
}

/**
 * Display number for a junction: its 1-based position in the saved list. New
 * junctions are only ever appended (splitTrailAt, liftJunction), so a number
 * stays put for the life of a save — but it is a label, never an identity, and
 * `hydrateTopology` dropping an orphan can renumber what follows it. Match on
 * `id` for anything that must be durable.
 */
function junctionNumber(junctions: SavedJunction[], id: string | undefined): number | null {
  if (!id) return null;
  const index = junctions.findIndex((junction) => junction.id === id);
  return index < 0 ? null : index + 1;
}

function describeTrailAnchor(
  anchor: Extract<AnchorRef, { kind: 'trail' }>, world: AnchorWorld,
): AnchorDescription {
  const trail = world.trails.find((t) => t.id === anchor.trailId);
  if (!trail) return { label: describeAnchor(anchor), detail: null };

  // Same nearest-segment search splitTrailAt runs, so the segment named here is
  // the one construction will actually cut the new junction into.
  const frame = makeFrame([anchor.point]);
  const pm = toMeters(frame, anchor.point);
  let best: { partIndex: number; segmentIndex: number; segment: SavedTrailSegment; d: number } | null = null;
  for (let partIndex = 0; partIndex < trail.parts.length; partIndex++) {
    const segments = trail.parts[partIndex].segments ?? [];
    for (let segmentIndex = 0; segmentIndex < segments.length; segmentIndex++) {
      const segment = segments[segmentIndex];
      for (let edge = 0; edge < segment.centerline.length - 1; edge++) {
        const hit = pointSegmentDistance(pm, toMeters(frame, segment.centerline[edge]),
          toMeters(frame, segment.centerline[edge + 1]));
        if (!best || hit.d < best.d) best = { partIndex, segmentIndex, segment, d: hit.d };
      }
    }
  }
  // A legacy run carries no segments. Naming the run is still the answer to
  // "which run", so that is worth more than refusing to describe it at all.
  if (!best) return { label: trail.name, detail: null };

  const parts: string[] = [];
  const segmentCount = (trail.parts[best.partIndex].segments ?? []).length;
  if (trail.parts.length > 1) parts.push(`part ${best.partIndex + 1}`);
  parts.push(`segment ${best.segmentIndex + 1} of ${segmentCount}`);

  const from = junctionNumber(world.junctions, best.segment.fromJunctionId);
  const to = junctionNumber(world.junctions, best.segment.toJunctionId);
  const atFrom = haversineMeters(anchor.point, best.segment.centerline[0]) <= SAME_POINT_M;
  const atTo = haversineMeters(anchor.point, best.segment.centerline.at(-1)!) <= SAME_POINT_M;
  // Landing mid-segment does not reuse a node, it creates one between these two
  // — "between" rather than "at" is the difference the player needs to see.
  if (atFrom && from != null) parts.push(`at node ${from}`);
  else if (atTo && to != null) parts.push(`at node ${to}`);
  else if (from != null && to != null) parts.push(`between nodes ${from} and ${to}`);

  return { label: trail.name, detail: parts.join(' · ') };
}

/**
 * Name an anchor against the resort it lives in. `describeAnchor` answers only
 * "what kind of thing is this" — fine for a placement hint, useless in a review
 * panel where two runs both read "On a run" and there is no way to tell which
 * connection was actually made. This resolves the id to its name, the point to
 * the segment it lands on, and the segment to the graph nodes bounding it.
 * Falls back to the generic label whenever the referenced entity is missing.
 */
export function describeAnchorDetail(anchor: AnchorRef, world: AnchorWorld): AnchorDescription {
  switch (anchor.kind) {
    case 'lift': {
      const lift = world.lifts.find((l) => l.id === anchor.liftId);
      const label = lift ? `${lift.name} ${anchor.end}` : describeAnchor(anchor);
      const terminal = world.junctions.find((junction) =>
        junction.liftTerminal?.liftId === anchor.liftId && junction.liftTerminal.end === anchor.end);
      const number = junctionNumber(world.junctions, terminal?.id);
      // A terminal only becomes a junction once something connects to it, so an
      // absent number here means "no node yet", not a lookup failure.
      return { label, detail: number == null ? null : `node ${number}` };
    }
    case 'trail':
      return describeTrailAnchor(anchor, world);
    case 'path': {
      const path = world.paths?.find((p) => p.id === anchor.pathId);
      return { label: path?.name ?? describeAnchor(anchor), detail: null };
    }
    case 'node': {
      const node = world.nodes?.find((n) => n.id === anchor.nodeId);
      return { label: node?.name ?? describeAnchor(anchor), detail: null };
    }
  }
}

/** One trail segment that touches a junction, and where it sits in the trail. */
export interface JunctionUse {
  trailId: string;
  partIndex: number;
  segmentIndex: number;
  /** Which end of the segment this junction is. */
  end: 'from' | 'to';
}

/**
 * Every trail segment incident to a junction. This is the junction's degree —
 * the number the removal rule is stated in terms of. A segment that both starts
 * and ends here (a closed loop) is counted twice, which is correct: two segment
 * ends meet there.
 */
export function junctionUses(trails: SavedTrail[], junctionId: string): JunctionUse[] {
  const out: JunctionUse[] = [];
  for (const trail of trails) {
    trail.parts.forEach((part, partIndex) => (part.segments ?? []).forEach((segment, segmentIndex) => {
      const at = { trailId: trail.id, partIndex, segmentIndex };
      if (segment.fromJunctionId === junctionId) out.push({ ...at, end: 'from' });
      if (segment.toJunctionId === junctionId) out.push({ ...at, end: 'to' });
    }));
  }
  return out;
}

export type JunctionRemoval =
  /** The two segments to fuse: `segmentIndex` and the one after it, in this part. */
  | { ok: true; trailId: string; partIndex: number; segmentIndex: number }
  | { ok: false; reason: string };

/**
 * Whether a node can be taken back out of the graph, and why not when it can't.
 *
 * Only a node with exactly two segment ends meeting at it is removable, and only
 * when those two are consecutive segments of one run — that node is pure
 * punctuation, so deleting it just fuses them back into the single segment they
 * were before the split. Anything else is load-bearing: a lift terminal, a path
 * end, a fork where three segments meet, the end of a run, or the point where
 * two different runs join. Removing one of those would silently drop a
 * connection the player built on purpose, so it is refused with the reason
 * shown in the panel rather than failing quietly.
 */
export function canRemoveJunction(
  trails: SavedTrail[], junctions: SavedJunction[], paths: SavedPath[], junctionId: string,
): JunctionRemoval {
  const junction = junctions.find((j) => j.id === junctionId);
  if (!junction) return { ok: false, reason: 'That node is no longer on the map.' };
  if (junction.liftTerminal) {
    return { ok: false, reason: 'That node is a lift terminal — the lift needs it.' };
  }
  if (junction.legacyAnchor) {
    return { ok: false, reason: 'That node holds a connection from an older save.' };
  }
  if (paths.some((path) => path.fromJunctionId === junctionId || path.toJunctionId === junctionId)) {
    return { ok: false, reason: 'A path connects there.' };
  }

  const uses = junctionUses(trails, junctionId);
  if (uses.length === 0) return { ok: false, reason: 'Nothing connects there.' };
  if (uses.length === 1) return { ok: false, reason: 'That node is the end of a run.' };
  if (uses.length > 2) {
    return { ok: false, reason: `${uses.length} run segments meet there — it is a junction.` };
  }
  const [first, second] = uses;
  if (first.trailId !== second.trailId || first.partIndex !== second.partIndex) {
    return { ok: false, reason: 'Two runs meet there.' };
  }
  // One segment has to arrive and the next leave, or there is nothing to fuse:
  // two segments that both start here point away from each other.
  const incoming = uses.find((use) => use.end === 'to');
  const outgoing = uses.find((use) => use.end === 'from');
  if (!incoming || !outgoing || outgoing.segmentIndex !== incoming.segmentIndex + 1) {
    return { ok: false, reason: 'The run does not pass straight through there.' };
  }
  return { ok: true, trailId: first.trailId, partIndex: first.partIndex,
    segmentIndex: incoming.segmentIndex };
}

/**
 * Delete a mid-run node, fusing the two segments it separated back into one.
 * The exact inverse of `splitTrailAt`: the removed node's point survives as an
 * ordinary interior station, so the painted run does not move a millimetre.
 * Returns null when `canRemoveJunction` refuses — callers should ask first so
 * they can show the reason.
 *
 * Note the junction list shrinks, which renumbers every junction after this one.
 * See `junctionNumber`: the number is a label, not an identity.
 */
export function removeJunction(
  trails: SavedTrail[], junctions: SavedJunction[], paths: SavedPath[], junctionId: string,
): { trails: SavedTrail[]; junctions: SavedJunction[] } | null {
  const check = canRemoveJunction(trails, junctions, paths, junctionId);
  if (!check.ok) return null;
  const trailIndex = trails.findIndex((t) => t.id === check.trailId);
  const trail = trails[trailIndex];
  const part = trail.parts[check.partIndex];
  const segments = [...(part.segments ?? [])];
  const left = segments[check.segmentIndex];
  const right = segments[check.segmentIndex + 1];
  const resolved = left.centerlineElevM.length === left.centerline.length &&
    right.centerlineElevM.length === right.centerline.length;
  const merged: SavedTrailSegment = {
    ...left,
    centerline: [...left.centerline, ...right.centerline.slice(1)],
    centerlineElevM: resolved ? [...left.centerlineElevM, ...right.centerlineElevM.slice(1)] : [],
    toJunctionId: right.toJunctionId,
  };
  segments.splice(check.segmentIndex, 2, merged);
  const nextTrail = { ...trail,
    parts: trail.parts.map((p, i) => i === check.partIndex ? rebuildPart(p, segments) : p) };
  return {
    trails: trails.map((t, i) => i === trailIndex ? nextTrail : t),
    junctions: junctions.filter((j) => j.id !== junctionId),
  };
}

/** A junction as the trails panel lists it. */
export interface JunctionSummary {
  id: string;
  point: [number, number];
  /** The 1-based number `describeAnchorDetail` prints, so the two agree. */
  number: number;
  /** What it belongs to — "Summit Express top", "Ridge Run", "Ridge Run · Bowl". */
  label: string;
  /** Why it cannot be removed, or null when it can. */
  blocked: string | null;
}

/**
 * Name and number every junction, and pre-answer "can this one go?" so the panel
 * can render a disabled delete with a reason instead of failing on click.
 */
export function summarizeJunctions(world: AnchorWorld): JunctionSummary[] {
  const paths = world.paths ?? [];
  return world.junctions.map((junction, index) => {
    const uses = junctionUses(world.trails, junction.id);
    const check = canRemoveJunction(world.trails, world.junctions, paths, junction.id);
    let label: string;
    if (junction.liftTerminal) {
      const lift = world.lifts.find((l) => l.id === junction.liftTerminal!.liftId);
      label = `${lift?.name ?? 'Lift'} ${junction.liftTerminal.end}`;
    } else {
      const names = [...new Set(uses.map((use) =>
        world.trails.find((trail) => trail.id === use.trailId)?.name ?? 'Unknown run'))];
      label = names.length > 0 ? names.join(' · ') : 'Unattached';
    }
    return { id: junction.id, point: junction.point, number: index + 1, label,
      blocked: check.ok ? null : check.reason };
  });
}
