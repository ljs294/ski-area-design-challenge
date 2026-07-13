import type maplibregl from 'maplibre-gl';
import type { SavedLift } from '../types';

// Lift rendering: every lift is a red line under a white casing (classic
// ski-map styling) with terminal dots and a name label. Complete lifts render
// solid; planning lifts render dashed. line-dasharray can't be data-driven, so
// complete / planning / draft each get their own line layer over one source.

export const LIFT_SOURCE = 'lifts';

// Classic ski-map lift red (matches the capacity emblems at the base).
const LIFT_RED = '#d42027';

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

/** In-progress line: anchor placed, second point tracking the cursor. */
export interface DraftLine {
  points: [[number, number], [number, number]];
}

export function liftsToGeoJSON(
  lifts: SavedLift[],
  draft: DraftLine | null
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const lift of lifts) {
    features.push({
      type: 'Feature',
      properties: {
        id: lift.id,
        name: lift.name,
        kind: 'line',
        draft: false,
        status: lift.status,
      },
      geometry: { type: 'LineString', coordinates: lift.points },
    });
    const known = lift.endpointElevM[0] != null && lift.endpointElevM[1] != null;
    lift.points.forEach((p, i) => {
      features.push({
        type: 'Feature',
        properties: {
          id: lift.id,
          kind: 'terminal',
          // points are stored bottom-first once elevations are known
          role: known ? (i === 0 ? 'bottom' : 'top') : 'unknown',
          draft: false,
        },
        geometry: { type: 'Point', coordinates: p },
      });
    });
  }
  if (draft) {
    features.push({
      type: 'Feature',
      properties: { kind: 'line', draft: true },
      geometry: { type: 'LineString', coordinates: draft.points },
    });
    for (const p of draft.points) {
      features.push({
        type: 'Feature',
        properties: { kind: 'terminal', role: 'unknown', draft: true },
        geometry: { type: 'Point', coordinates: p },
      });
    }
  }
  return { type: 'FeatureCollection', features };
}

/** Adds the lift source + layers on top of the current style. Idempotent. */
export function addLiftLayers(map: maplibregl.Map): void {
  if (map.getSource(LIFT_SOURCE)) return;

  map.addSource(LIFT_SOURCE, { type: 'geojson', data: EMPTY });
  // White casing under every built line for contrast on any basemap.
  map.addLayer({
    id: 'lift-line-casing',
    type: 'line',
    source: LIFT_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false]],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': '#ffffff', 'line-width': 5 },
  });
  // Complete lifts: solid red.
  map.addLayer({
    id: 'lift-line-complete',
    type: 'line',
    source: LIFT_SOURCE,
    filter: [
      'all',
      ['==', ['get', 'kind'], 'line'],
      ['==', ['get', 'draft'], false],
      ['==', ['get', 'status'], 'complete'],
    ],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': LIFT_RED, 'line-width': 3 },
  });
  // Planning lifts: dashed red.
  map.addLayer({
    id: 'lift-line-planning',
    type: 'line',
    source: LIFT_SOURCE,
    filter: [
      'all',
      ['==', ['get', 'kind'], 'line'],
      ['==', ['get', 'draft'], false],
      ['==', ['get', 'status'], 'planning'],
    ],
    paint: { 'line-color': LIFT_RED, 'line-width': 3, 'line-dasharray': [2, 1.5] },
  });
  // In-progress draft while placing the second terminal.
  map.addLayer({
    id: 'lift-line-draft',
    type: 'line',
    source: LIFT_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], true]],
    paint: { 'line-color': LIFT_RED, 'line-width': 2.5, 'line-dasharray': [2, 1.5] },
  });
  map.addLayer({
    id: 'lift-terminals',
    type: 'circle',
    source: LIFT_SOURCE,
    filter: ['==', ['get', 'kind'], 'terminal'],
    paint: {
      'circle-radius': ['match', ['get', 'role'], 'top', 5, 4],
      'circle-color': [
        'case',
        ['==', ['get', 'draft'], true],
        LIFT_RED,
        ['match', ['get', 'role'], 'top', '#111827', '#374151'],
      ],
      'circle-stroke-color': '#ffffff',
      'circle-stroke-width': 1.5,
    },
  });
  map.addLayer({
    id: 'lift-labels',
    type: 'symbol',
    source: LIFT_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false]],
    layout: {
      'symbol-placement': 'line-center',
      'text-field': ['get', 'name'],
      'text-size': 12,
      'text-font': ['Noto Sans Regular'],
      'text-optional': true,
    },
    paint: {
      'text-color': '#111827',
      'text-halo-color': '#ffffff',
      'text-halo-width': 1.5,
    },
  });
}

/** Replaces the lift source data. No-op before the source exists (pre style.load). */
export function setLiftData(map: maplibregl.Map, fc: GeoJSON.FeatureCollection): void {
  const src = map.getSource(LIFT_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (!src) return;
  src.setData(fc);
}
