import type maplibregl from 'maplibre-gl';
import { maskToPolygons } from '../coverPolygons';
import { METERS_PER_DEGREE_LAT } from '../geo';
import {
  sampleCoverGrid,
  BUCKET_ORDER,
  type CoverBounds,
  type CoverBucket,
} from './worldcoverProtocol';

// Turns the raster ESA WorldCover map into crisp vector cover polygons for a
// locked build site — computed ONCE when the site is locked, then rendered as a
// static GeoJSON source (zero per-frame cost, sharp edges, and a real polygon
// the trail tool can later clear into). See coverPolygons.ts for the raster->
// vector core and worldcoverProtocol.sampleCoverGrid for the tile sampling.

export const COVER_SOURCE = 'cover-vector';
export const COVER_FILL_LAYER = 'cover-fill';
export const COVER_OUTLINE_LAYER = 'cover-outline';
/** Both cover layer ids, in draw order — what the Ground cover toggle drives
 *  once a site is locked. */
export const COVER_LAYER_IDS = [COVER_FILL_LAYER, COVER_OUTLINE_LAYER];

export type CoverGeoJSON = GeoJSON.FeatureCollection<GeoJSON.Polygon, { bucket: CoverBucket }>;

// Cartographic palette — deeper forest + pale open ground, echoing a printed
// resort trail map. Hillshade draws over these (they sit just beneath it), so
// relief still reads across the fills.
const FILL: Record<CoverBucket, string> = {
  tree: '#41703f',
  grass: '#e4dfc7',
  alpine: '#eef2f6',
  rock: '#c7bfb2',
  water: '#7ea8c9',
};
const OUTLINE: Record<CoverBucket, string> = {
  tree: '#2c4d2b',
  grass: '#c9c3a4',
  alpine: '#cdd6de',
  rock: '#a49a89',
  water: '#5e83a6',
};

// Target ~12 m cells (WorldCover is 10 m native — finer buys nothing), clamped
// so even an 8 km site stays a light one-time trace.
const TARGET_CELL_M = 12;
const MIN_N = 96;
const MAX_N = 512;

function resolutionFor(bounds: CoverBounds): number {
  const midLat = (bounds.north + bounds.south) / 2;
  const widthM =
    Math.abs(bounds.east - bounds.west) * METERS_PER_DEGREE_LAT * Math.cos((midLat * Math.PI) / 180);
  const heightM = Math.abs(bounds.north - bounds.south) * METERS_PER_DEGREE_LAT;
  const span = Math.max(widthM, heightM);
  return Math.min(MAX_N, Math.max(MIN_N, Math.round(span / TARGET_CELL_M)));
}

/**
 * Sample the locked site's cover into an n×n grid, then trace each bucket into
 * smoothed polygons and project them to lng/lat. Cheap and one-time — a few
 * tile fetches plus a marching-squares pass per bucket.
 */
export async function vectorizeCover(bounds: CoverBounds): Promise<CoverGeoJSON> {
  const n = resolutionFor(bounds);
  const grid = await sampleCoverGrid(bounds, n);

  // sample (row r, col c) -> lng/lat; row 0 is the north edge (grid convention).
  const spanLng = bounds.east - bounds.west;
  const spanLat = bounds.north - bounds.south;
  const denom = n - 1 || 1;
  const toLngLat = (x: number, y: number): [number, number] => [
    bounds.west + (x / denom) * spanLng,
    bounds.north - (y / denom) * spanLat,
  ];

  const features: GeoJSON.Feature<GeoJSON.Polygon, { bucket: CoverBucket }>[] = [];
  const mask = new Uint8Array(n * n);

  for (let b = 0; b < BUCKET_ORDER.length; b++) {
    const bucket = BUCKET_ORDER[b];
    let any = false;
    for (let i = 0; i < grid.length; i++) {
      const hit = grid[i] === b ? 1 : 0;
      mask[i] = hit;
      if (hit) any = true;
    }
    if (!any) continue;

    const polys = maskToPolygons(mask, n, { minAreaCells: 6 });
    for (const poly of polys) {
      const coordinates = [poly.outer, ...poly.holes].map((ring) =>
        ring.map(([x, y]) => toLngLat(x, y))
      );
      features.push({
        type: 'Feature',
        properties: { bucket },
        geometry: { type: 'Polygon', coordinates },
      });
    }
  }

  return { type: 'FeatureCollection', features };
}

function fillColorExpr(): maplibregl.ExpressionSpecification {
  return [
    'match',
    ['get', 'bucket'],
    ...BUCKET_ORDER.flatMap((b) => [b, FILL[b]]),
    '#cccccc',
  ] as unknown as maplibregl.ExpressionSpecification;
}
function outlineColorExpr(): maplibregl.ExpressionSpecification {
  return [
    'match',
    ['get', 'bucket'],
    ...BUCKET_ORDER.flatMap((b) => [b, OUTLINE[b]]),
    '#999999',
  ] as unknown as maplibregl.ExpressionSpecification;
}

/**
 * (Re)adds the vector cover source + fill/outline layers, just beneath the
 * hillshade so shaded relief reads over the fills. Idempotent — removes any
 * prior instance first, so it is safe to call on every style (re)load.
 */
export function addCoverLayers(map: maplibregl.Map, geojson: CoverGeoJSON, visible: boolean): void {
  removeCoverLayers(map);
  map.addSource(COVER_SOURCE, { type: 'geojson', data: geojson });

  const before = map.getLayer('hillshade') ? 'hillshade' : undefined;
  const vis: 'visible' | 'none' = visible ? 'visible' : 'none';

  map.addLayer(
    {
      id: COVER_FILL_LAYER,
      type: 'fill',
      source: COVER_SOURCE,
      layout: { visibility: vis },
      paint: { 'fill-color': fillColorExpr(), 'fill-opacity': 0.82, 'fill-antialias': true },
    },
    before
  );
  map.addLayer(
    {
      id: COVER_OUTLINE_LAYER,
      type: 'line',
      source: COVER_SOURCE,
      layout: { visibility: vis, 'line-join': 'round' },
      paint: {
        'line-color': outlineColorExpr(),
        // Forest edges read hard; other class borders stay faint.
        'line-width': [
          'match',
          ['get', 'bucket'],
          'tree',
          1,
          0.4,
        ] as unknown as maplibregl.ExpressionSpecification,
        'line-opacity': 0.55,
      },
    },
    before
  );
}

/** Removes the cover source + layers if present. Safe to call anytime. */
export function removeCoverLayers(map: maplibregl.Map): void {
  for (const id of COVER_LAYER_IDS) if (map.getLayer(id)) map.removeLayer(id);
  if (map.getSource(COVER_SOURCE)) map.removeSource(COVER_SOURCE);
}
