import { SLOPE_LEGEND, ASPECT_LEGEND } from './terrainProtocols';

export type OverlayId = 'slope' | 'aspect' | 'groundcover';

// Water is not part of the ground-cover overlay — it renders from the dedicated
// Water layer/toggle — so it is intentionally absent here.
const COVER_LEGEND = [
  { label: 'Forest', color: 'rgb(82, 105, 82)' },
  { label: 'Alpine', color: 'rgb(215, 216, 207)' },
  { label: 'Grassland', color: 'rgb(177, 183, 145)' },
];

const LEGENDS: Record<OverlayId, { title: string; rows: { label: string; color: string }[] }> = {
  slope: { title: 'Slope angle', rows: SLOPE_LEGEND },
  aspect: { title: 'Exposure', rows: ASPECT_LEGEND },
  groundcover: { title: 'Detailed terrain cover', rows: COVER_LEGEND },
};

/** Legend for the currently active overlay only; nothing when none is active. */
export function Legend({ overlay }: { overlay: OverlayId | null }) {
  if (!overlay) return null;
  const { title, rows } = LEGENDS[overlay];
  return (
    <div className="legend">
      <div className="legend-title">{title}</div>
      {rows.map((r) => (
        <div key={r.label} className="legend-row">
          <span className="legend-swatch" style={{ background: r.color }} />
          <span>{r.label}</span>
        </div>
      ))}
    </div>
  );
}
