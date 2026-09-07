import { useLayoutEffect, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';

/** Restore orientation and handler settings without undoing dashboard pan/zoom. */
export function lockDashboardCamera(map: maplibregl.Map): () => void {
  const prior = { pitch: map.getPitch(), bearing: map.getBearing(), maxPitch: map.getMaxPitch(),
    rotate: map.dragRotate.isEnabled(), touchPitch: map.touchPitch.isEnabled() };
  map.stop(); map.dragRotate.disable(); map.touchPitch.disable(); map.setMaxPitch(0);
  const holdBearing = () => { if (Math.abs(map.getBearing() - prior.bearing) > 0.001) map.setBearing(prior.bearing); };
  map.on('rotate', holdBearing);
  let restored = false;
  return () => {
    if (restored) return; restored = true;
    map.off('rotate', holdBearing); map.setMaxPitch(prior.maxPitch);
    map.jumpTo({ pitch: prior.pitch, bearing: prior.bearing });
    if (prior.rotate) map.dragRotate.enable();
    if (prior.touchPitch) map.touchPitch.enable();
  };
}
export function useDashboardCamera(mapRef: RefObject<maplibregl.Map | null>, active: boolean): void {
  useLayoutEffect(() => {
    if (active && mapRef.current) return lockDashboardCamera(mapRef.current);
  }, [active, mapRef]);
}
