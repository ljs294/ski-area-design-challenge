import type { SnowGrid } from '../types/snow';
import type { RenderQuality } from './renderProfile';
import { renderProfileFor } from './renderProfile';
import type { SnowDisplayMode } from './snowStyle';

interface Pending { resolve(value: ArrayBuffer): void; reject(error: Error): void }
interface Slot { worker: Worker; pending: Map<number, Pending> }

export class SnowTileWorkerClient {
  private slots: Slot[] = [];
  private generation = 0;
  private nextTaskId = 1;
  private nextSlot = 0;
  private grid: SnowGrid | null = null;
  private quality: RenderQuality = 'standard';

  get supported(): boolean {
    return typeof Worker !== 'undefined' && typeof OffscreenCanvas !== 'undefined';
  }

  configure(grid: SnowGrid | null, quality: RenderQuality): void {
    if (this.grid === grid && this.quality === quality) return;
    this.stop();
    this.grid = grid;
    this.quality = quality;
    this.generation++;
    if (!grid || !this.supported) return;
    for (let index = 0; index < renderProfileFor(quality).tileWorkerCount; index++) {
      const worker = new Worker(new URL('./snowTile.worker.ts', import.meta.url), { type: 'module' });
      const slot: Slot = { worker, pending: new Map() };
      worker.onmessage = (event: MessageEvent<{ type: 'result' | 'error'; generation: number;
        taskId: number; data?: ArrayBuffer; error?: string }>) => {
        const response = event.data;
        const pending = slot.pending.get(response.taskId);
        if (!pending || response.generation !== this.generation) return;
        slot.pending.delete(response.taskId);
        if (response.type === 'result' && response.data) pending.resolve(response.data);
        else pending.reject(new Error(response.error ?? 'Snow tile worker failed'));
      };
      worker.onerror = () => {
        for (const pending of slot.pending.values()) pending.reject(new Error('Snow tile worker crashed'));
        slot.pending.clear();
      };
      const depthM = grid.depthM.slice(), surface = grid.surface.slice();
      worker.postMessage({ type: 'init', generation: this.generation,
        grid: { ...grid, depthM, surface } }, [depthM.buffer, surface.buffer]);
      this.slots.push(slot);
    }
  }

  render(z: number, x: number, y: number, mode: SnowDisplayMode): Promise<ArrayBuffer> {
    const slot = this.slots[this.nextSlot++ % this.slots.length];
    if (!slot) return Promise.reject(new Error('Snow tile workers are unavailable'));
    const taskId = this.nextTaskId++;
    return new Promise((resolve, reject) => {
      slot.pending.set(taskId, { resolve, reject });
      slot.worker.postMessage({ type: 'render', generation: this.generation, taskId, z, x, y, mode });
    });
  }

  stop(): void {
    for (const slot of this.slots) {
      slot.worker.terminate();
      for (const pending of slot.pending.values()) pending.reject(new Error('Snow tile generation changed'));
    }
    this.slots = [];
  }
}
