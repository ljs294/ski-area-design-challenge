import maplibregl from 'maplibre-gl';
import mlcontour from 'maplibre-contour';
import type { TerrainRecord } from '../types';
import { registerWorldcoverProtocol, WORLDCOVER_PROTOCOL } from './worldcoverProtocol';
import { registerTerrainProtocols, SLOPE_PROTOCOL, ASPECT_PROTOCOL } from './terrainProtocols';
import {
  localTileBounds,
  registerResortProtocols,
  resortDemBounds,
  resortProtocolUrl,
  RESORT_ASPECT_PROTOCOL,
  RESORT_COVER_PROTOCOL,
  RESORT_DEM_PROTOCOL,
  RESORT_SLOPE_PROTOCOL,
} from './resortProtocols';
import { MASTER_PLAN_LAYER_IDS } from './masterPlanStyle';
import { unitToLngLat } from '../geo';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { addCoverLayers, COVER_LAYER_IDS, COVER_SOURCE } from './coverVectorize';
import { localContextGeoJSON } from './localContextGeoJSON';
import { localContourGeoJSON } from './localContours';
import { EMPTY_CONTOURS, GRADED_CONTOUR_SOURCE } from './terrainGradeMap';
import { waterLinePixelWidth } from './waterLineStyle';
import type { SnowGrid } from '../types/snow';
import { registerSnowProtocol, setSnowRenderQuality, snowProtocolUrl } from './snowProtocol';
import type { SnowDisplayMode } from './snowStyle';
import { renderProfileFor, type RenderQuality } from './renderProfile';
export { localContourGeoJSON } from './localContours';
export { localContextGeoJSON } from './localContextGeoJSON';

const TERRARIUM_TILES = 'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

export interface LayerToggle {
  id: string;
  label: string;
  layerIds: string[];
  visible: boolean;
  exclusiveGroup?: string;
  section?: 'Imagery' | 'Master plan' | 'Analysis' | 'Structures';
}

export type OverlayId = 'slope' | 'aspect' | 'groundcover' | 'snow';

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

function remoteContourTiles(imperial: boolean): string[] {
  const dem = contourDemFor(TERRARIUM_TILES);
  return [dem.contourProtocolUrl({
    multiplier: imperial ? 3.28084 : 1,
    overzoom: 1,
    thresholds: imperial
      ? { 10: [200, 1000], 12: [100, 500], 13: [40, 200], 15: [20, 100] }
      : { 10: [50, 250], 12: [25, 100], 13: [10, 50], 15: [5, 25] },
    elevationKey: 'ele', levelKey: 'level', contourLayer: 'contours',
  })];
}

/** Update contour units without rebuilding the entire MapLibre style. */
export function setContourUnits(
  map: maplibregl.Map | null,
  terrain: TerrainRecord | null,
  units: 'imperial' | 'metric',
): void {
  if (!map) return;
  const imperial = units === 'imperial';
  const source = map.getSource('contours') as
    | maplibregl.GeoJSONSource
    | { setTiles?: (tiles: string[]) => void }
    | undefined;
  if (terrain?.coverGrid && terrain.bounds && source && 'setData' in source) {
    source.setData(localContourGeoJSON(terrain, imperial));
  } else if (source && 'setTiles' in source) {
    source.setTiles?.(remoteContourTiles(imperial));
  }
  if (map.getLayer('contour-labels')) {
    map.setLayoutProperty('contour-labels', 'text-field', [
      'concat',
      ['number-format', ['coalesce', ['get', 'ele'], 0], { 'max-fraction-digits': 0 }],
      imperial ? "'" : ' m',
    ]);
  }
}

export function applyAnalysisRenderProfile(
  map: maplibregl.Map,
  quality: RenderQuality,
  isOverhead: boolean,
  requested: { hillshade?: boolean; contours?: boolean } = {},
): void {
  const profile = renderProfileFor(quality);
  if (map.getLayer('hillshade')) {
    const allowed = profile.hillshade === 'full' ||
      (profile.hillshade === 'overhead' && isOverhead);
    map.setLayoutProperty(
      'hillshade',
      'visibility',
      allowed && requested.hillshade !== false ? 'visible' : 'none',
    );
  }
  if (map.getLayer('contour-labels')) {
    map.setLayoutProperty(
      'contour-labels',
      'visibility',
      profile.contourLabels && requested.contours !== false ? 'visible' : 'none',
    );
  }
  if (map.getLayer('contour-lines')) {
    map.setFilter('contour-lines', profile.contourLabels
      ? null
      : ['==', ['coalesce', ['get', 'level'], 0], 1] as maplibregl.FilterSpecification);
  }
}

const ANALYSIS_PRESENTATION_LAYERS = [
  'satellite', 'hillshade', ...COVER_LAYER_IDS, 'groundcover',
  'cover-boundary-halo', 'cover-boundaries',
  'local-water-fill', 'local-water-selected', 'local-water-lines',
  'local-water-line-selected', 'local-water-line-hit', 'local-water-labels',
  'local-building-fill',
  'contour-lines', 'graded-contour-lines', 'contour-labels',
  'slope', 'aspect', 'snow',
] as const;

const ANALYSIS_PRESENTATION_SOURCES = [
  'satellite', 'dem', COVER_SOURCE, 'worldcover', 'cover-boundaries',
  'local-context', 'contours', GRADED_CONTOUR_SOURCE, 'slope', 'aspect', 'snow',
] as const;

/** Remove only the analysis family's presentation before a live profile rebuild. */
export function removeAnalysisLayers(map: maplibregl.Map): void {
  for (const id of ANALYSIS_PRESENTATION_LAYERS) {
    if (map.getLayer(id)) map.removeLayer(id);
  }
  for (const id of ANALYSIS_PRESENTATION_SOURCES) {
    if (map.getSource(id)) map.removeSource(id);
  }
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

export function setLocalContextData(map: maplibregl.Map, record: TerrainRecord,
  lakeNameOverrides: Record<string, string> = {},
  streamWidthOverrides: Record<string, number> = {}): void {
  (map.getSource('local-context') as maplibregl.GeoJSONSource | undefined)
    ?.setData(localContextGeoJSON(record, lakeNameOverrides, streamWidthOverrides));
}

export function setSelectedLake(map: maplibregl.Map | null, lakeId: string | null): void {
  if (!map?.getLayer('local-water-selected')) return;
  map.setFilter('local-water-selected', [
    'all', ['==', ['get', 'kind'], 'water'], ['==', ['get', 'id'], lakeId ?? ''],
  ]);
}

export function setSelectedStream(map: maplibregl.Map | null, streamId: string | null): void {
  if (!map?.getLayer('local-water-line-selected')) return;
  map.setFilter('local-water-line-selected', [
    'all', ['==', ['get', 'kind'], 'water-line'], ['==', ['get', 'id'], streamId ?? ''],
  ]);
}

export function setupAnalysisLayers(
  map: maplibregl.Map,
  terrain?: TerrainRecord | null,
  units: 'imperial' | 'metric' = 'imperial',
  coverDisplay?: CoverDisplayGeoJSON | null,
  localImageryUrl?: string | null,
  lakeNameOverrides: Record<string, string> = {},
  streamWidthOverrides: Record<string, number> = {},
  snow?: SnowGrid | null,
  snowMode: SnowDisplayMode = 'depth',
  quality: RenderQuality = 'standard',
): LayerToggle[] {
  setSnowRenderQuality(quality);
  const profile = renderProfileFor(quality);
  const local = terrain?.coverGrid && terrain.bounds ? terrain : null;
  const activeCoverDisplay = profile.coverMode === 'vector' ? coverDisplay : null;
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
  // The aerial is a base underlay, not an overlay: anchor it beneath every
  // resort layer so the translucent cover, hillshade, and contours all read on
  // top of the photo — matching the picker, where masterPlanStyle places the
  // aerial at the bottom of the stack. Skip the satellite layer itself: in game
  // we remove and re-add it, so using it as its own anchor would leave the
  // re-added layer with a missing anchor (MapLibre then drops it silently).
  const bottomAnchor =
    styleLayers.find((l) => l.type !== 'background' && l.id !== satelliteLayer)?.id ?? before;
  // The downloaded NAIP aerial is the master-plan base in game: show it whenever
  // a package actually carries imagery, so the translucent cover reads over the
  // photo (Stevens Pass Fig 4-2). Packages without imagery fall back to the paper
  // background — fully offline, no network aerial. The picker keeps the aerial on.
  const satelliteVisible = local ? !!(localImageryUrl && local.localImageryMetadata) : true;

  if (local && localImageryUrl && local.localImageryMetadata) {
    if (map.getLayer(satelliteLayer)) map.removeLayer(satelliteLayer);
    if (map.getSource('satellite')) map.removeSource('satellite');
    const b = local.localImageryMetadata.bounds;
    map.addSource('satellite', {
      type: 'image', url: localImageryUrl,
      coordinates: [[b.west, b.north], [b.east, b.north], [b.east, b.south], [b.west, b.south]],
    });
    map.addLayer({ id: satelliteLayer, type: 'raster', source: 'satellite', layout: { visibility: satelliteVisible ? 'visible' : 'none' }, paint: { 'raster-opacity': 0.6, 'raster-saturation': -0.35, 'raster-contrast': -0.06 } }, bottomAnchor);
  } else if (!local && !map.getSource('satellite')) {
    // Picker only: live aerial while choosing a site. In game we never add a
    // network aerial — a package without local imagery falls back to the paper
    // background (fully offline, nothing streams or drapes over the mesh).
    map.addSource('satellite', { type: 'raster', tiles: ['https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}'], tileSize: 256, maxzoom: 19, attribution: 'Imagery © Esri, Maxar, Earthstar Geographics' });
  }
  // Only touch the aerial layer when its source actually exists — in the offline
  // game-without-imagery case there is no satellite source, so the placeholder
  // layer stays hidden and no layer references a missing source.
  if (map.getSource('satellite')) {
    if (!map.getLayer(satelliteLayer)) {
      map.addLayer({ id: satelliteLayer, type: 'raster', source: 'satellite', layout: { visibility: satelliteVisible ? 'visible' : 'none' }, paint: { 'raster-opacity': 0.6, 'raster-saturation': -0.35, 'raster-contrast': -0.08 } }, bottomAnchor);
    } else {
      map.setLayoutProperty(satelliteLayer, 'visibility', satelliteVisible ? 'visible' : 'none');
    }
  }

  let demUrl = TERRARIUM_TILES;
  // Core bounds clamp the play-box-only layers (cover, slope, aspect, contours).
  let bounds: [number, number, number, number] | undefined;
  // The DEM/hillshade span the wider perimeter ring so neighbouring relief
  // renders (shaded) out to the 3 km edge, while the analysis layers above stay
  // clamped to the property. Falls back to core bounds for ring-less packages.
  let demBounds: [number, number, number, number] | undefined;
  let coverVisible = false;
  let coverLabel = 'Ground cover preview';
  const waterWidth = waterLinePixelWidth(local?.bounds
    ? (local.bounds.north + local.bounds.south) / 2
    : 0);
  if (local) {
    registerResortProtocols();
    bounds = localTileBounds(local);
    demBounds = resortDemBounds(local);
    demUrl = resortProtocolUrl(RESORT_DEM_PROTOCOL, local);
    if (!activeCoverDisplay) map.addSource('worldcover', { type: 'raster', tiles: [`${RESORT_COVER_PROTOCOL}://${encodeURIComponent(local.key)}/{z}/{x}/{y}`], tileSize: 256, maxzoom: profile.terrainMaxZoom, bounds, attribution: 'ESA WorldCover 2021 · 10 m © ESA / Copernicus' });
    map.addSource('local-context', { type: 'geojson', data: localContextGeoJSON(local, lakeNameOverrides, streamWidthOverrides), attribution: 'Local OSM context © OpenStreetMap contributors' });
    coverVisible = true;
    coverLabel = local.coverGrid?.source === 'usgs-four-class-v1' ? 'Detailed terrain cover (local)' : 'ESA WorldCover 2021 · 10 m (local)';
  } else {
    registerWorldcoverProtocol();
    map.addSource('worldcover', { type: 'raster', tiles: [`${WORLDCOVER_PROTOCOL}://{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, attribution: '© ESA WorldCover project / Copernicus' });
  }

  // Hillshade lands below cover; later additions before the same anchor draw above it.
  if (profile.hillshade !== 'none') map.addSource('dem', { type: 'raster-dem', tiles: [demUrl], encoding: 'terrarium', tileSize: 256, maxzoom: profile.terrainMaxZoom, ...(demBounds ? { bounds: demBounds } : {}), attribution: local ? 'Local resort elevation package' : 'Terrain: Terrarium tiles, Mapzen/AWS Open Data' });
  // Over the aerial base the photo already carries relief, so ease the hillshade
  // to a subtle deepening and mute the highlights that would otherwise bleach
  // sunlit slopes. On the paper fallback keep the stronger, brighter relief.
  if (profile.hillshade !== 'none') map.addLayer({
    id: 'hillshade', type: 'hillshade', source: 'dem',
    layout: { visibility: profile.hillshade === 'overhead' && map.getPitch() > 0.5 ? 'none' : 'visible' },
    paint: {
      'hillshade-method': 'multidirectional',
      'hillshade-illumination-direction': [315, 45, 225],
      'hillshade-illumination-altitude': [42, 28, 18],
      'hillshade-exaggeration': satelliteVisible ? 0.25 : 0.42,
      'hillshade-shadow-color': ['#34403f', '#48504b', '#514b46'],
      'hillshade-highlight-color': satelliteVisible
        ? ['#e8e2d4', '#e2e6de', '#e0dbd0']
        : ['#f7f3e8', '#eef2eb', '#ede8df'],
      'hillshade-accent-color': '#4b514c',
    },
  } as maplibregl.HillshadeLayerSpecification, coverAnchor);
  if (local && activeCoverDisplay) addCoverLayers(map, activeCoverDisplay, coverVisible, 'hillshade', satelliteVisible);
  else map.addLayer({ id: 'groundcover', type: 'raster', source: 'worldcover', layout: { visibility: coverVisible ? 'visible' : 'none' }, paint: { 'raster-opacity': local ? 0.78 : 0.9, 'raster-resampling': 'nearest' } }, coverAnchor);
  if (local && !activeCoverDisplay) {
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
      id: 'local-building-fill', type: 'fill-extrusion', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'building'],
      minzoom: 13,
      paint: {
        'fill-extrusion-color': [
          'interpolate', ['linear'], ['get', 'heightM'],
          3, '#b8aca0', 12, '#a39488', 40, '#81756c',
        ],
        'fill-extrusion-height': ['get', 'heightM'],
        'fill-extrusion-base': ['get', 'minHeightM'],
        'fill-extrusion-opacity': 0.88,
        'fill-extrusion-vertical-gradient': true,
      },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-fill', type: 'fill', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'water'],
      paint: { 'fill-color': '#6ca3be', 'fill-opacity': 0.72, 'fill-outline-color': '#397f9f' },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-selected', type: 'line', source: 'local-context',
      filter: ['all', ['==', ['get', 'kind'], 'water'], ['==', ['get', 'id'], '']],
      paint: {
        'line-color': '#f6fbff',
        'line-width': ['interpolate', ['linear'], ['zoom'], 10, 2, 16, 5],
        'line-opacity': 0.95,
      },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-lines', type: 'line', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'water-line'],
      paint: {
        'line-color': '#397f9f',
        'line-width': waterWidth,
        'line-opacity': 0.9,
      },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-line-selected', type: 'line', source: 'local-context',
      filter: ['all', ['==', ['get', 'kind'], 'water-line'], ['==', ['get', 'id'], '']],
      paint: {
        'line-color': '#f6fbff',
        'line-width': 3,
        'line-gap-width': waterWidth,
        'line-opacity': 0.95,
      },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-line-hit', type: 'line', source: 'local-context',
      filter: ['==', ['get', 'kind'], 'water-line'],
      paint: { 'line-color': '#000000', 'line-width': 14, 'line-opacity': 0.01 },
    }, contourAnchor);
    map.addLayer({
      id: 'local-water-labels', type: 'symbol', source: 'local-context',
      filter: ['all', ['==', ['get', 'kind'], 'water'], ['!=', ['get', 'customName'], '']],
      layout: {
        'text-field': ['get', 'customName'],
        'text-font': ['Noto Sans Regular'],
        'text-size': ['interpolate', ['linear'], ['zoom'], 10, 10, 16, 13],
        'text-letter-spacing': 0.04,
        'text-max-width': 10,
      },
      paint: {
        'text-color': '#315f73',
        'text-opacity': 0.58,
        'text-halo-color': 'rgba(240,245,241,0.72)',
        'text-halo-width': 1.2,
      },
    }, contourAnchor);
  }

  const imperial = units === 'imperial';
  if (local) {
    map.addSource('contours', { type: 'geojson', data: localContourGeoJSON(local, imperial) });
  } else {
    map.addSource('contours', {
      type: 'vector',
      tiles: remoteContourTiles(imperial),
      maxzoom: 15,
    });
  }
  map.addLayer({
    id: 'contour-lines', type: 'line', source: 'contours', ...(local ? {} : { 'source-layer': 'contours' }),
    ...(profile.contourLabels ? {} : { filter: ['==', ['coalesce', ['get', 'level'], 0], 1] }),
    paint: {
      'line-color': ['match', ['coalesce', ['get', 'level'], 0], 1, 'rgba(248,246,237,0.84)', 'rgba(244,242,232,0.48)'],
      'line-width': ['match', ['coalesce', ['get', 'level'], 0], 1, 1.25, 0.55],
    },
  }, contourAnchor);
  // Contours over ground a pending terrain edit would change, drawn in yellow on
  // top of the normal set. Empty except while a grading preview is up.
  map.addSource(GRADED_CONTOUR_SOURCE, { type: 'geojson', data: EMPTY_CONTOURS });
  map.addLayer({
    id: 'graded-contour-lines', type: 'line', source: GRADED_CONTOUR_SOURCE,
    paint: {
      'line-color': '#facc15',
      'line-width': ['match', ['coalesce', ['get', 'level'], 0], 1, 2.6, 1.6],
      'line-opacity': 0.95,
    },
  }, contourAnchor);
  const slopeProtocol = local ? RESORT_SLOPE_PROTOCOL : SLOPE_PROTOCOL;
  const aspectProtocol = local ? RESORT_ASPECT_PROTOCOL : ASPECT_PROTOCOL;
  if (!local) registerTerrainProtocols();
  map.addSource('slope', { type: 'raster', tiles: [local ? resortProtocolUrl(slopeProtocol, local) : `${slopeProtocol}://{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, ...(bounds ? { bounds } : {}) });
  map.addLayer({ id: 'slope', type: 'raster', source: 'slope', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } }, analysisAnchor);
  map.addSource('aspect', { type: 'raster', tiles: [local ? resortProtocolUrl(aspectProtocol, local) : `${aspectProtocol}://{z}/{x}/{y}`], tileSize: 256, maxzoom: 14, ...(bounds ? { bounds } : {}) });
  map.addLayer({ id: 'aspect', type: 'raster', source: 'aspect', layout: { visibility: 'none' }, paint: { 'raster-opacity': 1 } }, analysisAnchor);
  if (local && snow) {
    registerSnowProtocol();
    map.addSource('snow', { type: 'raster', tiles: [snowProtocolUrl(snowMode)], tileSize: 256,
      maxzoom: 18, ...(bounds ? { bounds } : {}) });
    map.addLayer({ id: 'snow', type: 'raster', source: 'snow', layout: { visibility: 'none' },
      paint: { 'raster-opacity': 1,
        'raster-resampling': snowMode === 'conditions' ? 'nearest' : 'linear' } }, analysisAnchor);
  }
  map.addLayer({
    id: 'contour-labels', type: 'symbol', source: 'contours', ...(local ? {} : { 'source-layer': 'contours' }),
    filter: ['==', ['coalesce', ['get', 'level'], 0], 1],
    layout: { visibility: profile.contourLabels ? 'visible' : 'none', 'symbol-placement': 'line', 'text-font': ['Noto Sans Regular'], 'text-size': 10, 'text-field': ['concat', ['number-format', ['coalesce', ['get', 'ele'], 0], { 'max-fraction-digits': 0 }], imperial ? "'" : ' m'] },
    paint: { 'text-color': '#3d4542', 'text-halo-color': 'rgba(248,246,237,0.9)', 'text-halo-width': 1.4 },
  }, contourAnchor);

  return [
    { id: 'satellite', label: 'Satellite imagery', layerIds: [satelliteLayer], visible: satelliteVisible, section: 'Imagery' },
    { id: 'groundcover', label: coverLabel, layerIds: local && activeCoverDisplay ? COVER_LAYER_IDS : local ? ['groundcover', 'cover-boundary-halo', 'cover-boundaries'] : ['groundcover'], visible: coverVisible, section: 'Master plan' },
    { id: 'hillshade', label: 'Terrain relief', layerIds: profile.hillshade === 'none' ? [] : ['hillshade'], visible: profile.hillshade !== 'none', section: 'Master plan' },
    { id: 'contours', label: 'Contours', layerIds: ['contour-lines', 'graded-contour-lines', 'contour-labels'], visible: true, section: 'Master plan' },
    { id: 'bm-water', label: 'Water', layerIds: local ? [...basemap.water, 'local-water-fill', 'local-water-selected', 'local-water-lines', 'local-water-line-selected', 'local-water-line-hit', 'local-water-labels'] : basemap.water, visible: true, section: 'Master plan' },
    { id: 'bm-roads', label: 'Roads', layerIds: basemap.roads, visible: true, section: 'Master plan' },
    { id: 'bm-buildings', label: 'Buildings', layerIds: local ? [...basemap.buildings, 'local-building-fill'] : basemap.buildings, visible: true, section: 'Master plan' },
    { id: 'bm-labels', label: 'Labels', layerIds: basemap.labels, visible: true, section: 'Master plan' },
    { id: 'slope', label: 'Slope angle', layerIds: ['slope'], visible: false, exclusiveGroup: 'analysis', section: 'Analysis' },
    { id: 'aspect', label: 'Aspect', layerIds: ['aspect'], visible: false, exclusiveGroup: 'analysis', section: 'Analysis' },
    ...(local && snow ? [{ id: 'snow', label: 'Snow', layerIds: ['snow'], visible: false,
      exclusiveGroup: 'analysis', section: 'Analysis' as const }] : []),
  ];
}
