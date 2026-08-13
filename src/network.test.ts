import { describe, expect, it } from 'vitest';
import { sanitizeLifts } from './lifts';
import { sanitizeNodes, sanitizePaths } from './skiNodes';
import { sanitizeTrails, trailStats } from './trails';
import {
  buildSkiNetwork,
  edgesForTrail,
  liftsFromTrail,
  makeFrame,
  networkSummary,
  pointSegmentDistance,
  reachableFrom,
  segmentIntersection,
  SKI_SPEED_MPS,
  toLngLat,
  toMeters,
  trailsFromLift,
  withLiftQueues,
  type LiftEdge,
  type PathEdge,
  type TrailEdge,
} from './network';
import type { AnchorRef } from './types/anchors';
import type { SavedNode, SavedPath } from './types/topology';
import type { SavedLift, SavedTrail } from './types';

// Fixtures are laid out in flat meters around a Cascades-ish anchor and then
// converted to lng/lat, so every distance in a test reads directly.
const LAT0 = 46.93;
const LNG0 = -121.5;
const M_PER_LAT = 111320;
const M_PER_LNG = 111320 * Math.cos((LAT0 * Math.PI) / 180);

/** [east meters, north meters] → [lng, lat]. */
function at(eastM: number, northM: number): [number, number] {
  return [LNG0 + eastM / M_PER_LNG, LAT0 + northM / M_PER_LAT];
}

/** A closed box ring padded around a centerline, so sanitizeTrails accepts it. */
function boxRing(centerline: [number, number][], padM = 15): [number, number][] {
  const lngs = centerline.map((p) => p[0]);
  const lats = centerline.map((p) => p[1]);
  const padLng = padM / M_PER_LNG;
  const padLat = padM / M_PER_LAT;
  const w = Math.min(...lngs) - padLng;
  const e = Math.max(...lngs) + padLng;
  const s = Math.min(...lats) - padLat;
  const n = Math.max(...lats) + padLat;
  return [
    [w, s],
    [e, s],
    [e, n],
    [w, n],
    [w, s],
  ];
}

function mkTrail(
  id: string,
  centerline: [number, number][],
  elevM: number[],
  extra: Record<string, unknown> = {}
): SavedTrail {
  return sanitizeTrails([
    {
      id,
      name: id,
      parts: [{ polygon: [boxRing(centerline)], centerline, centerlineElevM: elevM }],
      brushWidthM: 30,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

function mkLift(
  id: string,
  a: [number, number],
  b: [number, number],
  elevs: [number | null, number | null],
  extra: Record<string, unknown> = {}
): SavedLift {
  return sanitizeLifts([
    {
      id,
      name: id,
      liftClass: 'fixed-grip',
      points: [a, b],
      endpointElevM: elevs,
      chairSize: 2,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

/** A straight run of `n` stations descending `dropM` in total. */
function straightRun(
  id: string,
  from: [number, number],
  to: [number, number],
  topElev: number,
  dropM: number,
  n = 5,
  extra: Record<string, unknown> = {}
): SavedTrail {
  const centerline: [number, number][] = [];
  const elevs: number[] = [];
  for (let i = 0; i < n; i++) {
    const t = i / (n - 1);
    centerline.push(at(from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t));
    elevs.push(topElev - dropM * t);
  }
  return mkTrail(id, centerline, elevs, extra);
}

const trailEdges = (edges: readonly { kind: string }[]) =>
  edges.filter((e): e is TrailEdge => e.kind === 'trail');
const pathEdges = (edges: readonly { kind: string }[]) =>
  edges.filter((e): e is PathEdge => e.kind === 'path');

function mkNode(id: string, point: [number, number], extra: Record<string, unknown> = {}): SavedNode {
  return sanitizeNodes([
    {
      id,
      name: id,
      point,
      elevM: null,
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

function mkPath(
  id: string,
  points: [number, number][],
  from: AnchorRef,
  to: AnchorRef,
  extra: Record<string, unknown> = {}
): SavedPath {
  return sanitizePaths([
    {
      id,
      name: id,
      points,
      pointElevM: [],
      widthM: 6,
      from,
      to,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
      ...extra,
    },
  ])[0];
}

// ---------------------------------------------------------------------------

describe('projection frame', () => {
  it('round-trips lng/lat through the meters frame', () => {
    const frame = makeFrame([at(0, 0), at(500, 800)]);
    const p = at(123, -456);
    const back = toLngLat(frame, toMeters(frame, p));
    expect(back[0]).toBeCloseTo(p[0], 12);
    expect(back[1]).toBeCloseTo(p[1], 12);
  });

  it('centres on the mean of its samples', () => {
    const frame = makeFrame([at(0, 0), at(100, 200)]);
    const mid = toMeters(frame, at(50, 100));
    expect(mid.x).toBeCloseTo(0, 6);
    expect(mid.y).toBeCloseTo(0, 6);
  });
});

describe('segmentIntersection', () => {
  const P = (x: number, y: number) => ({ x, y });

  it('finds a clean X at the expected parameters', () => {
    const hit = segmentIntersection(P(-10, 0), P(10, 0), P(0, -5), P(0, 15));
    expect(hit).not.toBeNull();
    expect(hit?.t).toBeCloseTo(0.5, 9);
    expect(hit?.u).toBeCloseTo(0.25, 9);
  });

  it('returns null for parallel segments', () => {
    expect(segmentIntersection(P(0, 0), P(10, 0), P(0, 5), P(10, 5))).toBeNull();
  });

  it('returns null for collinear overlapping segments', () => {
    expect(segmentIntersection(P(0, 0), P(10, 0), P(5, 0), P(15, 0))).toBeNull();
  });

  it('returns null when the infinite lines cross outside both segments', () => {
    expect(segmentIntersection(P(0, 0), P(1, 0), P(100, -5), P(100, 5))).toBeNull();
  });

  it('is scale-free: two 2 m segments still register', () => {
    const hit = segmentIntersection(P(-1, 0), P(1, 0), P(0, -1), P(0, 1));
    expect(hit).not.toBeNull();
    expect(hit?.t).toBeCloseTo(0.5, 9);
  });
});

describe('pointSegmentDistance', () => {
  const P = (x: number, y: number) => ({ x, y });

  it('clamps before the start', () => {
    const r = pointSegmentDistance(P(-5, 0), P(0, 0), P(10, 0));
    expect(r.u).toBe(0);
    expect(r.d).toBeCloseTo(5, 9);
  });

  it('clamps past the end', () => {
    const r = pointSegmentDistance(P(15, 0), P(0, 0), P(10, 0));
    expect(r.u).toBe(1);
    expect(r.d).toBeCloseTo(5, 9);
  });

  it('drops a perpendicular foot in the middle', () => {
    const r = pointSegmentDistance(P(4, 3), P(0, 0), P(10, 0));
    expect(r.u).toBeCloseTo(0.4, 9);
    expect(r.d).toBeCloseTo(3, 9);
  });
});

// ---------------------------------------------------------------------------

describe('trivial builds', () => {
  it('returns a valid empty network without throwing', () => {
    const net = buildSkiNetwork([], []);
    expect(net.nodes).toHaveLength(0);
    expect(net.edges).toHaveLength(0);
    expect(net.diagnostics.componentCount).toBe(0);
  });

  it('turns a lone run into one edge between two nodes', () => {
    const trail = straightRun('a', [0, 400], [0, 0], 500, 120);
    const net = buildSkiNetwork([trail], []);
    expect(trailEdges(net.edges)).toHaveLength(1);
    expect(net.nodes).toHaveLength(2);
    expect(net.diagnostics.componentCount).toBe(1);
    const [head, foot] = [net.nodes.find((n) => n.kind === 'trail-head'), net.nodes.find((n) => n.kind === 'trail-out')];
    expect(head).toBeDefined();
    expect(foot).toBeDefined();
  });

  it('reproduces the parent stats exactly when there is nothing to split', () => {
    const trail = straightRun('a', [0, 400], [0, 0], 500, 120);
    const net = buildSkiNetwork([trail], []);
    const edge = trailEdges(net.edges)[0];
    const whole = trailStats(trail.parts[0].centerline, trail.parts[0].centerlineElevM);
    expect(edge.lengthM).toBeCloseTo(whole.lengthM, 6);
    expect(edge.avgSlopeDeg).toBeCloseTo(whole.avgSlopeDeg, 6);
    expect(edge.maxSlopeDeg).toBeCloseTo(whole.maxSlopeDeg, 6);
    expect(edge.difficulty).toBe(trail.difficulty);
    expect(edge.areaM2).toBeCloseTo(trail.areaM2, 6);
  });
});

// ---------------------------------------------------------------------------

describe('crossings', () => {
  /** A north-south run crossed east-west at s = 150 m from its head. */
  function crossingFixture() {
    // Steep upper half, near-flat lower half, so the two segments must not
    // simply inherit one grade from the parent.
    const spine: [number, number][] = [at(0, 400), at(0, 300), at(0, 200), at(0, 100), at(0, 0)];
    const a = mkTrail('a', spine, [400, 320, 240, 235, 230]);
    const b = straightRun('b', [-150, 250], [150, 250], 260, 20, 3);
    return { a, b };
  }

  it('splits both runs into two and shares one crossing node', () => {
    const { a, b } = crossingFixture();
    const net = buildSkiNetwork([a, b], []);
    expect(trailEdges(net.edges)).toHaveLength(4);
    const crossings = net.nodes.filter((n) => n.kind === 'crossing');
    expect(crossings).toHaveLength(1);
    expect(crossings[0].trailIds).toEqual(['a', 'b']);
    expect(net.diagnostics.componentCount).toBe(1);
  });

  it('rates each segment on its own terrain, not the parent grade', () => {
    const { a, b } = crossingFixture();
    const net = buildSkiNetwork([a, b], []);
    const segs = edgesForTrail(net, 'a');
    expect(segs).toHaveLength(2);
    expect(segs[0].difficulty).not.toBe(segs[1].difficulty);
    // The upper pitch is far steeper than the run as a whole.
    expect(segs[0].maxSlopeDeg).toBeGreaterThan(a.avgSlopeDeg);
    expect(segs.some((s) => s.difficulty !== a.difficulty)).toBe(true);
  });

  it('finds nothing between runs whose bounding boxes do not overlap', () => {
    const a = straightRun('a', [0, 400], [0, 0], 500, 100);
    const b = straightRun('b', [5000, 400], [5000, 0], 500, 100);
    const net = buildSkiNetwork([a, b], []);
    expect(net.diagnostics.crossingCount).toBe(0);
    expect(trailEdges(net.edges)).toHaveLength(2);
    expect(net.diagnostics.componentCount).toBe(2);
  });

  it('never splits a run where a lift line passes over it', () => {
    // Skiers ski under lifts. Only lift TERMINALS attach to the trail network.
    const a = straightRun('a', [0, 400], [0, 0], 500, 100);
    const lift = mkLift('L', at(-400, 200), at(400, 200), [400, 460]);
    const net = buildSkiNetwork([a], [lift]);
    expect(edgesForTrail(net, 'a')).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------

describe('proximity snapping', () => {
  /** Run `b` descends to a foot that stops `gapM` short of run `a`'s centerline. */
  function tJunction(gapM: number, brushWidthM: number) {
    const a = straightRun('a', [-200, 0], [200, 0], 300, 40, 5, { brushWidthM });
    const b = mkTrail(
      'b',
      [at(0, 300), at(0, 150), at(0, gapM)],
      [420, 360, 305],
      { brushWidthM }
    );
    return buildSkiNetwork([a, b], []);
  }

  it('creates a T-junction when the gap is inside the brush footprint', () => {
    const net = tJunction(20, 30); // tolerance = 30 m
    expect(net.diagnostics.componentCount).toBe(1);
    expect(edgesForTrail(net, 'a')).toHaveLength(2);
    const bFoot = edgesForTrail(net, 'b')[0].to;
    expect(net.nodeById.get(bFoot)?.trailIds).toEqual(['a', 'b']);
  });

  it('leaves the runs disconnected when the gap exceeds the tolerance', () => {
    const net = tJunction(40, 30); // tolerance = 30 m
    expect(net.diagnostics.componentCount).toBe(2);
    expect(edgesForTrail(net, 'a')).toHaveLength(1);
  });

  it('scales the tolerance with brush width, so wider paint connects further', () => {
    const net = tJunction(40, 90); // tolerance = 90 m
    expect(net.diagnostics.componentCount).toBe(1);
    expect(edgesForTrail(net, 'a')).toHaveLength(2);
  });

  it('merges a run that ends where another begins', () => {
    const a = straightRun('a', [0, 400], [0, 200], 500, 60);
    const b = straightRun('b', [0, 190], [0, 0], 438, 60);
    const net = buildSkiNetwork([a, b], []);
    expect(net.diagnostics.componentCount).toBe(1);
    expect(net.nodes).toHaveLength(3);
    expect(edgesForTrail(net, 'a')[0].to).toBe(edgesForTrail(net, 'b')[0].from);
  });

  it('never joins the two ends of one run to each other', () => {
    // A tight hairpin whose head and foot come back within the snap tolerance.
    const spine: [number, number][] = [at(0, 0), at(0, 100), at(20, 100), at(20, 10)];
    const a = mkTrail('a', spine, [400, 380, 370, 350]);
    const net = buildSkiNetwork([a], []);
    expect(trailEdges(net.edges)).toHaveLength(1);
    expect(net.nodes).toHaveLength(2);
  });

  it('does not chain five run ends spaced at the tolerance into one node', () => {
    const runs = [0, 30, 60, 90, 120].map((x, i) =>
      straightRun(`r${i}`, [x, 50], [x, 0], 400, 20, 3)
    );
    const net = buildSkiNetwork(runs, []);
    const feet = runs.map((r) => edgesForTrail(net, r.id)[0].to);
    expect(new Set(feet).size).toBeGreaterThan(1);
  });
});

// ---------------------------------------------------------------------------

describe('lifts', () => {
  it('shares a node with the run heads at its top and reports them served', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const a = straightRun('a', [10, 590], [10, 0], 500, 300);
    const b = straightRun('b', [-10, 590], [-200, 0], 500, 300);
    const net = buildSkiNetwork([a, b], [lift]);
    const liftEdge = net.edgeById.get('l:L') as LiftEdge;
    expect(liftEdge.servesTrailIds).toEqual(['a', 'b']);
    expect(edgesForTrail(net, 'a')[0].from).toBe(liftEdge.to);
  });

  it('closes the loop when a run returns to the lift base', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const a = straightRun('a', [10, 590], [10, 10], 500, 300);
    const net = buildSkiNetwork([a], [lift]);
    expect(net.diagnostics.componentCount).toBe(1);
    expect(net.diagnostics.orphanTrailIds).toEqual([]);
    expect(net.diagnostics.isolatedLiftIds).toEqual([]);
  });

  it('splits a run mid-line when the terminal has no run end to share', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    // Passes 20 m east of the top terminal, but starts far above it.
    const a = straightRun('a', [20, 1200], [20, 0], 800, 600, 7);
    const net = buildSkiNetwork([a], [lift]);
    expect(net.diagnostics.liftSplitCount).toBeGreaterThan(0);
    expect(edgesForTrail(net, 'a').length).toBeGreaterThan(1);
  });

  it('always points base → top, even when drawn top-first', () => {
    const lift = mkLift('L', at(0, 600), at(0, 0), [500, 200]);
    const net = buildSkiNetwork([], [lift]);
    const edge = net.edgeById.get('l:L') as LiftEdge;
    expect(edge.orientationResolved).toBe(true);
    expect(net.nodeById.get(edge.from)?.elevM).toBeCloseTo(200, 6);
    expect(net.nodeById.get(edge.to)?.elevM).toBeCloseTo(500, 6);
    expect(edge.rideTimeS).toBeCloseTo(lift.lengthM / (400 * 0.00508), 6);
    expect(edge.capacityPph).toBe(1200);
    expect(edge.peopleWaiting).toBe(0);
    expect(edge.waitTimeS).toBe(0);
  });

  it('uses the shared type catalog for non-chair and tram performance', () => {
    const carpet = mkLift('carpet', at(0, 0), at(0, 200), [200, 220], {
      liftTypeId: 'magic-carpet',
    });
    const tram = mkLift('tram', at(300, 0), at(300, 800), [200, 700], {
      liftTypeId: 'tram-80',
    });
    const net = buildSkiNetwork([], [carpet, tram]);
    const carpetEdge = net.edgeById.get('l:carpet') as LiftEdge;
    const tramEdge = net.edgeById.get('l:tram') as LiftEdge;
    expect(carpetEdge).toMatchObject({ liftTypeId: 'magic-carpet', capacityPph: 1000 });
    expect(carpetEdge.rideTimeS).toBeCloseTo(carpet.lengthM / (100 * 0.00508), 6);
    expect(tramEdge.liftTypeId).toBe('tram-80');
    expect(tramEdge.capacityPph).toBeCloseTo(
      (80 * 3600) / (tramEdge.rideTimeS + 240),
      6,
    );
  });

  it('still emits a lift whose terminal elevations never resolved', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [null, null]);
    const net = buildSkiNetwork([], [lift]);
    const edge = net.edgeById.get('l:L') as LiftEdge;
    expect(edge.orientationResolved).toBe(false);
    expect(net.diagnostics.unresolvedLiftIds).toEqual(['L']);
  });

  it('merges two lifts that meet at a mid-mountain transfer', () => {
    const lower = mkLift('low', at(0, 0), at(0, 600), [200, 500]);
    const upper = mkLift('up', at(10, 610), at(0, 1200), [505, 800]);
    const net = buildSkiNetwork([], [lower, upper]);
    const a = net.edgeById.get('l:low') as LiftEdge;
    const b = net.edgeById.get('l:up') as LiftEdge;
    expect(a.to).toBe(b.from);
    const shared = net.nodeById.get(a.to);
    expect(shared?.liftTops).toEqual(['low']);
    expect(shared?.liftBases).toEqual(['up']);
  });

  it('flags a lift that serves nothing', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const net = buildSkiNetwork([], [lift]);
    expect(net.diagnostics.isolatedLiftIds).toEqual(['L']);
  });
});

// ---------------------------------------------------------------------------

describe('direction', () => {
  it('keeps every descending segment pointing downhill', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const a = straightRun('a', [10, 590], [10, 10], 500, 300);
    const b = straightRun('b', [-10, 590], [-300, 10], 500, 280);
    const net = buildSkiNetwork([a, b], [lift]);
    for (const e of trailEdges(net.edges)) {
      if (e.traverse) continue;
      const from = net.nodeById.get(e.from)?.elevM;
      const to = net.nodeById.get(e.to)?.elevM;
      if (from == null || to == null) continue;
      expect(from).toBeGreaterThanOrEqual(to - 1e-6);
    }
  });

  it('does not flip a roller that climbs, which would sever the run', () => {
    // Stations every 100 m; the middle leg gains 10 m before dropping again.
    const spine: [number, number][] = [at(0, 300), at(0, 200), at(0, 100), at(0, 0)];
    const a = mkTrail('a', spine, [200, 180, 190, 150]);
    // Two runs cross it at s = 110 and s = 190, isolating the rising leg.
    const c1 = straightRun('c1', [-120, 190], [120, 190], 260, 20, 3);
    const c2 = straightRun('c2', [-120, 110], [120, 110], 240, 20, 3);
    const net = buildSkiNetwork([a, c1, c2], []);
    const segs = edgesForTrail(net, 'a');
    expect(segs).toHaveLength(3);
    const middle = segs[1];
    expect(middle.netVerticalM).toBeLessThan(0);
    expect(middle.traverse).toBe(true);
    // Parent order is preserved: the run still reads head → foot in sequence.
    expect(segs[0].to).toBe(middle.from);
    expect(middle.to).toBe(segs[2].from);
  });

  it('costs a traverse at walking pace and a pitch at skiing pace', () => {
    const flat = straightRun('flat', [0, 300], [0, 0], 400, 0);
    const steep = straightRun('steep', [900, 300], [900, 0], 400, 200);
    const net = buildSkiNetwork([flat, steep], []);
    const flatEdge = edgesForTrail(net, 'flat')[0];
    const steepEdge = edgesForTrail(net, 'steep')[0];
    expect(flatEdge.traverse).toBe(true);
    expect(flatEdge.travelTimeS).toBeCloseTo(flatEdge.lengthM / 2, 6);
    expect(steepEdge.traverse).toBe(false);
    expect(steepEdge.difficulty).not.toBe('green');
    expect(steepEdge.travelTimeS).toBeCloseTo(
      steepEdge.lengthM / SKI_SPEED_MPS[steepEdge.difficulty],
      6
    );
  });

  it('emits a reverse twin for a flat connector only when asked', () => {
    const flat = straightRun('flat', [0, 300], [0, 0], 400, 0);
    const steep = straightRun('steep', [900, 300], [900, 0], 400, 200);
    const off = buildSkiNetwork([flat, steep], []);
    expect(off.edges.some((e) => e.id.endsWith(':r'))).toBe(false);

    const on = buildSkiNetwork([flat, steep], [], { bidirectionalTraverses: true });
    const twins = on.edges.filter((e) => e.id.endsWith(':r'));
    expect(twins).toHaveLength(1);
    expect(twins[0].id).toBe('t:flat:0:0:r');
    const forward = on.edgeById.get('t:flat:0:0') as TrailEdge;
    expect(twins[0].from).toBe(forward.to);
    expect(twins[0].to).toBe(forward.from);
  });
});

// ---------------------------------------------------------------------------

describe('split invariants', () => {
  it('conserves length and area across the segments of a run', () => {
    const spine: [number, number][] = [at(0, 400), at(0, 300), at(0, 200), at(0, 100), at(0, 0)];
    const a = mkTrail('a', spine, [400, 320, 240, 235, 230]);
    const b = straightRun('b', [-150, 250], [150, 250], 260, 20, 3);
    const c = straightRun('c', [-150, 120], [150, 120], 250, 20, 3);
    const net = buildSkiNetwork([a, b, c], []);
    const segs = edgesForTrail(net, 'a');
    expect(segs.length).toBe(3);
    const length = segs.reduce((sum, e) => sum + e.lengthM, 0);
    const area = segs.reduce((sum, e) => sum + e.areaM2, 0);
    expect(length / a.lengthM).toBeCloseTo(1, 3);
    expect(area).toBeCloseTo(a.areaM2, 6);
  });

  it('collapses a split near an endpoint instead of emitting a stub', () => {
    const a = straightRun('a', [0, 400], [0, 0], 500, 120, 5);
    // Crosses 2 m below a's head — far too close to become its own segment.
    const b = straightRun('b', [-150, 398], [150, 398], 500, 20, 3);
    const net = buildSkiNetwork([a, b], []);
    const segs = edgesForTrail(net, 'a');
    expect(net.diagnostics.mergedSplitCount).toBeGreaterThan(0);
    expect(segs).toHaveLength(1);
    for (const e of trailEdges(net.edges)) expect(e.lengthM).toBeGreaterThanOrEqual(8);
  });

  it('leaves segment geometry on the real centerline, not on the node centroid', () => {
    const net = buildSkiNetwork(
      [
        straightRun('a', [-200, 0], [200, 0], 300, 40),
        mkTrail('b', [at(0, 300), at(0, 150), at(0, 20)], [420, 360, 305]),
      ],
      []
    );
    for (const e of trailEdges(net.edges)) {
      const node = net.nodeById.get(e.from);
      if (!node) continue;
      const dx = (e.path[0][0] - node.lngLat[0]) * M_PER_LNG;
      const dy = (e.path[0][1] - node.lngLat[1]) * M_PER_LAT;
      expect(Math.hypot(dx, dy)).toBeLessThanOrEqual(60);
    }
  });
});

// ---------------------------------------------------------------------------

describe('elevations, status and condition', () => {
  it('inherits the parent grade rather than claiming a false green', () => {
    const spine: [number, number][] = [at(0, 400), at(0, 200), at(0, 0)];
    const a = mkTrail('a', spine, []);
    a.difficulty = 'black';
    const net = buildSkiNetwork([a], []);
    const edge = edgesForTrail(net, 'a')[0];
    expect(edge.elevationResolved).toBe(false);
    expect(edge.difficulty).toBe('black');
    expect(edge.netVerticalM).toBeNull();
    expect(net.diagnostics.unresolvedElevationTrailIds).toEqual(['a']);
  });

  it('keeps a planned run visible but not open, and can drop it entirely', () => {
    const a = straightRun('a', [0, 400], [0, 0], 500, 120, 5, { status: 'planning' });
    const shown = buildSkiNetwork([a], []);
    expect(edgesForTrail(shown, 'a')[0].planned).toBe(true);
    expect(edgesForTrail(shown, 'a')[0].open).toBe(false);

    const hidden = buildSkiNetwork([a], [], { includePlanning: false });
    expect(hidden.edges).toHaveLength(0);
  });

  it('propagates a closed run to every one of its segments', () => {
    const spine: [number, number][] = [at(0, 400), at(0, 300), at(0, 200), at(0, 100), at(0, 0)];
    const a = mkTrail('a', spine, [400, 320, 240, 235, 230], { closed: true });
    const b = straightRun('b', [-150, 250], [150, 250], 260, 20, 3);
    const net = buildSkiNetwork([a, b], []);
    const segs = edgesForTrail(net, 'a');
    expect(segs).toHaveLength(2);
    for (const e of segs) {
      expect(e.condition).toBe('closed');
      expect(e.open).toBe(false);
    }
    expect(edgesForTrail(net, 'b').every((e) => e.open)).toBe(true);
  });

  it('routes open-only queries around a closed run', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const open = straightRun('open', [10, 590], [10, 10], 500, 300);
    const shut = straightRun('shut', [-10, 590], [-300, 10], 500, 280, 5, { closed: true });
    const net = buildSkiNetwork([open, shut], [lift]);
    expect(trailsFromLift(net, 'L')?.direct).toEqual(['open', 'shut']);
    expect(trailsFromLift(net, 'L', { openOnly: true })?.direct).toEqual(['open']);
  });
});

// ---------------------------------------------------------------------------

describe('queries', () => {
  /** Lift → run `top`, which forks at a crossing into `west` and `east`. */
  function forkFixture() {
    const lift = mkLift('L', at(0, 0), at(0, 800), [200, 600]);
    const top = straightRun('top', [0, 790], [0, 400], 600, 120, 4);
    const west = straightRun('west', [-10, 395], [-400, 0], 478, 200);
    const east = straightRun('east', [10, 395], [400, 0], 478, 200);
    return { lift, top, west, east };
  }

  it('separates directly served runs from everything downstream', () => {
    const { lift, top, west, east } = forkFixture();
    const net = buildSkiNetwork([top, west, east], [lift]);
    const result = trailsFromLift(net, 'L');
    expect(result?.direct).toEqual(['top']);
    expect(result?.reachable).toEqual(['east', 'top', 'west']);
  });

  it('climbs a second lift only when told to include lifts', () => {
    const lower = mkLift('low', at(0, 0), at(0, 600), [200, 500]);
    const upper = mkLift('up', at(5, 605), at(0, 1200), [502, 800]);
    const highRun = straightRun('high', [10, 1190], [10, 620], 800, 295, 4);
    const net = buildSkiNetwork([highRun], [lower, upper]);
    expect(trailsFromLift(net, 'low')?.reachable).toEqual([]);
    expect(trailsFromLift(net, 'low', { includeLifts: true })?.reachable).toEqual(['high']);
  });

  it('inverts cleanly: liftsFromTrail mirrors trailsFromLift', () => {
    const { lift, top, west, east } = forkFixture();
    const net = buildSkiNetwork([top, west, east], [lift]);
    expect(liftsFromTrail(net, 'top')?.direct).toEqual(['L']);
    expect(liftsFromTrail(net, 'west')?.direct).toEqual([]);
    expect(liftsFromTrail(net, 'west')?.reachable).toEqual(['L']);
  });

  it('returns null for ids that are not in the network', () => {
    const net = buildSkiNetwork([], []);
    expect(trailsFromLift(net, 'nope')).toBeNull();
    expect(liftsFromTrail(net, 'nope')).toBeNull();
  });

  it('records both of two parallel runs between the same junctions', () => {
    // Two runs sharing a head and a foot: a real multigraph edge pair.
    const left = mkTrail('left', [at(0, 400), at(-60, 200), at(0, 0)], [400, 300, 200]);
    const right = mkTrail('right', [at(0, 400), at(60, 200), at(0, 0)], [400, 300, 200]);
    const net = buildSkiNetwork([left, right], []);
    const head = edgesForTrail(net, 'left')[0].from;
    expect(edgesForTrail(net, 'right')[0].from).toBe(head);
    const reach = reachableFrom(net, [head]);
    expect(reach.edgeIds).toContain('t:left:0:0');
    expect(reach.edgeIds).toContain('t:right:0:0');
  });

  it('terminates on a lift-up / run-down cycle and honours maxEdges', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const a = straightRun('a', [10, 590], [10, 10], 500, 300);
    const net = buildSkiNetwork([a], [lift]);
    const full = reachableFrom(net, [net.nodes[0].id], { includeLifts: true } as never);
    expect(full.edgeIds.length).toBeLessThanOrEqual(net.edges.length);
    const capped = reachableFrom(net, [(net.edgeById.get('l:L') as LiftEdge).from], { maxEdges: 1 });
    expect(capped.edgeIds).toHaveLength(1);
  });
});

// ---------------------------------------------------------------------------

describe('diagnostics, determinism and purity', () => {
  it('names runs no lift can reach, and clears once one does', () => {
    const stranded = straightRun('stranded', [900, 400], [900, 0], 500, 120);
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const served = straightRun('served', [10, 590], [10, 10], 500, 300);
    const before = buildSkiNetwork([stranded, served], [lift]);
    expect(before.diagnostics.orphanTrailIds).toEqual(['stranded']);

    const reachLift = mkLift('R', at(900, 0), at(900, 420), [380, 500]);
    const after = buildSkiNetwork([stranded, served], [lift, reachLift]);
    expect(after.diagnostics.orphanTrailIds).toEqual([]);
  });

  it('counts disjoint pods and drops to one when a connector joins them', () => {
    const podA = straightRun('a', [0, 400], [0, 0], 500, 120);
    const podB = straightRun('b', [900, 400], [900, 0], 500, 120);
    expect(buildSkiNetwork([podA, podB], []).diagnostics.componentCount).toBe(2);
    const link = straightRun('link', [5, 5], [895, 5], 380, 5, 5);
    expect(buildSkiNetwork([podA, podB, link], []).diagnostics.componentCount).toBe(1);
  });

  it('is deterministic and independent of input order', () => {
    const lift = mkLift('L', at(0, 0), at(0, 800), [200, 600]);
    const top = straightRun('top', [0, 790], [0, 400], 600, 120, 4);
    const west = straightRun('west', [-10, 395], [-400, 0], 478, 200);
    const east = straightRun('east', [10, 395], [400, 0], 478, 200);

    const a = buildSkiNetwork([top, west, east], [lift]);
    const b = buildSkiNetwork([top, west, east], [lift]);
    expect(b.nodes.map((n) => n.id)).toEqual(a.nodes.map((n) => n.id));
    expect(b.edges.map((e) => e.id)).toEqual(a.edges.map((e) => e.id));

    const shuffled = buildSkiNetwork([east, top, west], [lift]);
    expect(shuffled.nodes.map((n) => [n.id, n.kind])).toEqual(a.nodes.map((n) => [n.id, n.kind]));
    expect([...shuffled.edges].map((e) => `${e.id}|${e.from}|${e.to}`).sort()).toEqual(
      [...a.edges].map((e) => `${e.id}|${e.from}|${e.to}`).sort()
    );
  });

  it('does not mutate the trails or lifts it was given', () => {
    const trail = straightRun('a', [0, 400], [0, 0], 500, 120);
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const trailBefore = JSON.stringify(trail);
    const liftBefore = JSON.stringify(lift);
    buildSkiNetwork([trail], [lift]);
    expect(JSON.stringify(trail)).toBe(trailBefore);
    expect(JSON.stringify(lift)).toBe(liftBefore);
  });
});

// ---------------------------------------------------------------------------

describe('withLiftQueues', () => {
  it('applies Little’s law to the placeholder queue and leaves topology alone', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const net = buildSkiNetwork([], [lift]);
    const updated = withLiftQueues(net, { L: { peopleWaiting: 600 } });
    const edge = updated.edgeById.get('l:L') as LiftEdge;
    expect(edge.peopleWaiting).toBe(600);
    expect(edge.waitTimeS).toBeCloseTo((600 / 1200) * 3600, 6);
    expect(edge.travelTimeS).toBeCloseTo(edge.rideTimeS + edge.waitTimeS, 6);
    // The source network is untouched, and the nodes are shared by identity.
    expect((net.edgeById.get('l:L') as LiftEdge).peopleWaiting).toBe(0);
    expect(updated.nodes).toBe(net.nodes);
  });

  it('returns the same network object when nothing changed', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const net = buildSkiNetwork([], [lift]);
    expect(withLiftQueues(net, {})).toBe(net);
  });
});

describe('networkSummary', () => {
  it('rolls counts and lengths up by grade', () => {
    const green = straightRun('g', [0, 400], [0, 0], 500, 40);
    const black = straightRun('k', [900, 400], [900, 0], 500, 260);
    const net = buildSkiNetwork([green, black], []);
    const summary = networkSummary(net);
    expect(summary.trailEdgeCount).toBe(2);
    expect(summary.liftEdgeCount).toBe(0);
    const total = Object.values(summary.byDifficulty).reduce((n, b) => n + b.count, 0);
    expect(total).toBe(2);
    expect(summary.trailLengthM).toBeCloseTo(green.lengthM + black.lengthM, 3);
  });
});

// ---------------------------------------------------------------------------
// Wave 2B: graph integration of user nodes, paths and explicit anchors.
// ---------------------------------------------------------------------------

describe('graph integration: nodes, paths and explicit anchors', () => {
  it('is byte-identical to the anchor-free build when nodes/paths are empty (back-compat pin)', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const a = straightRun('a', [10, 590], [10, 0], 500, 300);
    const b = straightRun('b', [-10, 590], [-200, 0], 500, 300);
    const base = buildSkiNetwork([a, b], [lift]);
    const withEmptyOpts = buildSkiNetwork([a, b], [lift], { nodes: [], paths: [] });
    expect(withEmptyOpts.nodes.map((n) => n.id)).toEqual(base.nodes.map((n) => n.id));
    expect(withEmptyOpts.edges.map((e) => `${e.id}|${e.from}|${e.to}`)).toEqual(
      base.edges.map((e) => `${e.id}|${e.from}|${e.to}`)
    );
  });

  it('shares a lift-top node with a trail head anchored to it, beyond what liftSnapM inference alone reaches', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    // Head sits 55 m from the lift top — past the 40 m liftSnapM radius, and
    // its bbox doesn't even overlap the terminal, so pure inference can't see it.
    const withoutAnchor = straightRun('b', [55, 600], [400, 0], 500, 300);
    const disconnected = buildSkiNetwork([withoutAnchor], [lift]);
    expect(disconnected.diagnostics.componentCount).toBe(2);

    const withAnchor = straightRun('b', [55, 600], [400, 0], 500, 300, 5, {
      anchor: { kind: 'lift', liftId: 'L', end: 'top', point: at(0, 600) },
    });
    const net = buildSkiNetwork([withAnchor], [lift]);
    const liftEdge = net.edgeById.get('l:L') as LiftEdge;
    expect(edgesForTrail(net, 'b')[0].from).toBe(liftEdge.to);
    expect(net.diagnostics.componentCount).toBe(1);
    expect(net.diagnostics.overreachingAnchorIds).toEqual([]);
  });

  it('can explicitly share a lift-base node when that terminal is chosen as the trailhead', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    const trail = straightRun('base-run', [35, 0], [300, -400], 200, 100, 5, {
      anchor: { kind: 'lift', liftId: 'L', end: 'base', point: at(0, 0) },
    });
    const net = buildSkiNetwork([trail], [lift]);
    const liftEdge = net.edgeById.get('l:L') as LiftEdge;
    expect(edgesForTrail(net, 'base-run')[0].from).toBe(liftEdge.from);
  });

  it('splits a run at an explicit mid-point anchor, sharing the junction and conserving length', () => {
    const a = straightRun('A', [-300, 0], [300, 0], 300, 60, 5);
    const b = straightRun('B', [0, 45], [0, 250], 260, 40, 3, {
      anchor: { kind: 'trail', trailId: 'A', point: at(0, 0) },
    });
    const net = buildSkiNetwork([a, b], []);
    const segsA = edgesForTrail(net, 'A');
    expect(segsA).toHaveLength(2);
    expect(segsA[0].to).toBe(segsA[1].from);
    const bHead = edgesForTrail(net, 'B')[0].from;
    expect(segsA[0].to).toBe(bHead);
    const totalLen = segsA.reduce((sum, e) => sum + e.lengthM, 0);
    expect(Math.abs(totalLen - a.lengthM) / a.lengthM).toBeLessThan(0.005);
    expect(net.diagnostics.overreachingAnchorIds).toEqual([]);
  });

  it('treats an anchor whose target trail no longer exists as unresolved, without changing the graph', () => {
    const withAnchor = straightRun('B', [0, 300], [0, 0], 260, 60, 5, {
      anchor: { kind: 'trail', trailId: 'ghost', point: at(0, 0) },
    });
    const withoutAnchor = straightRun('B', [0, 300], [0, 0], 260, 60, 5);
    const net = buildSkiNetwork([withAnchor], []);
    const baseline = buildSkiNetwork([withoutAnchor], []);
    expect(net.diagnostics.unresolvedAnchorTrailIds).toEqual(['B']);
    expect(net.nodes.map((n) => n.kind)).toEqual(baseline.nodes.map((n) => n.kind));
    expect(net.edges.map((e) => `${e.id}|${e.from}|${e.to}`)).toEqual(
      baseline.edges.map((e) => `${e.id}|${e.from}|${e.to}`)
    );
  });

  it('treats an anchor whose cached point drifted beyond anchorResolveM as unresolved', () => {
    const a = straightRun('A', [-300, 0], [300, 0], 300, 60, 5);
    // Cached 90 m off A's current centerline — beyond the 50 m default.
    const b = straightRun('B', [0, 300], [0, 100], 260, 60, 3, {
      anchor: { kind: 'trail', trailId: 'A', point: at(0, 90) },
    });
    const net = buildSkiNetwork([a, b], []);
    expect(net.diagnostics.unresolvedAnchorTrailIds).toEqual(['B']);
    expect(net.diagnostics.componentCount).toBe(2);
  });

  it('rejects a self-anchor without throwing or creating a self-loop', () => {
    const a = straightRun('A', [-300, 0], [300, 0], 300, 60, 5, {
      anchor: { kind: 'trail', trailId: 'A', point: at(0, 0) },
    });
    expect(() => buildSkiNetwork([a], [])).not.toThrow();
    const net = buildSkiNetwork([a], []);
    expect(net.diagnostics.unresolvedAnchorTrailIds).toEqual(['A']);
    const segs = edgesForTrail(net, 'A');
    expect(segs).toHaveLength(1);
    for (const e of segs) expect(e.from).not.toBe(e.to);
  });

  it('unions an anchor across a distance the cluster cap would refuse, flagging it as overreaching', () => {
    const lift = mkLift('L', at(0, 0), at(0, 600), [200, 500]);
    // ~200 m from the lift top at (0,600) — far past maxClusterRadiusM (60 m).
    const b = straightRun('B', [0, 800], [400, 800], 500, 100, 3, {
      anchor: { kind: 'lift', liftId: 'L', end: 'top', point: at(0, 600) },
    });
    const net = buildSkiNetwork([b], [lift]);
    const liftEdge = net.edgeById.get('l:L') as LiftEdge;
    const bHead = edgesForTrail(net, 'B')[0].from;
    expect(bHead).toBe(liftEdge.to);
    expect(net.diagnostics.overreachingAnchorIds).toEqual(['B']);
  });

  it('drops componentCount from 2 to 1 when a path connects two otherwise-disconnected runs', () => {
    const podA = straightRun('a', [0, 400], [0, 0], 500, 120);
    const podB = straightRun('b', [900, 400], [900, 0], 500, 120);
    const disconnected = buildSkiNetwork([podA, podB], []);
    expect(disconnected.diagnostics.componentCount).toBe(2);

    const path = mkPath(
      'p1',
      [at(5, 5), at(895, 5)],
      { kind: 'trail', trailId: 'a', point: at(5, 5) },
      { kind: 'trail', trailId: 'b', point: at(895, 5) }
    );
    const connected = buildSkiNetwork([podA, podB], [], { paths: [path] });
    expect(connected.diagnostics.componentCount).toBe(1);
  });

  it('emits PathEdges with their own difficulty; a dead-flat path is a traverse, mirrored when bidirectional', () => {
    const flatPoints: [number, number][] = [at(0, 300), at(0, 150), at(0, 0)];
    const n1 = mkNode('n1', flatPoints[0]);
    const n2 = mkNode('n2', flatPoints[2]);
    const path = mkPath(
      'flatpath',
      flatPoints,
      { kind: 'node', nodeId: 'n1', point: flatPoints[0] },
      { kind: 'node', nodeId: 'n2', point: flatPoints[2] },
      { pointElevM: [400, 400, 400] }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1, n2], paths: [path] });
    const segs = pathEdges(net.edges).filter((e) => !e.id.endsWith(':r'));
    expect(segs.length).toBeGreaterThan(0);
    for (const e of segs) {
      expect(e.pathId).toBe('flatpath');
      expect(e.traverse).toBe(true);
      expect(e.difficulty).toBe('green');
    }
    expect(net.pathEdgeIds.get('flatpath')?.length).toBe(segs.length);

    const bidi = buildSkiNetwork([], [], {
      nodes: [n1, n2],
      paths: [path],
      bidirectionalTraverses: true,
    });
    const twins = pathEdges(bidi.edges).filter((e) => e.id.endsWith(':r'));
    expect(twins).toHaveLength(segs.length);
  });

  it('splits both a path and the trail it crosses mid-run', () => {
    const a = straightRun('a', [-200, 100], [200, 100], 300, 40, 5);
    const n1 = mkNode('n1', at(0, 250));
    const n2 = mkNode('n2', at(0, -50));
    const path = mkPath(
      'crosser',
      [at(0, 250), at(0, -50)],
      { kind: 'node', nodeId: 'n1', point: at(0, 250) },
      { kind: 'node', nodeId: 'n2', point: at(0, -50) }
    );
    const net = buildSkiNetwork([a], [], { nodes: [n1, n2], paths: [path] });
    expect(edgesForTrail(net, 'a')).toHaveLength(2);
    const segs = pathEdges(net.edges).filter((e) => !e.id.endsWith(':r'));
    expect(segs).toHaveLength(2);
    expect(net.diagnostics.crossingCount).toBeGreaterThan(0);
  });

  it('keeps a lone SavedNode as a user-node with no incident edges', () => {
    const n = mkNode('solo', at(5000, 5000));
    const net = buildSkiNetwork([], [], { nodes: [n] });
    expect(net.nodes).toHaveLength(1);
    expect(net.nodes[0].kind).toBe('user-node');
    expect(net.nodes[0].nodeIds).toEqual(['solo']);
    expect(net.nodes[0].outgoing).toEqual([]);
    expect(net.nodes[0].incoming).toEqual([]);
  });

  it('joins a node dropped on a run into that run, not an island beside it', () => {
    // A node placed mid-run records what it sits on; honouring that anchor is
    // what stops it becoming a visible junction connected to nothing.
    const a = straightRun('a', [0, 400], [0, 0], 500, 120);
    const mid = a.parts[0].centerline[2];
    const n = mkNode('hub', mid, {
      anchor: { kind: 'trail', trailId: 'a', point: mid } satisfies AnchorRef,
    });
    const net = buildSkiNetwork([a], [], { nodes: [n] });
    expect(net.diagnostics.componentCount).toBe(1);
    const hub = net.nodes.find((x) => x.nodeIds.includes('hub'));
    expect(hub).toBeDefined();
    expect(hub?.trailIds).toEqual(['a']);
    // Sitting mid-run, it splits the run rather than dangling off the side.
    expect(edgesForTrail(net, 'a').length).toBe(2);
  });

  it('leaves an unanchored node islanded, so the anchor is what does the work', () => {
    const a = straightRun('a', [0, 400], [0, 0], 500, 120);
    const n = mkNode('hub', a.parts[0].centerline[2]);
    const net = buildSkiNetwork([a], [], { nodes: [n] });
    expect(net.diagnostics.componentCount).toBe(2);
  });

  it('flags a legacy trail with no anchor field as unanchored, leaving output unchanged', () => {
    const a = straightRun('a', [0, 400], [0, 0], 500, 120);
    const net = buildSkiNetwork([a], []);
    expect(net.diagnostics.unanchoredTrailIds).toEqual(['a']);
    expect(trailEdges(net.edges)).toHaveLength(1);
  });

  it('is deterministic and order-independent across shuffled trails, nodes and paths', () => {
    const lift = mkLift('L', at(0, 0), at(0, 800), [200, 600]);
    const top = straightRun('top', [0, 790], [0, 400], 600, 120, 4);
    const west = straightRun('west', [-10, 395], [-400, 0], 478, 200);
    const east = straightRun('east', [10, 395], [400, 0], 478, 200);
    const n1 = mkNode('n1', at(-800, 800));
    const n2 = mkNode('n2', at(800, 800));
    const path = mkPath(
      'link',
      [at(-800, 800), at(800, 800)],
      { kind: 'node', nodeId: 'n1', point: at(-800, 800) },
      { kind: 'node', nodeId: 'n2', point: at(800, 800) }
    );

    const a = buildSkiNetwork([top, west, east], [lift], { nodes: [n1, n2], paths: [path] });
    const b = buildSkiNetwork([east, top, west], [lift], { nodes: [n2, n1], paths: [path] });
    expect(b.nodes.map((n) => n.id)).toEqual(a.nodes.map((n) => n.id));
    expect([...b.edges].map((e) => `${e.id}|${e.from}|${e.to}`).sort()).toEqual(
      [...a.edges].map((e) => `${e.id}|${e.from}|${e.to}`).sort()
    );
  });

  it('populates pathEdgeIds parallel to trailEdgeIds', () => {
    const n1 = mkNode('n1', at(0, 300));
    const n2 = mkNode('n2', at(0, 0));
    const path = mkPath(
      'p1',
      [at(0, 300), at(0, 0)],
      { kind: 'node', nodeId: 'n1', point: at(0, 300) },
      { kind: 'node', nodeId: 'n2', point: at(0, 0) }
    );
    const net = buildSkiNetwork([], [], { nodes: [n1, n2], paths: [path] });
    const ids = net.pathEdgeIds.get('p1');
    expect(ids).toBeDefined();
    expect(ids!.length).toBeGreaterThan(0);
    for (const id of ids!) {
      const e = net.edgeById.get(id);
      expect(e?.kind).toBe('path');
    }
  });
});
