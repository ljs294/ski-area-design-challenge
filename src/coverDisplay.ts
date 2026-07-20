import { maskToPolygonsRect, type CoverPolygon } from './coverPolygons';
import type { CoverClassCode, CoverGrid, WorldCoverClassCode } from './types';
import { isFourClassGrid, TERRAIN_COVER_CODES } from './fourClassCover';

export const COVER_DISPLAY_SMOOTHING_M = 24;
export const COVER_DISPLAY_SIMPLIFY_M = 10;
export const COVER_DISPLAY_MIN_FEATURE_M2 = 600;
export const COVER_DISPLAY_VERTEX_BUDGET = 250_000;

export const WORLD_COVER_CODES: WorldCoverClassCode[] = [
  10, 20, 30, 40, 50, 60, 70, 80, 90, 95, 100,
];
export const FOUR_CLASS_COVER_CODES: CoverClassCode[] = [
  TERRAIN_COVER_CODES.forest, TERRAIN_COVER_CODES.alpine, TERRAIN_COVER_CODES.grassland, TERRAIN_COVER_CODES.water,
];
const ALL_COVER_CODES: CoverClassCode[] = [...WORLD_COVER_CODES, ...FOUR_CLASS_COVER_CODES];

export interface CoverDisplayStats {
  polygonCount: number;
  ringCount: number;
  vertexCount: number;
  smoothingM: number;
  simplifyM: number;
  minFeatureM2: number;
}

export interface DerivedCoverDisplay {
  geometry: number[];
  stats: CoverDisplayStats;
}

export type CoverDisplayGeoJSON = GeoJSON.FeatureCollection<
  GeoJSON.Polygon,
  { code: CoverClassCode }
>;

function traceAt(grid: CoverGrid, simplifyM: number): DerivedCoverDisplay {
  const mask = new Uint8Array(grid.width * grid.height);
  const geometry: number[] = [];
  let polygonCount = 0;
  let ringCount = 0;
  let vertexCount = 0;
  const cellM = Math.max(0.1, grid.cellSizeM);
  const fourClass = isFourClassGrid(grid);
  const smoothingM = fourClass ? 6 : COVER_DISPLAY_SMOOTHING_M;
  const minFeatureM2 = fourClass ? 16 : COVER_DISPLAY_MIN_FEATURE_M2;
  const options = {
    blurRadius: Math.max(1, Math.min(5, Math.round(smoothingM / cellM))),
    blurIterations: 2,
    simplifyTol: Math.max(0.5, simplifyM / cellM),
    minAreaCells: Math.max(1, Math.round(minFeatureM2 / (cellM * cellM))),
  };

  const appendPolygon = (code: CoverClassCode, polygon: CoverPolygon) => {
    const rings = [polygon.outer, ...polygon.holes];
    geometry.push(code, rings.length);
    polygonCount++;
    ringCount += rings.length;
    for (const ring of rings) {
      geometry.push(ring.length);
      vertexCount += ring.length;
      for (const [x, y] of ring) {
        geometry.push(x / Math.max(1, grid.width - 1), y / Math.max(1, grid.height - 1));
      }
    }
  };

  for (const code of fourClass ? FOUR_CLASS_COVER_CODES : WORLD_COVER_CODES) {
    let any = false;
    for (let i = 0; i < grid.data.length; i++) {
      const hit = grid.data[i] === code ? 1 : 0;
      mask[i] = hit;
      if (hit) any = true;
    }
    if (!any) continue;
    for (const polygon of maskToPolygonsRect(mask, grid.width, grid.height, options)) {
      appendPolygon(code, polygon);
    }
  }

  return {
    geometry,
    stats: {
      polygonCount,
      ringCount,
      vertexCount,
      smoothingM,
      simplifyM,
      minFeatureM2,
    },
  };
}

/** One-time raster-to-vector preparation with a deterministic vertex budget. */
export function deriveCoverDisplayGeometry(grid: CoverGrid): DerivedCoverDisplay {
  const initialSimplifyM = isFourClassGrid(grid) ? 2 : COVER_DISPLAY_SIMPLIFY_M;
  let result = traceAt(grid, initialSimplifyM);
  for (let simplifyM = initialSimplifyM + 2; result.stats.vertexCount > COVER_DISPLAY_VERTEX_BUDGET && simplifyM <= 20; simplifyM += 2) {
    result = traceAt(grid, simplifyM);
  }
  return result.stats.vertexCount > COVER_DISPLAY_VERTEX_BUDGET
    ? limitGeometry(result, COVER_DISPLAY_VERTEX_BUDGET)
    : result;
}

interface EncodedPolygon {
  order: number;
  values: number[];
  ringCount: number;
  vertexCount: number;
  area: number;
}

function encodedPolygons(values: ArrayLike<number>): EncodedPolygon[] {
  const data = Array.from(values);
  const polygons: EncodedPolygon[] = [];
  let i = 0;
  while (i < data.length) {
    const start = i;
    const code = data[i++];
    const ringCount = data[i++];
    if (!ALL_COVER_CODES.includes(code as CoverClassCode) || !Number.isInteger(ringCount) || ringCount < 1) throw new Error('Invalid ground-cover display geometry header.');
    let vertexCount = 0;
    let area = 0;
    for (let ringIndex = 0; ringIndex < ringCount; ringIndex++) {
      const pointCount = data[i++];
      if (!Number.isInteger(pointCount) || pointCount < 4 || i + pointCount * 2 > data.length) throw new Error('Invalid ground-cover display geometry ring.');
      vertexCount += pointCount;
      for (let pointIndex = 0; pointIndex < pointCount; pointIndex++) {
        if (!Number.isFinite(data[i + pointIndex * 2]) || !Number.isFinite(data[i + pointIndex * 2 + 1])) throw new Error('Invalid ground-cover display coordinate.');
      }
      if (ringIndex === 0) {
        for (let pointIndex = 0; pointIndex < pointCount; pointIndex++) {
          const current = i + pointIndex * 2;
          const next = i + ((pointIndex + 1) % pointCount) * 2;
          area += data[current] * data[next + 1] - data[next] * data[current + 1];
        }
        area = Math.abs(area) / 2;
      }
      i += pointCount * 2;
    }
    polygons.push({ order: polygons.length, values: data.slice(start, i), ringCount, vertexCount, area });
  }
  return polygons;
}

function limitGeometry(result: DerivedCoverDisplay, budget: number): DerivedCoverDisplay {
  const candidates = encodedPolygons(result.geometry).sort((a, b) => b.area - a.area || a.order - b.order);
  const retained: EncodedPolygon[] = [];
  let vertexCount = 0;
  for (const polygon of candidates) {
    if (vertexCount + polygon.vertexCount > budget) continue;
    retained.push(polygon);
    vertexCount += polygon.vertexCount;
  }
  retained.sort((a, b) => a.order - b.order);
  return {
    geometry: retained.flatMap((polygon) => polygon.values),
    stats: {
      ...result.stats,
      polygonCount: retained.length,
      ringCount: retained.reduce((sum, polygon) => sum + polygon.ringCount, 0),
      vertexCount,
    },
  };
}

export function inspectCoverDisplayGeometry(values: ArrayLike<number>): Pick<CoverDisplayStats, 'polygonCount' | 'ringCount' | 'vertexCount'> {
  const polygons = encodedPolygons(values);
  return {
    polygonCount: polygons.length,
    ringCount: polygons.reduce((sum, polygon) => sum + polygon.ringCount, 0),
    vertexCount: polygons.reduce((sum, polygon) => sum + polygon.vertexCount, 0),
  };
}

/** Decode the compact Float32 stream into the GeoJSON MapLibre consumes. */
export function coverDisplayToGeoJSON(
  values: ArrayLike<number>,
  bounds: { west: number; south: number; east: number; north: number }
): CoverDisplayGeoJSON {
  const features: CoverDisplayGeoJSON['features'] = [];
  let i = 0;
  while (i < values.length) {
    const code = values[i++] as CoverClassCode;
    const ringCount = values[i++];
    if (!ALL_COVER_CODES.includes(code) || !Number.isInteger(ringCount) || ringCount < 1) {
      throw new Error('Invalid ground-cover display geometry header.');
    }
    const coordinates: GeoJSON.Position[][] = [];
    for (let ringIndex = 0; ringIndex < ringCount; ringIndex++) {
      const pointCount = values[i++];
      if (!Number.isInteger(pointCount) || pointCount < 4 || i + pointCount * 2 > values.length) {
        throw new Error('Invalid ground-cover display geometry ring.');
      }
      const ring: GeoJSON.Position[] = [];
      for (let pointIndex = 0; pointIndex < pointCount; pointIndex++) {
        const x = values[i++];
        const y = values[i++];
        if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error('Invalid ground-cover display coordinate.');
        ring.push([
          bounds.west + x * (bounds.east - bounds.west),
          bounds.north - y * (bounds.north - bounds.south),
        ]);
      }
      coordinates.push(ring);
    }
    features.push({
      type: 'Feature',
      properties: { code },
      geometry: { type: 'Polygon', coordinates },
    });
  }
  return { type: 'FeatureCollection', features };
}
