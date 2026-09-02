import { useEffect, useState } from 'react';
import type { SavedBuilding } from '../types/buildings';
import type { SavedSnowmakingNode } from '../types/snowmaking';
import { formatBuildingHeight, formatBuildingLength, gableRidgeHeightM } from '../buildingUnits';
import type { Units } from './SettingsContext';

function formatBearing(value: number): string {
  return `${value.toFixed(1)}°`;
}

function formatVolume(value: number, units: Units): string {
  if (units === 'imperial') return `${(value * 1.30795062).toFixed(1)} yd³`;
  return `${value.toFixed(1)} m³`;
}

export interface PumpHouseDetailProps {
  building: SavedBuilding;
  pump: SavedSnowmakingNode | null;
  connectedPipeCount: number;
  units: Units;
  onRename: (id: string, name: string) => void;
  onRemove: (id: string) => void;
  onClose: () => void;
}

/** Read-only built detail; only the name and removal command are mutable. */
export function PumpHouseDetail({ building, pump, connectedPipeCount, units,
  onRename, onRemove, onClose }: PumpHouseDetailProps) {
  const [name, setName] = useState(building.name);
  const [confirmRemove, setConfirmRemove] = useState(false);
  useEffect(() => setName(building.name), [building.name]);
  const ridgeHeight = gableRidgeHeightM(building.dimensions.widthM,
    building.dimensions.eaveHeightM, building.roof.pitchRise, building.roof.pitchRun);
  const commitName = (value: string) => {
    setName(value);
    if (value.trim()) onRename(building.id, value);
  };
  return <div className="site-control site-control-wide pump-house-detail" data-testid="pump-house-detail">
    <div className="dock-head"><span className="dock-head-title">{building.name}</span>
      <button className="settings-close-x" aria-label="Close" onClick={onClose}>×</button></div>
    <div className="site-hint">Built snowmaking pump house. Dimensions, heading, roof, and foundation are locked.</div>
    <label className="name-entry-row"><span className="lift-stat-label">Name</span>
      <input className="name-entry-input lift-name-input" data-testid="pump-house-built-name"
        aria-label="Pump house name" value={name} onChange={(event) => commitName(event.target.value)} /></label>
    <div className="lift-stats">
      <div className="readout-line"><span className="lift-stat-label">Length</span>
        <span className="lift-stat-value">{formatBuildingLength(building.dimensions.lengthM, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Width</span>
        <span className="lift-stat-value">{formatBuildingLength(building.dimensions.widthM, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Eave height</span>
        <span className="lift-stat-value">{formatBuildingHeight(building.dimensions.eaveHeightM, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Ridge height</span>
        <span className="lift-stat-value">{formatBuildingHeight(ridgeHeight, units)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Bearing</span>
        <span className="lift-stat-value">{formatBearing(building.bearingDeg)}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Roof</span>
        <span className="lift-stat-value">Gable · {building.roof.pitchRise}:{building.roof.pitchRun}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Foundation</span>
        <span className="lift-stat-value">{building.foundation.kind === 'flattened'
          ? 'Flattened site' : 'Level structure on slope'}</span></div>
      {building.foundation.kind === 'flattened' && <>
        <div className="readout-line"><span className="lift-stat-label">Finished floor</span>
          <span className="lift-stat-value">{formatBuildingHeight(building.foundation.finishedFloorElevationM, units)}</span></div>
        <div className="readout-line"><span className="lift-stat-label">Earthwork cut / fill</span>
          <span className="lift-stat-value">{formatVolume(building.foundation.earthwork.cutM3, units)} / {
            formatVolume(building.foundation.earthwork.fillM3, units)}</span></div>
      </>}
      {building.foundation.kind === 'slope' && <div className="readout-line"><span className="lift-stat-label">Foundation samples</span>
        <span className="lift-stat-value">{building.foundation.perimeterGroundElevationsM.length} perimeter elevations</span></div>}
    </div>
    <div className="lift-stats pump-house-equipment">
      <div className="readout-line"><span className="lift-stat-label">Owned pump</span>
        <span className="lift-stat-value">{pump?.name ?? building.connection.nodeId}</span></div>
      <div className="readout-line"><span className="lift-stat-label">Equipment</span>
        <span className="lift-stat-value">1,000 hp / 85% efficiency</span></div>
      <div className="readout-line"><span className="lift-stat-label">Capital cost</span>
        <span className="lift-stat-value">TBD</span></div>
      <div className="readout-line"><span className="lift-stat-label">Maintenance</span>
        <span className="lift-stat-value">TBD</span></div>
    </div>
    {confirmRemove ? <div className="pump-house-remove-confirm" data-testid="pump-house-remove-confirm">
      <div className="lift-warning">Remove “{building.name}”? This also removes its owned pump.</div>
      {connectedPipeCount > 0 && <div className="lift-warning">
        The pump has {connectedPipeCount} connected {connectedPipeCount === 1 ? 'pipe' : 'pipes'}.
        Pipe geometry remains, but its ends will be detached.</div>}
      <div className="site-actions"><button className="site-btn site-btn-danger" data-testid="remove-pump-house"
        onClick={() => onRemove(building.id)}>Remove</button>
        <button className="site-btn" onClick={() => setConfirmRemove(false)}>Keep</button></div>
    </div> : <div className="site-actions pump-house-detail-actions">
      <button className="site-btn site-btn-danger-ghost" data-testid="remove-pump-house-start"
        onClick={() => setConfirmRemove(true)}>Remove</button></div>}
  </div>;
}

