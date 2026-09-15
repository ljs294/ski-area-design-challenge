import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';

type CaseName = 'known-raster' | 'snow-flat' | 'snow-dem' | 'snow-ci-dem' | 'analysis-composition';
const outputRoot = path.resolve('test-results/snow-reproducer');

test('framebuffer capture ignores covering DOM overlays', async ({ page }) => {
  await page.setContent('<canvas id="map" width="32" height="32"></canvas>');
  const hashes = await page.evaluate(() => {
    const canvas = document.querySelector<HTMLCanvasElement>('#map')!;
    const gl = canvas.getContext('webgl')!;
    const capture = () => {
      const pixels = new Uint8Array(32 * 32 * 4);
      gl.readPixels(0, 0, 32, 32, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
      return Array.from(pixels).reduce((hash, value) => Math.imul(hash ^ value, 0x01000193) >>> 0, 0x811c9dc5);
    };
    gl.clearColor(0.1, 0.4, 0.8, 1); gl.clear(gl.COLOR_BUFFER_BIT);
    const before = capture(), overlay = document.createElement('div');
    overlay.style.cssText = 'position:absolute;inset:0;background:#f00;z-index:9999'; document.body.append(overlay);
    return { before, after: capture() };
  });
  expect(hashes.after).toBe(hashes.before);
});

test('public MapLibre snow source isolation', async ({ page, baseURL }) => {
  test.setTimeout(180_000);
  const output = path.join(outputRoot, `attempt-${Date.now()}`); mkdirSync(output, { recursive: true });
  writeFileSync(path.join(outputRoot, 'latest-attempt.json'), `${JSON.stringify({ output }, null, 2)}\n`);
  const rows: Record<string, unknown>[] = [];
  const requestedCases = (process.env.SNOW_REPRO_CASES?.split(',')
    ?? ['known-raster', 'snow-flat', 'snow-ci-dem', 'analysis-composition']) as CaseName[];
  for (const name of requestedCases) {
    let evidence: Record<string, unknown> = { name, status: 'failed-before-result' };
    try {
      // Reload between cases so production protocol worker singletons cannot inherit a
      // prior map's teardown state and turn this source/layer comparison into a harness artifact.
      await page.goto(baseURL!, { waitUntil: 'load' });
      await page.waitForFunction(() => !!(globalThis as typeof globalThis & { snowReproducer?: unknown }).snowReproducer);
      const terrainUrl = name === 'snow-dem'
        ? `/@fs/${path.resolve('test-results/integrated-fixtures/jackson/terrain.json').replaceAll('\\', '/')}`
        : undefined;
      evidence = await page.evaluate(async ({ caseName, terrain }) => (globalThis as typeof globalThis & { snowReproducer: {
        runCase(name: CaseName, terrain?: string): Promise<Record<string, unknown>> } }).snowReproducer.runCase(caseName, terrain),
      { caseName: name, terrain: terrainUrl });
      evidence.status = 'completed';
      const telemetry = evidence.telemetry as { entries?: { stage?: string }[] } | undefined;
      if (name === 'known-raster') expect((evidence.events as { type?: string }[]).some(event => event.type === 'known-request')).toBe(true);
      else expect(telemetry?.entries?.some(entry => entry.stage === 'snow-tile-generated')).toBe(true);
      if (name === 'known-raster' || name === 'snow-flat') expect(evidence.terrain).toBeNull();
      if (name === 'snow-ci-dem') {
        expect(evidence.terrain).toMatchObject({ source: 'terrain-dem', exaggeration: 1 });
        expect(evidence.demSource).toMatchObject({ type: 'raster-dem', encoding: 'terrarium' });
      }
      const center = (evidence.pixels as { center: number[] }).center;
      expect(center[3]).toBe(255);
      if (name === 'known-raster') expect(center[0]).toBeGreaterThan(center[2]! + 100);
      else expect(center[2], 'snow must be visibly blue in the actual framebuffer').toBeGreaterThan(center[0]! + 20);
    } catch (error) {
      const failureSnapshot = await page.evaluate(() => (globalThis as typeof globalThis & { snowReproducer: {
        snapshot(): Record<string, unknown> } }).snowReproducer.snapshot()).catch(() => evidence);
      evidence = { ...evidence, failureSnapshot, status: 'failed', error: error instanceof Error ? error.message : String(error) };
      throw error;
    }
    finally {
      writeFileSync(path.join(output, `${name}.json`), `${JSON.stringify(evidence, null, 2)}\n`);
      const screenshot = path.join(output, `${name}.png`);
      try { await page.screenshot({ path: screenshot }); evidence.screenshot = screenshot; }
      catch (screenshotError) { evidence.screenshotError = screenshotError instanceof Error ? screenshotError.message : String(screenshotError); }
      rows.push(evidence);
      writeFileSync(path.join(output, `${name}.json`), `${JSON.stringify(evidence, null, 2)}\n`);
      writeFileSync(path.join(output, 'evidence-table.json'), `${JSON.stringify(rows, null, 2)}\n`);
    }
  }
});
