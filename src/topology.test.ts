import { describe, expect, it } from 'vitest';
import { sanitizeTrails } from './trails';
import { canRemoveJunction, describeAnchorDetail, hydrateJunctions, hydrateTopology,
  junctionUses, removeJunction, splitTrailAt, summarizeJunctions } from './topology';
import type { SavedJunction, SavedPath } from './types/topology';
import type { SavedLift } from './types';

const rawTrail = {
  id: 'run', name: 'Run', parts: [{
    polygon: [[[-121.501, 46.931], [-121.499, 46.931], [-121.499, 46.929],
      [-121.501, 46.929], [-121.501, 46.931]]],
    centerline: [[-121.5, 46.931], [-121.5, 46.93], [-121.5, 46.929]],
    centerlineElevM: [300, 250, 200],
  }], brushWidthM: 30, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
};

describe('persisted trail topology', () => {
  it('migrates a continuous legacy centerline into a durable segment and endpoint junctions', () => {
    const trails = sanitizeTrails([rawTrail]);
    expect(trails[0].parts[0].segments).toHaveLength(1);
    expect(trails[0].parts[0].segments?.[0].id).toBe('run:0:0');
    const junctions = hydrateJunctions(trails, []);
    expect(junctions.map((junction) => junction.id)).toEqual([
      'junction:run:0:start', 'junction:run:0:end',
    ]);
  });

  it('splits one segment exactly, interpolates elevation, and reuses the junction', () => {
    const trails = sanitizeTrails([rawTrail]);
    const junctions = hydrateJunctions(trails, []);
    let sequence = 0;
    const first = splitTrailAt(trails, junctions, 'run', [-121.5, 46.93],
      () => `new-${++sequence}`)!;
    const segments = first.trails[0].parts[0].segments!;
    expect(segments).toHaveLength(2);
    expect(segments[0].id).toBe('run:0:0');
    expect(segments[0].toJunctionId).toBe(first.junction.id);
    expect(segments[1].fromJunctionId).toBe(first.junction.id);
    expect(segments[0].centerlineElevM.at(-1)).toBeCloseTo(250);
    const again = splitTrailAt(first.trails, first.junctions, 'run', first.junction.point,
      () => `new-${++sequence}`)!;
    expect(again.junction.id).toBe(first.junction.id);
    expect(again.trails[0].parts[0].segments).toHaveLength(2);
  });

  it('migrates an explicit legacy trail anchor into one shared junction', () => {
    const child = { ...rawTrail, id: 'child', name: 'Child',
      parts: [{ ...rawTrail.parts[0],
        centerline: [[-121.5, 46.93], [-121.499, 46.929]], centerlineElevM: [250, 200] }],
      anchor: { kind: 'trail', trailId: 'run', point: [-121.5, 46.93] } };
    const migrated = hydrateTopology(sanitizeTrails([rawTrail, child]), [], [], []);
    const target = migrated.trails.find((trail) => trail.id === 'run')!;
    const branch = migrated.trails.find((trail) => trail.id === 'child')!;
    expect(target.parts[0].segments).toHaveLength(2);
    const shared = branch.parts[0].segments![0].fromJunctionId;
    expect(target.parts[0].segments!.some((segment) =>
      segment.fromJunctionId === shared || segment.toJunctionId === shared)).toBe(true);
  });
});

describe('describeAnchorDetail', () => {
  const lift: SavedLift = {
    id: 'lift', name: 'Summit Express', liftTypeId: 'fixed-grip-quad',
    points: [[-121.5, 46.929], [-121.5, 46.931]], endpointElevM: [200, 300],
    lengthM: 250, verticalM: 100, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('names the lift and its terminal, with the node once one exists', () => {
    const trails = sanitizeTrails([rawTrail]);
    const junctions = hydrateJunctions(trails, []);
    const anchor = { kind: 'lift', liftId: 'lift', end: 'top', point: lift.points[1] } as const;

    // No terminal junction yet — nothing has connected to this lift.
    expect(describeAnchorDetail(anchor, { trails, lifts: [lift], junctions }))
      .toEqual({ label: 'Summit Express top', detail: null });

    const wired = [...junctions, { id: 'terminal', point: lift.points[1], elevM: 300,
      liftTerminal: { liftId: 'lift', end: 'top' as const },
      createdAt: '2026-01-01T00:00:00.000Z' }];
    expect(describeAnchorDetail(anchor, { trails, lifts: [lift], junctions: wired }))
      .toEqual({ label: 'Summit Express top', detail: 'node 3' });
  });

  it('names the run, the segment, and the two nodes a mid-segment landing falls between', () => {
    const trails = sanitizeTrails([{ ...rawTrail, name: 'Ridge Run' }]);
    const junctions = hydrateJunctions(trails, []);
    // Between stations 1 and 2 of the only segment, well clear of either end.
    const described = describeAnchorDetail(
      { kind: 'trail', trailId: 'run', point: [-121.5, 46.9295] },
      { trails, lifts: [], junctions });
    expect(described.label).toBe('Ridge Run');
    expect(described.detail).toBe('segment 1 of 1 · between nodes 1 and 2');
  });

  it('says "at" the exact node when the landing reuses one, and renumbers after a split', () => {
    const trails = sanitizeTrails([rawTrail]);
    const junctions = hydrateJunctions(trails, []);
    let sequence = 0;
    const split = splitTrailAt(trails, junctions, 'run', [-121.5, 46.93], () => `new-${++sequence}`)!;
    const world = { trails: split.trails, lifts: [], junctions: split.junctions };

    // The new junction was appended, so it is node 3 — and the anchor sits on it.
    expect(describeAnchorDetail({ kind: 'trail', trailId: 'run', point: split.junction.point }, world))
      .toEqual({ label: 'Run', detail: 'segment 1 of 2 · at node 3' });
  });

  it('falls back to the generic label when the referenced entity is gone', () => {
    expect(describeAnchorDetail({ kind: 'trail', trailId: 'missing', point: [-121.5, 46.93] },
      { trails: [], lifts: [], junctions: [] })).toEqual({ label: 'On a run', detail: null });
    expect(describeAnchorDetail({ kind: 'node', nodeId: 'missing', point: [-121.5, 46.93] },
      { trails: [], lifts: [], junctions: [] })).toEqual({ label: 'Node', detail: null });
  });
});

describe('adding and removing mid-run nodes', () => {
  /** One run split mid-edge — exactly the shape the Add node tool produces. */
  function splitOnce() {
    const trails = sanitizeTrails([rawTrail]);
    const junctions = hydrateJunctions(trails, []);
    let sequence = 0;
    return splitTrailAt(trails, junctions, 'run', [-121.5, 46.9305], () => `new-${++sequence}`)!;
  }

  it('counts both segment ends that meet at a node', () => {
    const split = splitOnce();
    expect(junctionUses(split.trails, split.junction.id)).toEqual([
      { trailId: 'run', partIndex: 0, segmentIndex: 0, end: 'to' },
      { trailId: 'run', partIndex: 0, segmentIndex: 1, end: 'from' },
    ]);
    // The run's own start is a dead end: one segment, so nothing to fuse.
    expect(junctionUses(split.trails, 'junction:run:0:start')).toHaveLength(1);
  });

  it('fuses the two segments back together without moving the run', () => {
    const split = splitOnce();
    expect(canRemoveJunction(split.trails, split.junctions, [], split.junction.id))
      .toEqual({ ok: true, trailId: 'run', partIndex: 0, segmentIndex: 0 });

    const merged = removeJunction(split.trails, split.junctions, [], split.junction.id)!;
    const segments = merged.trails[0].parts[0].segments!;
    expect(segments).toHaveLength(1);
    expect(segments[0].fromJunctionId).toBe('junction:run:0:start');
    expect(segments[0].toJunctionId).toBe('junction:run:0:end');
    // The removed node survives as an ordinary station, so the painted line is
    // unchanged — this is the exact inverse of the split.
    expect(segments[0].centerline).toEqual([
      [-121.5, 46.931], [-121.5, 46.9305], [-121.5, 46.93], [-121.5, 46.929],
    ]);
    const [first, interpolated, ...rest] = segments[0].centerlineElevM;
    expect([first, ...rest]).toEqual([300, 250, 200]);
    expect(interpolated).toBeCloseTo(275); // the split's own interpolated height
    expect(merged.trails[0].parts[0].centerline).toEqual(segments[0].centerline);
    expect(merged.junctions.map((j) => j.id)).not.toContain(split.junction.id);
  });

  it('refuses the end of a run, a lift terminal, and a node a path connects to', () => {
    const split = splitOnce();
    expect(canRemoveJunction(split.trails, split.junctions, [], 'junction:run:0:end'))
      .toEqual({ ok: false, reason: 'That node is the end of a run.' });

    const terminal: SavedJunction = { ...split.junction, liftTerminal: { liftId: 'lift', end: 'top' } };
    const withTerminal = split.junctions.map((j) => j.id === split.junction.id ? terminal : j);
    expect(canRemoveJunction(split.trails, withTerminal, [], split.junction.id))
      .toEqual({ ok: false, reason: 'That node is a lift terminal — the lift needs it.' });

    const path = { fromJunctionId: split.junction.id, toJunctionId: 'elsewhere' } as SavedPath;
    expect(canRemoveJunction(split.trails, split.junctions, [path], split.junction.id))
      .toEqual({ ok: false, reason: 'A path connects there.' });

    // A refusal must also be a refusal to act, not just a message.
    expect(removeJunction(split.trails, split.junctions, [path], split.junction.id)).toBeNull();
  });

  it('refuses a fork, and refuses where two different runs meet', () => {
    const split = splitOnce();
    const branch = (id: string, name: string) => ({
      ...sanitizeTrails([{ ...rawTrail, id, name }])[0],
      parts: [{ ...split.trails[0].parts[0], segments: [{ id: `${id}:0`,
        centerline: [[-121.5, 46.9305], [-121.499, 46.929]] as [number, number][],
        centerlineElevM: [275, 200], fromJunctionId: split.junction.id, toJunctionId: `${id}:end` }] }],
    });

    // Three segment ends at one point: the split pair plus a run branching off.
    expect(canRemoveJunction([...split.trails, branch('b', 'Branch')], split.junctions, [],
      split.junction.id)).toEqual({ ok: false, reason: '3 run segments meet there — it is a junction.' });

    // Two ends, but they belong to different runs — fusing them would weld two
    // runs into one, which is not what "remove a node" means.
    const trimmed = split.trails.map((trail) => ({ ...trail,
      parts: [{ ...trail.parts[0], segments: [trail.parts[0].segments![0]] }] }));
    expect(canRemoveJunction([...trimmed, branch('b', 'Branch')], split.junctions, [],
      split.junction.id)).toEqual({ ok: false, reason: 'Two runs meet there.' });
  });

  it('numbers and labels every node, and pre-answers whether it can go', () => {
    const split = splitOnce();
    const lift: SavedLift = {
      id: 'lift', name: 'Summit Express', liftTypeId: 'fixed-grip-quad',
      points: [[-121.5, 46.929], [-121.5, 46.931]], endpointElevM: [200, 300],
      lengthM: 250, verticalM: 100, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
    };
    const junctions = [...split.junctions, { id: 'terminal', point: lift.points[1], elevM: 300,
      liftTerminal: { liftId: 'lift', end: 'top' as const }, createdAt: '2026-01-01T00:00:00.000Z' }];
    const rows = summarizeJunctions({ trails: split.trails, lifts: [lift], junctions });

    expect(rows.map((r) => r.number)).toEqual([1, 2, 3, 4]);
    // Node 3 is the split, and the only removable one here.
    expect(rows[2]).toMatchObject({ id: split.junction.id, label: 'Run', blocked: null });
    expect(rows[0].blocked).toBe('That node is the end of a run.');
    expect(rows[3]).toMatchObject({ label: 'Summit Express top' });
    expect(rows[3].blocked).toBe('That node is a lift terminal — the lift needs it.');
  });
});
