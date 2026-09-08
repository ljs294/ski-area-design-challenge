import { useState } from 'react';
import type { SavedPond } from '../types';
import type { Units } from './SettingsContext';
import { PondEarthworkStats, PondStats } from './SnowmakingControl';

/** Read-only inspector for a pond that is not part of the snowmaking system. */
export function PondDetail({ pond, units, onRemove }: {
  pond: SavedPond;
  units: Units;
  onRemove(): void;
}) {
  const [confirmRemove, setConfirmRemove] = useState(false);
  return <div className="lift-detail pond-detail">
    <div className="lift-detail-sub">Standalone pond · Not connected to snowmaking</div>
    <PondStats pond={pond} units={units} />
    <PondEarthworkStats pond={pond} units={units} />
    {confirmRemove ? <div className="lift-delete-confirm">
      <div className="lift-delete-warn">Remove “{pond.name}”? This can't be undone.</div>
      <div className="site-actions">
        <button className="site-btn site-btn-danger" onClick={onRemove}>Remove</button>
        <button className="site-btn" onClick={() => setConfirmRemove(false)}>Keep</button>
      </div>
    </div> : <div className="site-actions lift-detail-actions">
      <button className="site-btn site-btn-danger-ghost" onClick={() => setConfirmRemove(true)}>Remove pond</button>
    </div>}
  </div>;
}
