import { maskToPolygons } from '../coverPolygons';
import { METERS_PER_DEGREE_LAT, haversineMeters } from '../geo';

// Turns a hand-painted brush stroke into a clean run polygon. The drag path is
// stamped as a union of disks (brush = a round tip) into a small local raster,
// then traced with the same marching-squares core the ground cover uses — so
// overlapping stamps and switchbacks resolve robustly and the edges come out
// smoothed, not stair-stepped. Computed ONCE on mouse-up (see MapView), never
// per frame. Pure: works in a local planar meters frame, no map/DOM needed.

// Cap on the raster's side length. A long run just uses coarser cells, keeping
// the one-time trace light (≤ ~130k cells) regardless of stroke length.
const MAX_N = 360;
const MIN_CELL_M = 1.5;

/** Local equirectangular meters frame for a set of lng/lat points, plus the
 *  inverse. Accurate at the few-km scale a single run spans. */
function localFrame(path: [number, number][]) {
  let latSum = 0;
  let minLng = Infinity;
  let maxLat = -Infinity;
  for (const [lng, lat] of path) {
    latSum += lat;
    if (lng < minLng) minLng = lng;
    if (lat > maxLat) maxLat = lat;
  }
  const latRef = latSum / path.length;
  const mPerLng = METERS_PER_DEGREE_LAT * Math.cos((latRef * Math.PI) / 180);
  const mPerLat = METERS_PER_DEGREE_LAT;
  // Origin at the NW corner so all local coords are >= 0 (x east, y south).
  const toMeters = (lng: number, lat: number): [number, number] => [
    (lng - minLng) * mPerLng,
    (maxLat - lat) * mPerLat,
  ];
  const toLngLat = (x: number, y: number): [number, number] => [
    minLng + x / mPerLng,
    maxLat - y / mPerLat,
  ];
  return { toMeters, toLngLat };
}

/**
 * Rasterize a brush stroke (lng/lat path, `brushWidthM` tip diameter) and trace
 * it into a run polygon. Returns rings as [outer, ...holes] in lng/lat, or []
 * for a degenerate stroke. Only the single largest connected blob is kept, so a
 * stroke always yields one well-formed polygon.
 */
export function strokeToPolygon(
  path: [number, number][],
  brushWidthM: number
): [number, number][][] {
  if (path.length === 0) return [];
  const radius = Math.max(brushWidthM / 2, MIN_CELL_M);
  const { toMeters, toLngLat } = localFrame(path);

  const pts = path.map(([lng, lat]) => toMeters(lng, lat));
  let minX = Infinity;
  let maxX = -Infinity;
  let minY = Infinity;
  let maxY = -Infinity;
  for (const [x, y] of pts) {
    if (x < minX) minX = x;
    if (x > maxX) maxX = x;
    if (y < minY) minY = y;
    if (y > maxY) maxY = y;
  }

  const margin = radius + 4; // a few meters of padding so the region closes inside
  const spanX = maxX - minX + 2 * margin;
  const spanY = maxY - minY + 2 * margin;
  const span = Math.max(spanX, spanY);
  const cell = Math.max(radius / 3, span / MAX_N, MIN_CELL_M);
  const n = Math.min(MAX_N, Math.ceil(span / cell) + 1);
  if (n < 2) return [];

  // Grid origin (meters) of cell (0,0)'s center.
  const gx0 = minX - margin;
  const gy0 = minY - margin;
  const mask = new Uint8Array(n * n);
  const radiusCells = radius / cell;
  const R = Math.ceil(radiusCells);
  const r2 = radiusCells * radiusCells;

  const stamp = (mx: number, my: number) => {
    const cCenter = Math.round((mx - gx0) / cell);
    const rCenter = Math.round((my - gy0) / cell);
    for (let dr = -R; dr <= R; dr++) {
      for (let dc = -R; dc <= R; dc++) {
        if (dr * dr + dc * dc > r2) continue;
        const rr = rCenter + dr;
        const cc = cCenter + dc;
        if (rr < 0 || rr >= n || cc < 0 || cc >= n) continue;
        mask[rr * n + cc] = 1;
      }
    }
  };

  // Densify along each segment so consecutive disks overlap (no gaps).
  if (pts.length === 1) {
    stamp(pts[0][0], pts[0][1]);
  } else {
    for (let i = 0; i < pts.length - 1; i++) {
      const [ax, ay] = pts[i];
      const [bx, by] = pts[i + 1];
      const segLen = Math.hypot(bx - ax, by - ay);
      const steps = Math.max(1, Math.ceil(segLen / cell));
      for (let s = 0; s <= steps; s++) {
        const t = s / steps;
        stamp(ax + (bx - ax) * t, ay + (by - ay) * t);
      }
    }
  }

  const polys = maskToPolygons(mask, n, { minAreaCells: 6 });
  if (polys.length === 0) return [];

  // Keep only the largest blob (a single drag is one connected region).
  let best = polys[0];
  let bestArea = ringAreaCells(best.outer);
  for (let i = 1; i < polys.length; i++) {
    const a = ringAreaCells(polys[i].outer);
    if (a > bestArea) {
      best = polys[i];
      bestArea = a;
    }
  }

  const toLL = (ring: [number, number][]) =>
    ring.map(([x, y]) => toLngLat(gx0 + x * cell, gy0 + y * cell));
  return [toLL(best.outer), ...best.holes.map(toLL)];
}

function ringAreaCells(ring: [number, number][]): number {
  let a = 0;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    a += (ring[j][0] + ring[i][0]) * (ring[j][1] - ring[i][1]);
  }
  return Math.abs(a / 2);
}

/**
 * Resample a drag path into evenly-spaced stations (by horizontal distance) —
 * the run's spine and elevation-profile sample points. ~1 station per 25 m,
 * clamped to [2, 80] so a long run still samples a bounded number of DEM points.
 */
export function resampleSpine(path: [number, number][]): [number, number][] {
  if (path.length < 2) return path.slice();

  // Cumulative horizontal distance to each vertex.
  const cum: number[] = [0];
  for (let i = 1; i < path.length; i++) {
    cum.push(cum[i - 1] + haversineMeters(path[i - 1], path[i]));
  }
  const total = cum[cum.length - 1];
  if (total === 0) return [path[0], path[path.length - 1]];

  const stations = Math.min(80, Math.max(2, Math.round(total / 25) + 1));
  const out: [number, number][] = [];
  let seg = 0;
  for (let k = 0; k < stations; k++) {
    const d = (k / (stations - 1)) * total;
    while (seg < cum.length - 2 && cum[seg + 1] < d) seg++;
    const segLen = cum[seg + 1] - cum[seg];
    const t = segLen > 0 ? (d - cum[seg]) / segLen : 0;
    const [ax, ay] = path[seg];
    const [bx, by] = path[seg + 1];
    out.push([ax + (bx - ax) * t, ay + (by - ay) * t]);
  }
  return out;
}
