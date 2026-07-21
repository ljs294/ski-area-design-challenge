// Browser-level trail painter smoke/performance check. Run against a preview
// server with network access so the normal New Resort terrain preparation can
// complete: node scripts/verifyTrailPainter.mjs http://localhost:4173/
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (error) => errors.push(String(error)));
page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });

try {
  await page.goto(base, { waitUntil: 'load' });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.trail-slat >> text=New Game');
  await page.waitForFunction(() => globalThis.appMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(520, 320); await page.mouse.down();
  await page.mouse.move(820, 620, { steps: 20 }); await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Painter Performance Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });

  await page.click('.dock-circle-trails');
  await page.click('.lift-add-btn');
  await page.waitForSelector('text=Paint ski run');
  await page.mouse.move(520, 580);
  const guide = await page.evaluate(() => {
    const map = globalThis.appMap;
    const source = map.getSource('trail-paint-preview');
    const data = source?._data;
    const kinds = data?.features?.map((feature) => feature.properties?.kind) ?? [];
    return { kinds, paintColor: map.getPaintProperty('trail-paint', 'line-color'),
      guideColor: map.getPaintProperty('trail-paint-guide', 'line-color') };
  });
  if (!guide.kinds.includes('guide') || !guide.kinds.includes('crosshair'))
    throw new Error('Trail brush guide was not visible before painting.');
  if (guide.paintColor !== guide.guideColor) throw new Error('Trail preview colors are inconsistent.');
  await page.evaluate(() => {
    globalThis.__trailFrames = [];
    globalThis.__trailLongTasks = [];
    let last = performance.now();
    const tick = (now) => { globalThis.__trailFrames.push(now - last); last = now; if (globalThis.__trailFrames.length < 180) requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    new PerformanceObserver((list) => globalThis.__trailLongTasks.push(...list.getEntries().map((e) => e.duration)))
      .observe({ type: 'longtask', buffered: true });
  });
  await page.mouse.move(520, 580); await page.mouse.down();
  await page.mouse.move(700, 350, { steps: 120 }); await page.mouse.up();
  await page.waitForFunction(() => !document.querySelector('.trail-panel button.site-btn-primary')?.disabled);
  await page.click('.trail-panel button.site-btn-primary');
  await page.waitForSelector('text=Review ski run', { timeout: 10_000 });

  const perf = await page.evaluate(() => {
    const sorted = globalThis.__trailFrames.slice().sort((a, b) => a - b);
    return { p95FrameMs: sorted[Math.floor(sorted.length * 0.95)] ?? 0,
      maxLongTaskMs: Math.max(0, ...globalThis.__trailLongTasks), frameCount: sorted.length };
  });
  console.log(JSON.stringify({ guide, perf, errors }, null, 2));
  if (perf.p95FrameMs > 25 || perf.maxLongTaskMs > 50) process.exitCode = 1;
} catch (error) {
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}
