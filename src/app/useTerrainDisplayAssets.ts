import { useEffect, useRef, type MutableRefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import { coverDisplayToGeoJSON, type CoverDisplayGeoJSON } from '../coverDisplay';
import type { TerrainRecord } from '../types/terrain';
import { prepareDisplayImagery } from './displayImagery';
import { renderProfileFor, type RenderQuality } from './renderProfile';

export function useTerrainDisplayAssets(options: {
  qualityRef: MutableRefObject<RenderQuality>;
  mapRef: MutableRefObject<maplibregl.Map | null>;
  reconfigureRef: MutableRefObject<(map: maplibregl.Map) => void>;
  reportError(message: string): void;
}) {
  const heightRef = useRef<{ checksum: string; heights: Float32Array } | null>(null);
  const coverRef = useRef<CoverDisplayGeoJSON | null>(null);
  const coverKeyRef = useRef<string | null>(null);
  const imageryUrlRef = useRef<string | null>(null);
  const imageryKeyRef = useRef<string | null>(null);
  const imageryAbortRef = useRef<AbortController | null>(null);

  function cache(record: TerrainRecord): void {
    const heightChecksum = record.packageManifest?.elevationChecksum ?? record.updatedAt;
    if (heightRef.current?.checksum !== heightChecksum) heightRef.current = {
      checksum: heightChecksum,
      heights: record.sampleHeights instanceof Float32Array
        ? record.sampleHeights : Float32Array.from(record.sampleHeights),
    };
    const profile = renderProfileFor(options.qualityRef.current);
    const bounds = record.bounds;
    const coverKey = profile.coverMode === 'raster' || !record.coverDisplayGeometry || !bounds
      ? null
      : `${record.coverDisplayMetadata?.checksum ?? record.coverDisplayGeometry.length}:` +
        `${bounds.west},${bounds.south},${bounds.east},${bounds.north}`;
    if (coverKeyRef.current !== coverKey) {
      coverRef.current = coverKey
        ? coverDisplayToGeoJSON(record.coverDisplayGeometry!, bounds!) : null;
      coverKeyRef.current = coverKey;
    }
    const imageryKey = record.localImagery
      ? `${record.localImageryMetadata?.checksum ?? record.localImagery.length}:` +
        `${record.localImageryMetadata?.mimeType ?? 'image/jpeg'}:${profile.imageryMaxSide}` : null;
    if (imageryKeyRef.current === imageryKey) return;
    imageryAbortRef.current?.abort();
    imageryAbortRef.current = null;
    if (imageryUrlRef.current) URL.revokeObjectURL(imageryUrlRef.current);
    imageryUrlRef.current = null;
    imageryKeyRef.current = imageryKey;
    if (!imageryKey || !record.localImagery || !record.localImageryMetadata) return;
    const metadata = record.localImageryMetadata;
    const bytes = record.localImagery instanceof Uint8Array
      ? record.localImagery.slice() : Uint8Array.from(record.localImagery);
    const apply = (displayBytes: Uint8Array) => {
      if (imageryKeyRef.current !== imageryKey) return;
      imageryUrlRef.current = URL.createObjectURL(new Blob(
        [displayBytes.buffer as ArrayBuffer], { type: metadata.mimeType }));
    };
    if (Math.max(metadata.width, metadata.height) <= profile.imageryMaxSide) return apply(bytes);
    const controller = new AbortController();
    imageryAbortRef.current = controller;
    void prepareDisplayImagery({ bytes, mimeType: metadata.mimeType,
      width: metadata.width, height: metadata.height, maxSide: profile.imageryMaxSide },
    controller.signal).then((displayBytes) => {
      if (controller.signal.aborted) return;
      apply(displayBytes);
      const map = options.mapRef.current;
      if (map?.isStyleLoaded()) options.reconfigureRef.current(map);
    }, (error) => {
      if (!(error instanceof DOMException && error.name === 'AbortError')) {
        options.reportError('Local imagery could not be optimized for this quality tier.');
      }
    });
  }

  useEffect(() => () => {
    imageryAbortRef.current?.abort();
    if (imageryUrlRef.current) URL.revokeObjectURL(imageryUrlRef.current);
  }, []);

  return { heightRef, coverRef, imageryUrlRef, cache };
}
