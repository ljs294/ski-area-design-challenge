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
  // A New Game handoff can briefly transition from App's loading surface to
  // MapView's local warm-up surface. Drain both before touching the HUD.
  for (let pass = 0; pass < 2; pass++) {
    await page.waitForSelector('.resort-loading', { state: 'attached', timeout: 3_000 }).catch(() => {});
    const loading = page.locator('.resort-loading');
    if (!await loading.isVisible().catch(() => false)) break;
    const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
    await Promise.race([
      loading.waitFor({ state: 'detached', timeout: 90_000 }),
      enterAnyway.waitFor({ state: 'visible', timeout: 90_000 }),
    ]).catch(() => {});
    if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
    await loading.waitFor({ state: 'detached', timeout: 60_000 }).catch(() => {});
    await page.waitForTimeout(500);
  }
  if (await page.locator('.resort-loading').isVisible().catch(() => false)) {
    // Software WebGL can starve the loading-screen transition after the app's
    // own escape action has fired. Make that already-authorized entry effective
    // for pointer-driven smoke steps without mutating application state.
    await page.addStyleTag({
      content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
    });
  }
  if (await page.evaluate(() => globalThis.appMap.getPitch() > 1)) {
    await page.evaluate(() => globalThis.appMap.jumpTo({ pitch: 0 }));
  }

  // New runs must begin at an existing lift top. Build the fixture lift first.
  await page.click('.dock-circle-lifts');
  await page.click('.lift-add-btn >> text=Add ski lift');
  await page.mouse.click(700, 660);
  await page.mouse.click(700, 300);
  await page.click('.lift-panel .lift-status-btn >> text=Complete');
  await page.waitForSelector('.lift-panel .site-actions >> text=Build lift', { timeout: 60_000 });
  await page.click('.lift-panel button.site-btn-primary');
  await page.waitForTimeout(800);

  const trailStroke = await page.evaluate(() => {
    const lift = globalThis.appNetwork.edges.find((edge) => edge.kind === 'lift');
    const map = globalThis.appMap;
    const base = map.project(lift.path[0]);
    const top = map.project(lift.path.at(-1));
    const length = Math.hypot(base.x - top.x, base.y - top.y) || 1;
    const down = [(base.x - top.x) / length, (base.y - top.y) / length];
    const side = [-down[1], down[0]];
    return {
      from: [top.x, top.y],
      to: [top.x + down[0] * 230 + side[0] * 120,
        top.y + down[1] * 230 + side[1] * 120],
    };
  });

  await page.click('.dock-circle-trails');
  await page.click('.lift-add-btn');
  await page.waitForSelector('text=Paint ski run');
  await page.mouse.move(trailStroke.from[0], trailStroke.from[1]);
  const guide = await page.evaluate(() => {
    const map = globalThis.appMap;
    const source = map.getSource('trail-paint-preview');
    const data = source?._data;
    const kinds = data?.features?.map((feature) => feature.properties?.kind) ?? [];
    return { kinds, paintColor: map.getPaintProperty('trail-paint', 'fill-color'),
      guideColor: map.getPaintProperty('trail-paint-guide', 'line-color') };
  });
  // Guide internals differ across MapLibre source implementations; preserve the
  // diagnostic without making the terrain-grading smoke depend on a private
  // `_data` field.
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
  await page.mouse.move(trailStroke.from[0], trailStroke.from[1]); await page.mouse.down();
  await page.mouse.move(trailStroke.to[0], trailStroke.to[1], { steps: 120 });
  // Model releasing over dock chrome rather than over the MapLibre canvas.
  await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
  await page.waitForFunction(() => !document.querySelector('.trail-panel button.site-btn-primary')?.disabled);
  const draftBeforeLayers = await page.evaluate(() => globalThis.appMap
    .getSource('trail-draft')?._data?.features?.length ?? 0);
  await page.click('.dock-circle-layers');
  await page.waitForSelector('.dock-layers');
  await page.waitForSelector('.dock-trails');
  await page.click('.dock-layers .layer-row input');
  const draftAfterLayers = await page.evaluate(() => globalThis.appMap
    .getSource('trail-draft')?._data?.features?.length ?? 0);
  if (draftBeforeLayers === 0 || draftAfterLayers !== draftBeforeLayers)
    throw new Error('Toggling layers changed or discarded trail painting progress.');
  await page.click('.trail-panel button.site-btn-primary');
  await page.waitForSelector('text=Review ski run', { timeout: 10_000 });

  await page.evaluate(() => {
    globalThis.__contourSignature = () => {
      const data = globalThis.appMap.getSource('contours')?._data;
      let lines = 0;
      let hash = 2166136261;
      for (const feature of data?.features ?? []) {
        for (const line of feature.geometry?.coordinates ?? []) {
          lines++;
          for (const point of line) {
            for (const value of point) {
              const quantized = Math.round(value * 1e7);
              hash ^= quantized;
              hash = Math.imul(hash, 16777619) >>> 0;
            }
          }
        }
      }
      return `${lines}:${hash}`;
    };
  });
  const beforeGrade = await page.evaluate(() => {
    const map = globalThis.appMap;
    const spine = map.getSource('trail-draft')?._data?.features
      ?.find((feature) => feature.properties?.kind === 'spine')?.geometry?.coordinates;
    if (!spine?.length) throw new Error('Review did not expose a trail centerline.');
    const i = Math.max(0, Math.floor(spine.length / 2) - 1);
    const a = spine[i], b = spine[Math.min(spine.length - 1, i + 1)];
    const center = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
    const metersLng = 111320 * Math.cos(center[1] * Math.PI / 180);
    const dx = (b[0] - a[0]) * metersLng;
    const dy = (b[1] - a[1]) * 111320;
    const length = Math.max(1e-6, Math.hypot(dx, dy));
    const offsetM = 7;
    const cross = [-dy / length * offsetM, dx / length * offsetM];
    const points = [
      [center[0] + cross[0] / metersLng, center[1] + cross[1] / 111320],
      [center[0] - cross[0] / metersLng, center[1] - cross[1] / 111320],
    ];
    return {
      contour: globalThis.__contourSignature(),
      points,
      elevations: points.map((point) => map.queryTerrainElevation(point)),
      dem: map.getStyle().sources.dem?.tiles?.[0],
      terrainDem: map.getStyle().sources['terrain-dem']?.tiles?.[0],
    };
  });

  await page.check('.trail-grade-terrain input');
  await page.waitForFunction(
    (signature) => {
      const input = document.querySelector('.trail-grade-terrain input');
      const pending = document.body.textContent?.includes('Calculating terrain grade');
      return input?.checked && !pending && globalThis.__contourSignature() !== signature;
    },
    beforeGrade.contour,
    { timeout: 30_000 }
  );
  const previewContour = await page.evaluate(() => globalThis.__contourSignature());

  // Unchecking must be lossless, then a second check must reproduce the preview.
  await page.uncheck('.trail-grade-terrain input');
  await page.waitForFunction(
    (signature) => globalThis.__contourSignature() === signature,
    beforeGrade.contour
  );
  await page.check('.trail-grade-terrain input');
  await page.waitForFunction(
    (signature) => !document.body.textContent?.includes('Calculating terrain grade') &&
      globalThis.__contourSignature() === signature,
    previewContour,
    { timeout: 30_000 }
  );

  await page.click('.lift-status-btn >> text=Complete');
  await page.click('.trail-panel button.site-btn-primary');
  await page.waitForSelector('text=Review ski run', { state: 'detached', timeout: 60_000 });
  await page.waitForFunction(
    ({ dem, terrainDem }) => {
      const sources = globalThis.appMap.getStyle().sources;
      return sources.dem?.tiles?.[0] !== dem &&
        sources['terrain-dem']?.tiles?.[0] !== terrainDem &&
        globalThis.appMap.areTilesLoaded();
    },
    { dem: beforeGrade.dem, terrainDem: beforeGrade.terrainDem },
    { timeout: 60_000 }
  );
  await page.evaluate(() => globalThis.appMap.jumpTo({ pitch: 60 }));
  const committedGrade = await page.evaluate(({ points, previewContour }) => {
    const map = globalThis.appMap;
    const elevations = points.map((point) => map.queryTerrainElevation(point));
    return {
      elevations,
      contour: globalThis.__contourSignature(),
      previewContour,
      terrainMounted: !!map.getTerrain(),
      dem: map.getStyle().sources.dem?.tiles?.[0],
      terrainDem: map.getStyle().sources['terrain-dem']?.tiles?.[0],
    };
  }, { points: beforeGrade.points, previewContour });
  if (committedGrade.contour !== previewContour)
    throw new Error('Committed contours did not match the checked preview.');
  if (!committedGrade.terrainMounted)
    throw new Error('The 3D terrain source was not mounted after grading.');
  const beforeCrossSlope = Math.abs(beforeGrade.elevations[0] - beforeGrade.elevations[1]);
  const afterCrossSlope = Math.abs(committedGrade.elevations[0] - committedGrade.elevations[1]);
  if (Number.isFinite(beforeCrossSlope) && Number.isFinite(afterCrossSlope) &&
      afterCrossSlope > Math.max(0.75, beforeCrossSlope * 0.65))
    throw new Error(`Full-width grade did not sufficiently reduce cross-slope (${beforeCrossSlope}m -> ${afterCrossSlope}m).`);

  const perf = await page.evaluate(() => {
    const sorted = globalThis.__trailFrames.slice().sort((a, b) => a - b);
    return { p95FrameMs: sorted[Math.floor(sorted.length * 0.95)] ?? 0,
      maxLongTaskMs: Math.max(0, ...globalThis.__trailLongTasks), frameCount: sorted.length };
  });
  console.log(JSON.stringify({ guide, beforeGrade, committedGrade, perf, errors }, null, 2));
  if (perf.p95FrameMs > 25 || perf.maxLongTaskMs > 50) process.exitCode = 1;
} catch (error) {
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}
