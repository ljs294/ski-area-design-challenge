import { describe, expect, it } from 'vitest';
import type { SiteCoverGrid } from './types';
import { canopyIntersectionAreaM2, deriveCoverBoundarySegments, intersectCoverGridWithPolygon } from './coverAnalysis';

function grid(data: number[], width = 3, height = 3): SiteCoverGrid {
  return {
    bounds: { west: 0, south: 0, east: width, north: height },
    width, height, cellSizeM: 10, data, complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200', vintage: '2021',
  };
}

describe('source-faithful cover geometry', () => {
  it('preserves a one-cell tree stand and boundary-touching canopy', () => {
    const value = grid([
      10, 30, 30,
      30, 10, 30,
      30, 30, 30,
    ]);
    const segments = deriveCoverBoundarySegments(value);
    expect(segments.length / 5).toBe(8);
    expect(segments.some((_, i) => i % 5 === 0 && segments[i] === 0)).toBe(true);
  });

  it('keeps a one-cell hole in a canopy block', () => {
    const value = grid([
      10, 10, 10,
      10, 30, 10,
      10, 10, 10,
    ]);
    // Outer perimeter (12 cell edges) plus the four exact hole edges.
    expect(deriveCoverBoundarySegments(value).length / 5).toBe(16);
  });

  it('intersects polygons with source cells and holes without clearing whole cells', () => {
    const value = grid([10, 30, 10, 30], 2, 2);
    const polygon: [number, number][][] = [
      [[0, 2], [2, 2], [2, 1], [0, 1], [0, 2]],
      [[0.25, 1.75], [0.75, 1.75], [0.75, 1.25], [0.25, 1.25], [0.25, 1.75]],
    ];
    const cells = intersectCoverGridWithPolygon(value, polygon);
    expect(cells).toHaveLength(2);
    expect(cells[0].coverage).toBeCloseTo(0.75);
    expect(canopyIntersectionAreaM2(value, polygon)).toBeCloseTo(75);
  });
});
