import type { SavedBuilding } from '../types/buildings';
import type { BuildingTool } from './buildingControllerModel';
import type { Units } from './SettingsContext';
import { formatBuildingHeight, formatBuildingLength } from '../buildingUnits';

function formatBearing(value: number): string {
  return `${value.toFixed(1)}°`;
}

function placementCopy(tool: Extract<BuildingTool, { phase: 'armed' | 'centered' }>): {
  title: string;
  hint: string;
} {
  if (tool.phase === 'armed') {
    return {
      title: 'Place pump house',
      hint: 'Move the pointer to preview the complete pump house, then click to fix its center.',
    };
  }
  return {
    title: 'Set pump-house direction',
    hint: 'The pointer controls the long-axis direction. Click at least 1 m from the center to review.',
  };
}

export interface PumpHouseOverviewProps {
  buildings: SavedBuilding[];
  units: Units;
  onArm: () => void;
  onSelect: (id: string) => void;
  onClose?: () => void;
  tool?: BuildingTool;
  onCancel?: () => void;
}

/**
 * Snowmaking's pump-house subsection and the placement instructions shown
 * before review. Keeping the placement copy here makes it available to the
 * dock and to the deterministic browser selectors without coupling it to the
 * map controller.
 */
export function PumpHouseOverview({ buildings, units, onArm, onSelect, onClose,
  tool, onCancel }: PumpHouseOverviewProps) {
  if (tool && (tool.phase === 'armed' || tool.phase === 'centered')) {
    const copy = placementCopy(tool);
    const dimensions = tool.dimensions;
    return <div className="pump-house-overview pump-house-placement site-control site-control-wide"
      data-testid="pump-house-placement" aria-live="polite">
      <div className="dock-head">
        <span className="dock-head-title">{copy.title}</span>
        <button className="settings-close-x" aria-label="Close" onClick={onCancel}>×</button>
      </div>
      <div className="site-hint">{copy.hint}</div>
      {tool.phase === 'armed' && !tool.cursor && <div className="site-hint">
        Hover over prepared terrain to see the preview.</div>}
      <div className="pump-house-placement-preview" data-testid="pump-house-placement-preview">
        <div className="readout-line"><span className="lift-stat-label">Dimensions</span>
          <span className="lift-stat-value">{formatBuildingLength(dimensions.lengthM, units)} × {
            formatBuildingLength(dimensions.widthM, units)} × {
            formatBuildingHeight(dimensions.eaveHeightM, units)}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Heading</span>
          <span className="lift-stat-value">{formatBearing(tool.bearingDeg)}</span></div>
      </div>
      <button className="site-btn" onClick={onCancel}>Cancel</button>
    </div>;
  }

  return <section className="pump-house-overview" data-testid="pump-house-overview"
    aria-label="Pump houses">
    <div className="network-section-title">Buildings ({buildings.length})</div>
    <button className="lift-add-btn site-btn site-btn-primary" data-testid="build-pump-house"
      onClick={onArm}>Build pump house</button>
    {buildings.length === 0 ? <div className="lift-overview-empty">No pump houses yet.</div> :
      <div className="lift-list" data-testid="pump-house-list">
        {buildings.map((building) => <button key={building.id} type="button"
          className="lift-row lift-row-button pump-house-row"
          data-testid={`pump-house-row-${building.id}`} onClick={() => onSelect(building.id)}
          title={`View ${building.name}`}>
          <span className="infrastructure-building-swatch" aria-hidden="true" />
          <span className="lift-row-main"><span className="lift-row-name">{building.name}</span>
            <span className="lift-row-summary">Snowmaking pump house · {
              formatBuildingLength(building.dimensions.lengthM, units)} · {
              formatBearing(building.bearingDeg)}</span></span>
        </button>)}
      </div>}
    {onClose && <button className="site-btn pump-house-overview-close" onClick={onClose}>Done</button>}
  </section>;
}
