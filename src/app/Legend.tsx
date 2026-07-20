import { SLOPE_LEGEND, ASPECT_LEGEND } from './terrainProtocols';

export type OverlayId = 'slope' | 'aspect' | 'groundcover';

const COVER_LEGEND = [
  { label: 'Tree cover', color: 'rgb(47, 81, 53)' },
  { label: 'Shrubland', color: 'rgb(113, 128, 90)' },
  { label: 'Grassland', color: 'rgb(197, 200, 153)' },
  { label: 'Cropland', color: 'rgb(202, 184, 139)' },
  { label: 'Built-up', color: 'rgb(154, 135, 125)' },
  { label: 'Bare / sparse', color: 'rgb(157, 151, 140)' },
  { label: 'Snow and ice', color: 'rgb(237, 240, 238)' },
  { label: 'Permanent water', color: 'rgb(83, 142, 174)' },
  { label: 'Herbaceous wetland', color: 'rgb(79, 145, 137)' },
  { label: 'Mangroves', color: 'rgb(39, 105, 69)' },
  { label: 'Moss and lichen', color: 'rgb(192, 193, 153)' },
];

const LEGENDS: Record<OverlayId, { title: string; rows: { label: string; color: string }[] }> = {
  slope: { title: 'Slope angle', rows: SLOPE_LEGEND },
  aspect: { title: 'Exposure', rows: ASPECT_LEGEND },
  groundcover: { title: 'ESA WorldCover 2021 · 10 m', rows: COVER_LEGEND },
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
