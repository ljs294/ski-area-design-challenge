// Verifies the R/F tilt keys, the U 2D<->3D toggle (both directions), and
// Q/E bearing rotation while pitched to 2D (top-down) — all inside the real
// resort/game view (not just the flat worldwide picker). Uses ?flat to keep
// the terrain mesh out of the headless SwiftShader path (it crashes there);
// bearing/pitch are pure camera-transform state and are unaffected by that.
// node scripts/verifyCameraFixes.mjs http://localhost:4184/?flat scratchpad/
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4184/?flat';
const outDir = process.argv[3] ?? 'scratchpad';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(String(e)));

const cam = () => page.evaluate(() => {
  const m = globalThis.appMap;
  return { bearing: m.getBearing(), pitch: m.getPitch() };
});

try {
  await page.goto(base, { waitUntil: 'load', timeout: 30000 });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 30000 });
  await page.click('.trail-slat >> text=New Game');
  await page.waitForFunction(() => globalThis.appMap?.isStyleLoaded?.(), null, { timeout: 30000 });
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(500, 300); await page.mouse.down();
  await page.mouse.move(820, 640, { steps: 20 }); await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Camera Fix Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 420000 });
  const loading = page.locator('.resort-loading');
  if (await loading.isVisible().catch(() => false)) {
    const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
    await Promise.race([
      loading.waitFor({ state: 'detached', timeout: 90000 }),
      enterAnyway.waitFor({ state: 'visible', timeout: 90000 }),
    ]).catch(() => {});
    if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
    await loading.waitFor({ state: 'detached', timeout: 60000 }).catch(() => {});
  }
  await page.waitForSelector('.view3d-btn', { timeout: 30000 });
  await page.waitForTimeout(500);

  const start = await cam();
  console.log('START:', JSON.stringify(start));

  // --- U toggle: from whatever we started in, toggle once, then toggle back.
  // Waits exceed terrain3d.ts's TILT_3D_MS/TILT_2D_MS (1200/1000ms) so each
  // ease is fully settled before the next action — avoids racing a still-
  // animating pitch, which previously caused a false read here.
  await page.click('.maplibregl-canvas');
  await page.keyboard.press('u');
  await page.waitForTimeout(1400);
  const afterU1 = await cam();
  console.log('AFTER_U_1:', JSON.stringify(afterU1), '(expect pitch to have flipped relative to start)');
  await page.keyboard.press('u');
  await page.waitForTimeout(1300);
  const afterU2 = await cam();
  console.log('AFTER_U_2:', JSON.stringify(afterU2), '(expect pitch back toward start — U must go BOTH ways)');

  // --- Force to a known 2D (pitch 0) state via the on-screen button ---
  const isPitched = (await cam()).pitch > 0.5;
  if (isPitched) {
    await page.click('.view3d-btn');
    await page.waitForFunction(() => globalThis.appMap.getPitch() === 0, null, { timeout: 5000 });
  }
  const twoD = await cam();
  console.log('FORCED_2D:', JSON.stringify(twoD));

  // --- Q/E rotate while at pitch 0 ---
  await page.keyboard.down('e');
  await page.waitForTimeout(600);
  await page.keyboard.up('e');
  await page.waitForTimeout(100);
  const afterE = await cam();
  console.log('ROTATE_E_AT_PITCH0:', JSON.stringify(afterE), '(expect bearing != 0)');

  await page.keyboard.press('n'); // snap back to north for a clean R/F test
  await page.waitForTimeout(400);

  // --- R/F tilt ---
  await page.keyboard.down('r');
  await page.waitForTimeout(600);
  await page.keyboard.up('r');
  await page.waitForTimeout(100);
  const afterR = await cam();
  console.log('TILT_R:', JSON.stringify(afterR), '(expect pitch increased above 0)');

  await page.keyboard.down('f');
  await page.waitForTimeout(600);
  await page.keyboard.up('f');
  await page.waitForTimeout(100);
  const afterF = await cam();
  console.log('TILT_F:', JSON.stringify(afterF), '(expect pitch decreased back down)');

  await page.screenshot({ path: `${outDir}/camera-fixes.png` });

  const ok =
    Math.round(afterU1.pitch) !== Math.round(start.pitch) &&
    Math.abs(afterU2.pitch - start.pitch) < 1 &&
    Math.round(twoD.pitch) === 0 &&
    afterE.bearing !== 0 &&
    afterR.pitch > 0.5 &&
    afterF.pitch < afterR.pitch;
  console.log(ok ? 'PASS' : 'FAIL: one or more camera-control assertions did not hold');
  console.log('PAGE_ERRORS:', pageErrors.join('\n') || '(none)');
  await browser.close();
  process.exit(ok && pageErrors.length === 0 ? 0 : 1);
} catch (e) {
  console.error('THROWN:', e.message);
  console.log('PAGE_ERRORS:', pageErrors.join('\n') || '(none)');
  await browser.close();
  process.exit(1);
}
