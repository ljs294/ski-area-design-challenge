import { useEffect, useState } from 'react';
import type { LakeAnalysis } from '../lakeAnalysis';
import { depthFromDisplay, depthToDisplay, formatLakeArea, formatLakeVolume } from '../lakeAnalysis';
import type { Units } from './SettingsContext';

export function LakeDetail({ lake, units, isSnowmaking, onSnowmakingChange,
  onNameOverride, onDepthOverride, onClose }: {
  lake: LakeAnalysis;
  units: Units;
  isSnowmaking: boolean;
  onSnowmakingChange: (enabled: boolean) => void;
  onNameOverride: (name: string | null) => void;
  onDepthOverride: (depthM: number | null) => void;
  onClose: () => void;
}) {
  const displayDepth = lake.averageDepthM == null ? '' : depthToDisplay(lake.averageDepthM, units).toFixed(1);
  const [draft, setDraft] = useState(displayDepth);
  useEffect(() => setDraft(displayDepth), [displayDepth]);
  const [nameDraft, setNameDraft] = useState(lake.nameSource === 'player' ? lake.name : '');
  useEffect(() => setNameDraft(lake.nameSource === 'player' ? lake.name : ''),
    [lake.id, lake.name, lake.nameSource]);

  const commit = () => {
    const parsed = Number(draft);
    const depthM = depthFromDisplay(parsed, units);
    if (!Number.isFinite(depthM) || depthM <= 0 || depthM > 1000) {
      setDraft(displayDepth);
      return;
    }
    onDepthOverride(depthM);
  };

  const commitName = () => {
    const name = nameDraft.trim().slice(0, 80);
    onNameOverride(name || null);
    setNameDraft(name);
  };

  return (
    <div className="lift-detail lake-detail">
      <div className="dock-head">
        <span className="dock-head-title lift-detail-name">{lake.name}</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>✕</button>
      </div>
      <div className="lake-detail-sub">Standing water · OpenStreetMap</div>
      <label className="trail-grade-terrain">
        <input type="checkbox" checked={isSnowmaking} aria-label="Snowmaking pond"
          onChange={(event) => onSnowmakingChange(event.target.checked)} />
        <span><strong>Snowmaking pond</strong>
          <small>Include this pond's estimated volume and add an intake to the snowmaking network.</small></span>
      </label>
      <label className="lake-name-row">
        <span className="lift-stat-label">Pond name</span>
        <input
          className="lake-name-input"
          type="text"
          maxLength={80}
          value={nameDraft}
          placeholder={lake.sourceName ?? 'Name this pond'}
          aria-label="Pond name"
          onChange={(event) => setNameDraft(event.target.value)}
          onBlur={commitName}
          onKeyDown={(event) => {
            if (event.key === 'Enter') event.currentTarget.blur();
            if (event.key === 'Escape') {
              event.preventDefault();
              setNameDraft(lake.nameSource === 'player' ? lake.name : '');
            }
          }}
        />
      </label>
      {lake.nameSource === 'player' && (
        <button className="lake-reset-btn" type="button" onClick={() => onNameOverride(null)}>
          {lake.sourceName ? 'Restore OpenStreetMap name' : 'Remove name'}
        </button>
      )}
      <div className="readout-line">
        <span className="lift-stat-label">Surface area</span>
        <span className="lift-stat-value">{formatLakeArea(lake.areaM2, units)}</span>
      </div>
      <label className="lake-depth-row">
        <span>
          <span className="lift-stat-label">Estimated average depth</span>
          <span className={`lake-depth-source lake-depth-source--${lake.depthSource}`}>
            {lake.depthSource === 'override' ? 'Custom' : lake.depthSource === 'terrain-estimate' ? 'Terrain estimate' : 'Unavailable'}
          </span>
        </span>
        <span className="lake-depth-input-wrap">
          <input
            className="lake-depth-input"
            type="number"
            min="0.1"
            step="0.1"
            value={draft}
            placeholder="—"
            aria-label={`Average depth in ${units === 'imperial' ? 'feet' : 'metres'}`}
            onChange={(event) => setDraft(event.target.value)}
            onBlur={commit}
            onKeyDown={(event) => {
              if (event.key === 'Enter') event.currentTarget.blur();
              if (event.key === 'Escape') {
                event.preventDefault();
                setDraft(displayDepth);
              }
            }}
          />
          <span>{units === 'imperial' ? 'ft' : 'm'}</span>
        </span>
      </label>
      <div className="lake-depth-note">Terrain proxy only—not measured bathymetry.</div>
      {lake.depthSource === 'override' && (
        <button className="lake-reset-btn" type="button" onClick={() => onDepthOverride(null)}>
          Reset to terrain estimate
        </button>
      )}
      <div className="readout-line lake-volume-row">
        <span className="lift-stat-label">Volume</span>
        <span className="lift-stat-value">{formatLakeVolume(lake.volumeM3, units)}</span>
      </div>
    </div>
  );
}
