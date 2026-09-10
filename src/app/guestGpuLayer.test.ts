import { describe, expect, it } from 'vitest';
import { GUEST_DOT_DIAMETER_CSS_PX, GUEST_GPU_BYTES_PER_GUEST, GuestGpuLayer, guestGpuFrameVertexData,
  guestGpuVertexData, guestLayerProjectionMatrix, interpolatedMotionPosition } from './guestGpuLayer';
import type { PreparedRoute } from '../dualClock/geometry';
import type { CustomRenderMethodInput } from 'maplibre-gl';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';

function frame(progress: number, statusFlags = 64): GuestSimulationRenderFrame {
  return { ids: new Uint32Array([1]), guestIds: new Uint32Array([1]), edgeIndices: new Int32Array([0]),
    progress: new Float32Array([progress]), statusFlags: new Uint32Array([statusFlags]),
    bytesPerGuest: 16, byteLength: 16 };
}

describe('guest GPU layer data', () => {
  it('uses nine CSS pixels while keeping the existing hit radius contract', () => {
    expect(GUEST_DOT_DIAMETER_CSS_PX).toBe(9);
  });

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

  it('uses MapLibre’s normalized-Mercator custom-layer matrix instead of its world-pixel model matrix', () => {
    const layer = new GuestGpuLayer('guest-projection');
    layer.setPoints([], [{ id: 'fixture-base', lng: -121.495, lat: 46.902, status: 'lift-queue' }], 0);
    const normalizedMercatorMatrix = new Float32Array([
      1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
    ]);
    const worldPixelModelMatrix = new Float32Array([
      3, 0, 0, 0, 0, -2.9544, -0.178, -0.1736, 0, 0.0068, -0.0132, -0.0129,
      -786432, 774484, 47414, 46270,
    ]);
    const options = { modelViewProjectionMatrix: worldPixelModelMatrix,
      defaultProjectionData: { mainMatrix: normalizedMercatorMatrix } } as unknown as CustomRenderMethodInput;

    expect(guestLayerProjectionMatrix(options)).toBe(normalizedMercatorMatrix);
    layer.updateScreenHitIndex(worldPixelModelMatrix, 100, 100, 1);
    expect(layer.hitTest({ x: 50, y: 50 })).toBeNull();
    layer.updateScreenHitIndex(guestLayerProjectionMatrix(options), 100, 100, 1);
    expect(layer.hitTest({ x: 58, y: 32 }, 3)?.id).toBe('fixture-base');
  });

  it('interpolates dual movement on the saved bend and holds authoritative coordinates when continuity is missing', () => {
    const route: PreparedRoute = { lanes: [[[0, 0], [1, 0], [1, 1]]], distances: Float64Array.of(0, 1, 2), length: 2 };
    const routes = { 'trail@1': route };
    const previous = { id: 'guest', lng: 99, lat: 99, status: 'skiing',
      motion: { routeId: 'trail@1', lane: 0, progress: 0.25, duration: 10 } };
    const next = { id: 'guest', lng: 88, lat: 88, status: 'skiing',
      motion: { routeId: 'trail@1', lane: 0, progress: 0.75, duration: 10 } };
    expect(interpolatedMotionPosition(previous, next, routes, 0.5)).toEqual([1, 0]);
    expect(interpolatedMotionPosition(previous, { ...next, motion: { ...next.motion, routeId: 'missing' } }, routes, 0.5))
      .toEqual([88, 88]);
  });
});
