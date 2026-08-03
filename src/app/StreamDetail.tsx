import { useEffect, useState } from 'react';
import type { StreamAnalysis } from '../streamAnalysis';
import { formatStreamDischarge, formatStreamDistance, widthFromDisplay, widthToDisplay } from '../streamAnalysis';
import type { Units } from './SettingsContext';

export function StreamDetail({ stream, units, onWidthOverride, onClose }: {
  stream: StreamAnalysis;
  units: Units;
  onWidthOverride: (widthM: number | null) => void;
  onClose: () => void;
}) {
  const displayWidth = widthToDisplay(stream.widthM, units).toFixed(1);
  const [draft, setDraft] = useState(displayWidth);
  useEffect(() => setDraft(displayWidth), [displayWidth]);

  const commit = () => {
    const widthM = widthFromDisplay(Number(draft), units);
    if (!Number.isFinite(widthM) || widthM < 0.25 || widthM > 500) {
      setDraft(displayWidth);
      return;
    }
    onWidthOverride(widthM);
  };

  return (
    <div className="lift-detail stream-detail">
      <div className="dock-head">
        <span className="dock-head-title lift-detail-name">{stream.name}</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>×</button>
      </div>
      <div className="lake-detail-sub">
        {stream.waterClass === 'river' ? 'River / canal' : 'Stream'} · OpenStreetMap
      </div>
      <div className="readout-line">
        <span className="lift-stat-label">Centerline length</span>
        <span className="lift-stat-value">{formatStreamDistance(stream.lengthM, units)}</span>
      </div>
      <label className="lake-depth-row">
        <span>
          <span className="lift-stat-label">Channel width</span>
          <span className={`lake-depth-source lake-depth-source--${stream.widthSource}`}>
            {stream.widthSource === 'override' ? 'Custom' : stream.widthSource === 'osm' ? 'OpenStreetMap' : 'Class default'}
          </span>
        </span>
        <span className="lake-depth-input-wrap">
          <input
            className="lake-depth-input"
            type="number"
            min={units === 'imperial' ? '0.8' : '0.25'}
            step="0.1"
            value={draft}
            aria-label={`Channel width in ${units === 'imperial' ? 'feet' : 'metres'}`}
            onChange={(event) => setDraft(event.target.value)}
            onBlur={commit}
            onKeyDown={(event) => {
              if (event.key === 'Enter') event.currentTarget.blur();
              if (event.key === 'Escape') {
                event.preventDefault();
                setDraft(displayWidth);
              }
            }}
          />
          <span>{units === 'imperial' ? 'ft' : 'm'}</span>
        </span>
      </label>
      {stream.widthSource === 'override' && (
        <button className="lake-reset-btn" type="button" onClick={() => onWidthOverride(null)}>
          Reset to {stream.sourceWidthM == null ? 'class default' : 'OpenStreetMap width'}
        </button>
      )}
      <div className="readout-line lake-volume-row">
        <span className="lift-stat-label">Gameplay flow estimate</span>
        <span className="lift-stat-value">{formatStreamDischarge(stream.dischargeM3s, units)}</span>
      </div>
      <div className="lake-depth-note">
        Fixed gameplay value based only on channel width. Used for snowmaking simulation; not measured streamflow.
      </div>
    </div>
  );
}
