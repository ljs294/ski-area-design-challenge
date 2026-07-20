// Pure raster->vector conversion for the ground-cover map. Turns a binary
// class mask (1 = "this cover bucket", 0 = not) into clean, closed, nested
// polygon rings via marching squares at the 0.5 iso-level, so a blocky 10 m
// raster reads as crisp cartographic shapes. Everything here is a pure
// function of the input mask — no DOM, no fetch — so it is cheap to unit test
// and runs once when the build site is locked (see coverVectorize.ts).

export type Ring = [number, number][];

export interface CoverPolygon {
  /** Outer boundary ring, sample-index coords in [0 .. n-1]. */
  outer: Ring;
  /** Interior holes (e.g. a meadow inside forest), same coord space. */
  holes: Ring[];
}

export interface MaskToPolygonsOpts {
  /** Iso-level to trace; 0.5 splits a binary mask down the middle. */
  level?: number;
  /** Box-blur radius (cells) applied before tracing to round the 45° chamfers
   *  a raw binary field produces into organic edges. 0 disables. */
  blurRadius?: number;
  blurIterations?: number;
  /** Douglas–Peucker tolerance in cells. */
  simplifyTol?: number;
  /** Drop rings smaller than this many cells² (speckle removal). */
  minAreaCells?: number;
}

const DEFAULTS: Required<MaskToPolygonsOpts> = {
  level: 0.5,
  blurRadius: 1,
  blurIterations: 1,
  simplifyTol: 0.75,
  minAreaCells: 4,
};

// ---- Blur -----------------------------------------------------------------

/** Separable box blur on an n×n field. Cheap smoothing so iso-lines curve
 *  instead of stair-stepping. Edges clamp (repeat the border sample). */
function boxBlur(src: Float32Array, width: number, height: number, radius: number, iterations: number): Float32Array {
  if (radius <= 0 || iterations <= 0) return src;
  let buf = src;
  const tmp = new Float32Array(width * height);
  const w = radius * 2 + 1;
  for (let it = 0; it < iterations; it++) {
    // Horizontal pass: buf -> tmp
    for (let r = 0; r < height; r++) {
      const row = r * width;
      for (let c = 0; c < width; c++) {
        let sum = 0;
        for (let k = -radius; k <= radius; k++) {
          const cc = Math.min(width - 1, Math.max(0, c + k));
          sum += buf[row + cc];
        }
        tmp[row + c] = sum / w;
      }
    }
    // Vertical pass: tmp -> out
    const out = new Float32Array(width * height);
    for (let c = 0; c < width; c++) {
      for (let r = 0; r < height; r++) {
        let sum = 0;
        for (let k = -radius; k <= radius; k++) {
          const rr = Math.min(height - 1, Math.max(0, r + k));
          sum += tmp[rr * width + c];
        }
        out[r * width + c] = sum / w;
      }
    }
    buf = out;
  }
  return buf;
}

// ---- Marching squares -> closed rings -------------------------------------

function lerp(v0: number, v1: number, level: number, p0: number, p1: number): number {
  const t = (level - v0) / (v1 - v0);
  return p0 + t * (p1 - p0);
}

/** Stable string key for a crossing point. Shared cell edges produce bit-for-bit
 *  identical crossing coordinates (same two samples, same interpolation), so a
 *  rounded key matches exactly across neighboring cells. */
function keyOf(x: number, y: number): string {
  return `${Math.round(x * 4096)}:${Math.round(y * 4096)}`;
}

interface Seg {
  a: [number, number];
  b: [number, number];
}

/**
 * Trace closed boundary rings of a scalar field at `level`. The field is padded
 * with a one-cell border below `level`, so any region touching the grid edge
 * still closes. Returned ring coords are shifted back into the inner
 * [0 .. n-1] sample space (edge crossings clamp to that range).
 */
function traceRings(field: Float32Array, width: number, height: number, level: number): Ring[] {
  const W = width + 2;
  const H = height + 2;
  const below = level - 1;
  const pad = new Float32Array(W * H).fill(below);
  for (let r = 0; r < height; r++) {
    for (let c = 0; c < width; c++) pad[(r + 1) * W + (c + 1)] = field[r * width + c];
  }

  const segs: Seg[] = [];
  for (let r = 0; r < H - 1; r++) {
    for (let c = 0; c < W - 1; c++) {
      const tl = pad[r * W + c];
      const tr = pad[r * W + c + 1];
      const br = pad[(r + 1) * W + c + 1];
      const bl = pad[(r + 1) * W + c];

      const cellMin = Math.min(tl, tr, br, bl);
      const cellMax = Math.max(tl, tr, br, bl);
      if (level < cellMin || level > cellMax) continue;

      const top: [number, number] | null =
        (tl >= level) !== (tr >= level) ? [lerp(tl, tr, level, c, c + 1), r] : null;
      const right: [number, number] | null =
        (tr >= level) !== (br >= level) ? [c + 1, lerp(tr, br, level, r, r + 1)] : null;
      const bottom: [number, number] | null =
        (bl >= level) !== (br >= level) ? [lerp(bl, br, level, c, c + 1), r + 1] : null;
      const left: [number, number] | null =
        (tl >= level) !== (bl >= level) ? [c, lerp(tl, bl, level, r, r + 1)] : null;

      const pts = [top, right, bottom, left].filter((p): p is [number, number] => p !== null);
      if (pts.length === 2) {
        segs.push({ a: pts[0], b: pts[1] });
      } else if (pts.length === 4) {
        // Saddle: pair edges around whichever diagonal corner is >= level.
        if (tl >= level) {
          segs.push({ a: top!, b: left! });
          segs.push({ a: right!, b: bottom! });
        } else {
          segs.push({ a: top!, b: right! });
          segs.push({ a: bottom!, b: left! });
        }
      }
    }
  }

  // Stitch segments into closed loops by shared endpoints (degree 2 everywhere).
  const incident = new Map<string, number[]>(); // point key -> segment indices
  for (let i = 0; i < segs.length; i++) {
    for (const p of [segs[i].a, segs[i].b]) {
      const k = keyOf(p[0], p[1]);
      const list = incident.get(k);
      if (list) list.push(i);
      else incident.set(k, [i]);
    }
  }

  const used = new Array<boolean>(segs.length).fill(false);
  const rings: Ring[] = [];

  const shift = (p: [number, number]): [number, number] => [
    Math.min(width - 1, Math.max(0, p[0] - 1)),
    Math.min(height - 1, Math.max(0, p[1] - 1)),
  ];

  for (let s = 0; s < segs.length; s++) {
    if (used[s]) continue;
    const ring: Ring = [];
    let curSeg = s;
    let curPt = segs[s].a;
    const startKey = keyOf(curPt[0], curPt[1]);
    used[s] = true;
    ring.push(shift(curPt));
    // Advance to the far end of this segment, then keep hopping to the next
    // unused segment sharing that endpoint until we return to the start.
    let next = segs[s].b;
    for (let guard = 0; guard < segs.length + 1; guard++) {
      ring.push(shift(next));
      const nk = keyOf(next[0], next[1]);
      if (nk === startKey) break;
      const cand = incident.get(nk);
      let advanced = false;
      if (cand) {
        for (const si of cand) {
          if (used[si]) continue;
          used[si] = true;
          curSeg = si;
          const other =
            keyOf(segs[si].a[0], segs[si].a[1]) === nk ? segs[si].b : segs[si].a;
          next = other;
          advanced = true;
          break;
        }
      }
      if (!advanced) break; // open chain (shouldn't happen with the padded border)
    }
    void curSeg;
    if (ring.length >= 4) rings.push(ring);
  }

  return rings;
}

// ---- Ring geometry --------------------------------------------------------

function signedArea(ring: Ring): number {
  let a = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    a += (ring[j][0] + ring[i][0]) * (ring[j][1] - ring[i][1]);
  }
  return a / 2;
}

function ringArea(ring: Ring): number {
  return Math.abs(signedArea(ring));
}

/** Even-odd point-in-polygon (ray cast). Rings here never cross, so a boundary
 *  vertex of one ring is unambiguously inside/outside any other ring. */
function pointInRing(pt: [number, number], ring: Ring): boolean {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const xi = ring[i][0];
    const yi = ring[i][1];
    const xj = ring[j][0];
    const yj = ring[j][1];
    const intersect =
      yi > pt[1] !== yj > pt[1] &&
      pt[0] < ((xj - xi) * (pt[1] - yi)) / (yj - yi) + xi;
    if (intersect) inside = !inside;
  }
  return inside;
}

// ---- Douglas–Peucker (closed rings) ---------------------------------------

function perpDist(p: [number, number], a: [number, number], b: [number, number]): number {
  const dx = b[0] - a[0];
  const dy = b[1] - a[1];
  const len2 = dx * dx + dy * dy;
  if (len2 === 0) return Math.hypot(p[0] - a[0], p[1] - a[1]);
  const t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / len2;
  const px = a[0] + t * dx;
  const py = a[1] + t * dy;
  return Math.hypot(p[0] - px, p[1] - py);
}

function dpOpen(pts: Ring, tol: number): Ring {
  if (pts.length < 3) return pts.slice();
  const keep = new Array<boolean>(pts.length).fill(false);
  keep[0] = keep[pts.length - 1] = true;
  const stack: [number, number][] = [[0, pts.length - 1]];
  while (stack.length) {
    const [lo, hi] = stack.pop()!;
    let maxD = -1;
    let idx = -1;
    for (let i = lo + 1; i < hi; i++) {
      const d = perpDist(pts[i], pts[lo], pts[hi]);
      if (d > maxD) {
        maxD = d;
        idx = i;
      }
    }
    if (maxD > tol && idx !== -1) {
      keep[idx] = true;
      stack.push([lo, idx], [idx, hi]);
    }
  }
  return pts.filter((_, i) => keep[i]);
}

/** Simplify a closed ring; keeps it closed and drops it if it collapses. */
function simplifyRing(ring: Ring, tol: number): Ring | null {
  if (ring.length <= 4) return ring;
  const simplified = dpOpen(ring, tol);
  if (simplified.length < 4) return null;
  return simplified;
}

// ---- Public entry point ---------------------------------------------------

/**
 * Convert a binary n×n cover mask into nested cover polygons. Coordinates are
 * in sample-index space [0 .. n-1]; the caller projects them to lng/lat.
 */
export function maskToPolygons(
  mask: Uint8Array,
  n: number,
  opts: MaskToPolygonsOpts = {}
): CoverPolygon[] {
  return maskToPolygonsRect(mask, n, n, opts);
}

/** Rectangular-grid form used by persisted resort ground-cover packages. */
export function maskToPolygonsRect(
  mask: Uint8Array,
  width: number,
  height: number,
  opts: MaskToPolygonsOpts = {}
): CoverPolygon[] {
  const o = { ...DEFAULTS, ...opts };
  if (width < 2 || height < 2 || mask.length !== width * height) return [];

  // Mask -> float field, optionally blurred so edges curve.
  const field = new Float32Array(width * height);
  for (let i = 0; i < field.length; i++) field[i] = mask[i] ? 1 : 0;
  const smoothed = boxBlur(field, width, height, o.blurRadius, o.blurIterations);

  let rings = traceRings(smoothed, width, height, o.level);

  // Simplify, then drop specks.
  rings = rings
    .map((r) => simplifyRing(r, o.simplifyTol))
    .filter((r): r is Ring => r !== null && ringArea(r) >= o.minAreaCells);

  if (rings.length === 0) return [];

  // Nest holes: a ring's depth = how many other rings contain it. Even depth =>
  // filled (outer); odd => hole of its smallest containing ring.
  const meta = rings.map((r) => ({ ring: r, area: ringArea(r) }));
  const depth = new Array<number>(rings.length).fill(0);
  const parent = new Array<number>(rings.length).fill(-1);

  for (let i = 0; i < rings.length; i++) {
    const probe = rings[i][0];
    let smallestParent = -1;
    let smallestArea = Infinity;
    for (let j = 0; j < rings.length; j++) {
      if (i === j) continue;
      if (meta[j].area <= meta[i].area) continue; // a container must be larger
      if (pointInRing(probe, rings[j])) {
        depth[i]++;
        if (meta[j].area < smallestArea) {
          smallestArea = meta[j].area;
          smallestParent = j;
        }
      }
    }
    parent[i] = smallestParent;
  }

  const polys: CoverPolygon[] = [];
  const outerIndex = new Map<number, number>(); // ring idx -> polys idx
  for (let i = 0; i < rings.length; i++) {
    if (depth[i] % 2 === 0) {
      outerIndex.set(i, polys.length);
      polys.push({ outer: rings[i], holes: [] });
    }
  }
  for (let i = 0; i < rings.length; i++) {
    if (depth[i] % 2 === 1 && parent[i] !== -1) {
      const pi = outerIndex.get(parent[i]);
      if (pi !== undefined) polys[pi].holes.push(rings[i]);
    }
  }

  return polys;
}
