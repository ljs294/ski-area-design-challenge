import { haversineMeters } from '../geo';
import { DIFFICULTY_COLORS } from '../trails';
import type { SavedTrailPart, TrailDifficulty } from '../types';
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
  parts,
  units,
  difficulty,
}: {
  parts: SavedTrailPart[];
  units: Units;
  difficulty: TrailDifficulty;
}) {
  const readyParts = parts.filter((p) => p.centerline.length >= 2 && p.centerlineElevM.length === p.centerline.length);
  const ready = readyParts.length === parts.length && readyParts.length > 0;
  if (!ready) {
    return <div className="trail-profile trail-profile-empty">Sampling elevation profile…</div>;
  }

  const elevations = readyParts.flatMap((p) => p.centerlineElevM);
  const lengths = readyParts.map((part) => {
    let length = 0;
    for (let i = 1; i < part.centerline.length; i++) length += haversineMeters(part.centerline[i - 1], part.centerline[i]);
    return length;
  });
  const gap = readyParts.length > 1 ? VB_W * 0.025 : 0;
  const usable = VB_W - gap * (readyParts.length - 1);
  const total = lengths.reduce((a, b) => a + b, 0) || 1;
  const maxE = Math.max(...elevations);
  const minE = Math.min(...elevations);
  const span = maxE - minE || 1;

  // 4 px top/bottom padding so the curve never clips the stroke.
  const py = (e: number) => 4 + (1 - (e - minE) / span) * (VB_H - 8);
  const color = DIFFICULTY_COLORS[difficulty];
  let offset = 0;
  const profiles = readyParts.map((part, partIndex) => {
    const dist = [0];
    for (let i = 1; i < part.centerline.length; i++) dist.push(dist[i - 1] + haversineMeters(part.centerline[i - 1], part.centerline[i]));
    const width = usable * lengths[partIndex] / total;
    const top = part.centerlineElevM.map((e, i) => `${(offset + width * dist[i] / (lengths[partIndex] || 1)).toFixed(1)},${py(e).toFixed(1)}`).join(' ');
    const area = `${offset},${VB_H} ${top} ${offset + width},${VB_H}`;
    offset += width + gap;
    return { top, area };
  });

  return (
    <div className="trail-profile">
      <svg
        className="trail-profile-svg"
        viewBox={`0 0 ${VB_W} ${VB_H}`}
        preserveAspectRatio="none"
        role="img"
        aria-label="Run elevation profile"
      >
        {profiles.map((profile, i) => <g key={i}><polygon points={profile.area} fill={color} fillOpacity={0.18} />
          <polyline points={profile.top} fill="none" stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" /></g>)}
      </svg>
      <div className="trail-profile-axis">
        <span>{fmtElev(maxE, units)}</span>
        <span className="trail-profile-axis-base">{fmtElev(minE, units)}</span>
      </div>
    </div>
  );
}
