import type { CoverClassCode, CoverGrid } from './types';

export interface CoverIntersectionCell {
  row: number;
  column: number;
  code: CoverClassCode;
  /** Fraction of this source cell covered by the polygon, from 0 through 1. */
  coverage: number;
  areaM2: number;
}

/** Tree cover and mangroves share the canopy planning treatment. */
export function isCanopyCode(code: number): boolean {
  return code === 1 || code === 10 || code === 95;
}

/**
 * Derive source-faithful canopy and shrub edges. No blur, simplification, or
 * speckle removal is allowed here: one-cell stands and holes must survive.
 * Tuples are [x1,y1,x2,y2,class], normalized to the package bounds.
 */
export function deriveCoverBoundarySegments(grid: CoverGrid): number[] {
  const out: number[] = [];
  const group = (code: number) => code === 1 ? 1 : isCanopyCode(code) ? 10 : code === 20 ? 20 : 0;
  const at = (row: number, col: number) =>
    row < 0 || row >= grid.height || col < 0 || col >= grid.width
      ? 0
      : group(grid.data[row * grid.width + col]);
  const add = (x1: number, y1: number, x2: number, y2: number, code: number) =>
    out.push(x1 / grid.width, y1 / grid.height, x2 / grid.width, y2 / grid.height, code);

  for (let row = 0; row < grid.height; row++) {
    for (let col = 0; col < grid.width; col++) {
      const code = at(row, col);
      if (!code) continue;
      if (at(row - 1, col) !== code) add(col, row, col + 1, row, code);
      if (at(row, col + 1) !== code) add(col + 1, row, col + 1, row + 1, code);
      if (at(row + 1, col) !== code) add(col + 1, row + 1, col, row + 1, code);
      if (at(row, col - 1) !== code) add(col, row + 1, col, row, code);
    }
  }
  return out;
}

type Point = [number, number];

function polygonArea(points: Point[]): number {
  let area = 0;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    area += points[j][0] * points[i][1] - points[i][0] * points[j][1];
  }
  return Math.abs(area) / 2;
}

function clipAxis(points: Point[], axis: 0 | 1, value: number, keepGreater: boolean): Point[] {
  if (!points.length) return points;
  const out: Point[] = [];
  const inside = (p: Point) => keepGreater ? p[axis] >= value : p[axis] <= value;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const a = points[j], b = points[i];
    const aIn = inside(a), bIn = inside(b);
    if (aIn !== bIn) {
      const denominator = b[axis] - a[axis];
      const t = denominator === 0 ? 0 : (value - a[axis]) / denominator;
      const p: Point = [a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])];
      p[axis] = value;
      out.push(p);
    }
    if (bIn) out.push(b);
  }
  return out;
}

function clippedArea(ring: Point[], col: number, row: number): number {
  let points = clipAxis(ring, 0, col, true);
  points = clipAxis(points, 0, col + 1, false);
  points = clipAxis(points, 1, row, true);
  points = clipAxis(points, 1, row + 1, false);
  return polygonArea(points);
}

/**
 * Exact polygon/source-grid intersection in grid-cell units. This reusable
 * boundary is intentionally independent of trail state so future clearing
 * previews and cost estimates can consume the same result.
 */
export function intersectCoverGridWithPolygon(
  grid: CoverGrid,
  rings: [number, number][][]
): CoverIntersectionCell[] {
  if (!rings.length || rings[0].length < 3) return [];
  const { west, east, south, north } = grid.bounds;
  const toGrid = ([lng, lat]: [number, number]): Point => [
    ((lng - west) / (east - west)) * grid.width,
    ((north - lat) / (north - south)) * grid.height,
  ];
  const transformed = rings.map((ring) => ring.map(toGrid));
  const xs = transformed[0].map((p) => p[0]);
  const ys = transformed[0].map((p) => p[1]);
  const minCol = Math.max(0, Math.floor(Math.min(...xs)));
  const maxCol = Math.min(grid.width - 1, Math.floor(Math.max(...xs)));
  const minRow = Math.max(0, Math.floor(Math.min(...ys)));
  const maxRow = Math.min(grid.height - 1, Math.floor(Math.max(...ys)));
  const result: CoverIntersectionCell[] = [];
  for (let row = minRow; row <= maxRow; row++) {
    for (let column = minCol; column <= maxCol; column++) {
      let coverage = clippedArea(transformed[0], column, row);
      for (let h = 1; h < transformed.length; h++) coverage -= clippedArea(transformed[h], column, row);
      coverage = Math.max(0, Math.min(1, coverage));
      if (coverage <= 1e-8) continue;
      result.push({
        row,
        column,
        code: grid.data[row * grid.width + column] as CoverClassCode,
        coverage,
        areaM2: coverage * grid.cellSizeM * grid.cellSizeM,
      });
    }
  }
  return result;
}

export function canopyIntersectionAreaM2(grid: CoverGrid, rings: [number, number][][]): number {
  return intersectCoverGridWithPolygon(grid, rings)
    .filter((cell) => isCanopyCode(cell.code))
    .reduce((sum, cell) => sum + cell.areaM2, 0);
}
