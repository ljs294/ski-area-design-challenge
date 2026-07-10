import type { SiteBox } from './sitePicker';

export type SiteMode = 'explore' | 'selecting' | 'locked';

export function SiteControl({
  mode,
  box,
  onStart,
  onConfirm,
  onCancel,
  onExit,
}: {
  mode: SiteMode;
  box: SiteBox | null;
  onStart: () => void;
  onConfirm: () => void;
  onCancel: () => void;
  onExit: () => void;
}) {
  const dims = box ? `${box.widthKm.toFixed(1)} × ${box.heightKm.toFixed(1)} km · ${box.areaKm2.toFixed(1)} km²` : null;

  if (mode === 'explore') {
    return (
      <div className="site-control">
        <button className="site-btn site-btn-primary" onClick={onStart}>
          ◱ Select site
        </button>
      </div>
    );
  }

  if (mode === 'selecting') {
    return (
      <div className="site-control site-control-wide">
        {dims ? (
          <div className="site-dims">{dims}</div>
        ) : (
          <div className="site-hint">Drag on the map to draw a site (2–10 km per side)</div>
        )}
        <div className="site-actions">
          <button className="site-btn site-btn-primary" onClick={onConfirm} disabled={!box}>
            View this area
          </button>
          <button className="site-btn" onClick={onCancel}>
            Cancel
          </button>
        </div>
      </div>
    );
  }

  // locked
  return (
    <div className="site-control site-control-wide">
      <div className="site-dims">Site · {dims}</div>
      <button className="site-btn" onClick={onExit}>
        ✕ Exit site
      </button>
    </div>
  );
}
