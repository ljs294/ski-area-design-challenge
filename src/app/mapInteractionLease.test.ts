import { describe, expect, it, vi } from 'vitest';
import { MapInteractionLease, type InteractionToggle, type MapInteractionTarget } from './mapInteractionLease';
import type { ToolId } from './toolCoordinator';

function toggle(initial: boolean): InteractionToggle & { enable: ReturnType<typeof vi.fn>; disable: ReturnType<typeof vi.fn> } {
  let enabled = initial;
  return {
    isEnabled: () => enabled,
    enable: vi.fn(() => { enabled = true; }),
    disable: vi.fn(() => { enabled = false; }),
  };
}

function target(cursor = 'grab', dragPan = true, doubleClickZoom = true) {
  const canvas = { style: { cursor } };
  return {
    canvas,
    map: {
      getCanvas: () => canvas,
      dragPan: toggle(dragPan),
      doubleClickZoom: toggle(doubleClickZoom),
    } satisfies MapInteractionTarget,
  };
}

describe('MapInteractionLease', () => {
  it('rejects an interaction override from an inactive controller', () => {
    const lease = new MapInteractionLease(() => false);
    expect(() => lease.acquire('road', target().map, { cursor: 'crosshair' }))
      .toThrow('Inactive tool road');
  });

  it('restores the exact prior cursor and handlers once', () => {
    const { map, canvas } = target('pointer', true, false);
    const lease = new MapInteractionLease(() => true);
    const handle = lease.acquire('trail', map, {
      cursor: 'none',
      dragPanEnabled: false,
      doubleClickZoomEnabled: false,
    });

    expect(canvas.style.cursor).toBe('none');
    expect(map.dragPan.isEnabled()).toBe(false);
    expect(map.doubleClickZoom.isEnabled()).toBe(false);

    handle.release();
    handle.release();
    lease.dispose();

    expect(canvas.style.cursor).toBe('pointer');
    expect(map.dragPan.isEnabled()).toBe(true);
    expect(map.doubleClickZoom.isEnabled()).toBe(false);
    expect(map.dragPan.enable).toHaveBeenCalledTimes(1);
    expect(map.doubleClickZoom.enable).not.toHaveBeenCalled();
  });

  it('restores the previous owner before an active replacement takes the lease', () => {
    let active: ToolId = 'road';
    const { map, canvas } = target('grab', true, true);
    const lease = new MapInteractionLease((toolId) => toolId === active);
    const road = lease.acquire('road', map, { cursor: 'crosshair', doubleClickZoomEnabled: false });

    active = 'trail';
    const trail = lease.acquire('trail', map, { cursor: 'none', dragPanEnabled: false });
    expect(canvas.style.cursor).toBe('none');
    expect(map.doubleClickZoom.isEnabled()).toBe(true);
    expect(map.dragPan.isEnabled()).toBe(false);

    road.release();
    expect(canvas.style.cursor).toBe('none');
    trail.release();
    expect(canvas.style.cursor).toBe('grab');
    expect(map.dragPan.isEnabled()).toBe(true);
  });

  it('ignores release requests from a controller that does not own the lease', () => {
    const { map } = target();
    const lease = new MapInteractionLease(() => true);
    lease.acquire('dam', map, { cursor: 'crosshair' });

    expect(lease.release('pond')).toBe(false);
    expect(lease.release('dam')).toBe(true);
    expect(lease.release('dam')).toBe(false);
  });

  it('restores an active lease when the map owner unmounts', () => {
    const { map, canvas } = target('grab', true, true);
    const lease = new MapInteractionLease(() => true);
    lease.acquire('lift', map, { cursor: 'crosshair', doubleClickZoomEnabled: false });

    lease.dispose();
    lease.dispose();

    expect(canvas.style.cursor).toBe('grab');
    expect(map.doubleClickZoom.isEnabled()).toBe(true);
    expect(map.doubleClickZoom.disable).toHaveBeenCalledTimes(1);
    expect(map.doubleClickZoom.enable).toHaveBeenCalledTimes(1);
  });
});
