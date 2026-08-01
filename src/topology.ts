import { haversineMeters } from './geo';
import { makeFrame, pointSegmentDistance, toMeters } from './network';
import { sanitizeAnchor, type AnchorRef, type SavedJunction, type SavedNode, type SavedPath } from './skiNodes';
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
