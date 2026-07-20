import maplibregl from 'maplibre-gl';
import mlcontour from 'maplibre-contour';
import { registerWorldcoverProtocol, WORLDCOVER_PROTOCOL } from './worldcoverProtocol';
import {
  registerTerrainProtocols,
  SLOPE_PROTOCOL,
  ASPECT_PROTOCOL,
} from './terrainProtocols';

// Verified keyless + CORS-open (see v1-scope memory).
const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

const M_TO_FT = 3.28084;

/** A toggleable analysis layer, possibly spanning several MapLibre layer ids. */
export interface LayerToggle {
  id: string;
  label: string;
  layerIds: string[];
  visible: boolean;
  /** Toggles sharing an exclusiveGroup can't be on at the same time. */
  exclusiveGroup?: string;
  /** Optional panel section heading this toggle falls under (e.g. 'Basemap'). */
  section?: string;
}

/** Collects basemap layer ids by category, for the water/roads/buildings/labels
 * toggles. Call with the ORIGINAL style layers, before analysis layers are added. */
function basemapCategories(styleLayers: maplibregl.LayerSpecification[]) {
  const water: string[] = [];
  const roads: string[] = [];
  const buildings: string[] = [];
  const labels: string[] = [];
  for (const l of styleLayers) {
    const sl = (l as { 'source-layer'?: string })['source-layer'];
    if (sl === 'water' || sl === 'waterway') water.push(l.id);
    else if (sl === 'transportation') roads.push(l.id);
    else if (sl === 'building') buildings.push(l.id);
    if (l.type === 'symbol') labels.push(l.id); // all basemap text/icon layers
  }
  return { water, roads, buildings, labels };
}

// maplibre-contour registers global protocols; keep one DemSource for the app.
let contourDem: InstanceType<typeof mlcontour.DemSource> | null = null;

function ensureContourDem(): InstanceType<typeof mlcontour.DemSource> {
  if (!contourDem) {
    contourDem = new mlcontour.DemSource({
      url: TERRARIUM_TILES,
      encoding: 'terrarium',
      maxzoom: 15,
      worker: false, // main-thread: avoids Vite worker-bundling complexity for now
    });
    contourDem.setupMaplibre(maplibregl);
  }
  return contourDem;
}

/**
 * Adds the Phase-1 analysis layers (ground cover, hillshade, contours) to a
 * freshly-loaded map, inserted beneath the basemap's labels, and returns the
 * toggle registry describing them (in panel display order).
 */
export function setupAnalysisLayers(map: maplibregl.Map): LayerToggle[] {
  // Insert analysis layers below the basemap's road layers, so roads (and the
  // labels above them) stay visible on top of ground cover / slope / aspect.
  const styleLayers = map.getStyle().layers ?? [];
  const roadAnchor = styleLayers.find(
    (l) => (l as { 'source-layer'?: string })['source-layer'] === 'transportation'
  )?.id;
  const before = roadAnchor ?? styleLayers.find((l) => l.type === 'symbol')?.id;

  // Capture basemap feature layer ids BEFORE we add our own layers.
  const basemap = basemapCategories(styleLayers);

  // --- Satellite imagery (Esri World Imagery) ---
  // Draped at the bottom of our stack (under hillshade + overlays) so shaded
  // relief reads over the photo. Off by default; the big "photoreal" lever,
  // especially over 3D terrain. Goes to z19 — far crisper than the vector
  // basemap when the LOD keeps that detail into the distance.
  map.addSource('satellite', {
    type: 'raster',
    tiles: [
      'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    ],
    tileSize: 256,
    maxzoom: 19,
    attribution: 'Imagery © Esri, Maxar, Earthstar Geographics',
  });
  map.addLayer(
    {
      id: 'satellite',
      type: 'raster',
      source: 'satellite',
      layout: { visibility: 'none' },
      paint: { 'raster-opacity': 1 },
    },
    before
  );

  // --- Ground cover: ESA WorldCover recolored into our buckets ---
  registerWorldcoverProtocol();
  map.addSource('worldcover', {
    type: 'raster',
    tiles: [`${WORLDCOVER_PROTOCOL}://{z}/{x}/{y}`],
    tileSize: 256,
    maxzoom: 14, // WorldCover WMTS tops out at z14 (10 m)
    attribution: '© ESA WorldCover project / Copernicus',
  });
  map.addLayer(
    {
      id: 'groundcover',
      type: 'raster',
      source: 'worldcover',
      layout: { visibility: 'none' },
      paint: {
        'raster-opacity': 0.9,
        // Crisp class edges instead of a bilinear smear. In-game (locked site)
        // this raster is superseded by the vectorized cover; this keeps the
        // pre-lock explore overlay sharp too.
        'raster-resampling': 'nearest',
      },
    },
    before
  );

  // --- Hillshade: Terrarium DEM ---
  map.addSource('dem', {
    type: 'raster-dem',
    tiles: [TERRARIUM_TILES],
    encoding: 'terrarium',
    tileSize: 256,
    maxzoom: 15,
    attribution: 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
  });
  map.addLayer(
    { id: 'hillshade', type: 'hillshade', source: 'dem', paint: { 'hillshade-exaggeration': 0.45 } },
    before
  );

  // --- Contours: maplibre-contour, labeled in feet, zoom-adaptive ---
  const dem = ensureContourDem();
  map.addSource('contours', {
    type: 'vector',
    tiles: [
      dem.contourProtocolUrl({
        multiplier: M_TO_FT, // meters -> feet
        overzoom: 1,
        thresholds: {
          10: [200, 1000],
          12: [100, 500],
          13: [80, 400],
          14: [40, 200],
        },
        elevationKey: 'ele',
        levelKey: 'level',
        contourLayer: 'contours',
      }),
    ],
    maxzoom: 15,
  });
  map.addLayer(
    {
      id: 'contour-lines',
      type: 'line',
      source: 'contours',
      'source-layer': 'contours',
      paint: {
        'line-color': 'rgba(94, 63, 38, 0.85)',
        // Major (index) lines thicker than minor. coalesce guards null level
        // on edge features.
        'line-width': ['match', ['coalesce', ['get', 'level'], 0], 1, 1.4, 0.6],
      },
    },
    before
  );
  // --- Slope angle + aspect: live custom protocols, inserted below contours ---
  registerTerrainProtocols();
  map.addSource('slope', {
    type: 'raster',
    tiles: [`${SLOPE_PROTOCOL}://{z}/{x}/{y}`],
    tileSize: 256,
    maxzoom: 14,
    attribution: 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
  });
  map.addLayer(
    { id: 'slope', type: 'raster', source: 'slope', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } },
    'contour-lines'
  );
  map.addSource('aspect', {
    type: 'raster',
    tiles: [`${ASPECT_PROTOCOL}://{z}/{x}/{y}`],
    tileSize: 256,
    maxzoom: 14,
    attribution: 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
  });
  map.addLayer(
    { id: 'aspect', type: 'raster', source: 'aspect', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } },
    'contour-lines'
  );

  map.addLayer(
    {
      id: 'contour-labels',
      type: 'symbol',
      source: 'contours',
      'source-layer': 'contours',
      filter: ['==', ['coalesce', ['get', 'level'], 0], 1], // label index lines only
      layout: {
        'symbol-placement': 'line',
        'text-font': ['Noto Sans Regular'], // must match a fontstack the basemap ships
        'text-size': 11,
        'text-field': [
          'concat',
          ['number-format', ['coalesce', ['get', 'ele'], 0], { 'max-fraction-digits': 0 }],
          "'",
        ],
      },
      paint: {
        'text-color': 'rgba(80, 50, 25, 1)',
        'text-halo-color': 'rgba(255, 255, 255, 0.85)',
        'text-halo-width': 1.5,
      },
    },
    before
  );

  return [
    { id: 'satellite', label: 'Satellite imagery', layerIds: ['satellite'], visible: false },
    { id: 'hillshade', label: 'Hillshade', layerIds: ['hillshade'], visible: true },
    { id: 'contours', label: 'Contours', layerIds: ['contour-lines', 'contour-labels'], visible: true },
    { id: 'slope', label: 'Slope angle', layerIds: ['slope'], visible: false, exclusiveGroup: 'overlay' },
    { id: 'aspect', label: 'Aspect', layerIds: ['aspect'], visible: false, exclusiveGroup: 'overlay' },
    { id: 'groundcover', label: 'Ground cover', layerIds: ['groundcover'], visible: false, exclusiveGroup: 'overlay' },
    // Basemap feature toggles (default on) — just flip visibility on style layers.
    { id: 'bm-water', label: 'Water', layerIds: basemap.water, visible: true, section: 'Basemap' },
    { id: 'bm-roads', label: 'Roads', layerIds: basemap.roads, visible: true, section: 'Basemap' },
    { id: 'bm-buildings', label: 'Buildings', layerIds: basemap.buildings, visible: true, section: 'Basemap' },
    { id: 'bm-labels', label: 'Labels', layerIds: basemap.labels, visible: true, section: 'Basemap' },
  ];
}
