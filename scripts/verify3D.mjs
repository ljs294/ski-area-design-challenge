// Toggle 3D via the on-map button, assert terrain/pitch/sky state, screenshot, revert.
// Usage: node scripts/verify3D.mjs <url> <shot>
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const shot = process.argv[3] ?? 'scratchpad/view3d.png';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));

await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());
const settle = () =>
  page.evaluate(
    () => new Promise((res) => {
      const m = window.appMap;
      if (m.areTilesLoaded()) return res();
      m.once('idle', res);
      setTimeout(res, 15000);
    })
  );
await settle();

// --- Enable 3D ---
await page.click('.view3d-btn');
await page.waitForFunction(() => window.appMap.getPitch() > 60, null, { timeout: 20000 });
await settle();
await page.waitForTimeout(2500); // terrain mesh + render-to-texture margin under SwiftShader

const on = await page.evaluate(() => {
  const m = window.appMap;
  return {
    terrain: !!m.getTerrain(),
    pitch: Math.round(m.getPitch()),
    maxPitch: m.getMaxPitch(),
    styleSky: !!m.getStyle().sky,
  };
});
console.log('3D on:', JSON.stringify(on));
await page.screenshot({ path: shot });

// --- Disable 3D ---
await page.click('.view3d-btn');
await page.waitForFunction(
  () => window.appMap.getPitch() === 0 && !window.appMap.getTerrain(),
  null,
  { timeout: 20000 }
);
const off = await page.evaluate(() => {
  const m = window.appMap;
  return { terrain: !!m.getTerrain(), pitch: m.getPitch(), maxPitch: m.getMaxPitch() };
});
console.log('3D off:', JSON.stringify(off));
console.log('pageerrors:', errors.length ? errors.join('\n') : '(none)');

const ok =
  on.terrain && on.pitch >= 60 && on.maxPitch === 85 && on.styleSky &&
  !off.terrain && off.pitch === 0 && off.maxPitch === 60;
if (!ok) console.error('FAIL: 3D toggle assertions did not hold');
await browser.close();
process.exit(ok && errors.length === 0 ? 0 : 1);
