// Verifies the site picker: draw a box -> dims shown -> View locks maxBounds
// -> Exit clears it.
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const outDir = process.argv[3] ?? '.';
const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1000, height: 720 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());

let pass = true;
const expect = (c, m) => { console.log(`${c ? 'OK  ' : 'FAIL'} ${m}`); if (!c) pass = false; };
const maxBounds = () =>
  page.evaluate(() => {
    const b = window.appMap.getMaxBounds();
    return b ? [b.getWest(), b.getSouth(), b.getEast(), b.getNorth()] : null;
  });

// 1. Enter select mode.
await page.locator('.site-btn', { hasText: 'Select site' }).click();
expect((await page.locator('.site-hint').count()) === 1, 'shows draw hint in selecting mode');

// 2. Drag a rectangle on the map (clear of the panels).
await page.mouse.move(450, 300);
await page.mouse.down();
await page.mouse.move(560, 400, { steps: 5 });
await page.mouse.move(700, 520, { steps: 10 });
await page.mouse.up();

await page.waitForSelector('.site-dims', { timeout: 5000 });
const dims = await page.locator('.site-dims').innerText();
expect(/\d+(\.\d+)?\s*×\s*\d+(\.\d+)?\s*km/.test(dims), `dims shown while selecting: ${JSON.stringify(dims)}`);
expect((await maxBounds()) === null, 'map not yet locked while selecting');
await page.screenshot({ path: `${outDir}/site_selecting.png` });

// 3. Confirm -> view locks; maxBounds = property + margin (so terrain shows
//    beyond the property line), exterior dimmed, boundary drawn solid.
await page.locator('.site-btn', { hasText: 'View this area' }).click();
await page.waitForTimeout(900); // let fitBounds settle
const mb = await maxBounds();
expect(mb !== null, `maxBounds set after View: ${JSON.stringify(mb)}`);
if (mb) {
  const wKm = (mb[2] - mb[0]) * 111.32 * Math.cos((mb[1] * Math.PI) / 180);
  const hKm = (mb[3] - mb[1]) * 111.32;
  // Outer = property (~2-10km) inflated 40% -> ~2.8-14km.
  expect(wKm >= 2 && wKm <= 15 && hKm >= 2 && hKm <= 15, `outer (margin) bounds (${wKm.toFixed(1)}×${hKm.toFixed(1)})`);
}
const lockState = await page.evaluate(() => ({
  maskFeatures: window.appMap.getSource('site-mask').serialize().data.features.length,
  solid: window.appMap.getLayoutProperty('site-box-line-solid', 'visibility'),
  dash: window.appMap.getLayoutProperty('site-box-line-dash', 'visibility'),
}));
expect(lockState.maskFeatures === 1, 'exterior mask present when locked');
expect(lockState.solid === 'visible' && lockState.dash === 'none', 'boundary line solid (not dashed) when locked');
expect((await page.locator('.site-btn', { hasText: 'Exit site' }).count()) === 1, 'Exit site button shown when locked');
await page.waitForTimeout(600);
await page.screenshot({ path: `${outDir}/site_locked.png` });

// 4. Exit -> lock cleared, back to explore.
await page.locator('.site-btn', { hasText: 'Exit site' }).click();
await page.waitForTimeout(200);
expect((await maxBounds()) === null, 'maxBounds cleared after Exit');
const maskAfter = await page.evaluate(() => window.appMap.getSource('site-mask').serialize().data.features.length);
expect(maskAfter === 0, 'exterior mask cleared after Exit');
expect((await page.locator('.site-btn', { hasText: 'Select site' }).count()) === 1, 'back to Select site');

console.log(pass ? 'PASS' : 'FAIL');
await browser.close();
process.exit(pass ? 0 : 1);
