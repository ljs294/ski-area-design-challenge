import type { TerrainRecord } from '../types/terrain';
import { renderProfileFor, type RenderQuality } from './renderProfile';
import type { RasterTerrainRecord, ResortTileKind } from './resortTileEngine';

interface Pending {
  id: number;
  generation: number;
  kind: ResortTileKind;
  z: number;
  x: number;
  y: number;
  priority: 'visible' | 'warm';
  resolve(data: ArrayBuffer): void;
  reject(error: Error): void;
}

interface WorkerSlot { worker: Worker; busy: boolean; taskId: number | null }

function runtimeRecord(record: TerrainRecord): RasterTerrainRecord {
  if (!record.bounds) throw new Error('Terrain bounds are unavailable.');
  return {
    key: record.key,
    bounds: record.bounds,
    sampleGridSize: record.sampleGridSize,
    sampleHeights: Float32Array.from(record.sampleHeights),
    surround: record.surround ? {
      bounds: record.surround.bounds,
      width: record.surround.width,
      height: record.surround.height,
      heights: Float32Array.from(record.surround.heights),
    } : undefined,
    coverGrid: record.coverGrid ? {
      bounds: record.coverGrid.bounds,
      width: record.coverGrid.width,
      height: record.coverGrid.height,
      data: Uint8Array.from(record.coverGrid.data),
    } : undefined,
  };
}

function transfers(record: RasterTerrainRecord): Transferable[] {
  return [record.sampleHeights.buffer,
    ...(record.surround ? [record.surround.heights.buffer] : []),
    ...(record.coverGrid ? [record.coverGrid.data.buffer] : [])];
}

export class ResortTileWorkerPool {
  private slots: WorkerSlot[] = [];
  private visible: Pending[] = [];
  private warm: Pending[] = [];
  private pending = new Map<number, Pending>();
  private generation = 0;
  private requestId = 0;
  private signature = '';

  supported(): boolean {
    return typeof Worker !== 'undefined' && typeof OffscreenCanvas !== 'undefined';
  }

  configure(record: TerrainRecord | null, quality: RenderQuality): void {
    const count = renderProfileFor(quality).tileWorkerCount;
    const signature = record
      ? `${record.key}:${record.packageManifest?.elevationChecksum ?? record.updatedAt}:${quality}:${count}`
      : `none:${quality}`;
    if (signature === this.signature) return;
    this.stop(new Error('Terrain tile generation changed.'));
    this.signature = signature;
    if (!record || !this.supported()) return;
    this.generation += 1;
    for (let index = 0; index < count; index += 1) {
      const worker = new Worker(new URL('./resortTile.worker.ts', import.meta.url), { type: 'module' });
      const slot: WorkerSlot = { worker, busy: false, taskId: null };
      worker.onmessage = (event: MessageEvent<{
        id: number; generation: number; ok: boolean; data?: ArrayBuffer; error?: string;
      }>) => this.complete(slot, event.data);
      worker.onerror = () => this.failSlot(slot, new Error('Terrain tile worker stopped unexpectedly.'));
      const initialized = runtimeRecord(record);
      worker.postMessage({ type: 'init', generation: this.generation, record: initialized }, transfers(initialized));
      this.slots.push(slot);
    }
  }

  render(
    kind: ResortTileKind,
    z: number,
    x: number,
    y: number,
    priority: 'visible' | 'warm',
  ): Promise<ArrayBuffer> | null {
    if (!this.slots.length) return null;
    return new Promise<ArrayBuffer>((resolve, reject) => {
      const task: Pending = {
        id: ++this.requestId, generation: this.generation, kind, z, x, y,
        priority, resolve, reject,
      };
      this.pending.set(task.id, task);
      (priority === 'visible' ? this.visible : this.warm).push(task);
      this.pump();
    });
  }

  stop(reason = new Error('Terrain tile workers stopped.')): void {
    for (const slot of this.slots) slot.worker.terminate();
    this.slots = [];
    for (const task of this.pending.values()) task.reject(reason);
    this.pending.clear();
    this.visible = [];
    this.warm = [];
  }

  private pump(): void {
    for (const slot of this.slots) {
      if (slot.busy) continue;
      const task = this.visible.shift() ?? this.warm.shift();
      if (!task) return;
      slot.busy = true;
      slot.taskId = task.id;
      slot.worker.postMessage({
        type: 'render', id: task.id, generation: task.generation,
        kind: task.kind, z: task.z, x: task.x, y: task.y,
      });
    }
  }

  private complete(slot: WorkerSlot, response: {
    id: number; generation: number; ok: boolean; data?: ArrayBuffer; error?: string;
  }): void {
    slot.busy = false;
    slot.taskId = null;
    const task = this.pending.get(response.id);
    if (task) {
      this.pending.delete(response.id);
      if (response.generation !== this.generation) task.reject(new Error('Stale terrain tile result.'));
      else if (response.ok && response.data) task.resolve(response.data);
      else task.reject(new Error(response.error ?? 'Terrain tile generation failed.'));
    }
    this.pump();
  }

  private failSlot(slot: WorkerSlot, error: Error): void {
    slot.worker.terminate();
    if (slot.taskId != null) {
      const task = this.pending.get(slot.taskId);
      this.pending.delete(slot.taskId);
      task?.reject(error);
    }
    this.slots = this.slots.filter((candidate) => candidate !== slot);
    if (!this.slots.length) this.stop(error);
    else this.pump();
  }
}
