// Browser smoke check for the standalone pond workflow: draw a boundary, read
// the berm and earthwork bill, build it, and confirm the terrain package was
// regraded. Run against a preview server with network access so terrain
// preparation can complete:
// node scripts/verifyPondBuilder.mjs http://localhost:4173/ scratchpad/pond.png
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const shot = process.argv[3] ?? 'scratchpad/pond.png';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error)));

try {
  await page.goto(base, { waitUntil: 'load' });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.trail-slat >> text=New Game');
  await page.waitForFunction(() => globalThis.appMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(500, 300); await page.mouse.down();
  await page.mouse.move(820, 640, { steps: 20 }); await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Pond Builder Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 420_000 });
  const loading = page.locator('.resort-loading');
  if (await loading.isVisible().catch(() => false)) {
    const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
    await Promise.race([
      loading.waitFor({ state: 'detached', timeout: 90_000 }),
      enterAnyway.waitFor({ state: 'visible', timeout: 90_000 }),
    ]).catch(() => {});
    if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
    await loading.waitFor({ state: 'detached', timeout: 60_000 }).catch(() => {});
  }
  // The overlay fades out; never let its tail intercept the first click.
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });

  // Frame the site flat and close in, so the drawn boundary is a pond-sized
  // patch of ground rather than a hillside seen edge-on.
  await page.evaluate(() => new Promise((resolve) => {
    const map = globalThis.appMap;
    map.easeTo({ zoom: 17.5, pitch: 0, bearing: 0, duration: 0 });
    map.once('idle', resolve);
    setTimeout(resolve, 8000);
  }));
  await page.click('.dock-circle-snowmaking');
  await page.waitForSelector('.dock-snowmaking');
  await page.click('.snowmaking-panel >> text=Build standalone pond');
  for (const [x, y] of [[590, 500], [650, 500], [650, 550], [590, 550]]) await page.mouse.click(x, y);
  await page.click('.snowmaking-panel >> text=Finish boundary');
  await page.waitForSelector('.snowmaking-panel >> text=Review standalone pond');

  const readReview = () => page.evaluate(() => {
    const panel = document.querySelector('.snowmaking-panel');
    const value = (label) => [...panel.querySelectorAll('.readout-line')]
      .find((row) => row.textContent?.startsWith(label))?.textContent ?? null;
    return { text: panel.textContent, cut: value('Cut'), fill: value('Fill'),
      balance: value('Balance'), berm: value('Max berm height'),
      crest: value('Berm crest elevation'), volume: value('Pond volume') };
  });
  const shallow = await readReview();
  if (!shallow.cut || !shallow.fill || !shallow.balance)
    throw new Error('Pond review did not report cut, fill, and balance.');
  if (!shallow.crest || !shallow.berm)
    throw new Error('Pond review did not report the berm holding the pool in.');

  // The pre-build ground preview: the graded contour layer carries the lines
  // this pond would move, before anything is committed.
  const gradedContourCount = () => page.evaluate(() => globalThis.appMap
    .getStyle().sources['graded-contours']?.data?.features?.length ?? 0);
  const previewContours = await gradedContourCount();
  if (previewContours === 0)
    throw new Error('The pond review did not highlight the contours it would move.');

  // Excavating the floor has to move both the earthwork bill and the capacity.
  const excavation = page.locator('.snowmaking-panel input[aria-label^="Excavation below full pool"]');
  await excavation.fill('3');
  await excavation.blur();
  await page.waitForTimeout(300);
  const dug = await readReview();
  if (dug.cut === shallow.cut || dug.volume === shallow.volume) {
    console.error(JSON.stringify({ shallow, dug,
      excavationValue: await excavation.inputValue() }, null, 2));
    throw new Error('Excavating the pond floor did not change the earthwork or the capacity.');
  }

  const beforeDem = await page.evaluate(() => globalThis.appMap.getStyle().sources.dem?.tiles?.[0]);
  await page.fill('.snowmaking-panel .name-entry-input', 'Snowmaking Pond');
  await page.click('.snowmaking-panel >> text=Build pond');
  await page.waitForSelector('.snowmaking-panel >> text=Snowmaking Pond', { timeout: 60_000 });
  await page.waitForFunction((url) => globalThis.appMap
    .getStyle().sources.dem?.tiles?.[0] !== url, beforeDem, { timeout: 60_000 });

  await page.waitForFunction(() => (globalThis.appMap.getStyle()
    .sources['player-standalone-ponds']?.data?.features?.length ?? 0) > 0,
  null, { timeout: 30_000 });
  const built = await page.evaluate(() => {
    const map = globalThis.appMap;
    const data = map.getStyle().sources['player-standalone-ponds']?.data;
    const pond = data?.features?.[0];
    const ring = pond?.geometry?.coordinates?.[0] ?? [];
    const center = ring.length
      ? [ring.reduce((sum, point) => sum + point[0], 0) / ring.length,
        ring.reduce((sum, point) => sum + point[1], 0) / ring.length] : null;
    return { features: data?.features?.length ?? 0, name: pond?.properties?.name ?? null,
      renderHeightM: pond?.properties?.renderHeightM ?? null, center };
  });
  if (!built.name || !built.center) {
    console.error(JSON.stringify(built, null, 2));
    throw new Error('The built pond is not on the map.');
  }
  // Committing turns the proposal into terrain, so the highlight must clear.
  const afterContours = await gradedContourCount();
  if (afterContours !== 0)
    throw new Error(`The grade highlight survived the build (${afterContours} features).`);

  // The pond source is re-tiled on a worker, so let the water surface appear
  // rather than querying the frame that was already in flight.
  const frame = async (path, camera) => {
    await page.evaluate((view) => new Promise((resolve) => {
      const map = globalThis.appMap;
      map.easeTo({ ...view, duration: 0 });
      map.once('idle', resolve);
      setTimeout(resolve, 8000);
    }), camera);
    await page.waitForTimeout(1500);
    await page.screenshot({ path });
  };
  const pondCamera = { center: built.center, zoom: 17, pitch: 0, bearing: 0 };
  built.rendered = 0;
  for (let attempt = 0; attempt < 4 && built.rendered === 0; attempt++) {
    await frame(shot, pondCamera);
    built.rendered = await page.evaluate(() => globalThis.appMap
      .queryRenderedFeatures({ layers: ['standalone-pond-fill'] }).length);
  }
  if (built.rendered === 0) throw new Error('The pond surface did not render over its berm.');

  // Same frame once the felled cover has been re-derived and committed.
  await page.waitForSelector('.build-status-bug', { state: 'detached', timeout: 120_000 })
    .catch(() => {});
  await frame(shot.replace(/\.png$/, '-cleared.png'), pondCamera);
  built.renderedAfterClear = await page.evaluate(() => ({
    pond: globalThis.appMap.queryRenderedFeatures({ layers: ['standalone-pond-fill'] }).length,
    all: globalThis.appMap.queryRenderedFeatures().length,
  }));
  // Second frame in the game's own 3D view: this is where a berm that failed to
  // tie into the hillside would show up as a wall.
  await page.click('.map-3d-toggle, button:has-text("3D")').catch(() => {});
  await frame(shot.replace(/\.png$/, '-3d.png'),
    { center: built.center, zoom: 16.5, pitch: 60, bearing: 30 });

  console.log(JSON.stringify({ shallow: { cut: shallow.cut, fill: shallow.fill,
    balance: shallow.balance, crest: shallow.crest, berm: shallow.berm, volume: shallow.volume },
    dug: { cut: dug.cut, fill: dug.fill, balance: dug.balance, volume: dug.volume },
    previewContours, built, pageErrors }, null, 2));
  if (pageErrors.length) throw new Error(`Page errors: ${pageErrors.join(' | ')}`);
} catch (error) {
  console.error(error);
  await page.screenshot({ path: shot }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
