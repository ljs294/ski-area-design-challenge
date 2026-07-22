import type { SavedTrailPart } from '../types';

export type PaintMode = 'paint' | 'erase';
export interface PaintPreview { polygons: [number, number][][][]; areaM2: number }
export interface PaintAnalysis extends PaintPreview { parts: SavedTrailPart[] }

const TILE_N = 256;
const MAX_TILES = 1024; // 64 MiB of mask bytes
const MAX_OCCUPIED_CELLS = 2_000_000;
const KEY_BIAS = 1_000_000;
const KEY_SPAN = 2_000_001;

const cellKey = (x: number, y: number) => (x + KEY_BIAS) * KEY_SPAN + y + KEY_BIAS;
const keyCell = (key: number): [number, number] => {
  const x = Math.floor(key / KEY_SPAN) - KEY_BIAS;
  return [x, key - (x + KEY_BIAS) * KEY_SPAN - KEY_BIAS];
};
const tileKey = (x: number, y: number) => `${Math.floor(x / TILE_N)},${Math.floor(y / TILE_N)}`;
const mod = (n: number, d: number) => ((n % d) + d) % d;

export class TrailPaintEngine {
  readonly cellSizeM: number;
  private readonly origin: [number, number];
  private readonly metersLng: number;
  private readonly radiusCells: number;
  private tiles = new Map<string, Uint8Array>();
  private occupied = 0;
  /** Changed cells encoded as [startKey, runLength] pairs. */
  private history: Float64Array[] = [];

  constructor(origin: [number, number], brushWidthM: number) {
    this.origin = origin;
    this.metersLng = 111_320 * Math.cos(origin[1] * Math.PI / 180);
    this.cellSizeM = Math.max(1, Math.min(4, brushWidthM / 8));
    this.radiusCells = brushWidthM / 2 / this.cellSizeM;
  }

  private toCell([lng, lat]: [number, number]): [number, number] {
    return [Math.round((lng - this.origin[0]) * this.metersLng / this.cellSizeM),
      Math.round((lat - this.origin[1]) * 111_320 / this.cellSizeM)];
  }

  private toLngLat(x: number, y: number): [number, number] {
    return [this.origin[0] + x * this.cellSizeM / this.metersLng,
      this.origin[1] + y * this.cellSizeM / 111_320];
  }

  private get(x: number, y: number): number {
    const tile = this.tiles.get(tileKey(x, y));
    return tile?.[mod(y, TILE_N) * TILE_N + mod(x, TILE_N)] ?? 0;
  }

  private set(x: number, y: number, value: number): boolean {
    const tk = tileKey(x, y);
    let tile = this.tiles.get(tk);
    if (!tile) {
      if (!value) return false;
      if (this.tiles.size >= MAX_TILES) throw new Error('Trail is too large at this brush resolution.');
      tile = new Uint8Array(TILE_N * TILE_N);
      this.tiles.set(tk, tile);
    }
    const i = mod(y, TILE_N) * TILE_N + mod(x, TILE_N);
    if (tile[i] === value) return false;
    if (value && this.occupied >= MAX_OCCUPIED_CELLS)
      throw new Error('Trail is too large at this brush resolution.');
    tile[i] = value;
    this.occupied += value ? 1 : -1;
    return true;
  }

  apply(path: [number, number][], mode: PaintMode): PaintPreview {
    if (path.length === 0) return this.preview();
    const points = path.map((p) => this.toCell(p));
    const changed: number[] = [];
    const changedSet = new Set<number>();
    const value = mode === 'paint' ? 1 : 0;
    const stamp = (cx: number, cy: number) => {
      const r = Math.ceil(this.radiusCells);
      const r2 = this.radiusCells * this.radiusCells;
      for (let y = cy - r; y <= cy + r; y++) for (let x = cx - r; x <= cx + r; x++) {
        if ((x - cx) ** 2 + (y - cy) ** 2 > r2) continue;
        const key = cellKey(x, y);
        if (changedSet.has(key)) continue;
        const before = this.get(x, y);
        if (before !== value && this.set(x, y, value)) {
          changedSet.add(key);
          changed.push(key);
        }
      }
    };
    try {
      for (let i = 0; i < points.length; i++) {
        const a = i === 0 ? points[i] : points[i - 1];
        const b = points[i];
        const distance = Math.hypot(b[0] - a[0], b[1] - a[1]);
        const steps = Math.max(1, Math.ceil(distance / 0.5));
        for (let s = i === 0 ? 0 : 1; s <= steps; s++) {
          const t = s / steps;
          stamp(Math.round(a[0] + (b[0] - a[0]) * t), Math.round(a[1] + (b[1] - a[1]) * t));
        }
      }
    } catch (error) {
      for (const key of changed) {
        const [x, y] = keyCell(key);
        this.set(x, y, value ? 0 : 1);
      }
      throw error;
    }
    if (changed.length > 0) {
      changed.sort((a, b) => a - b);
      const spans: number[] = [];
      let start = changed[0], previous = start, length = 1;
      for (let i = 1; i < changed.length; i++) {
        if (changed[i] === previous + 1) length++;
        else { spans.push(start, length); start = changed[i]; length = 1; }
        previous = changed[i];
      }
      spans.push(start, length);
      this.history.push(Float64Array.from(spans));
    }
    return this.preview();
  }

  undo(): PaintPreview {
    const changed = this.history.pop();
    if (changed) for (let i = 0; i < changed.length; i += 2) {
      const start = changed[i], length = changed[i + 1];
      for (let offset = 0; offset < length; offset++) {
        const [x, y] = keyCell(start + offset);
        this.set(x, y, this.get(x, y) ? 0 : 1);
      }
    }
    return this.preview();
  }

  clear(): PaintPreview {
    this.tiles.clear();
    this.history = [];
    this.occupied = 0;
    return this.preview();
  }

  canUndo(): boolean { return this.history.length > 0; }
  areaM2(): number { return this.occupied * this.cellSizeM * this.cellSizeM; }

  private activeCells(): Set<number> {
    const cells = new Set<number>();
    for (const [tk, tile] of this.tiles) {
      const [tx, ty] = tk.split(',').map(Number);
      for (let i = 0; i < tile.length; i++) if (tile[i]) {
        cells.add(cellKey(tx * TILE_N + i % TILE_N, ty * TILE_N + Math.floor(i / TILE_N)));
      }
    }
    return cells;
  }

  private components(): Set<number>[] {
    const remaining = this.activeCells();
    const parts: Set<number>[] = [];
    const dirs = [[1, 0], [-1, 0], [0, 1], [0, -1]] as const;
    while (remaining.size) {
      const first = remaining.values().next().value as number;
      const part = new Set<number>([first]);
      remaining.delete(first);
      const queue = [first];
      for (let q = 0; q < queue.length; q++) {
        const [x, y] = keyCell(queue[q]);
        for (const [dx, dy] of dirs) {
          const k = cellKey(x + dx, y + dy);
          if (remaining.delete(k)) { part.add(k); queue.push(k); }
        }
      }
      parts.push(part);
    }
    return parts;
  }

  private polygonOf(cells: Set<number>): [number, number][][] {
    return smoothMaskRings(cells).map((ring) =>
      simplifyRing(ring, 0.5).map(([x, y]) => this.toLngLat(x, y))
    ).sort((a, b) => Math.abs(signedArea(b)) - Math.abs(signedArea(a)));
  }

  preview(): PaintPreview {
    const minCells = Math.max(1, Math.floor(Math.PI * this.radiusCells * this.radiusCells));
    const retained = this.components().filter((c) => c.size >= minCells);
    const polygons = retained.map((c) => this.polygonOf(c));
    return { polygons, areaM2: retained.reduce((sum, c) => sum + c.size, 0) * this.cellSizeM ** 2 };
  }

  analyze(): PaintAnalysis {
    const minCells = Math.max(1, Math.floor(Math.PI * this.radiusCells * this.radiusCells));
    const components = this.components().filter((c) => c.size >= minCells);
    const parts: SavedTrailPart[] = [];
    for (const component of components) {
      const centerCells = thin(component);
      const route = skeletonDiameter(centerCells);
      if (route.length < 2) continue;
      const local = smoothRoute(route.map(keyCell));
      const sampled = resampleLocal(local, 25 / this.cellSizeM, 80);
      parts.push({
        polygon: this.polygonOf(component),
        centerline: sampled.map(([x, y]) => this.toLngLat(x, y)),
        centerlineElevM: [],
      });
    }
    const retainedAreaM2 = components.reduce((sum, c) => sum + c.size, 0) * this.cellSizeM ** 2;
    return { polygons: parts.map((p) => p.polygon), areaM2: retainedAreaM2, parts };
  }
}

interface MaskSegment { a: [number, number]; b: [number, number] }

const contourPointKey = ([x, y]: [number, number]) =>
  `${Math.round(x * 4096)}:${Math.round(y * 4096)}`;

/** Trace a softened sparse mask without ever assembling the component's dense
 * bounding rectangle. Two radius-one box blurs are equivalent to the separable
 * [1,2,3,2,1]/9 kernel used here. Only squares near a true mask boundary can
 * cross the 0.5 iso-level, keeping work proportional to perimeter. */
function smoothMaskRings(cells: Set<number>): [number, number][][] {
  const candidateSquares = new Set<number>();
  const cardinal = [[1, 0], [-1, 0], [0, 1], [0, -1]] as const;
  for (const key of cells) {
    const [x, y] = keyCell(key);
    if (cardinal.every(([dx, dy]) => cells.has(cellKey(x + dx, y + dy)))) continue;
    for (let dy = -3; dy <= 2; dy++) for (let dx = -3; dx <= 2; dx++)
      candidateSquares.add(cellKey(x + dx, y + dy));
  }

  const weights = [1, 2, 3, 2, 1];
  const field = new Map<number, number>();
  const valueAt = (x: number, y: number) => {
    const key = cellKey(x, y);
    const known = field.get(key);
    if (known != null) return known;
    let sum = 0;
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++)
      if (cells.has(cellKey(x + dx, y + dy))) sum += weights[dx + 2] * weights[dy + 2];
    const value = sum / 81;
    field.set(key, value);
    return value;
  };
  const crossing = (v0: number, v1: number, a: [number, number], b: [number, number]) => {
    const t = (0.5 - v0) / (v1 - v0);
    return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t] as [number, number];
  };

  const segments: MaskSegment[] = [];
  for (const key of candidateSquares) {
    const [x, y] = keyCell(key);
    const bl = valueAt(x, y), br = valueAt(x + 1, y);
    const tr = valueAt(x + 1, y + 1), tl = valueAt(x, y + 1);
    if (Math.min(bl, br, tr, tl) >= 0.5 || Math.max(bl, br, tr, tl) < 0.5) continue;
    const bottom = (bl >= 0.5) !== (br >= 0.5) ? crossing(bl, br, [x, y], [x + 1, y]) : null;
    const right = (br >= 0.5) !== (tr >= 0.5) ? crossing(br, tr, [x + 1, y], [x + 1, y + 1]) : null;
    const top = (tl >= 0.5) !== (tr >= 0.5) ? crossing(tl, tr, [x, y + 1], [x + 1, y + 1]) : null;
    const left = (bl >= 0.5) !== (tl >= 0.5) ? crossing(bl, tl, [x, y], [x, y + 1]) : null;
    const points = [bottom, right, top, left].filter((p): p is [number, number] => p !== null);
    if (points.length === 2) segments.push({ a: points[0], b: points[1] });
    else if (points.length === 4) {
      // Deterministic saddle resolution, matching the ground-cover vectorizer.
      if (bl >= 0.5) {
        segments.push({ a: bottom!, b: left! }, { a: right!, b: top! });
      } else {
        segments.push({ a: bottom!, b: right! }, { a: top!, b: left! });
      }
    }
  }

  const incident = new Map<string, number[]>();
  for (let i = 0; i < segments.length; i++) for (const point of [segments[i].a, segments[i].b]) {
    const key = contourPointKey(point);
    const list = incident.get(key) ?? [];
    list.push(i); incident.set(key, list);
  }
  const used = new Uint8Array(segments.length);
  const rings: [number, number][][] = [];
  for (let startSegment = 0; startSegment < segments.length; startSegment++) {
    if (used[startSegment]) continue;
    used[startSegment] = 1;
    const start = segments[startSegment].a;
    const startKey = contourPointKey(start);
    const ring: [number, number][] = [start];
    let next = segments[startSegment].b;
    for (let guard = 0; guard <= segments.length; guard++) {
      ring.push(next);
      const nextKey = contourPointKey(next);
      if (nextKey === startKey) break;
      const candidates = incident.get(nextKey);
      const segmentIndex = candidates?.find((index) => !used[index]);
      if (segmentIndex == null) break;
      used[segmentIndex] = 1;
      const segment = segments[segmentIndex];
      next = contourPointKey(segment.a) === nextKey ? segment.b : segment.a;
    }
    if (ring.length >= 4 && contourPointKey(ring.at(-1)!) === startKey) rings.push(ring);
  }
  return rings;
}

function signedArea(ring: [number, number][]): number {
  let sum = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++)
    sum += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
  return sum / 2;
}

function simplifyRing(points: [number, number][], tolerance: number): [number, number][] {
  if (points.length < 6) return points;
  const open = points.slice(0, -1);
  const keep = new Uint8Array(open.length); keep[0] = keep[open.length - 1] = 1;
  const stack: [number, number][] = [[0, open.length - 1]];
  while (stack.length) {
    const [lo, hi] = stack.pop()!;
    const [ax, ay] = open[lo], [bx, by] = open[hi];
    const dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
    let best = tolerance, at = -1;
    for (let i = lo + 1; i < hi; i++) {
      const t = len2 ? ((open[i][0] - ax) * dx + (open[i][1] - ay) * dy) / len2 : 0;
      const d = Math.hypot(open[i][0] - (ax + t * dx), open[i][1] - (ay + t * dy));
      if (d > best) { best = d; at = i; }
    }
    if (at >= 0) { keep[at] = 1; stack.push([lo, at], [at, hi]); }
  }
  const result = open.filter((_, i) => keep[i]);
  if (result.length < 3) return points;
  result.push(result[0]);
  return result;
}

const neighbors = (key: number, set: Set<number>) => {
  const [x, y] = keyCell(key);
  const out: number[] = [];
  for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    if (!dx && !dy) continue;
    const k = cellKey(x + dx, y + dy); if (set.has(k)) out.push(k);
  }
  return out;
};

/** Zhang-Suen thinning on the occupied sparse cells. */
function thin(input: Set<number>): Set<number> {
  const set = new Set(input);
  let changed = true;
  const phase = (second: boolean) => {
    const remove: number[] = [];
    for (const key of set) {
      const [x, y] = keyCell(key);
      const p = [cellKey(x, y + 1), cellKey(x + 1, y + 1), cellKey(x + 1, y), cellKey(x + 1, y - 1),
        cellKey(x, y - 1), cellKey(x - 1, y - 1), cellKey(x - 1, y), cellKey(x - 1, y + 1)].map((k) => set.has(k) ? 1 : 0);
      const count = p.reduce<number>((a, b) => a + b, 0);
      if (count < 2 || count > 6) continue;
      let transitions = 0;
      for (let i = 0; i < 8; i++) if (!p[i] && p[(i + 1) % 8]) transitions++;
      if (transitions !== 1) continue;
      const a = second ? p[0] * p[2] * p[6] : p[0] * p[2] * p[4];
      const b = second ? p[0] * p[4] * p[6] : p[2] * p[4] * p[6];
      if (!a && !b) remove.push(key);
    }
    for (const key of remove) set.delete(key);
    return remove.length > 0;
  };
  let guard = 0;
  while (changed && guard++ < 256) {
    const first = phase(false);
    const second = phase(true);
    changed = first || second;
  }
  return set;
}

function skeletonDiameter(set: Set<number>): number[] {
  if (set.size === 0) return [];
  const endpoints = [...set].filter((k) => neighbors(k, set).length <= 1);
  const start = endpoints[0] ?? set.values().next().value as number;
  const farthest = (source: number, parents = false) => {
    const dist = new Map<number, number>([[source, 0]]), parent = new Map<number, number>();
    const queue: [number, number][] = [[0, source]];
    let far = source;
    while (queue.length) {
      queue.sort((a, b) => b[0] - a[0]);
      const [d, key] = queue.pop()!;
      if (d !== dist.get(key)) continue;
      if (d > (dist.get(far) ?? -1)) far = key;
      const [x, y] = keyCell(key);
      for (const n of neighbors(key, set)) {
        const [nx, ny] = keyCell(n);
        const nd = d + (x === nx || y === ny ? 1 : Math.SQRT2);
        if (nd < (dist.get(n) ?? Infinity)) { dist.set(n, nd); parent.set(n, key); queue.push([nd, n]); }
      }
    }
    return { far, parent: parents ? parent : new Map<number, number>() };
  };
  const a = farthest(start).far;
  const result = farthest(a, true);
  const path = [result.far];
  while (path[path.length - 1] !== a) {
    const p = result.parent.get(path[path.length - 1]); if (p == null) break; path.push(p);
  }
  return path.reverse();
}

function smoothRoute(points: [number, number][]): [number, number][] {
  if (points.length < 3) return points;
  return points.map((p, i) => i === 0 || i === points.length - 1 ? p : [
    (points[i - 1][0] + 2 * p[0] + points[i + 1][0]) / 4,
    (points[i - 1][1] + 2 * p[1] + points[i + 1][1]) / 4,
  ]);
}

function resampleLocal(path: [number, number][], spacing: number, max: number): [number, number][] {
  const cum = [0];
  for (let i = 1; i < path.length; i++) cum.push(cum[i - 1] + Math.hypot(path[i][0] - path[i - 1][0], path[i][1] - path[i - 1][1]));
  const total = cum[cum.length - 1];
  const count = Math.min(max, Math.max(2, Math.round(total / spacing) + 1));
  const out: [number, number][] = []; let seg = 0;
  for (let i = 0; i < count; i++) {
    const d = total * i / (count - 1);
    while (seg < cum.length - 2 && cum[seg + 1] < d) seg++;
    const span = cum[seg + 1] - cum[seg] || 1, t = (d - cum[seg]) / span;
    out.push([path[seg][0] + (path[seg + 1][0] - path[seg][0]) * t,
      path[seg][1] + (path[seg + 1][1] - path[seg][1]) * t]);
  }
  return out;
}
