import type { PondEarthworkDesign } from '../pondEarthwork';
import type { EarthworkTerrainPatch } from '../earthwork';
import type { TerrainRecord } from '../types/terrain';
import type { PondEarthworkRequest, PondEarthworkResponse } from './pondEarthworkProtocol';

export interface PondEarthworkResult {
  design: PondEarthworkDesign;
  grade: EarthworkTerrainPatch;
}

let nextJobId = 1;

export class PondEarthworkAdapter {
  private worker: Worker | null = null;
  private rejectPending: ((reason: Error) => void) | null = null;

  run(record: TerrainRecord, terrainRevision: number, boundary: [number, number][],
    topElevationM: number, excavationDepthM: number, poolAreaM2: number): Promise<PondEarthworkResult> {
    this.cancel();
    if (!record.bounds) return Promise.reject(new Error('The local elevation package is unavailable.'));
    const worker = new Worker(new URL('./pondEarthwork.worker.ts', import.meta.url), { type: 'module' });
    this.worker = worker;
    const jobId = nextJobId++;
    const heights = Float32Array.from(record.sampleHeights);
    const request: PondEarthworkRequest = {
      type: 'pond-earthwork', jobId, terrainRevision, profileRevision: 0,
      heights, gridSize: record.sampleGridSize, bounds: record.bounds,
      contourGridSize: record.contourMetadata?.gridSize ?? Math.min(512, record.sampleGridSize),
      contourIntervalM: record.contourMetadata?.intervalM ?? 6.096,
      baseElevationChecksum: record.packageManifest?.elevationChecksum ?? '',
      boundary, topElevationM, excavationDepthM, poolAreaM2,
    };
    return new Promise<PondEarthworkResult>((resolve, reject) => {
      this.rejectPending = reject;
      const settle = () => {
        if (this.worker === worker) this.worker = null;
        this.rejectPending = null;
        worker.terminate();
      };
      worker.onerror = (event) => { settle(); reject(new Error(event.message || 'Pond worker failed.')); };
      worker.onmessage = (event: MessageEvent<PondEarthworkResponse>) => {
        const response = event.data;
        if (response.jobId !== jobId || response.terrainRevision !== terrainRevision
          || response.profileRevision !== 0) return;
        settle();
        if (response.type === 'error') reject(new Error(response.error));
        else resolve({ design: response.design, grade: response.grade });
      };
      worker.postMessage(request, [heights.buffer]);
    });
  }

  cancel(): void {
    this.worker?.terminate();
    this.worker = null;
    const reject = this.rejectPending;
    this.rejectPending = null;
    reject?.(new DOMException('Pond earthwork superseded', 'AbortError'));
  }
}
