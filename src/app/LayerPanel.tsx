import type { LayerToggle } from './analysisLayers';
import { Legend, type OverlayId } from './Legend';

/** The layer rows + contextual legend, with no container chrome. Shared by the
 *  top-left LayerPanel (Graphics Lab) and the in-game layers dock (MapView). */
export function LayerList({
  layers,
  onToggle,
  activeOverlay,
  inlineLegend = true,
}: {
  layers: LayerToggle[];
  onToggle: (id: string) => void;
  activeOverlay: OverlayId | null;
  /** Render the contextual legend inside the list (default). MapView's dock
   *  passes false and floats the legend above the panel so the menu never
   *  resizes when overlays are toggled. */
  inlineLegend?: boolean;
}) {
  return (
    <div className="layer-list">
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
      {/* Legend appears only for the active overlay. */}
      {inlineLegend && <Legend overlay={activeOverlay} />}
    </div>
  );
}

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
          <LayerList layers={layers} onToggle={onToggle} activeOverlay={activeOverlay} />
        </div>
      )}
    </div>
  );
}
