import type { OverlayId } from './Legend';

export interface Readout {
  elevationFt: number;
  overlay: OverlayId | null;
  slopeDeg: number;
  aspectCompass: string;
  coverLabel: string | null;
}

/** Lower-left readout: elevation always; active-overlay stat when one is on. */
export function CursorReadout({ readout }: { readout: Readout | null }) {
  if (!readout) return null;
  const ft = Math.round(readout.elevationFt).toLocaleString();

  let stat: { label: string; value: string } | null = null;
  if (readout.overlay === 'slope') stat = { label: 'Slope', value: `${Math.round(readout.slopeDeg)}°` };
  else if (readout.overlay === 'aspect') stat = { label: 'Exposure', value: readout.aspectCompass };
  else if (readout.overlay === 'groundcover') stat = { label: 'Cover', value: readout.coverLabel ?? '—' };

  return (
    <div className="cursor-readout">
      <div className="readout-line">
        <span className="readout-label">Elevation</span>
        <span className="readout-value">{ft} ft</span>
      </div>
      {stat && (
        <div className="readout-line">
          <span className="readout-label">{stat.label}</span>
          <span className="readout-value">{stat.value}</span>
        </div>
      )}
    </div>
  );
}
