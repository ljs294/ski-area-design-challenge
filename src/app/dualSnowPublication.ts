import type { SnowGrid } from '../types/snow';

export interface DualSnowPatch { x: number; y: number; width: number; height: number;
  depthM: Float32Array; surface: Uint8Array }

/** Tracks presentation copies only. The worker's authoritative grid never transfers ownership. */
export class DualSnowPublisher {
  private previous: SnowGrid | null = null;
  invalidate(): void { this.previous = null; }
  frame(grid: SnowGrid, forceFull = false): { snow?: SnowGrid; snowPatch?: DualSnowPatch } {
    const previous = this.previous;
    if (forceFull || !previous || previous.width !== grid.width || previous.height !== grid.height) {
      this.previous = { ...grid, depthM: grid.depthM.slice(), surface: grid.surface.slice() };
      return { snow: { ...grid, depthM: grid.depthM.slice(), surface: grid.surface.slice() } };
    }
    let x0 = grid.width, y0 = grid.height, x1 = -1, y1 = -1;
    for (let i = 0; i < grid.depthM.length; i++) {
      if (grid.depthM[i] === previous.depthM[i] && grid.surface[i] === previous.surface[i]) continue;
      const x = i % grid.width, y = Math.floor(i / grid.width);
      x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y);
    }
    if (x1 < 0) return {};
    const width = x1 - x0 + 1, height = y1 - y0 + 1;
    if (width * height > grid.depthM.length / 2) return this.frame(grid, true);
    const patch: DualSnowPatch = { x: x0, y: y0, width, height, depthM: new Float32Array(width * height), surface: new Uint8Array(width * height) };
    for (let y = 0; y < height; y++) {
      const source = (y + y0) * grid.width + x0, target = y * width;
      patch.depthM.set(grid.depthM.subarray(source, source + width), target);
      patch.surface.set(grid.surface.subarray(source, source + width), target);
      previous.depthM.set(patch.depthM.subarray(target, target + width), source);
      previous.surface.set(patch.surface.subarray(target, target + width), source);
    }
    return { snowPatch: patch };
  }
}

export function applyDualSnowPatch(grid: SnowGrid, patch: DualSnowPatch): SnowGrid {
  if (patch.x < 0 || patch.y < 0 || patch.x + patch.width > grid.width || patch.y + patch.height > grid.height) throw new Error('Snow patch does not match the current grid.');
  const next = { ...grid, depthM: grid.depthM.slice(), surface: grid.surface.slice() };
  for (let y = 0; y < patch.height; y++) {
    const source = y * patch.width, target = (y + patch.y) * grid.width + patch.x;
    next.depthM.set(patch.depthM.subarray(source, source + patch.width), target);
    next.surface.set(patch.surface.subarray(source, source + patch.width), target);
  }
  return next;
}
