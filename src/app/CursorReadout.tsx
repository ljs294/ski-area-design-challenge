import { useSyncExternalStore } from 'react';
import type { OverlayId } from './Legend';
import type { Units } from './SettingsContext';

const M_TO_FT = 3.28084;

export interface Readout {
  elevationM: number;
  overlay: OverlayId | null;
  slopeDeg: number;
  aspectCompass: string;
  coverLabel: string | null;
  snowDepthM?: number;
  snowSurface?: number;
}

function displayKey(readout: Readout | null): string {
  if (!readout) return 'none';
  return [
    Math.round(readout.elevationM),
    readout.overlay ?? '',
    Math.round(readout.slopeDeg),
    readout.aspectCompass,
    readout.coverLabel ?? '',
    readout.snowDepthM == null ? '' : Math.round(readout.snowDepthM * 100),
    readout.snowSurface ?? '',
  ].join('|');
}

export class CursorReadoutStore {
  private value: Readout | null = null;
  private key = displayKey(null);
  private readonly listeners = new Set<() => void>();

  getSnapshot = (): Readout | null => this.value;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  set(next: Readout | null): void {
    const key = displayKey(next);
    if (key === this.key) return;
    this.key = key;
    this.value = next;
    for (const listener of this.listeners) listener();
  }
}

export function useCursorReadout(store: CursorReadoutStore): Readout | null {
  return useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
}

/** Lower-left readout: elevation always; active-overlay stat when one is on. */
export function CursorReadout({ readout, units }: { readout: Readout | null; units: Units }) {
  if (!readout) return null;

  const elev =
    units === 'imperial'
      ? `${Math.round(readout.elevationM * M_TO_FT).toLocaleString()} ft`
      : `${Math.round(readout.elevationM).toLocaleString()} m`;

  let stat: { label: string; value: string } | null = null;
  if (readout.overlay === 'slope') stat = { label: 'Slope', value: `${Math.round(readout.slopeDeg)}°` };
  else if (readout.overlay === 'aspect') stat = { label: 'Exposure', value: readout.aspectCompass };
  else if (readout.overlay === 'groundcover') stat = { label: 'Cover', value: readout.coverLabel ?? '—' };

  return (
    <div className="cursor-readout">
      <div className="readout-line">
        <span className="readout-label">Elevation</span>
        <span className="readout-value">{elev}</span>
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

export function CursorReadoutSubscriber({ store, units }: {
  store: CursorReadoutStore;
  units: Units;
}) {
  return <CursorReadout readout={useCursorReadout(store)} units={units} />;
}
