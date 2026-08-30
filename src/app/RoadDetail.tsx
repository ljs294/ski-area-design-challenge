import type { RoadAnalysis } from '../roadAnalysis';
import { fmtDistance } from '../lifts';
import type { Units } from './SettingsContext';

const WIDTH_SOURCE_LABEL: Record<RoadAnalysis['widthSource'], string> = {
  osm: 'OpenStreetMap width',
  lanes: 'Lane estimate',
  default: 'Road-class default',
  'player-built': 'Player-built',
};

export function RoadDetail({ road, units, onClose }: {
  road: RoadAnalysis;
  units: Units;
  onClose(): void;
}) {
  return <div className="lift-detail road-detail">
    <div className="dock-head">
      <span className="dock-head-title lift-detail-name">{road.name}</span>
      <button className="settings-close-x" aria-label="Close" onClick={onClose}>×</button>
    </div>
    <div className="lake-detail-sub">
      Paved road · {road.source === 'osm' ? 'OpenStreetMap' : 'Resort infrastructure'}
    </div>
    <div className="readout-line">
      <span className="lift-stat-label">Paved width</span>
      <span className="lift-stat-value">{fmtDistance(road.widthM, units)}</span>
    </div>
    <div className="readout-line">
      <span className="lift-stat-label">Width basis</span>
      <span className="lift-stat-value">{WIDTH_SOURCE_LABEL[road.widthSource]}</span>
    </div>
    <div className="readout-line">
      <span className="lift-stat-label">Lanes</span>
      <span className="lift-stat-value">{road.totalLanes}</span>
    </div>
  </div>;
}
