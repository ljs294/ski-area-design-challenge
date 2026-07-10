import { chromium } from 'playwright';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 900, height: 700 } });

let wmtsReqs = 0, wmtsOk = 0, wmtsBad = 0;
page.on('response', (r) => {
  if (r.url().includes('terrascope')) {
    wmtsReqs++;
    if (r.status() === 200) wmtsOk++; else { wmtsBad++; console.log('  WMTS', r.status(), r.url().slice(-60)); }
  }
});
page.on('console', (m) => { if (m.type() === 'error' || m.text().includes('[worldcover]')) console.log('  console:', m.text()); });
page.on('requestfailed', (r) => { if (r.url().includes('terrascope') || r.url().includes('worldcover')) console.log('  reqfailed:', r.failure()?.errorText, r.url().slice(-50)); });

await page.goto('http://localhost:5175/', { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());
await page.locator('.layer-row', { hasText: 'Ground cover' }).locator('input').click();
await page.waitForTimeout(6000);

// Sample the actual rendered color at map center via a canvas readback.
const info = await page.evaluate(() => {
  const m = window.appMap;
  return {
    sourceLoaded: m.isSourceLoaded('worldcover'),
    tilesLoaded: m.areTilesLoaded(),
    coverVisible: m.getLayoutProperty('groundcover', 'visibility'),
    opacity: m.getPaintProperty('groundcover', 'raster-opacity'),
  };
});
console.log('WMTS requests:', wmtsReqs, 'ok:', wmtsOk, 'bad:', wmtsBad);
console.log('map state:', JSON.stringify(info));
await browser.close();
