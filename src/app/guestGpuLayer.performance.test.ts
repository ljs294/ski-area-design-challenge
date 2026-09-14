import { mkdirSync, writeFileSync } from 'node:fs';
import { cpus, totalmem } from 'node:os';
import { expect, it } from 'vitest';
import { guestGpuFrameVertexData } from './guestGpuLayer';
import type { GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';

/** Stable CPU renderer fixtures used for renderer changes and regressions. */
export const GUEST_RENDERER_POPULATIONS = [1_000, 3_000, 10_000] as const;

function frame(count: number, phase: number): GuestSimulationRenderFrame {
  const ids = Uint32Array.from({ length: count }, (_, index) => index + 1);
  const edgeIndices = new Int32Array(count); edgeIndices.fill(-1);
  const progress = Float32Array.from({ length: count }, (_, index) => ((index * 17 + phase) % 100) / 100);
  const statusFlags = new Uint32Array(count); statusFlags.fill(64);
  return { ids, guestIds: ids, edgeIndices, progress, statusFlags,
    bytesPerGuest: 16, byteLength: count * 16 };
}

function checksum(data: Float32Array): number {
  let value = 2_166_136_261;
  for (let index = 0; index < data.length; index += 1) value = Math.imul(value ^ Math.fround(data[index]!) * 1_000_000, 16_777_619) >>> 0;
  return value;
}

it.runIf(process.env.GUEST_RENDERER_BENCHMARK === '1')('measures compact CPU projection at 1k, 3k, and 10k guests', () => {
  const measurements = GUEST_RENDERER_POPULATIONS.map((population) => {
    const previous = frame(population, 1), next = frame(population, 2);
    for (let warmup = 0; warmup < 3; warmup += 1) guestGpuFrameVertexData(previous, next, [], [-121.5, 46.9]);
    const started = performance.now();
    let result = new Float32Array(0);
    for (let iteration = 0; iteration < 8; iteration += 1) result = guestGpuFrameVertexData(previous, next, [], [-121.5, 46.9]);
    const elapsedMs = performance.now() - started;
    return { population, iterations: 8, elapsedMs, perFrameMs: elapsedMs / 8,
      outputBytes: result.byteLength, checksum: checksum(result) };
  });
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync('test-results/dual-clock/guest-renderer-performance.json', JSON.stringify({
    cpu: cpus()[0]?.model, ramGiB: totalmem() / 2 ** 30, node: process.version, measurements,
  }, null, 2));
  for (const measurement of measurements) {
    expect(measurement.outputBytes).toBe(measurement.population * 24);
    expect(measurement.checksum).not.toBe(0);
  }
});
