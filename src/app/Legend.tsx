import { SLOPE_LEGEND, ASPECT_LEGEND } from './terrainProtocols';
import { COVER_BUCKETS, COVER_LABELS, type CoverBucket } from './worldcoverProtocol';

export type OverlayId = 'slope' | 'aspect' | 'groundcover';

const COVER_LEGEND = (Object.keys(COVER_BUCKETS) as CoverBucket[]).map((b) => ({
  label: COVER_LABELS[b],
  color: `rgb(${COVER_BUCKETS[b][0]}, ${COVER_BUCKETS[b][1]}, ${COVER_BUCKETS[b][2]})`,
}));

const LEGENDS: Record<OverlayId, { title: string; rows: { label: string; color: string }[] }> = {
  slope: { title: 'Slope angle', rows: SLOPE_LEGEND },
  aspect: { title: 'Exposure', rows: ASPECT_LEGEND },
  groundcover: { title: 'Ground cover', rows: COVER_LEGEND },
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
