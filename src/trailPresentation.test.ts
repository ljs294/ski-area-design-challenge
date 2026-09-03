import { describe, expect, it } from 'vitest';
import { haversineMeters } from './geo';
import { resolveTrailHitId } from './trailHit';
import { compileTrailPresentation } from './trailPresentation';
import type { SavedJunction, SavedTrailSegment } from './types/topology';
import type { SavedTrail } from './types/trails';

const ORIGIN: [number, number] = [-121.5, 46.9];
const METERS_LNG = 111_320 * Math.cos(ORIGIN[1] * Math.PI / 180);
const point = (x: number, y: number): [number, number] =>
  [ORIGIN[0] + x / METERS_LNG, ORIGIN[1] + y / 111_320];
const rect = (x0: number, y0: number, x1: number, y1: number): [number, number][][] => [[
  point(x0, y0), point(x1, y0), point(x1, y1), point(x0, y1), point(x0, y0),
]];

function trail(
  id: string,
  polygon: [number, number][][],
  centerline: [number, number][],
  segments?: SavedTrailSegment[],
  width = 20,
): SavedTrail {
  return {
    id, name: id, parts: [{ polygon, centerline, centerlineElevM: centerline.map(() => 1000),
      segments }], brushWidthM: width, areaM2: 1000, lengthM: 100, verticalM: 10,
    avgSlopeDeg: 5, maxSlopeDeg: 8, difficulty: id === 'branch' ? 'green' : 'blue',
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
  };
}

describe('trail presentation geometry', () => {
  it('unions overlapping swaths deterministically without merging nearby runs', () => {
    const left = trail('left', rect(-30, -10, 10, 10), [point(-30, 0), point(10, 0)]);
    const right = trail('right', rect(-10, -10, 30, 10), [point(-10, 0), point(30, 0)]);
    const nearby = trail('nearby', rect(40, -10, 60, 10), [point(40, 0), point(60, 0)]);

    const forward = compileTrailPresentation({ trails: [left, right, nearby], junctions: [] });
    const reverse = compileTrailPresentation({ trails: [nearby, right, left], junctions: [] });

    expect(reverse).toEqual(forward);
    expect(forward.surface).toHaveLength(2);
    const leftVisible = forward.routes.filter((route) => route.trailId === 'left')
      .flatMap((route) => route.coordinates);
    const rightVisible = forward.routes.filter((route) => route.trailId === 'right')
      .flatMap((route) => route.coordinates);
    expect(leftVisible.every((coordinate) => haversineMeters(coordinate, point(-30, 0)) < 21))
      .toBe(true);
    expect(rightVisible.every((coordinate) => haversineMeters(coordinate, point(30, 0)) < 21))
      .toBe(true);
    expect(forward.routes.filter((route) => route.trailId === 'nearby').length).toBeGreaterThan(0);
    expect(forward.labels).toHaveLength(3);
  });

  it('keeps a same-run continuation through a junction and trims the terminating branch', () => {
    const node = point(0, 0);
    const top = point(0, 50), bottom = point(0, -50), branchStart = point(-50, 0);
    const throughSegments: SavedTrailSegment[] = [
      { id: 'through:upper', centerline: [top, node], centerlineElevM: [1050, 1000],
        fromJunctionId: 'top', toJunctionId: 'join' },
      { id: 'through:lower', centerline: [node, bottom], centerlineElevM: [1000, 950],
        fromJunctionId: 'join', toJunctionId: 'bottom' },
    ];
    const branchSegments: SavedTrailSegment[] = [{
      id: 'branch:end', centerline: [branchStart, node], centerlineElevM: [1010, 1000],
      fromJunctionId: 'branch-start', toJunctionId: 'join',
    }];
    const through = trail('through', rect(-10, -55, 10, 55), [top, node, bottom], throughSegments);
    const branch = trail('branch', rect(-55, -10, 5, 10), [branchStart, node], branchSegments);
    const junctions: SavedJunction[] = [
      { id: 'join', point: node, elevM: 1000, createdAt: '2026-01-01T00:00:00.000Z' },
      { id: 'top', point: top, elevM: 1050, createdAt: '2026-01-01T00:00:00.000Z' },
      { id: 'bottom', point: bottom, elevM: 950, createdAt: '2026-01-01T00:00:00.000Z' },
      { id: 'branch-start', point: branchStart, elevM: 1010, createdAt: '2026-01-01T00:00:00.000Z' },
    ];

    const result = compileTrailPresentation({ trails: [branch, through], junctions });
    const resolution = result.junctions.find((entry) => entry.junctionId === 'join');
    expect(resolution).toMatchObject({
      throughSegmentIds: ['through:upper', 'through:lower'],
      yieldingSegmentIds: ['branch:end'],
      clearanceM: 10,
    });
    const throughPoints = result.routes.filter((route) => route.trailId === 'through')
      .flatMap((route) => route.coordinates);
    expect(Math.min(...throughPoints.map((coordinate) => haversineMeters(coordinate, node))))
      .toBeLessThan(1);
    const branchPoints = result.routes.filter((route) => route.trailId === 'branch')
      .flatMap((route) => route.coordinates);
    expect(Math.min(...branchPoints.map((coordinate) => haversineMeters(coordinate, node))))
      .toBeGreaterThanOrEqual(9.5);
  });

  it('keeps tiny union cleanup presentation-only', () => {
    const saved = trail('saved', rect(0, 0, 30, 30), [point(15, 30), point(15, 0)]);
    const before = structuredClone(saved);
    compileTrailPresentation({ trails: [saved], junctions: [] });
    expect(saved).toEqual(before);
  });

  it('selects the nearest centerline and cycles one-metre ties', () => {
    const west = trail('west', rect(-20, -20, 20, 20), [point(0, -20), point(0, 20)]);
    const east = trail('east', rect(-20, -20, 20, 20), [point(0.5, -20), point(0.5, 20)]);
    const candidates = ['west', 'east', 'west'];

    expect(resolveTrailHitId([west, east], candidates, point(0.1, 0), null)).toBe('east');
    expect(resolveTrailHitId([west, east], candidates, point(0.1, 0), 'east')).toBe('west');
    expect(resolveTrailHitId([west, east], candidates, point(0.1, 0), 'west')).toBe('east');
    expect(resolveTrailHitId([west, east], ['west'], point(10, 0), null)).toBe('west');
  });
});
