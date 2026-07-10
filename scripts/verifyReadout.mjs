// Verifies the cursor readout (elevation + active-overlay stat) and the
// context-sensitive legend (only when panel open, only for active overlay).
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

const cx = 550, cy = 380; // a point over the map, clear of the top-left panel
async function hover() {
  await page.mouse.move(cx - 20, cy - 20);
  await page.mouse.move(cx, cy);
  await page.mouse.move(cx + 1, cy + 1);
}
const readoutText = () => page.locator('.cursor-readout').innerText().catch(() => '');
const legendTitle = () => page.locator('.legend-title').innerText().catch(() => '(none)');
const enable = (label) => page.locator('.layer-row', { hasText: label }).locator('input').click();

let pass = true;
const expect = (cond, msg) => { console.log(`${cond ? 'OK  ' : 'FAIL'} ${msg}`); if (!cond) pass = false; };

// Baseline: no overlay -> elevation only, no legend.
await hover();
await page.waitForFunction(() => document.querySelector('.cursor-readout')?.textContent?.match(/\d/), null, { timeout: 8000 });
let txt = await readoutText();
expect(/Elevation/.test(txt) && /ft/.test(txt), `baseline shows elevation: ${JSON.stringify(txt)}`);
expect((await page.locator('.legend').count()) === 0, 'no legend when no overlay active');

for (const [label, statLabel, id] of [
  ['Slope angle', 'Slope', 'slope'],
  ['Aspect', 'Exposure', 'aspect'],
  ['Ground cover', 'Cover', 'groundcover'],
]) {
  await enable(label);
  await hover();
  await page.waitForFunction(
    (sl) => document.querySelector('.cursor-readout')?.textContent?.includes(sl),
    statLabel,
    { timeout: 9000 }
  ).catch(() => {});
  txt = await readoutText();
  const lt = await legendTitle();
  expect(txt.includes(statLabel), `${label}: readout has "${statLabel}" -> ${JSON.stringify(txt)}`);
  expect(lt !== '(none)', `${label}: legend shown ("${lt}")`);
  await page.screenshot({ path: `${outDir}/readout_${id}.png` });
  await enable(label); // turn back off before next
}

// Panel collapse hides the legend entirely.
await enable('Slope angle');
expect((await page.locator('.legend').count()) === 1, 'legend present with slope on + panel open');
await page.locator('.layer-panel-header').click(); // collapse
expect((await page.locator('.legend').count()) === 0, 'legend hidden when panel collapsed');
expect((await page.locator('.layer-row').count()) === 0, 'layer rows hidden when panel collapsed');

console.log(pass ? 'PASS' : 'FAIL');
await browser.close();
process.exit(pass ? 0 : 1);
