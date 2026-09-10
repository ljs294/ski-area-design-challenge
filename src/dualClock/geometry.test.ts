import { describe, expect, it } from 'vitest';
import { prepareRoute, routePosition, type PreparedRoute } from './geometry';
import type { NetworkEdge } from '../network';
import type { SavedTrail } from '../types/trails';

function edge(path: [number, number][]): NetworkEdge {
  return { id: 'trail-edge', kind: 'trail', from: 'top', to: 'base', trailId: 'trail', trailName: 'Authored',
    lengthM: 100, travelTimeS: 10, condition: 'open', open: true, partIndex: 0, segmentIndex: 0, path,
    elevM: path.map((_, index) => 1000 + index), difficulty: 'blue',
    avgSlopeDeg: 20, maxSlopeDeg: 20, verticalM: 2, netVerticalM: 2, areaM2: 100, brushWidthM: 20,
    status: 'complete', planned: false, elevationResolved: true, traverse: false };
}

function trail(polygon: [number, number][][]): SavedTrail {
  return { id: 'trail', name: 'Authored', brushWidthM: 20, areaM2: 100, lengthM: 100,
    verticalM: 2, avgSlopeDeg: 20, maxSlopeDeg: 20, difficulty: 'blue', status: 'complete', createdAt: '',
    parts: [{ polygon, centerline: [], centerlineElevM: [] }] };
}

describe('dual clock guest routes', () => {
  it('keeps one authored, direction-preserving distance index through bends and uneven samples', () => {
    const path: [number, number][] = [[0, 0], [0.001, 0], [0.001, 0.002], [0.004, 0.002]];
    const route = prepareRoute(edge(path));
    expect(route.lanes).toEqual([path]);
    expect(route.lanes).toHaveLength(1);
    expect(route.length).toBeGreaterThan(0);
    expect(routePosition(route, 0, 'guest-a')).toEqual(path[0]);
    expect(routePosition(route, 1, 'guest-b')).toEqual(path.at(-1));
    expect(routePosition(route, route.distances[1]! / route.length, 'guest-c')).toEqual(path[1]);
    // Identity no longer chooses a lateral lane; every guest follows the same
    // saved centerline, including a second junction publication.
    expect(routePosition(route, 0.55, 'guest-a')).toEqual(routePosition(route, 0.55, 'guest-b'));
  });

  it('preserves boundary endpoints while validating the authored line in the footprint', () => {
    const square: [number, number][] = [[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]];
    const path: [number, number][] = [[0, 0.5], [0.35, 0.5], [0.7, 0.65], [1, 1]];
    const route = prepareRoute(edge(path), trail([square]));
    expect(route.lanes[0]?.[0]).toEqual(path[0]);
    expect(route.lanes[0]?.at(-1)).toEqual(path.at(-1));
    expect(route.length).toBeGreaterThan(0);
  });

  it('hides a centerline that crosses a polygon hole instead of routing around it', () => {
    const outer: [number, number][] = [[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]];
    const hole: [number, number][] = [[0.4, 0.4], [0.6, 0.4], [0.6, 0.6], [0.4, 0.6], [0.4, 0.4]];
    const route = prepareRoute(edge([[0.1, 0.5], [0.9, 0.5]]), trail([outer, hole]));
    expect(route.length).toBe(0);
    expect(route.lanes).toEqual([[]]);
  });

  it('rejects a narrow hole in geographic coordinates without cross-product cancellation', () => {
    const west = -121.5, south = 46.9, width = 0.002, height = 0.002;
    const outer: [number, number][] = [[west, south], [west + width, south], [west + width, south + height],
      [west, south + height], [west, south]];
    const holeWest = west + width * 0.49, holeEast = west + width * 0.51;
    const holeSouth = south + height * 0.45, holeNorth = south + height * 0.55;
    const hole: [number, number][] = [[holeWest, holeSouth], [holeEast, holeSouth], [holeEast, holeNorth],
      [holeWest, holeNorth], [holeWest, holeSouth]];
    const route = prepareRoute(edge([[west + width * 0.1, south + height / 2],
      [west + width * 0.9, south + height / 2]]), trail([outer, hole]));
    expect(route.length).toBe(0);
  });

  it('hides malformed footprints and invalid centerlines', () => {
    const openRing: [number, number][] = [[0, 0], [1, 0], [1, 1], [0, 1]];
    expect(prepareRoute(edge([[0.1, 0.1], [0.9, 0.9]]), trail([openRing])).length).toBe(0);
    expect(prepareRoute(edge([[0.1, 0.1], [0.9, 0.9]]), trail([
      [[0, 0], [1, 0], null as unknown as [number, number], [0, 0]],
    ]))).toEqual({ lanes: [[]], distances: new Float64Array(0), length: 0 });
    expect(prepareRoute(edge([[0, 0], [Number.NaN, 1]])).length).toBe(0);
    expect(prepareRoute(edge([[0, 0]])).length).toBe(0);
  });

  it('drops only repeated centerline vertices while preserving the route endpoints', () => {
    const path: [number, number][] = [[0, 0], [0, 0], [0.5, 0.5], [0.5, 0.5], [1, 1]];
    const route = prepareRoute(edge(path));
    expect(route.lanes).toEqual([[[0, 0], [0.5, 0.5], [1, 1]]]);
    expect(route.length).toBeGreaterThan(0);
  });

  it('keeps identity lane selection for retained pre-edit geometry snapshots', () => {
    const route: PreparedRoute = { lanes: [[[0, 0], [1, 0]], [[0, 1], [1, 1]]], distances: Float64Array.of(0, 1), length: 1 };
    const positions = new Set(['guest-a', 'guest-b', 'guest-c'].map(id => routePosition(route, 0.5, id)[1]));
    expect(positions.size).toBe(2);
  });
});
