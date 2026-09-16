import { afterEach, describe, expect, it, vi } from 'vitest';
import { getResortRenderStats, invalidateResortRenderQueue, isResortSceneReady,
  nextResortSceneDrawCount, requestResortTile } from './resortProtocols';

describe('resort loading readiness', () => {
  const ready = {
    generation: 4,
    currentGeneration: 4,
    cancelled: false,
    visibleTilesLoaded: true,
    visiblePending: 0,
    mapLoaded: true,
    completedSceneDraws: 2,
  };

  it('reveals after visible loading and two scene draws, independent of warm work', () => {
    expect(isResortSceneReady({ ...ready, backgroundPending: 99 })).toBe(true);
    expect(isResortSceneReady({ ...ready, visiblePending: 1 })).toBe(false);
    expect(isResortSceneReady({ ...ready, completedSceneDraws: 1 })).toBe(false);
  });

  it('rejects cancelled and stale style generations', () => {
    expect(isResortSceneReady({ ...ready, cancelled: true })).toBe(false);
    expect(isResortSceneReady({ ...ready, generation: 3 })).toBe(false);
  });

  it('resets the consecutive scene draw count when a prerequisite goes false', () => {
    expect(nextResortSceneDrawCount(2, false)).toBe(0);
    expect(nextResortSceneDrawCount(0, true)).toBe(1);
  });
});

describe('resort render queue priority', () => {
  afterEach(() => {
    invalidateResortRenderQueue();
    vi.unstubAllGlobals();
  });

  it('promotes a warm request during the outer dispatch gap', async () => {
    let dispatch!: () => void;
    vi.stubGlobal('window', { setTimeout: (callback: () => void) => { dispatch = callback; return 1; } });
    const url = 'resort-dem://a/12/1/2';
    const warm = requestResortTile('dem', url, 'warm');
    expect(getResortRenderStats().visiblePending).toBe(0);
    const visible = requestResortTile('dem', url, 'visible');
    expect(visible).toBe(warm);
    expect(getResortRenderStats().visiblePending).toBe(1);
    invalidateResortRenderQueue();
    expect(getResortRenderStats().visiblePending).toBe(0);
    dispatch();
    await expect(warm).rejects.toThrow('Resort render generation changed.');
  });
});
