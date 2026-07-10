// Interaction check: toggle layers via the panel and confirm the map updates.
// Turns Ground cover ON and Contours + Hillshade OFF, then screenshots.
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const shot = process.argv[3] ?? 'scratchpad/toggle.png';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));
page.on('console', (m) => {
  const t = m.text();
  if (m.type() === 'error' || m.type() === 'warning' || t.includes('[worldcover]')) {
    console.log(`  console.${m.type()}: ${t}`);
  }
});

await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());

async function settle() {
  await page.evaluate(
    () => new Promise((res) => {
      const m = window.appMap;
      if (m.areTilesLoaded()) return res();
      m.once('idle', res);
      setTimeout(res, 8000);
    })
  );
}
await settle();

// Toggle: Ground cover on, Contours off, Hillshade off.
await page.locator('.layer-row', { hasText: 'Ground cover' }).click();
await page.locator('.layer-row', { hasText: 'Contours' }).click();
await page.locator('.layer-row', { hasText: 'Hillshade' }).click();
await settle();
await page.waitForTimeout(500);

// Report the actual MapLibre visibility state to prove toggles took effect.
const vis = await page.evaluate(() => ({
  groundcover: window.appMap.getLayoutProperty('groundcover', 'visibility'),
  contourLines: window.appMap.getLayoutProperty('contour-lines', 'visibility'),
  hillshade: window.appMap.getLayoutProperty('hillshade', 'visibility'),
}));
console.log('visibility after toggles:', JSON.stringify(vis));
console.log('pageerrors:', errors.length ? errors.join('\n') : '(none)');

await page.screenshot({ path: shot });
await browser.close();
process.exit(errors.length ? 1 : 0);
