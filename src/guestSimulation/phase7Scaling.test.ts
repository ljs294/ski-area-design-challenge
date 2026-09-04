import { describe, expect, it } from 'vitest';

import { createIndexedGraph, findIndexedRoute } from './benchmarkKernels.ts';
import {
  checksumGuestPublication,
  fromGuestPublicationEnvelope,
  guestPublicationTransferables,
  packGuestPublication,
  readGuestPublicationRow,
  toGuestPublicationEnvelope,
} from './phase7Publication.ts';
import { IntrusiveFifoQueue } from './phase7Queue.ts';
import { DeterministicRouteCache } from './phase7RouteCache.ts';
import {
  DEFAULT_GUEST_PUBLICATION_POLICY,
  GuestPublicationBacklog,
  PHASE7_DEGRADATION_ORDER,
  decideGuestPublication,
  selectGuestPublicationRows,
} from './phase7Policy.ts';
import {
  PHASE7_STANDARD_POPULATIONS,
  createPhase7PublicationRows,
  runPhase7ScalingKernels,
} from './phase7Benchmark.ts';

describe('Phase 7 compact publication slabs', () => {
  it('round-trips deterministic typed columns through a transferable envelope', () => {
    const rows = [
      { guestId: 7, x: 1.25, y: -2.5, elevation: 1_234.5, statusCode: 3, satisfaction: 201, flags: 4 },
      { guestId: 9, x: -4, y: 5, elevation: 90, statusCode: 1, satisfaction: 12 },
    ];
    const metadata = { tick: 42, sequence: 8, environmentRevision: 2, topologyRevision: 5 };
    const first = packGuestPublication(rows, metadata);
    const second = packGuestPublication(rows, metadata);
    expect(first.byteLength).toBe(rows.length * 19);
    expect(checksumGuestPublication(first)).toBe(checksumGuestPublication(second));
    expect(guestPublicationTransferables(first)).toEqual([first.buffer]);
    const restored = fromGuestPublicationEnvelope(toGuestPublicationEnvelope(first));
    expect(restored.tick).toBe(42);
    expect(restored.sequence).toBe(8);
    expect(checksumGuestPublication(restored)).toBe(checksumGuestPublication(first));
    expect(readGuestPublicationRow(restored, 1)).toEqual({
      guestId: 9, x: -4, y: 5, elevation: 90, statusCode: 1, satisfaction: 12, flags: 0,
    });
    expect(() => packGuestPublication([{ ...rows[0], guestId: 7 }, { ...rows[1], guestId: 7 }], metadata))
      .toThrow(RangeError);
    expect(() => fromGuestPublicationEnvelope({ ...toGuestPublicationEnvelope(first), length: 3 }))
      .toThrow(RangeError);
  });
});

describe('Phase 7 intrusive queue', () => {
  it('preserves FIFO order and removes arbitrary nodes in O(1)', () => {
    const queue = new IntrusiveFifoQueue(5);
    queue.enqueue(2);
    queue.enqueue(4);
    queue.enqueue(1);
    queue.assertConsistent();
    expect(queue.remove(4)).toBe(true);
    expect(queue.remove(4)).toBe(false);
    expect(queue.head).toBe(2);
    expect(queue.tail).toBe(1);
    expect(queue.dequeue()).toBe(2);
    expect(queue.dequeue()).toBe(1);
    expect(queue.dequeue()).toBeUndefined();
    queue.assertConsistent();
    expect(queue.tryEnqueue(0)).toBe(true);
    expect(queue.tryEnqueue(0)).toBe(false);
    expect(queue.size).toBe(1);
  });

  it('bounds producers and conserves every accepted node exactly once', () => {
    const queue = new IntrusiveFifoQueue(3);
    expect(queue.tryEnqueue(0)).toBe(true);
    expect(queue.tryEnqueue(1)).toBe(true);
    expect(queue.tryEnqueue(2)).toBe(true);
    expect(queue.tryEnqueue(1)).toBe(false);
    const drained: number[] = [];
    expect(queue.drain((node) => drained.push(node))).toBe(3);
    expect(drained).toEqual([0, 1, 2]);
    expect(new Set(drained).size).toBe(3);
    expect(queue.size).toBe(0);
  });
});

describe('Phase 7 bounded route cache', () => {
  it('is deterministic, revision-aware, and never exceeds capacity', () => {
    const graph = createIndexedGraph(32, 11);
    const cache = new DeterministicRouteCache(2, 1);
    const compute = (start: number, goal: number, topologyRevision = 1) =>
      cache.getOrCompute(start, goal, topologyRevision, () => findIndexedRoute(graph, start, goal));
    const expected = findIndexedRoute(graph, 1, 7);
    expect(compute(1, 7)).toEqual(expected);
    expect(compute(1, 7)).toEqual(expected);
    expect(compute(2, 8)).toEqual(findIndexedRoute(graph, 2, 8));
    expect(compute(3, 9)).toEqual(findIndexedRoute(graph, 3, 9));
    expect(cache.size).toBe(2);
    expect(cache.getStats().evictions).toBe(1);
    expect(cache.entries().length).toBe(2);
    expect(compute(1, 7, 2)).toEqual(expected);
    expect(cache.size).toBe(1);
    expect(cache.getStats().revisionResets).toBe(1);
    expect(cache.getStats().hits + cache.getStats().misses).toBe(5);
  });
});

describe('Phase 7 cadence, viewport, and backlog policy', () => {
  it('applies the documented degradation order and deterministic sampling', () => {
    expect(PHASE7_DEGRADATION_ORDER).toEqual([
      'cadence', 'viewport-cull', 'stable-sample', 'coalesce-latest', 'pause-backlog',
    ]);
    const rows = Array.from({ length: 12 }, (_, guestId) => ({
      guestId,
      x: guestId,
      y: 0,
      elevation: 0,
      statusCode: 0,
      satisfaction: 0,
    }));
    const first = selectGuestPublicationRows(rows, { minX: 0, minY: -1, maxX: 11, maxY: 1 }, 4, 5, 0);
    const second = selectGuestPublicationRows(rows, { minX: 0, minY: -1, maxX: 11, maxY: 1 }, 4, 5, 0);
    expect(first.stage).toBe('stable-sample');
    expect(first.selectedIndices.length).toBe(4);
    expect(Array.from(first.selectedIndices)).toEqual(Array.from(second.selectedIndices));
    expect(new Set(first.selectedIndices).size).toBe(4);
    const culled = selectGuestPublicationRows(rows, { minX: 2, minY: -1, maxX: 3, maxY: 1 }, 20, 1, 0);
    expect(culled.stage).toBe('viewport-cull');
    expect(culled.visibleCount).toBe(2);
    expect(culled.culledCount).toBe(10);
  });

  it('throttles cadence, pauses hard backlog, and coalesces latest work', () => {
    expect(decideGuestPublication({
      tick: 0, lastPublishedTick: null, pendingPublications: 0,
      runActive: true, cameraMoving: false, hasChanges: true,
    }, DEFAULT_GUEST_PUBLICATION_POLICY).shouldPublish).toBe(true);
    expect(decideGuestPublication({
      tick: 0, lastPublishedTick: 0, pendingPublications: 0,
      runActive: true, cameraMoving: false, hasChanges: true,
    }).reason).toBe('cadence');
    expect(decideGuestPublication({
      tick: 2, lastPublishedTick: 0, pendingPublications: 4,
      runActive: true, cameraMoving: false, hasChanges: true,
    }).reason).toBe('backlog-paused');
    const backlog = new GuestPublicationBacklog(1);
    expect(backlog.offer()).toBe('accepted');
    expect(backlog.offer()).toBe('paused');
    expect(backlog.complete()).toBe(true);
    expect(backlog.complete()).toBe(false);
    expect(backlog.paused).toBe(1);
  });
});

describe('Phase 7 benchmark fixtures', () => {
  it('keeps standard population sizes and small-kernel checksums deterministic', () => {
    expect(PHASE7_STANDARD_POPULATIONS).toEqual([1_000, 10_000, 25_000, 50_000]);
    const rows = createPhase7PublicationRows(32, 77);
    expect(createPhase7PublicationRows(32, 77)).toEqual(rows);
    const first = runPhase7ScalingKernels(64, 77);
    const second = runPhase7ScalingKernels(64, 77);
    expect(first).toEqual(second);
    expect(first.every((result) => result.bytes >= 0 && result.operations > 0)).toBe(true);
  });
});
