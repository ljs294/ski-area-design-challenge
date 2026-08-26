import { describe, expect, it, vi } from 'vitest';
import { CursorReadoutStore } from './CursorReadout';

describe('CursorReadoutStore', () => {
  it('publishes only display-relevant rounded changes', () => {
    const store = new CursorReadoutStore();
    const listener = vi.fn();
    store.subscribe(listener);
    const base = {
      elevationM: 1000.1,
      overlay: null,
      slopeDeg: 12.1,
      aspectCompass: 'N',
      coverLabel: null,
    };
    store.set(base);
    store.set({ ...base, elevationM: 1000.2, slopeDeg: 12.2 });
    expect(listener).toHaveBeenCalledOnce();
    store.set({ ...base, elevationM: 1001 });
    expect(listener).toHaveBeenCalledTimes(2);
  });
});
