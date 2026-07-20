import type maplibregl from 'maplibre-gl';
import type { SavedTrail, TrailDifficulty } from '../types';
import { DIFFICULTY_COLORS, DIFFICULTY_SYMBOL, TRAIL_DIFFICULTIES } from '../trails';

// Ski-run rendering: each run is a difficulty-colored filled polygon (its
// painted footprint) with a graded outline and a name label — the printed
// trail-map look. Complete runs get a solid outline, planning runs a dashed
// one, and the run being reviewed a bright dashed highlight. A separate wide,
// round, translucent line previews the live brush stroke while painting; its
// pixel width is set from meters by MapView (see setTrailPaintWidth).

export const TRAIL_SOURCE = 'trails';

const EMPTY: GeoJSON.FeatureCollection = { type: 'FeatureCollection', features: [] };

/** The run currently in the review panel, rendered as a bright draft. */
export interface TrailReview {
  polygon: [number, number][][];
  spine: [number, number][];
  difficulty: TrailDifficulty;
  name: string;
}

function difficultyMatch(fallback: string): maplibregl.ExpressionSpecification {
  return [
    'match',
    ['get', 'difficulty'],
    ...TRAIL_DIFFICULTIES.flatMap((d) => [d, DIFFICULTY_COLORS[d]]),
    fallback,
  ] as unknown as maplibregl.ExpressionSpecification;
}

export function trailsToGeoJSON(
  trails: SavedTrail[],
  review: TrailReview | null,
  paintPath: [number, number][] | null
): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];

  const pushRun = (
    polygon: [number, number][][],
    spine: [number, number][],
    props: Record<string, unknown>
  ) => {
    if (polygon.length > 0) {
      features.push({
        type: 'Feature',
        properties: { kind: 'trail', ...props },
        geometry: { type: 'Polygon', coordinates: polygon },
      });
    }
    if (spine.length >= 2) {
      features.push({
        type: 'Feature',
        properties: { kind: 'spine', ...props },
        geometry: { type: 'LineString', coordinates: spine },
      });
    }
  };

  for (const t of trails) {
    pushRun(t.polygon, t.spine, {
      id: t.id,
      name: t.name,
      label: `${DIFFICULTY_SYMBOL[t.difficulty]} ${t.name}`,
      difficulty: t.difficulty,
      status: t.status,
      draft: false,
    });
  }

  if (review) {
    pushRun(review.polygon, review.spine, {
      name: review.name,
      label: review.name,
      difficulty: review.difficulty,
      status: 'planning',
      draft: true,
    });
  }

  if (paintPath && paintPath.length >= 2) {
    features.push({
      type: 'Feature',
      properties: { kind: 'paint' },
      geometry: { type: 'LineString', coordinates: paintPath },
    });
  }

  return { type: 'FeatureCollection', features };
}

/** Adds the trail source + layers on top of the current style. Idempotent.
 *  Call before addLiftLayers so lifts draw over runs (ski-map convention). */
export function addTrailLayers(map: maplibregl.Map): void {
  if (map.getSource(TRAIL_SOURCE)) return;
  map.addSource(TRAIL_SOURCE, { type: 'geojson', data: EMPTY });

  // Graded fill for every run (built + review draft).
  map.addLayer({
    id: 'trail-fill',
    type: 'fill',
    source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'trail'],
    paint: {
      'fill-color': difficultyMatch('#888888'),
      'fill-opacity': ['case', ['==', ['get', 'draft'], true], 0.45, 0.32],
      'fill-antialias': true,
    },
  });

  // Complete runs: solid graded outline.
  map.addLayer({
    id: 'trail-outline',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: [
      'all',
      ['==', ['get', 'kind'], 'trail'],
      ['==', ['get', 'draft'], false],
      ['==', ['get', 'status'], 'complete'],
    ],
    layout: { 'line-join': 'round' },
    paint: { 'line-color': difficultyMatch('#888888'), 'line-width': 2 },
  });

  // Planning runs: dashed graded outline.
  map.addLayer({
    id: 'trail-outline-planning',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: [
      'all',
      ['==', ['get', 'kind'], 'trail'],
      ['==', ['get', 'draft'], false],
      ['==', ['get', 'status'], 'planning'],
    ],
    layout: { 'line-join': 'round' },
    paint: { 'line-color': difficultyMatch('#888888'), 'line-width': 2, 'line-dasharray': [2, 1.5] },
  });

  // The run being reviewed: bright dashed highlight over a white casing.
  map.addLayer({
    id: 'trail-outline-draft-casing',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'trail'], ['==', ['get', 'draft'], true]],
    layout: { 'line-join': 'round' },
    paint: { 'line-color': '#ffffff', 'line-width': 4.5 },
  });
  map.addLayer({
    id: 'trail-outline-draft',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'trail'], ['==', ['get', 'draft'], true]],
    layout: { 'line-join': 'round' },
    paint: { 'line-color': difficultyMatch('#f59e0b'), 'line-width': 2.5, 'line-dasharray': [1.5, 1] },
  });

  // Subtle centerline (spine) so the run's fall line reads.
  map.addLayer({
    id: 'trail-spine',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'spine'],
    layout: { 'line-cap': 'round' },
    paint: { 'line-color': 'rgba(255,255,255,0.7)', 'line-width': 1.2, 'line-dasharray': [1, 2] },
  });

  // Live brush preview: wide translucent round stroke. Width (px) set from
  // meters by setTrailPaintWidth as the brush size / zoom change.
  map.addLayer({
    id: 'trail-paint',
    type: 'line',
    source: TRAIL_SOURCE,
    filter: ['==', ['get', 'kind'], 'paint'],
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: { 'line-color': '#38bdf8', 'line-width': 20, 'line-opacity': 0.4 },
  });

  map.addLayer({
    id: 'trail-labels',
    type: 'symbol',
    source: TRAIL_SOURCE,
    filter: ['all', ['==', ['get', 'kind'], 'trail'], ['==', ['get', 'draft'], false]],
    layout: {
      'text-field': ['get', 'label'],
      'text-size': 13,
      // Regular is the one weight both light + dark basemap fontstacks ship.
      'text-font': ['Noto Sans Regular'],
      'text-optional': true,
    },
    paint: {
      'text-color': '#1f2937',
      'text-halo-color': '#ffffff',
      'text-halo-width': 1.6,
    },
  });
}

/** Replaces the trail source data. No-op before the source exists. */
export function setTrailData(map: maplibregl.Map, fc: GeoJSON.FeatureCollection): void {
  const src = map.getSource(TRAIL_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (src) src.setData(fc);
}

/** Sets the live brush-preview line width in pixels (from meters × zoom). */
export function setTrailPaintWidth(map: maplibregl.Map, widthPx: number): void {
  if (map.getLayer('trail-paint')) {
    map.setPaintProperty('trail-paint', 'line-width', Math.max(2, widthPx));
  }
}
