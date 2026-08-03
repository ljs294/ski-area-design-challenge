import { useEffect, useState } from 'react';
import type { SavedDam, SavedLift, SavedPond, SavedTrail } from '../types';
import type { Units } from './SettingsContext';
import { fmtDistance } from '../lifts';
import { fmtArea } from '../trails';
import { resortElevations, resortTrailTotals, snowmakingWaterCapacityM3 } from './resortStats';
import { reverseGeocode } from './SearchBox';
import { formatLakeVolume } from '../lakeAnalysis';

function StatRow({ label, value, tbd }: { label: string; value: string; tbd?: boolean }) {
  return (
    <div className="stat-row">
      <span className="stat-row-label">{label}</span>
      <span className={`stat-row-value${tbd ? ' is-tbd' : ''}`}>{value}</span>
    </div>
  );
}

/**
 * Ski-area overview, opened from the toolbar name. The name is editable in
 * place; elevations derive from the lift network; location reverse-geocodes the
 * resort center. Run count and snowfall are placeholders until those systems land.
 */
export function ResortStatsPanel({
  name,
  onRename,
  lifts,
  trails,
  dams,
  ponds,
  center,
  units,
  onClose,
}: {
  name: string;
  onRename: (name: string) => void;
  lifts: SavedLift[];
  trails: SavedTrail[];
  dams: SavedDam[];
  ponds: SavedPond[];
  center: [number, number];
  units: Units;
  onClose: () => void;
}) {
  const { summitM, baseM, verticalM } = resortElevations(lifts, trails);
  const runTotals = resortTrailTotals(trails);
  const snowmakingCapacityM3 = snowmakingWaterCapacityM3(dams, ponds);
  const [location, setLocation] = useState<string | null>(null); // null = still loading

  const [lng, lat] = center;
  useEffect(() => {
    let alive = true;
    setLocation(null);
    reverseGeocode(lat, lng).then((r) => {
      if (alive) setLocation(r?.place || '—');
    });
    return () => {
      alive = false;
    };
  }, [lat, lng]);

  const elev = (m: number | null) => (m == null ? '—' : fmtDistance(m, units));

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="settings-panel resort-stats" onClick={(e) => e.stopPropagation()}>
        <div className="settings-header">
          <input
            className="resort-stats-name"
            type="text"
            value={name}
            aria-label="Ski area name"
            onChange={(e) => onRename(e.target.value)}
          />
          <button className="settings-close-x" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="resort-stats-loc">
          {location === null ? 'Locating…' : location}
        </div>
        <div className="resort-stats-body">
          <StatRow label="Summit elevation" value={elev(summitM)} />
          <StatRow label="Base elevation" value={elev(baseM)} />
          <StatRow label="Vertical drop" value={elev(verticalM)} />
          <StatRow label="Ski lifts" value={lifts.length.toLocaleString()} />
          <StatRow label="Ski runs" value={runTotals.count.toLocaleString()} />
          {runTotals.count > 0 && (
            <StatRow label="Total run length" value={fmtDistance(runTotals.totalLengthM, units)} />
          )}
          {/* Same fmtArea the per-run panel uses, so the resort total reads in the
              same unit as the runs it is the sum of. */}
          {runTotals.count > 0 && (
            <StatRow label="Skiable area" value={fmtArea(runTotals.totalAreaM2, units)} />
          )}
          <StatRow label="Snowmaking water capacity"
            value={formatLakeVolume(snowmakingCapacityM3, units)} />
          <StatRow label="Avg. annual snowfall" value="TBD" tbd />
        </div>
      </div>
    </div>
  );
}
