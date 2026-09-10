import { mkdirSync, writeFileSync } from 'node:fs';
import { cpus, totalmem } from 'node:os';
import { test, expect } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

test.skip(process.env.DUAL_CLOCK_GPU !== '1', 'Opt-in hardware renderer measurement');
test.use({ launchOptions: { args: ['--use-gl=angle', '--use-angle=d3d11'] } });

test('measures 3000 interpolated guests in the production map renderer', async ({ page, browser }) => {
  test.setTimeout(90_000);
  await seedPreparedResort(page); await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0);
  const metrics = await page.evaluate(async () => {
    type Point = { id: string; lng: number; lat: number; status: string; motion: { routeId: string; lane: number; progress: number; duration: number } };
    const map = (window as unknown as { appMap: {
      getLayer(id: string): { implementation: { setMotionRoutes(routes: unknown): void; setPoints(previous: Point[], next: Point[], duration: number): void } };
      getCanvas(): HTMLCanvasElement; fitBounds(bounds: number[][], options: unknown): void;
      on(event: string, callback: () => void): void; off(event: string, callback: () => void): void;
    } }).appMap;
    map.fitBounds([[-121.5, 46.9], [-121.49, 46.91]], { padding: 100, duration: 0 });
    const layer = map.getLayer('guest-simulation-dots').implementation;
    const lanes = Array.from({ length: 17 }, (_, lane) => Array.from({ length: 120 }, (_, i) => [
      -121.499 + i / 119 * 0.008, 46.905 + Math.sin(i / 119 * Math.PI * 2) * 0.0028 + (lane - 8) * 0.000025,
    ]));
    const distances = Float64Array.from({ length: 120 }, (_, i) => i * 12);
    layer.setMotionRoutes({ benchmark: { lanes, distances, length: distances[119] } });
    const canvas = map.getCanvas(), gl = canvas.getContext('webgl2') ?? canvas.getContext('webgl');
    const debug = gl?.getExtension('WEBGL_debug_renderer_info');
    const renderer = debug ? String(gl!.getParameter(debug.UNMASKED_RENDERER_WEBGL)) : 'unavailable';
    const results = [];
    for (const [preset, rate] of [[1, 1], [2, 1.75], [4, 2.75]]) {
      let previous: Point[] = [], lastFrame = 0; const frameTimes: number[] = [], started = performance.now();
      const render = () => { const now = performance.now(); if (lastFrame && now - started > 2000) frameTimes.push(now - lastFrame); lastFrame = now; };
      map.on('render', render);
      const publication = setInterval(() => {
        const elapsed = (performance.now() - started) / 1000 * rate;
        const next = Array.from({ length: 3000 }, (_, i) => {
          const progress = i / 3000 * 0.8 + elapsed / 300, lane = i % 17, station = Math.min(119, Math.floor(progress * 119));
          return { id: `benchmark-${i}`, lng: lanes[lane][station][0], lat: lanes[lane][station][1], status: 'skiing',
            motion: { routeId: 'benchmark', lane, progress, duration: 300 } };
        });
        layer.setPoints(previous, next, 100); previous = next;
      }, 100);
      await new Promise(resolve => setTimeout(resolve, 10_000)); clearInterval(publication); map.off('render', render);
      const sorted = [...frameTimes].sort((a, b) => a - b), percentile = (p: number) => sorted[Math.floor((sorted.length - 1) * p)] ?? Infinity;
      results.push({ preset, guests: 3000, fps: 1000 / (frameTimes.reduce((a, b) => a + b, 0) / frameTimes.length),
        frameMsP50: percentile(0.5), frameMsP95: percentile(0.95), frameMsP99: percentile(0.99) });
    }
    layer.setPoints([], [], 0);
    return { renderer, viewport: [innerWidth, innerHeight], results };
  });
  const report = { ...metrics, cpu: cpus()[0].model, ramGiB: totalmem() / 2 ** 30, browser: browser.version(),
    scope: 'Production MapLibre renderer and 10 Hz movement publication; simulation paused; decorative network map tiles blocked. This is not an integrated resort or Electron certification.' };
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync('test-results/dual-clock/gpu-performance.json', JSON.stringify(report, null, 2));
  expect(metrics.results).toHaveLength(3);
  expect(metrics.results.every(result => Number.isFinite(result.fps))).toBe(true);
});
