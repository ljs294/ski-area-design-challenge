import { useEffect } from 'react';
import { snowSurfaceCode, snowSurfaceName } from '../snow';
import type { Readout } from './CursorReadout';
import type { Units } from './SettingsContext';
import { SNOW_CONDITION_LEGEND, SNOW_DEPTH_LEGEND,
  type SnowDisplayMode } from './snowStyle';

export function SnowLayerControl({ mode, onModeChange, onClose, escapeEnabled = true, readout, units }: {
  mode: SnowDisplayMode;
  onModeChange(mode: SnowDisplayMode): void;
  onClose(): void;
  escapeEnabled?: boolean;
  readout: Readout | null;
  units: Units;
}) {
  useEffect(() => {
    if (!escapeEnabled) return;
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', closeOnEscape);
    return () => window.removeEventListener('keydown', closeOnEscape);
  }, [escapeEnabled, onClose]);

  const depth = readout?.snowDepthM;
  const surface = readout?.snowSurface ?? 0;
  const depthText = depth == null ? '—' : units === 'imperial'
    ? `${Math.round(depth * 39.3701)} in`
    : `${Math.round(depth * 100)} cm`;
  const code = snowSurfaceCode(surface), name = snowSurfaceName(surface);
  const conditionText = code && name ? `${code} · ${name}` : depth == null ? '—' : 'No snow';
  const rows = mode === 'depth' ? SNOW_DEPTH_LEGEND : SNOW_CONDITION_LEGEND;
  return (
    <div className="snow-layer-control" aria-label="Snow layer controls">
      <div className="dock-head snow-layer-heading"><span className="dock-head-title">Snow</span>
        <button className="settings-close-x" aria-label="Close Snow layer" onClick={onClose}>âœ•</button>
      </div>
      <div className="segmented snow-mode-toggle" role="group" aria-label="Snow display">
        <button className={`seg-btn${mode === 'depth' ? ' seg-btn-active' : ''}`}
          aria-pressed={mode === 'depth'} onClick={() => onModeChange('depth')}>Depth</button>
        <button className={`seg-btn${mode === 'conditions' ? ' seg-btn-active' : ''}`}
          aria-pressed={mode === 'conditions'} onClick={() => onModeChange('conditions')}>Conditions</button>
      </div>
      <div className="snow-live-readout" aria-live="polite">
        <span><b>Depth</b>{depthText}</span>
        <span><b>Surface</b>{conditionText}</span>
      </div>
      <div className={`snow-legend${mode === 'conditions' ? ' is-conditions' : ''}`}>
        {rows.map((row) => <div className="legend-row" key={row.label}>
          <span className="legend-swatch" style={{ background: row.color }} />
          <span>{row.label}</span>
        </div>)}
      </div>
    </div>
  );
}
