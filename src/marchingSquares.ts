// Pure function: trace real isolines (contour lines) from a dense
// row-major height grid via the standard marching-squares algorithm —
// smooth, linearly-interpolated line segments crossing each grid cell,
// rather than the blocky axis-aligned stubs a naive neighbor-threshold
// check produces.

export interface ContourSegment {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  level: number;
}

type Point = [number, number];

function lerp(v0: number, v1: number, level: number, p0: number, p1: number): number {
  const t = (level - v0) / (v1 - v0);
  return p0 + t * (p1 - p0);
}

/**
 * Marching squares for a single cell/level. Corners are TL/TR/BR/BL; edges
 * are the 4 cell sides. 0 or 2 edge crossings are unambiguous (draw
 * nothing, or connect the 2 points). 4 crossings is the classic "saddle"
 * case — the cell's corners alternate high/low diagonally — resolved by
 * connecting each pair of edges adjacent to whichever diagonal corner is
 * >= level.
 */
function addCellSegments(
  tl: number,
  tr: number,
  br: number,
  bl: number,
  x0: number,
  x1: number,
  y0: number,
  y1: number,
  level: number,
  out: ContourSegment[]
): void {
  const top: Point | null = (tl >= level) !== (tr >= level) ? [lerp(tl, tr, level, x0, x1), y0] : null;
  const right: Point | null = (tr >= level) !== (br >= level) ? [x1, lerp(tr, br, level, y0, y1)] : null;
  const bottom: Point | null = (bl >= level) !== (br >= level) ? [lerp(bl, br, level, x0, x1), y1] : null;
  const left: Point | null = (tl >= level) !== (bl >= level) ? [x0, lerp(tl, bl, level, y0, y1)] : null;

  const crossings = [top, right, bottom, left].filter((p): p is Point => p !== null);

  if (crossings.length === 2) {
    const [a, b] = crossings;
    out.push({ x1: a[0], y1: a[1], x2: b[0], y2: b[1], level });
  } else if (crossings.length === 4) {
    if (tl >= level) {
      out.push({ x1: top![0], y1: top![1], x2: left![0], y2: left![1], level });
      out.push({ x1: right![0], y1: right![1], x2: bottom![0], y2: bottom![1], level });
    } else {
      out.push({ x1: top![0], y1: top![1], x2: right![0], y2: right![1], level });
      out.push({ x1: bottom![0], y1: bottom![1], x2: left![0], y2: left![1], level });
    }
  }
}

/**
 * Trace every isoline at multiples of `intervalUnit` (in whatever unit
 * `heights` is expressed in) across the grid's actual min/max range, mapped
 * onto a mapSize x mapSize world-space square. Returns unordered line
 * segments per level — connecting them into continuous paths isn't
 * necessary for stroking, since canvas draws each segment independently.
 */
export function traceContours(
  heights: number[],
  gridSize: number,
  mapSize: number,
  intervalUnit: number
): ContourSegment[] {
  const lastIndex = gridSize - 1;
  const toX = (col: number) => (col / lastIndex) * mapSize;
  const toY = (row: number) => (row / lastIndex) * mapSize;

  let hMin = Infinity;
  let hMax = -Infinity;
  for (const h of heights) {
    if (h < hMin) hMin = h;
    if (h > hMax) hMax = h;
  }
  if (!Number.isFinite(hMin) || hMax === hMin) return [];

  const segments: ContourSegment[] = [];

  for (let r = 0; r < lastIndex; r++) {
    const y0 = toY(r);
    const y1 = toY(r + 1);

    for (let c = 0; c < lastIndex; c++) {
      const tl = heights[r * gridSize + c];
      const tr = heights[r * gridSize + c + 1];
      const br = heights[(r + 1) * gridSize + c + 1];
      const bl = heights[(r + 1) * gridSize + c];

      const cellMin = Math.min(tl, tr, br, bl);
      const cellMax = Math.max(tl, tr, br, bl);
      if (cellMax === cellMin) continue;

      const lo = Math.max(hMin, Math.ceil(cellMin / intervalUnit) * intervalUnit);
      const hi = Math.min(hMax, Math.floor(cellMax / intervalUnit) * intervalUnit);
      if (lo > hi) continue;

      const x0 = toX(c);
      const x1 = toX(c + 1);

      for (let level = lo; level <= hi; level += intervalUnit) {
        addCellSegments(tl, tr, br, bl, x0, x1, y0, y1, level, segments);
      }
    }
  }

  return segments;
}
