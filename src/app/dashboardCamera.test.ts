import { describe, expect, it, vi } from 'vitest';
import type maplibregl from 'maplibre-gl';
import { lockDashboardCamera } from './dashboardCamera';
describe('dashboard camera ownership', () => {
  it('locks orientation and restores handlers exactly once without restoring pan or zoom', () => {
    const toggle = (enabled: boolean) => ({ isEnabled: () => enabled, enable: vi.fn(), disable: vi.fn() });
    const map = { getPitch: () => 50, getBearing: () => 28, getMaxPitch: () => 85,
      dragRotate: toggle(true), touchPitch: toggle(false), stop: vi.fn(), setMaxPitch: vi.fn(),
      setBearing: vi.fn(), jumpTo: vi.fn(), on: vi.fn(), off: vi.fn() };
    const release = lockDashboardCamera(map as unknown as maplibregl.Map);
    expect(map.setMaxPitch).toHaveBeenCalledWith(0);
    expect(map.dragRotate.disable).toHaveBeenCalledOnce();
    release(); release();
    expect(map.jumpTo).toHaveBeenCalledExactlyOnceWith({ pitch: 50, bearing: 28 });
    expect(map.setMaxPitch).toHaveBeenLastCalledWith(85);
    expect(map.dragRotate.enable).toHaveBeenCalledOnce();
    expect(map.touchPitch.enable).not.toHaveBeenCalled();
    expect(map.off).toHaveBeenCalledOnce();
  });
});
