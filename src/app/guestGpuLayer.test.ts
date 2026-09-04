import { describe, expect, it } from 'vitest';
import { GUEST_GPU_BYTES_PER_GUEST, GuestGpuLayer, guestGpuFrameVertexData, guestGpuVertexData } from './guestGpuLayer';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';

function frame(progress: number, statusFlags = 64): GuestSimulationRenderFrame {
  return { ids: new Uint32Array([1]), guestIds: new Uint32Array([1]), edgeIndices: new Int32Array([0]),
    progress: new Float32Array([progress]), statusFlags: new Uint32Array([statusFlags]),
    bytesPerGuest: 16, byteLength: 16 };
}

describe('guest GPU layer data', () => {
  it('uses no more than the 24-byte per guest budget', () => {
    expect(GUEST_GPU_BYTES_PER_GUEST).toBe(20);
    const data = guestGpuVertexData([], [{ id: 'g1', lng: -110, lat: 43, status: 'skiing' }]);
    expect(data.byteLength).toBeLessThanOrEqual(24);
  });

  it('retains separate previous and next positions for interpolation', () => {
    const data = guestGpuVertexData([{ id: 'g1', lng: -110, lat: 43, status: 'walking' }],
      [{ id: 'g1', lng: -109.9, lat: 43.1, status: 'skiing' }]);
    expect(data[0]).not.toBe(data[2]);
    expect(data[1]).not.toBe(data[3]);
    expect(data[4]).toBe(2);
  });

  it('projects compact typed-array frames without allocating guest point objects', () => {
    const data = guestGpuFrameVertexData(frame(0), frame(1), [[[-110, 43], [-109, 44]]]);
    expect(data).toHaveLength(5);
    expect(data[0]).not.toBe(data[2]);
    expect(data[1]).not.toBe(data[3]);
    expect(data[4]).toBe(2);
  });

  it('hits the interpolated GPU position through a screen-space grid', () => {
    const layer = new GuestGpuLayer('guest-test');
    layer.setPoints([{ id: 'guest-a', lng: -100, lat: 0, status: 'skiing' }],
      [{ id: 'guest-a', lng: 100, lat: 0, status: 'skiing' }], 50);
    layer.updateScreenHitIndex(new Float32Array([
      1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
    ]), 100, 100, 0.5);
    expect(layer.hitTest({ x: 75, y: 25 }, 1)?.id).toBe('guest-a');
    expect(layer.hitTest({ x: 40, y: 25 }, 1)).toBeNull();
  });
});
