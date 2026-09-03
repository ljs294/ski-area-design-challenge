import type { SnowGrid } from '../types/snow';
import type { TerrainRecord } from '../types/terrain';
import type { ResolvedWeatherHour } from '../weather/weatherModel';
import type { SnowStepRequest, SnowStepResponse } from './snowStepProtocol';

export class SnowStepClient {
  private worker: Worker | null = null;
  private rejectPending: ((reason: Error) => void) | null = null;
  private nextId = 1;

  run(terrainBinding: string, terrain: TerrainRecord, grid: SnowGrid,
    hours: readonly ResolvedWeatherHour[]): Promise<Extract<SnowStepResponse, { ok: true }>> {
    this.cancel();
    const worker = new Worker(new URL('./snowStep.worker.ts', import.meta.url), { type: 'module' });
    this.worker = worker;
    const id = this.nextId++;
    const request: SnowStepRequest = {
      id, terrainBinding, terrain,
      grid: { ...grid, depthM: new Float32Array(grid.depthM), surface: new Uint8Array(grid.surface) },
      hours,
    };
    return new Promise((resolve, reject) => {
      this.rejectPending = reject;
      const retire = () => { if (this.worker === worker) this.worker = null; worker.terminate(); };
      worker.onerror = () => { retire(); this.rejectPending = null;
        reject(new Error('Snow simulation worker crashed.')); };
      worker.onmessage = (event: MessageEvent<SnowStepResponse>) => {
        const response = event.data;
        if (response.id !== id || response.terrainBinding !== terrainBinding) return;
        retire();
        this.rejectPending = null;
        if (response.ok) resolve(response); else reject(new Error(response.error));
      };
      worker.postMessage(request, [request.grid.depthM.buffer, request.grid.surface.buffer]);
    });
  }

  cancel(): void {
    this.worker?.terminate();
    this.worker = null;
    this.rejectPending?.(new Error('Snow simulation cancelled.'));
    this.rejectPending = null;
  }
}
