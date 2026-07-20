import { haversineMeters } from '../geo';
import { DIFFICULTY_COLORS } from '../trails';
import type { TrailDifficulty } from '../types';
import type { Units } from './SettingsContext';

const M_TO_FT = 3.28084;
const VB_W = 300;
const VB_H = 88;

function fmtElev(m: number, units: Units): string {
  return units === 'imperial'
    ? `${Math.round(m * M_TO_FT).toLocaleString()} ft`
    : `${Math.round(m).toLocaleString()} m`;
}

/**
 * Rough elevation profile of a run: terrain elevation sampled at each spine
 * station, drawn as a filled area vs. horizontal distance and tinted by the
 * run's grade. Shown live in the review panel and in the run detail. Falls back
 * to a placeholder until the spine elevations resolve.
 */
export function TrailProfile({
  spine,
  spineElevM,
  units,
  difficulty,
}: {
  spine: [number, number][];
  spineElevM: number[];
  units: Units;
  difficulty: TrailDifficulty;
}) {
  const ready = spineElevM.length === spine.length && spineElevM.length >= 2;
  if (!ready) {
    return <div className="trail-profile trail-profile-empty">Sampling elevation profile…</div>;
  }

  // Cumulative horizontal distance to each station.
  const dist: number[] = [0];
  for (let i = 1; i < spine.length; i++) {
    dist.push(dist[i - 1] + haversineMeters(spine[i - 1], spine[i]));
  }
  const total = dist[dist.length - 1] || 1;
  const maxE = Math.max(...spineElevM);
  const minE = Math.min(...spineElevM);
  const span = maxE - minE || 1;

  const px = (i: number) => (dist[i] / total) * VB_W;
  // 4 px top/bottom padding so the curve never clips the stroke.
  const py = (e: number) => 4 + (1 - (e - minE) / span) * (VB_H - 8);

  const top = spineElevM.map((e, i) => `${px(i).toFixed(1)},${py(e).toFixed(1)}`).join(' ');
  const area = `0,${VB_H} ${top} ${VB_W},${VB_H}`;
  const color = DIFFICULTY_COLORS[difficulty];

  return (
    <div className="trail-profile">
      <svg
        className="trail-profile-svg"
        viewBox={`0 0 ${VB_W} ${VB_H}`}
        preserveAspectRatio="none"
        role="img"
        aria-label="Run elevation profile"
      >
        <polygon points={area} fill={color} fillOpacity={0.18} />
        <polyline points={top} fill="none" stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" />
      </svg>
      <div className="trail-profile-axis">
        <span>{fmtElev(maxE, units)}</span>
        <span className="trail-profile-axis-base">{fmtElev(minE, units)}</span>
      </div>
    </div>
  );
}
