import { useEffect, useState, type Dispatch, type MutableRefObject,
  type SetStateAction } from 'react';
import type maplibregl from 'maplibre-gl';
import { fillElevationGaps } from '../trails';
import { compass8, sampleTerrainAt } from './terrainProtocols';
import { COVER_LABELS, sampleCoverAt } from './worldcoverProtocol';
import { sampleLocalCoverAt, sampleLocalTerrainAt, WORLD_COVER_LABELS } from './resortProtocols';
import type { Readout } from './CursorReadout';
import type { OverlayId } from './Legend';
import type { TerrainRecord } from '../types/terrain';
import type { SnowGrid } from '../types/snow';
import { sampleSnowGrid } from '../snow';

export { useSnowLayer } from './useSnowLayer';

interface MapSamplingOptions {
  mapRef: MutableRefObject<maplibregl.Map | null>;
  terrainRecordRef: MutableRefObject<TerrainRecord | null>;
  activeOverlay: OverlayId | null;
  activeOverlayRef: MutableRefObject<OverlayId | null>;
  lastLngLatRef: MutableRefObject<{ lng: number; lat: number } | null>;
  sampleTokenRef: MutableRefObject<number>;
  doSampleRef: MutableRefObject<(lngLat: { lng: number; lat: number }) => void>;
  snowGridRef: MutableRefObject<SnowGrid | null>;
}

export interface MapSampling {
  readout: Readout | null;
  setReadout: Dispatch<SetStateAction<Readout | null>>;
  samplePoint(lng: number, lat: number, zoom: number): Promise<{
    elevation: number;
    slopeDeg: number;
    aspectDeg: number;
  } | null>;
  sampleProfile(line: [number, number][], zoom: number): Promise<number[] | null>;
}

/** Cursor and construction sampling over either the local package or picker services. */
export function useMapSampling(options: MapSamplingOptions): MapSampling {
  const [readout, setReadout] = useState<Readout | null>(null);
  const samplePoint = (lng: number, lat: number, zoom: number) => {
    if (!options.terrainRecordRef.current) {
      return sampleTerrainAt(lng, lat, zoom).then((sample) => sample, () => null);
    }
    return Promise.resolve(sampleLocalTerrainAt(lng, lat));
  };

  const sampleProfile = async (line: [number, number][], zoom: number) => {
    const samples = await Promise.all(line.map(([lng, lat]) => samplePoint(lng, lat, zoom)));
    return fillElevationGaps(samples.map((sample) => sample?.elevation ?? null));
  };

  options.doSampleRef.current = (lngLat) => {
    const map = options.mapRef.current;
    if (!map) return;
    const zoom = Math.min(14, Math.max(10, Math.round(map.getZoom())));
    const overlay = options.activeOverlayRef.current;
    const token = ++options.sampleTokenRef.current;
    void (async () => {
      const localRecord = options.terrainRecordRef.current;
      const terrain = await samplePoint(lngLat.lng, lngLat.lat, zoom);
      if (!terrain || token !== options.sampleTokenRef.current) return;
      let coverLabel: string | null = null;
      const snow = overlay === 'snow' && options.snowGridRef.current
        ? sampleSnowGrid(options.snowGridRef.current, lngLat.lng, lngLat.lat) : null;
      if (localRecord) {
        const code = sampleLocalCoverAt(lngLat.lng, lngLat.lat);
        coverLabel = code == null ? '—' : WORLD_COVER_LABELS[code] ?? 'Unknown';
      } else if (overlay === 'groundcover') {
        const bucket = await sampleCoverAt(lngLat.lng, lngLat.lat, zoom).catch(() => null);
        if (token !== options.sampleTokenRef.current) return;
        coverLabel = bucket ? COVER_LABELS[bucket] : '—';
      }
      setReadout({
        elevationM: terrain.elevation,
        overlay,
        slopeDeg: terrain.slopeDeg,
        aspectCompass: compass8(terrain.aspectDeg),
        coverLabel,
        snowDepthM: snow?.depthM,
        snowSurface: snow?.surface,
      });
    })();
  };

  useEffect(() => {
    options.activeOverlayRef.current = options.activeOverlay;
    if (options.lastLngLatRef.current) options.doSampleRef.current(options.lastLngLatRef.current);
  }, [options.activeOverlay, options.activeOverlayRef,
    options.doSampleRef, options.lastLngLatRef]);

  return { readout, setReadout, samplePoint, sampleProfile };
}
