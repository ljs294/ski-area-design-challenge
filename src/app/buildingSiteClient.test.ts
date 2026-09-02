import { describe, expect, it } from 'vitest';
import type { BuildingSiteRequest, BuildingSiteResponse } from './buildingSiteProtocol';
import { BuildingSiteAdapter } from './buildingSiteClient';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<BuildingSiteRequest, BuildingSiteResponse> {
  posted: BuildingSiteRequest[];
  deliver(response: BuildingSiteResponse): void;
}

function fakeFactory(workers: FakeWorker[]): () => FakeWorker {
  return () => {
    const worker: FakeWorker = {
      onmessage: null, onerror: null, posted: [],
      postMessage(message) { this.posted.push(message); },
      terminate() { /* the test intentionally retains the callback for late delivery */ },
      deliver(response) { this.onmessage?.({ data: response } as MessageEvent<BuildingSiteResponse>); },
    };
    workers.push(worker);
    return worker;
  };
}

function request(key: string): Omit<BuildingSiteRequest, 'id'> {
  return { type: 'analyze-building-site', center: [0, 0], bearingDeg: 0,
    dimensions: { lengthM: 18, widthM: 12, eaveHeightM: 4 }, foundationMode: 'slope',
    heights: new Float32Array([100, 100, 100, 100]), gridSize: 2,
    bounds: { west: -0.01, south: -0.01, east: 0.01, north: 0.01 },
    terrainRevision: 1, elevationChecksum: 'checksum-a', geometryKey: key };
}

const result = {
  geometryKey: 'site-a', terrainRevision: 1, baseElevationChecksum: 'checksum-a',
  foundationMode: 'slope' as const, bearingDeg: 0, center: [0, 0] as [number, number],
  dimensions: { lengthM: 18, widthM: 12, eaveHeightM: 4 }, finishedFloorElevationM: 100,
  finishedFloorM: 100, perimeterSamples: [], perimeterElevationsM: [], footprintRing: [],
  padRing: [], apronRing: [], patchIndices: new Uint32Array(), patchHeights: new Float32Array(),
  contourSegments: [], editedContourSegments: [], contourGridSize: 2, contourIntervalM: 6,
  disturbancePolygons: [], earthwork: { cutM3: 0, fillM3: 0, balanceM3: 0 }, terrainGraded: false,
  pumpNodeElevationM: 100, terrainPatch: {} as never,
  foundation: { kind: 'slope' as const, mode: 'slope' as const, finishedFloorElevationM: 100,
    perimeterSamples: [], perimeterGroundElevationsM: [], terrainGraded: false,
    earthwork: { cutM3: 0, fillM3: 0, balanceM3: 0 } },
};

describe('BuildingSiteAdapter', () => {
  it('terminates superseded workers and ignores late responses', () => {
    const workers: FakeWorker[] = [];
    const adapter = new BuildingSiteAdapter(fakeFactory(workers));
    const received: string[] = [];
    adapter.run(request('site-a'), {
      onResult: () => received.push('old'), onError: () => received.push('error'),
    });
    adapter.run(request('site-b'), {
      onResult: (_, identity) => received.push(identity.geometryKey), onError: () => received.push('error'),
    });
    workers[0].deliver({ id: 1, ok: true, geometryKey: 'site-a', terrainRevision: 1,
      elevationChecksum: 'checksum-a', result });
    workers[1].deliver({ id: 2, ok: true, geometryKey: 'site-b', terrainRevision: 1,
      elevationChecksum: 'checksum-a', result: { ...result, geometryKey: 'site-b' } });
    expect(received).toEqual(['site-b']);
    expect(adapter.isPending).toBe(false);
  });

  it('cancels and invalidates a pending request', () => {
    const workers: FakeWorker[] = [];
    const adapter = new BuildingSiteAdapter(fakeFactory(workers));
    const received: string[] = [];
    adapter.run(request('site-a'), {
      onResult: () => received.push('result'), onError: () => received.push('error'),
    });
    adapter.cancel();
    workers[0].deliver({ id: 1, ok: true, geometryKey: 'site-a', terrainRevision: 1,
      elevationChecksum: 'checksum-a', result });
    expect(received).toEqual([]);
    expect(adapter.isPending).toBe(false);
  });
});
