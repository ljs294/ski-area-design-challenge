import type maplibregl from 'maplibre-gl';
import { orientedRectFootprint } from '../buildingMesh';

/** GeoJSON companion layers for the non-queryable native building renderer. */
export const BUILDING_SOURCE = 'player-buildings';
export const BUILDING_DRAFT_SOURCE = 'building-draft';
export const BUILDING_BUILT_LAYER_IDS = [
  'building-foundation-fill',
  'building-footprint',
  'building-foundation-outline',
  'building-selected-outline',
  'building-hit',
] as const;
export const BUILDING_DRAFT_LAYER_IDS = [
  'building-draft-foundation',
  'building-draft-footprint',
  'building-draft-outline',
  'building-draft-grade',
] as const;
export const BUILDING_LAYER_IDS = BUILDING_BUILT_LAYER_IDS;
export const BUILDING_HIT_LAYERS = ['building-hit'] as const;

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };
const PAD_APRON_M = 1.8288;
const METRES_PER_DEGREE = 111_320;

export interface BuildingRenderRecord {
  readonly id: string;
  readonly name?: string;
  readonly center: readonly [number, number];
  readonly bearingDeg?: number;
  readonly dimensions: {
    readonly lengthM: number;
    readonly widthM: number;
    readonly eaveHeightM?: number;
  };
  readonly foundation?: {
    readonly mode?: string;
    readonly kind?: string;
    readonly finishedFloorElevationM?: number;
    readonly perimeterGroundElevationsM?: readonly number[];
    readonly perimeterElevationsM?: readonly number[];
    readonly groundElevationsM?: readonly number[];
  };
  readonly finishedFloorElevationM?: number;
}

export interface BuildingDraftMapData {
  readonly center: readonly [number, number];
  readonly lengthM: number;
  readonly widthM: number;
  readonly eaveHeightM?: number;
  readonly finishedFloorElevationM?: number;
  readonly perimeterGroundElevationsM?: readonly number[];
  readonly bearingDeg?: number;
  readonly foundationMode?: string;
  readonly gradePolygons?: readonly (readonly (readonly [number, number])[])[];
}

export interface BuildingCaptureState {
  hidden: boolean;
  draft: BuildingDraftMapData | null;
}

const captureStates = new WeakMap<object, BuildingCaptureState>();

function degreesPerMetre(lat: number): { lng: number; lat: number } {
  const cos = Math.max(1e-6, Math.cos((lat * Math.PI) / 180));
  return { lng: 1 / (METRES_PER_DEGREE * cos), lat: 1 / METRES_PER_DEGREE };
}

function polygonFor(
  center: readonly [number, number],
  lengthM: number,
  widthM: number,
  bearingDeg = 0,
): [number, number][] {
  const [lng, lat] = center;
  const scale = degreesPerMetre(lat);
  const points = orientedRectFootprint(lengthM, widthM, bearingDeg);
  const ring = points.map(([east, north]) => [lng + east * scale.lng, lat + north * scale.lat] as [number, number]);
  ring.push(ring[0]);
  return ring;
}

function feature(
  id: string,
  properties: Record<string, unknown>,
  coordinates: [number, number][],
): GeoJSON.Feature<GeoJSON.Polygon> {
  return { type: 'Feature', id, properties, geometry: { type: 'Polygon', coordinates: [coordinates] } };
}

function foundationPolygon(
  building: BuildingRenderRecord,
): [number, number][] {
  return polygonFor(building.center, building.dimensions.lengthM + PAD_APRON_M * 2,
    building.dimensions.widthM + PAD_APRON_M * 2, building.bearingDeg ?? 0);
}

export function buildingGeoJSON(
  buildings: readonly BuildingRenderRecord[],
  selectedId: string | null = null,
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const building of buildings) {
    const properties = {
      id: building.id,
      name: building.name ?? '',
      kind: 'building-footprint',
      selected: building.id === selectedId,
    };
    features.push(feature(`building:${building.id}:footprint`, properties,
      polygonFor(building.center, building.dimensions.lengthM, building.dimensions.widthM,
        building.bearingDeg ?? 0)));
    features.push(feature(`building:${building.id}:foundation`, {
      ...properties, kind: 'building-foundation',
    }, foundationPolygon(building)));
  }
  return { type: 'FeatureCollection', features };
}

export function buildingDraftGeoJSON(
  draft: BuildingDraftMapData | null,
): GeoJSON.FeatureCollection {
  if (!draft) return EMPTY;
  const bearingDeg = draft.bearingDeg ?? 0;
  const footprint = polygonFor(draft.center, draft.lengthM, draft.widthM, bearingDeg);
  const apron = polygonFor(draft.center, draft.lengthM + PAD_APRON_M * 2,
    draft.widthM + PAD_APRON_M * 2, bearingDeg);
  const features: GeoJSON.Feature[] = [
    feature('building:draft:foundation', { kind: 'foundation', foundationMode: draft.foundationMode ?? 'flattened' }, apron),
    feature('building:draft:footprint', { kind: 'footprint' }, footprint),
  ];
  for (const [index, polygon] of (draft.gradePolygons ?? []).entries()) {
    const ring = polygon.map(([lng, lat]) => [lng, lat] as [number, number]);
    if (ring.length < 3) continue;
    if (ring.length && (ring[0][0] !== ring.at(-1)![0] || ring[0][1] !== ring.at(-1)![1])) ring.push(ring[0]);
    features.push(feature(`building:draft:grade:${index}`, { kind: 'grade' }, ring));
  }
  return { type: 'FeatureCollection', features };
}

/** Adds all ordinary GeoJSON layers used around the native custom layer. */
export function addBuildingLayers(map: maplibregl.Map, beforeId?: string): void {
  if (map.getSource(BUILDING_SOURCE)) return;
  map.addSource(BUILDING_SOURCE, { type: 'geojson', data: EMPTY });
  map.addSource(BUILDING_DRAFT_SOURCE, { type: 'geojson', data: EMPTY });

  const anchor = beforeId && map.getLayer(beforeId) ? beforeId : undefined;
  map.addLayer({
    id: 'building-foundation-fill', type: 'fill', source: BUILDING_SOURCE,
    filter: ['==', ['get', 'kind'], 'building-foundation'],
    paint: { 'fill-color': '#9b9d9f', 'fill-opacity': 0.22 },
  }, anchor);
  map.addLayer({
    id: 'building-footprint', type: 'fill', source: BUILDING_SOURCE,
    filter: ['==', ['get', 'kind'], 'building-footprint'],
    paint: { 'fill-color': '#c7cbd0', 'fill-opacity': 0.15 },
  }, anchor);
  map.addLayer({
    id: 'building-foundation-outline', type: 'line', source: BUILDING_SOURCE,
    filter: ['==', ['get', 'kind'], 'building-foundation'],
    paint: { 'line-color': '#696d70', 'line-width': 1.25, 'line-opacity': 0.7 },
  }, anchor);
  map.addLayer({
    id: 'building-selected-outline', type: 'line', source: BUILDING_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'building-footprint'], ['==', ['get', 'selected'], true]],
    paint: { 'line-color': '#38bdf8', 'line-width': 3, 'line-opacity': 0.95 },
  }, anchor);
  // A transparent, broad polygon is what MapLibre hit testing uses. The
  // custom layer itself is deliberately not queryable.
  map.addLayer({
    id: 'building-hit', type: 'fill', source: BUILDING_SOURCE,
    filter: ['==', ['get', 'kind'], 'building-footprint'],
    paint: { 'fill-color': '#000000', 'fill-opacity': 0.001 },
  }, anchor);

  map.addLayer({
    id: 'building-draft-foundation', type: 'fill', source: BUILDING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'foundation'],
    paint: { 'fill-color': '#b2b5b8', 'fill-opacity': 0.19 },
  }, anchor);
  map.addLayer({
    id: 'building-draft-footprint', type: 'fill', source: BUILDING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'footprint'],
    paint: { 'fill-color': '#d0d4d8', 'fill-opacity': 0.25 },
  }, anchor);
  map.addLayer({
    id: 'building-draft-outline', type: 'line', source: BUILDING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'footprint'],
    paint: { 'line-color': '#e8f0f5', 'line-width': 2, 'line-dasharray': [2, 1] },
  }, anchor);
  map.addLayer({
    id: 'building-draft-grade', type: 'fill', source: BUILDING_DRAFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'grade'],
    paint: { 'fill-color': '#b58b59', 'fill-opacity': 0.18 },
  }, anchor);
}

export function setBuildingData(
  map: maplibregl.Map,
  buildings: readonly BuildingRenderRecord[],
  selectedId: string | null = null,
): void {
  (map.getSource(BUILDING_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(buildingGeoJSON(buildings, selectedId));
}

export function setBuildingDraftData(
  map: maplibregl.Map,
  draft: BuildingDraftMapData | null,
): void {
  (map.getSource(BUILDING_DRAFT_SOURCE) as maplibregl.GeoJSONSource | undefined)
    ?.setData(buildingDraftGeoJSON(draft));
}

export function setSelectedBuilding(map: maplibregl.Map, selectedId: string | null): void {
  if (!map.getLayer('building-selected-outline')) return;
  map.setFilter('building-selected-outline', [
    'all', ['==', ['get', 'kind'], 'building-footprint'], ['==', ['get', 'id'], selectedId ?? ''],
  ]);
}

/**
 * Hide only draft/grade presentation during capture. The committed footprint,
 * hit polygon, and custom 3D building remain visible. Repeated calls are
 * idempotent and preserve the exact prior draft for restoration.
 */
export function setBuildingCaptureTransient(
  map: maplibregl.Map,
  hidden: boolean,
  draft: BuildingDraftMapData | null,
): void {
  let state = captureStates.get(map);
  if (!state) {
    state = { hidden: false, draft: null };
    captureStates.set(map, state);
  }
  if (hidden) {
    if (state.hidden) return;
    state.hidden = true;
    state.draft = draft;
    setBuildingDraftData(map, null);
    return;
  }
  if (!state.hidden) return;
  state.hidden = false;
  setBuildingDraftData(map, state.draft);
  state.draft = null;
}

export function clearBuildingLayers(map: maplibregl.Map): void {
  for (const id of [...BUILDING_DRAFT_LAYER_IDS, ...BUILDING_BUILT_LAYER_IDS]) {
    if (map.getLayer(id)) map.removeLayer(id);
  }
  if (map.getSource(BUILDING_DRAFT_SOURCE)) map.removeSource(BUILDING_DRAFT_SOURCE);
  if (map.getSource(BUILDING_SOURCE)) map.removeSource(BUILDING_SOURCE);
  captureStates.delete(map);
}
