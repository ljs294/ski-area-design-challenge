import { describe, expect, it } from 'vitest';

import {
  STANDARD_POPULATIONS,
  buildCompactPointBuffer,
  buildGeoJsonSnapshot,
  checksumCompactPointBuffer,
  checksumGeoJsonSnapshot,
  chunkedGuestEventsByteLength,
  createChunkedGuestEvents,
  createGuestEventRecords,
  createIndexedGraph,
  createPointWorkload,
  createRouteQueries,
  findIndexedRoute,
  runChunkedSoAKernel,
  runCompactPointBufferKernel,
  runDirectMappedRouteCacheKernel,
  runGeoJsonSnapshotKernel,
  runLruRouteCacheKernel,
  runObjectRecordKernel,
  runStableBinaryHeapKernel,
  runTimingWheelKernel,
} from './benchmarkKernels';

describe('guest simulation benchmark kernels', () => {
  it('keeps the object-record and chunked SoA event kernels equivalent', () => {
    const records = createGuestEventRecords(96, 17);
    const chunked = createChunkedGuestEvents(records, 31);

    expect(createGuestEventRecords(96, 17)).toEqual(records);
    expect(chunked.chunks.map((chunk) => chunk.length)).toEqual([31, 31, 31, 3]);
    expect(chunkedGuestEventsByteLength(chunked)).toBe(96 * 19);
    expect(runChunkedSoAKernel(chunked, 3)).toEqual(runObjectRecordKernel(records, 3));
    expect(() => runChunkedSoAKernel({ ...chunked, length: chunked.length - 1 })).toThrow(RangeError);
  });

  it('keeps stable heap and timing-wheel scheduling order equivalent', () => {
    const events = [
      { dueTick: 4, sequence: 2, guestId: 20, routeId: 3 },
      { dueTick: 1, sequence: 1, guestId: 10, routeId: 2 },
      { dueTick: 4, sequence: 0, guestId: 21, routeId: 3 },
      { dueTick: 257, sequence: 3, guestId: 22, routeId: 4 },
      { dueTick: 1, sequence: 4, guestId: 11, routeId: 2 },
    ];

    expect(runTimingWheelKernel(events, 16)).toEqual(runStableBinaryHeapKernel(events));
  });

  it('keeps bounded route-cache strategies semantically equivalent', () => {
    const graph = createIndexedGraph(48, 22);
    const queries = createRouteQueries(240, graph.nodeCount, 23);
    const lru = runLruRouteCacheKernel(graph, queries, 12);
    const direct = runDirectMappedRouteCacheKernel(graph, queries, 12);

    expect(lru.checksum).toBe(direct.checksum);
    expect(lru.operations).toBe(queries.length);
    expect(lru.hits + lru.misses).toBe(queries.length);
    expect(direct.hits + direct.misses).toBe(queries.length);
    expect(lru.capacity).toBe(12);
    expect(direct.capacity).toBe(12);
    expect(findIndexedRoute(graph, 7, 7)).toEqual(Int32Array.of(7));
  });

  it('keeps GeoJSON and compact point-buffer snapshots equivalent', () => {
    const points = createPointWorkload(128, 29);
    const geoJson = buildGeoJsonSnapshot(points);
    const compact = buildCompactPointBuffer(points);

    expect(compact.bytesPerGuest).toBe(16);
    expect(compact.ids.byteLength + compact.positions.byteLength).toBe(points.length * 16);
    expect(checksumGeoJsonSnapshot(geoJson)).toBe(checksumCompactPointBuffer(compact));
    expect(runGeoJsonSnapshotKernel(points)).toEqual(runCompactPointBufferKernel(points));
    expect(createPointWorkload(128, 29)).toEqual(points);
    const precise = [{ id: 7, x: 999.9999, y: -123.456789, elevation: 2_345.6789 }];
    expect(checksumGeoJsonSnapshot(buildGeoJsonSnapshot(precise)))
      .toBe(checksumCompactPointBuffer(buildCompactPointBuffer(precise)));
  });

  it('advertises standard fixtures without making the largest fixture implicit', () => {
    expect(STANDARD_POPULATIONS).toEqual([1_000, 10_000, 25_000, 50_000]);
  });
});
