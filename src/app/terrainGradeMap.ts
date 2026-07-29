import type maplibregl from 'maplibre-gl';
import type { LatLonBounds } from '../elevation';
import type { TerrainRecord } from '../types';
import { contourGeoJSON, localContourGeoJSON } from './localContours';
import {
  clearResortElevationCache,
  RESORT_ASPECT_PROTOCOL,
  RESORT_DEM_PROTOCOL,
  RESORT_SLOPE_PROTOCOL,
  resortProtocolUrl,
} from './resortProtocols';
import { TERRAIN_DEM_SOURCE } from './terrain3d';

export const GRADED_CONTOUR_SOURCE = 'graded-contours';
export const EMPTY_CONTOURS: GeoJSON.FeatureCollection =
  { type: 'FeatureCollection', features: [] };

export function setTerrainContourData(
  map: maplibregl.Map | null,
  record: TerrainRecord,
  imperial: boolean
): void {
  const source = map?.getSource('contours') as maplibregl.GeoJSONSource | undefined;
  if (source && record.bounds) source.setData(localContourGeoJSON(record, imperial));
}

/** Highlight the contours a pending grade would move. Pass `null` to clear —
 * once an edit is committed it is terrain, not a proposal, and belongs in the
 * ordinary contour colour. */
export function setGradedContourPreview(
  map: maplibregl.Map | null,
  segments: ArrayLike<number> | null,
  bounds: LatLonBounds | undefined,
  imperial: boolean
): void {
  const source = map?.getSource(GRADED_CONTOUR_SOURCE) as maplibregl.GeoJSONSource | undefined;
  if (!source) return;
  source.setData(segments && segments.length > 0 && bounds
    ? contourGeoJSON(segments, bounds, imperial)
    : EMPTY_CONTOURS);
}

/** Refresh every map consumer of elevation after an atomic grading commit. */
export function refreshTerrainGradeSources(
  map: maplibregl.Map | null,
  record: TerrainRecord,
  imperial: boolean
): void {
  if (!map) return;
  clearResortElevationCache();
  const setTiles = (id: string, protocol: string) => {
    const source = map.getSource(id) as { setTiles?: (tiles: string[]) => void } | undefined;
    source?.setTiles?.([resortProtocolUrl(protocol, record)]);
  };
  setTiles('dem', RESORT_DEM_PROTOCOL);
  setTiles(TERRAIN_DEM_SOURCE, RESORT_DEM_PROTOCOL);
  setTiles('slope', RESORT_SLOPE_PROTOCOL);
  setTiles('aspect', RESORT_ASPECT_PROTOCOL);
  setTerrainContourData(map, record, imperial);
  setGradedContourPreview(map, null, record.bounds, imperial);
  map.triggerRepaint();
}
