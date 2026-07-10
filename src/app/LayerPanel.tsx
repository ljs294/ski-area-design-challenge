import type { LayerToggle } from './analysisLayers';
import { Legend, type OverlayId } from './Legend';

export function LayerPanel({
  layers,
  onToggle,
  open,
  onToggleOpen,
  activeOverlay,
}: {
  layers: LayerToggle[];
  onToggle: (id: string) => void;
  open: boolean;
  onToggleOpen: () => void;
  activeOverlay: OverlayId | null;
}) {
  if (layers.length === 0) return null;
  return (
    <div className="layer-panel">
      <button className="layer-panel-header" onClick={onToggleOpen} aria-expanded={open}>
        <span className="layer-panel-title">Layers</span>
        <span className="layer-panel-chevron">{open ? '▾' : '▸'}</span>
      </button>
      {open && (
        <div className="layer-panel-body">
          {layers.map((l, i) => (
            <div key={l.id}>
              {l.section && l.section !== layers[i - 1]?.section && (
                <div className="layer-section-title">{l.section}</div>
              )}
              <label className="layer-row">
                <input type="checkbox" checked={l.visible} onChange={() => onToggle(l.id)} />
                <span>{l.label}</span>
              </label>
            </div>
          ))}
          {/* Legend appears only while the panel is open, only for the active overlay. */}
          <Legend overlay={activeOverlay} />
        </div>
      )}
    </div>
  );
}
