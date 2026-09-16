import { mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { cpus, totalmem } from 'node:os';
import { test, expect } from '../support/deterministicApp';
import { dualFixture, fixtureTerrain, fixtureHour } from '../../../src/dualClock/fixtures';
import { defaultDualAmenities } from '../../../src/dualClock/amenities';
import type { DualWorkerResponse } from '../../../src/app/dualClockProtocol';

test.skip(process.env.DUAL_CLOCK_WORKER_BENCHMARK !== '1', 'Opt-in full-winter browser-worker benchmark');
test.use({ launchOptions: { args: ['--use-gl=angle', '--use-angle=d3d11'] } });
test('measures prepared winter advancement in the shipped dedicated browser worker', async ({ page, browser }) => {
  test.setTimeout(150_000);
  await page.goto('/?flat');
  const input = dualFixture(10000, 512);
  input.snow!.bounds = { west: -120.02, east: -119.98, south: 44.98, north: 45.03 };
  input.terrain = fixtureTerrain(input); input.resort.amenities = defaultDualAmenities('base');
  input.weather = Array.from({ length: 168 * 24 + 1 }, (_, i) => fixtureHour(new Date(Date.parse(input.at) + i * 3600000).toISOString(), -5, i % 24 === 0 ? 1 : 0));
  const filename = readdirSync('dist/assets').find(name => name.startsWith('dualClock.worker-') && name.endsWith('.js'))!;
  const metrics = await page.evaluate(async ({ input, filename }) => {
    const worker = new Worker(`/assets/${filename}`, { type: 'module' });
    const prepared = { ...input, snow: { ...input.snow!, depthM: new Float32Array(input.snow!.depthM), surface: new Uint8Array(input.snow!.surface) } };
    let started = 0, initialized = 0, coldWeekMs = 0, weekMs = 0, messages = 0, bytes = 0, lastFrame = 0;
    const frameIntervals: number[] = [];
    let frame = 0;
    const tick = (now: number) => { if (lastFrame) frameIntervals.push(now - lastFrame); lastFrame = now; frame = requestAnimationFrame(tick); };
    frame = requestAnimationFrame(tick);
    return await new Promise<{ initializationMs: number; coldWeekMs: number; weekMs: number; winterMs: number; messages: number; bytes: number; frameP95: number; admitted: number }>((resolve, reject) => {
      const timeout = setTimeout(() => { worker.terminate(); cancelAnimationFrame(frame); reject(new Error('Winter worker benchmark timed out')); }, 140_000);
      const cleanup = () => { clearTimeout(timeout); worker.terminate(); cancelAnimationFrame(frame); };
      worker.onerror = event => { cleanup(); reject(new Error(event.message)); };
      worker.onmessage = ({ data }: MessageEvent<DualWorkerResponse>) => {
        if (data.type === 'error') { cleanup(); reject(new Error(data.error)); return; }
        messages++; bytes += JSON.stringify(data.publication).length + (data.snow?.depthM.byteLength ?? 0) + (data.snow?.surface.byteLength ?? 0);
        if (data.id === 1) {
          initialized = performance.now();
          worker.postMessage({ generation: 1, requestId: 2, committedRevision: data.committedRevision, type: 'skip', request: {
            destination: 'week', target: new Date(Date.parse(input.at) + 7 * 86400000).toISOString() } });
        } else if (data.id === 2 && data.publication?.advance?.state === 'completed') {
          coldWeekMs = performance.now() - initialized;
          worker.postMessage({ generation: 1, requestId: 3, committedRevision: data.committedRevision, type: 'skip', request: {
            destination: 'week', target: new Date(Date.parse(input.at) + 14 * 86400000).toISOString() } });
        } else if (data.id === 3 && data.publication?.advance?.state === 'completed') {
          weekMs = performance.now() - initialized - coldWeekMs;
          worker.postMessage({ generation: 1, requestId: 4, committedRevision: data.committedRevision, type: 'skip', request: {
            destination: 'season', target: data.publication.clock.winterEnd } });
        } else if (data.id === 4 && data.publication?.advance?.state === 'completed') {
          const winterMs = performance.now() - initialized;
          frameIntervals.sort((a, b) => a - b); cleanup();
          resolve({ initializationMs: initialized - started, coldWeekMs, weekMs, winterMs, messages, bytes,
            frameP95: frameIntervals[Math.floor(frameIntervals.length * 0.95)], admitted: data.publication.flow.admitted });
        }
      };
      started = performance.now();
      worker.postMessage({ generation: 1, requestId: 1, committedRevision: 0, type: 'initialize', input: prepared });
    });
  }, { filename, input: { ...input, snow: { ...input.snow!, depthM: Array.from(input.snow!.depthM), surface: Array.from(input.snow!.surface) } } });
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync('test-results/dual-clock/worker-performance.json', JSON.stringify({ ...metrics,
    cpu: cpus()[0].model, ramGiB: totalmem() / 2 ** 30, browser: browser.version(), grid: '512x512',
    dailyDemand: 10000, lifts: 1, trails: 1, amenities: 2, graphics: 'ANGLE Direct3D11 (hardware)', scope: 'Shipped browser worker, prepared weather, progress and final snow transfers; menu UI remains responsive. Cold and warmed weeks reported separately. No Electron certification.' }, null, 2));
  expect(metrics.weekMs).toBeLessThan(5000); expect(metrics.winterMs).toBeLessThan(60000);
});
