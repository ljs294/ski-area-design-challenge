import maplibregl from 'maplibre-gl';
import mlcontour from 'maplibre-contour';
import type { TerrainRecord } from '../types';
import { registerWorldcoverProtocol, WORLDCOVER_PROTOCOL } from './worldcoverProtocol';
import { registerTerrainProtocols, SLOPE_PROTOCOL, ASPECT_PROTOCOL } from './terrainProtocols';
import {
  localTileBounds,
  registerResortProtocols,
  RESORT_ASPECT_PROTOCOL,
  RESORT_COVER_PROTOCOL,
  RESORT_DEM_PROTOCOL,
  RESORT_SLOPE_PROTOCOL,
} from './resortProtocols';
import { MASTER_PLAN_LAYER_IDS } from './masterPlanStyle';
import { unitToLngLat } from '../geo';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { addCoverLayers, COVER_LAYER_IDS } from './coverVectorize';

const TERRARIUM_TILES = 'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

export interface LayerToggle {
  id: string;
  label: string;
  layerIds: string[];
  visible: boolean;
  exclusiveGroup?: string;
  section?: 'Imagery' | 'Master plan' | 'Analysis';
}

function basemapCategories(layers: maplibregl.LayerSpecification[]) {
  const water: string[] = [], roads: string[] = [], buildings: string[] = [], labels: string[] = [];
  for (const layer of layers) {
    const sl = (layer as { 'source-layer'?: string })['source-layer'];
    if (sl === 'water' || sl === 'waterway') water.push(layer.id);
    if (sl === 'transportation') roads.push(layer.id);
    if (sl === 'building') buildings.push(layer.id);
    if (layer.type === 'symbol') labels.push(layer.id);
  }
  return { water, roads, buildings, labels };
}

function contourDemFor(url: string): InstanceType<typeof mlcontour.DemSource> {
  // Each map/style gets its own protocol instance. Sharing one DemSource across
  // the two Graphics Lab maps reuses transferred ArrayBuffers after detachment.
  const dem = new mlcontour.DemSource({ url, encoding: 'terrarium', maxzoom: 15, worker: false });
  dem.setupMaplibre(maplibregl);
  return dem;
}

function localContourGeoJSON(record: TerrainRecord, imperial: boolean): GeoJSON.FeatureCollection {
  const b = record.bounds!;
  const byLevel = new Map<number, GeoJSON.Position[][]>();
  const data = record.contourSegments ?? [];
  for (let i = 0; i + 4 < data.length; i += 5) {
    const levelM = data[i + 4];
    const level = imperial ? levelM * 3.28084 : levelM;
    const lines = byLevel.get(level) ?? [];
    lines.push([
      unitToLngLat(data[i], data[i + 1], b),
      unitToLngLat(data[i + 2], data[i + 3], b),
    ]);
    byLevel.set(level, lines);
  }
  return {
    type: 'FeatureCollection',
    features: [...byLevel.entries()].map(([ele, coordinates]) => ({
      type: 'Feature',
      properties: { ele, level: Math.round(ele / (imperial ? 20 : 6.096)) % 5 === 0 ? 1 : 0 },
      geometry: { type: 'MultiLineString', coordinates },
    })),
  };
}

function localCoverBoundaryGeoJSON(record: TerrainRecord): GeoJSON.FeatureCollection {
  const b = record.bounds!;
  const byClass = new Map<number, GeoJSON.Position[][]>();
  const data = record.coverBoundarySegments ?? [];
  for (let i = 0; i + 4 < data.length; i += 5) {
    const code = data[i + 4];
    const lines = byClass.get(code) ?? [];
    lines.push([
      unitToLngLat(data[i], data[i + 1], b),
      unitToLngLat(data[i + 2], data[i + 3], b),
    ]);
    byClass.set(code, lines);
  }
  return {
    type: 'FeatureCollection',
    features: [...byClass.entries()].map(([code, coordinates]) => ({
      type: 'Feature',
      properties: { code },
      geometry: { type: 'MultiLineString', coordinates },
    })),
  };
}

function localContextGeoJSON(record: TerrainRecord): GeoJSON.FeatureCollection {
  const vectors = record.vectorFeatures;
  if (!vectors) return { type: 'FeatureCollection', features: [] };
  const features: GeoJSON.Feature[] = [];
  for (const water of vectors.waterPolygons) {
    features.push({ type: 'Feature', properties: { kind: 'water' }, geometry: { type: 'Polygon', coordinates: water.rings } });
  }
  for (const water of vectors.waterLines) {
    features.push({ type: 'Feature', properties: { kind: 'water-line', class: water.waterClass }, geometry: { type: 'LineString', coordinates: water.points } });
  }
  for (const road of vectors.roads) {
    features.push({ type: 'Feature', properties: { kind: 'road', class: road.roadClass }, geometry: { type: 'LineString', coordinates: road.points } });
  }
  return { type: 'FeatureCollection', features };
}

export function setupAnalysisLayers(
  map: maplibregl.Map,
  terrain?: TerrainRecord | null,
  units: 'imperial' | 'metric' = 'imperial',
  coverDisplay?: CoverDisplayGeoJSON | null
): LayerToggle[] {
  const local = terrain?.coverGrid && terrain.bounds ? terrain : null;
  const styleLayers = map.getStyle().layers ?? [];
  const roadAnchor = styleLayers.find((l) => (l as { 'source-layer'?: string })['source-layer'] === 'transportation')?.id;
  const before = roadAnchor ?? styleLayers.find((l) => l.type === 'symbol')?.id;
  const coverAnchor = map.getLayer(MASTER_PLAN_LAYER_IDS.water) ? MASTER_PLAN_LAYER_IDS.water : before;
  const contourAnchor = map.getLayer(MASTER_PLAN_LAYER_IDS.buildings) ? MASTER_PLAN_LAYER_IDS.buildings : before;
  const analysisAnchor = map.getLayer(MASTER_PLAN_LAYER_IDS.labels) ? MASTER_PLAN_LAYER_IDS.labels : before;
  const basemap = basemapCategories(styleLayers);
  const satelliteLayer = map.getLayer(MASTER_PLAN_LAYER_IDS.satellite)
    ? MASTER_PLAN_LAYER_IDS.satellite
    : 'satellite';

  if (!map.getSource('satellite')) {
    map.addSource('satellite', { type: 'raster', tiles: ['https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}'], tileSize: 256, maxzoom: 19, attribution: 'Imagery © Esri, Maxar, Earthstar Geographics' });
  }
  if (!map.getLayer(satelliteLayer)) {
    map.addLayer({ id: satelliteLayer, type: 'raster', source: 'satellite', paint: { 'raster-opacity': 0.7, 'raster-saturation': -0.35, 'raster-contrast': -0.08 } }, before);
  }

  let demUrl = TERRARIUM_TILES;
  let bounds: [number, number, number, number] | undefined;
  let coverVisible = false;
  let coverLabel = 'Ground cover preview';
  if (local) {
    registerResortProtocols();
    const key = encodeURIComponent(local.key);
    bounds = localTileBounds(local);
    demUrl = `${RESORT_DEM_PROTOCOL}://${key}/{z}/{x}/{y}`;
    if (!coverDisplay) map.addSource('worldcover', { type: 'raster', tiles: [`${RESORT_COVER_PROTOCOL}://${key}/{z}/{x}/{y}`], tileSize: 256, maxzoom: 18, bounds, attribution: 'ESA WorldCover 2021 · 10 m © ESA / Copernicus' });
    map.addSource('local-context', { type: 'geojson', data: localContextGeoJSON(local), attribution: 'Local OSM context © OpenStreetMap contributors' });
    coverVisible = true;
    coverLabel = 'ESA WorldCover 2021 · 10 m (local)';
  } else {
    registerWorldcoverProtocol();
    map.addSource('worldcover', { type: 'raster', tiles: [`${WORLDCOVER_PROTOCOL}://{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, attribution: '© ESA WorldCover project / Copernicus' });
  }

  // Hillshade lands below cover; later additions before the same anchor draw above it.
  map.addSource('dem', { type: 'raster-dem', tiles: [demUrl], encoding: 'terrarium', tileSize: 256, maxzoom: 15, ...(bounds ? { bounds } : {}), attribution: local ? 'Local resort elevation package' : 'Terrain: Terrarium tiles, Mapzen/AWS Open Data' });
  map.addLayer({
    id: 'hillshade', type: 'hillshade', source: 'dem',
    paint: {
      'hillshade-method': 'multidirectional',
      'hillshade-illumination-direction': [315, 45, 225],
      'hillshade-illumination-altitude': [42, 28, 18],
      'hillshade-exaggeration': 0.42,
      'hillshade-shadow-color': ['#34403f', '#48504b', '#514b46'],
      'hillshade-highlight-color': ['#f7f3e8', '#eef2eb', '#ede8df'],
      'hillshade-accent-color': '#4b514c',
    },
  } as maplibregl.HillshadeLayerSpecification, coverAnchor);
  if (local && coverDisplay) addCoverLayers(map, coverDisplay, coverVisible, 'hillshade');
  else map.addLayer({ id: 'groundcover', type: 'raster', source: 'worldcover', layout: { visibility: coverVisible ? 'visible' : 'none' }, paint: { 'raster-opacity': local ? 0.78 : 0.9, 'raster-resampling': 'nearest' } }, coverAnchor);
  if (local && !coverDisplay) {
    map.addSource('cover-boundaries', { type: 'geojson', data: localCoverBoundaryGeoJSON(local) });
    map.addLayer({
      id: 'cover-boundary-halo', type: 'line', source: 'cover-boundaries',
      paint: { 'line-color': 'rgba(246,244,234,0.62)', 'line-width': ['interpolate', ['linear'], ['zoom'], 11, 0.4, 16, 1.8] },
    }, coverAnchor);
    map.addLayer({
      id: 'cover-boundaries', type: 'line', source: 'cover-boundaries',
      paint: {
        'line-color': ['match', ['get', 'code'], 10, '#274d31', 20, '#66743d', '#4d5c45'],
        'line-width': ['interpolate', ['linear'], ['zoom'], 11, 0.25, 16, 1.05],
      },
    }, coverAnchor);
  }

  if (local) {
    map.addLayer({
      id: 'local-water-fill', type: 'fill', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'water'],
      paint: { 'fill-color': '#6ca3be', 'fill-opacity': 0.72, 'fill-outline-color': '#397f9f' },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-lines', type: 'line', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'water-line'],
      paint: { 'line-color': '#397f9f', 'line-width': ['match', ['get', 'class'], 'river', 1.5, 0.8], 'line-opacity': 0.9 },
    }, contourAnchor);
  }

  const imperial = units === 'imperial';
  if (local) {
    map.addSource('contours', { type: 'geojson', data: localContourGeoJSON(local, imperial) });
  } else {
    const dem = contourDemFor(demUrl);
    map.addSource('contours', {
      type: 'vector',
      tiles: [dem.contourProtocolUrl({
        multiplier: imperial ? 3.28084 : 1,
        overzoom: 1,
        thresholds: imperial ? { 10: [200, 1000], 12: [100, 500], 13: [40, 200], 15: [20, 100] } : { 10: [50, 250], 12: [25, 100], 13: [10, 50], 15: [5, 25] },
        elevationKey: 'ele', levelKey: 'level', contourLayer: 'contours',
      })],
      maxzoom: 15,
    });
  }
  map.addLayer({
    id: 'contour-lines', type: 'line', source: 'contours', ...(local ? {} : { 'source-layer': 'contours' }),
    paint: {
      'line-color': ['match', ['coalesce', ['get', 'level'], 0], 1, 'rgba(248,246,237,0.84)', 'rgba(244,242,232,0.48)'],
      'line-width': ['match', ['coalesce', ['get', 'level'], 0], 1, 1.25, 0.55],
    },
  }, contourAnchor);
  if (local) {
    map.addLayer({
      id: 'local-roads', type: 'line', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'road'],
      paint: {
        'line-color': '#55534e',
        'line-width': ['match', ['get', 'class'], 'major', 1.7, 'minor', 1, 0.55],
        'line-opacity': 0.72,
      },
    }, contourAnchor);
  }

  const slopeProtocol = local ? RESORT_SLOPE_PROTOCOL : SLOPE_PROTOCOL;
  const aspectProtocol = local ? RESORT_ASPECT_PROTOCOL : ASPECT_PROTOCOL;
  if (!local) registerTerrainProtocols();
  map.addSource('slope', { type: 'raster', tiles: [`${slopeProtocol}://${local ? `${encodeURIComponent(local.key)}/` : ''}{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, ...(bounds ? { bounds } : {}) });
  map.addLayer({ id: 'slope', type: 'raster', source: 'slope', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } }, analysisAnchor);
  map.addSource('aspect', { type: 'raster', tiles: [`${aspectProtocol}://${local ? `${encodeURIComponent(local.key)}/` : ''}{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, ...(bounds ? { bounds } : {}) });
  map.addLayer({ id: 'aspect', type: 'raster', source: 'aspect', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } }, analysisAnchor);
  map.addLayer({
    id: 'contour-labels', type: 'symbol', source: 'contours', ...(local ? {} : { 'source-layer': 'contours' }),
    filter: ['==', ['coalesce', ['get', 'level'], 0], 1],
    layout: { 'symbol-placement': 'line', 'text-font': ['Noto Sans Regular'], 'text-size': 10, 'text-field': ['concat', ['number-format', ['coalesce', ['get', 'ele'], 0], { 'max-fraction-digits': 0 }], imperial ? "'" : ' m'] },
    paint: { 'text-color': '#3d4542', 'text-halo-color': 'rgba(248,246,237,0.9)', 'text-halo-width': 1.4 },
  }, contourAnchor);

  return [
    { id: 'satellite', label: 'Satellite imagery', layerIds: [satelliteLayer], visible: true, section: 'Imagery' },
    { id: 'groundcover', label: coverLabel, layerIds: local && coverDisplay ? COVER_LAYER_IDS : local ? ['groundcover', 'cover-boundary-halo', 'cover-boundaries'] : ['groundcover'], visible: coverVisible, section: 'Master plan' },
    { id: 'hillshade', label: 'Terrain relief', layerIds: ['hillshade'], visible: true, section: 'Master plan' },
    { id: 'contours', label: 'Contours', layerIds: ['contour-lines', 'contour-labels'], visible: true, section: 'Master plan' },
    { id: 'bm-water', label: 'Water', layerIds: local ? [...basemap.water, 'local-water-fill', 'local-water-lines'] : basemap.water, visible: true, section: 'Master plan' },
    { id: 'bm-roads', label: 'Roads', layerIds: local ? [...basemap.roads, 'local-roads'] : basemap.roads, visible: true, section: 'Master plan' },
    { id: 'bm-buildings', label: 'Buildings', layerIds: basemap.buildings, visible: true, section: 'Master plan' },
    { id: 'bm-labels', label: 'Labels', layerIds: basemap.labels, visible: true, section: 'Master plan' },
    { id: 'slope', label: 'Slope angle', layerIds: ['slope'], visible: false, exclusiveGroup: 'analysis', section: 'Analysis' },
    { id: 'aspect', label: 'Aspect', layerIds: ['aspect'], visible: false, exclusiveGroup: 'analysis', section: 'Analysis' },
  ];
}
