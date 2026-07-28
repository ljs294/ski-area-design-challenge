import type maplibregl from 'maplibre-gl';
import type { TerrainRecord } from '../types';
import { localContourGeoJSON } from './localContours';
import {
  clearResortElevationCache,
  RESORT_ASPECT_PROTOCOL,
  RESORT_DEM_PROTOCOL,
  RESORT_SLOPE_PROTOCOL,
  resortProtocolUrl,
} from './resortProtocols';
import { TERRAIN_DEM_SOURCE } from './terrain3d';

export function setTerrainContourData(
  map: maplibregl.Map | null,
  record: TerrainRecord,
  imperial: boolean
): void {
  const source = map?.getSource('contours') as maplibregl.GeoJSONSource | undefined;
  if (source && record.bounds) source.setData(localContourGeoJSON(record, imperial));
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
  map.triggerRepaint();
}
