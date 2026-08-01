import { useEffect, useState } from 'react';
import type { StreamAnalysis } from '../streamAnalysis';
import { streamWidthFromDisplay, streamWidthToDisplay } from '../streamAnalysis';
import { fmtDistance } from '../lifts';
import type { Units } from './SettingsContext';

function formatFlow(flowM3s: number, units: Units): string {
  return units === 'imperial'
    ? `${Math.round(flowM3s * 15_850.323).toLocaleString()} US gal/min`
    : `${Math.round(flowM3s * 1000).toLocaleString()} L/s`;
}

export function StreamDetail({ stream, units, onWidthOverride, onClose }: {
  stream: StreamAnalysis;
  units: Units;
  onWidthOverride: (widthM: number | null) => void;
  onClose: () => void;
}) {
  const displayWidth = streamWidthToDisplay(stream.widthM, units).toFixed(1);
  const [draft, setDraft] = useState(displayWidth);
  useEffect(() => setDraft(displayWidth), [displayWidth]);
  const commit = () => {
    const widthM = streamWidthFromDisplay(Number(draft), units);
    if (!Number.isFinite(widthM) || widthM <= 0 || widthM > 1000) return setDraft(displayWidth);
    onWidthOverride(widthM);
  };
  const sourceLabel = stream.widthSource === 'override' ? 'Custom' : stream.widthSource === 'osm' ? 'OpenStreetMap' : 'Gameplay default';
  return <div className="lift-detail lake-detail stream-detail">
    <div className="dock-head"><span className="dock-head-title lift-detail-name">{stream.name}</span>
      <button className="settings-close-x" aria-label="Close" onClick={onClose}>✕</button></div>
    <div className="lake-detail-sub">{stream.waterClass === 'stream' ? 'Stream' : 'River / canal'} · OpenStreetMap</div>
    <label className="lake-depth-row"><span><span className="lift-stat-label">Channel width</span>
      <span className={`lake-depth-source lake-depth-source--${stream.widthSource}`}>{sourceLabel}</span></span>
      <span className="lake-depth-input-wrap"><input className="lake-depth-input" type="number" min="0.1" step="0.1"
        value={draft} aria-label={`Channel width in ${units === 'imperial' ? 'feet' : 'metres'}`}
        onChange={(event) => setDraft(event.target.value)} onBlur={commit}
        onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur();
          if (event.key === 'Escape') { event.preventDefault(); setDraft(displayWidth); } }} />
        <span>{units === 'imperial' ? 'ft' : 'm'}</span></span></label>
    {stream.widthSource === 'override' && <button className="lake-reset-btn" type="button"
      onClick={() => onWidthOverride(null)}>Reset to source/default width</button>}
    <div className="readout-line"><span className="lift-stat-label">Mapped length</span>
      <span className="lift-stat-value">{fmtDistance(stream.lengthM, units)}</span></div>
    <div className="readout-line lake-volume-row"><span className="lift-stat-label">Gameplay flow</span>
      <span className="lift-stat-value">{formatFlow(stream.flowM3s, units)}</span></div>
    <div className="lake-depth-note">Fixed gameplay capacity based on effective channel width; not current or seasonal discharge.</div>
  </div>;
}
