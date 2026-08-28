import { useEffect, useRef, useState } from 'react';
import type { Map as MapLibreMap, Marker } from 'maplibre-gl';

export function LocationMap({ latitude, longitude }: { latitude: number; longitude: number }) {
  const containerRef = useRef<HTMLDivElement>(null); const mapRef = useRef<MapLibreMap | null>(null);
  const markerRef = useRef<Marker | null>(null); const positionRef = useRef({ latitude, longitude }); positionRef.current = { latitude, longitude };
  const [unavailable, setUnavailable] = useState(false);
  useEffect(() => {
    if (!containerRef.current || mapRef.current) return; let disposed = false;
    void import('maplibre-gl').then(({ default: maplibregl }) => {
      if (disposed || !containerRef.current) return; const position = positionRef.current;
      try {
        const map = new maplibregl.Map({ container: containerRef.current, style: 'https://tiles.openfreemap.org/styles/positron',
          center: [position.longitude, position.latitude], zoom: 6, interactive: false, attributionControl: { compact: true } });
        map.on('error', () => setUnavailable(true)); mapRef.current = map;
        markerRef.current = new maplibregl.Marker({ color: '#ffb45e' }).setLngLat([position.longitude, position.latitude]).addTo(map);
      } catch { setUnavailable(true); }
    }).catch(() => setUnavailable(true));
    return () => { disposed = true; markerRef.current?.remove(); markerRef.current = null; mapRef.current?.remove(); mapRef.current = null; };
  }, []);
  useEffect(() => {
    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return;
    markerRef.current?.setLngLat([longitude, latitude]); mapRef.current?.jumpTo({ center: [longitude, latitude] });
  }, [latitude, longitude]);
  return <div className="location-map-shell"><div ref={containerRef} className="location-map" aria-label={`Map showing ${latitude}, ${longitude}`}/>
    {unavailable && <div className="map-unavailable">Map tiles unavailable · coordinates remain usable</div>}</div>;
}
