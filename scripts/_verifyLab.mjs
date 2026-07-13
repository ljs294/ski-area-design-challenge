import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/#graphics-lab';
const shot = process.argv[3] ?? 'lab.png';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1600, height: 900 } });
const logs = [];
page.on('console', (m) => logs.push(`[${m.type()}] ${m.text()}`));
const errs = [];
page.on('pageerror', (e) => errs.push(String(e)));
let esriOk = 0;
page.on('response', (r) => {
  if (r.url().includes('arcgisonline') && r.status() === 200) esriOk++;
});

try {
  await page.goto(url, { waitUntil: 'load', timeout: 30000 });
  await page.waitForTimeout(3000);
  // Turn on Satellite imagery in the RIGHT pane (pane B) to prove imagery loads.
  const paneB = page.locator('.glab-pane').nth(1);
  const satInput = paneB.locator('.layer-row', { hasText: 'Satellite imagery' }).locator('input');
  if (await satInput.count()) await satInput.check();
  await page.waitForTimeout(6000);
} catch (e) {
  console.error('NAV ERROR', e.message);
}
console.log('esri 200 responses:', esriOk);

const info = await page.evaluate(() => {
  const panes = [...document.querySelectorAll('.glab-pane')].map((p) => {
    const r = p.getBoundingClientRect();
    const c = p.querySelector('canvas');
    const cr = c ? c.getBoundingClientRect() : null;
    return { paneWH: [Math.round(r.width), Math.round(r.height)], canvasWH: cr ? [Math.round(cr.width), Math.round(cr.height)] : null };
  });
  return {
    hasGlab: !!document.querySelector('.glab'),
    panesCount: document.querySelectorAll('.glab-pane').length,
    layerPanels: document.querySelectorAll('.layer-panel').length,
    panes,
  };
});
console.log('===== DOM INFO =====');
console.log(JSON.stringify(info, null, 2));
console.log('===== PAGE ERRORS =====');
console.log(errs.join('\n') || '(none)');
console.log('===== CONSOLE (tail) =====');
console.log(logs.slice(-8).join('\n') || '(none)');

await page.screenshot({ path: shot });
console.log('shot:', shot);
await browser.close();
