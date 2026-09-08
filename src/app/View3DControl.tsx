import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import type maplibregl from 'maplibre-gl';

export function View3DControl({ is3D, onToggle, map }: {
  is3D: boolean; onToggle: () => void; map?: maplibregl.Map | null;
}) {
  const [host, setHost] = useState<HTMLDivElement | null>(null);
  useEffect(() => {
    if (!map) return;
    const container = document.createElement('div');
    container.className = 'maplibregl-ctrl view3d-map-control';
    const control = { onAdd: () => container, onRemove: () => container.remove() };
    map.addControl(control, 'bottom-right'); setHost(container);
    return () => { if (map.hasControl(control)) map.removeControl(control); };
  }, [map]);
  const button = (
    <div className="view3d-control">
      <button
        className={`view3d-btn${is3D ? ' view3d-btn-active' : ''}`}
        aria-pressed={is3D}
        title={is3D ? 'Return to top-down 2D view' : 'Tilt into 3D terrain view'}
        onClick={onToggle}
      >
        {is3D ? '2D' : '3D'}
      </button>
    </div>
  );
  return map !== undefined ? map && host ? createPortal(button, host) : null : button;
}
