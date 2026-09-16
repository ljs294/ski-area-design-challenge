import { describe, expect, it } from 'vitest';
import { GUEST_DOT_DIAMETER_CSS_PX, GUEST_GPU_BYTES_PER_GUEST, GuestGpuLayer, guestGpuFrameVertexData,
  guestGpuVertexData, guestLayerProjectionMatrix, interpolatedMotionPosition,
  updateGuestTerrainElevations } from './guestGpuLayer';
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
    expect(GUEST_GPU_BYTES_PER_GUEST).toBe(24);
    const data = guestGpuVertexData([], [{ id: 'g1', lng: -110, lat: 43, status: 'skiing' }]);
    expect(data).toHaveLength(6);
    expect(data.byteLength).toBe(24);
    expect(data[5]).toBe(0);
  });

  it('retains separate previous and next positions for interpolation', () => {
    const data = guestGpuVertexData([{ id: 'g1', lng: -110, lat: 43, status: 'walking' }],
      [{ id: 'g1', lng: -109.9, lat: 43.1, status: 'skiing' }]);
    expect(data[0]).not.toBe(data[2]);
    expect(data[1]).not.toBe(data[3]);
    expect(data[4]).toBe(2);
    expect(data[5]).toBe(0);
  });

  it('projects compact typed-array frames without allocating guest point objects', () => {
    const data = guestGpuFrameVertexData(frame(0), frame(1), [[[-110, 43], [-109, 44]]]);
    expect(data).toHaveLength(6);
    expect(data[0]).not.toBe(data[2]);
    expect(data[1]).not.toBe(data[3]);
    expect(data[4]).toBe(2);
    expect(data[5]).toBe(0);
  });

  it('keeps the previous edge geometry when a compact frame crosses a topology edit', () => {
    const layer = new GuestGpuLayer('guest-geometry-edit');
    layer.setRenderFrame(frame(0), [[[0, 0], [1, 0]]], [0, 0], 50);
    layer.setRenderFrame(frame(1), [[[10, 0], [11, 0]]], [0, 0], 50);
    const pending = (layer as unknown as { pending: Float32Array }).pending;
    expect(pending[0]).toBeCloseTo(0.5);
    expect(pending[2]).toBeCloseTo((180 + 11) / 360);
  });

  it('marks a snapped compact frame for a fresh GPU upload', () => {
    const layer = new GuestGpuLayer('guest-snap');
    layer.setRenderFrame(frame(0), [], [0, 0], 50);
    (layer as unknown as { bufferDirty: boolean }).bufferDirty = false;
    layer.snapCompactFrame();
    expect((layer as unknown as { bufferDirty: boolean }).bufferDirty).toBe(true);
  });

  it('refreshes terrain elevation and uses the same elevated position for hit testing', () => {
    const layer = new GuestGpuLayer('guest-terrain');
    layer.setPoints([], [{ id: 'guest-terrain', lng: 0, lat: 0, status: 'skiing' }], 0);
    const pending = (layer as unknown as { pending: Float32Array }).pending;
    const matrix = new Float32Array([
      1, 0, 0, 0, 0, 1, 0, 0, 10_000, 0, 1, 0, 0, 0, 0, 1,
    ]);

    updateGuestTerrainElevations(pending, 1, 1, () => null);
    expect(pending[5]).toBe(0);
    layer.updateScreenHitIndex(matrix, 100, 100, 1);
    expect(layer.hitTest({ x: 75, y: 25 }, 1)?.id).toBe('guest-terrain');

    updateGuestTerrainElevations(pending, 1, 1, () => 1_000);
    const raisedX = (0.5 + 10_000 * pending[5]!) * 50 + 50;
    expect(pending[5]).toBeGreaterThan(0);
    layer.updateScreenHitIndex(matrix, 100, 100, 1);
    expect(layer.hitTest({ x: raisedX, y: 25 }, 1)?.id).toBe('guest-terrain');
    expect(layer.hitTest({ x: 75, y: 25 }, 1)).toBeNull();

    const raisedZ = pending[5]!;
    updateGuestTerrainElevations(pending, 1, 1, () => -250);
    expect(pending[5]).toBeLessThan(0);
    expect(pending[5]).not.toBe(raisedZ);
    updateGuestTerrainElevations(pending, 1, 1, () => Number.NaN);
    expect(pending[5]).toBe(0);
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

  it('keeps the prior picking index available across a frame replacement', () => {
    const layer = new GuestGpuLayer('guest-frame-replacement');
    const matrix = new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
    layer.setRenderFrame(frame(0), [], [0, 0], 0);
    layer.updateScreenHitIndex(matrix, 100, 100, 1);
    layer.setRenderFrame(frame(0.5), [], [0, 0], 0);
    expect(layer.hitTest({ x: 75, y: 25 }, 8)?.id).toBe('guest-000001');
  });

  it('skips shader-discarded guests and resolves overlapping dots by distance then id', () => {
    const layer = new GuestGpuLayer('guest-overlap');
    layer.setRenderFrame({ ids: new Uint32Array([9, 3, 5]), guestIds: new Uint32Array([9, 3, 5]),
      edgeIndices: new Int32Array([-1, -1, -1]), progress: new Float32Array([0, 0, 0]),
      statusFlags: new Uint32Array([512, 64, 64]), bytesPerGuest: 16, byteLength: 48 }, [], [0, 0], 0);
    layer.updateScreenHitIndex(new Float32Array([
      1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
    ]), 100, 100, 1);
    expect(layer.hitTest({ x: 75, y: 25 }, 1)?.id).toBe('guest-000003');
    expect(layer.hitTest({ x: 75, y: 25 }, 0)?.id).toBe('guest-000003');
    expect(layer.renderedPosition('guest-000009')).toBeNull();
    expect(layer.renderedPosition('guest-000003')).toEqual([0, 0]);
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

  it('keeps compact projection allocation bounded for the 1k, 3k, and 10k renderer fixtures', () => {
    for (const count of [1_000, 3_000, 10_000]) {
      const ids = Uint32Array.from({ length: count }, (_, index) => index + 1);
      const edges = new Int32Array(count); edges.fill(-1);
      const progress = new Float32Array(count); progress.fill(0);
      const statuses = new Uint32Array(count); statuses.fill(64);
      const next: GuestSimulationRenderFrame = { ids, guestIds: ids, edgeIndices: edges, progress,
        statusFlags: statuses, bytesPerGuest: 16, byteLength: count * 16 };
      const data = guestGpuFrameVertexData(null, next, [], [-121.5, 46.9]);
      expect(data).toHaveLength(count * 6);
      expect(data.byteLength).toBe(count * GUEST_GPU_BYTES_PER_GUEST);
      expect(data[4]).toBe(2);
    }
  });
});
