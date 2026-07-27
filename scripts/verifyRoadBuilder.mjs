// Browser smoke check for the Infrastructure road workflow. Run against a
// preview server with network access so terrain preparation can complete:
// node scripts/verifyRoadBuilder.mjs http://localhost:4173/
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });

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
  await page.fill('.name-entry-input', 'Road Builder Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });

  await page.click('.dock-circle-infrastructure');
  await page.waitForSelector('.dock-infrastructure');
  const option = await page.locator('.infrastructure-panel select option').textContent();
  if (!option?.includes('Two-lane road') || !option.includes('7 m'))
    throw new Error('Two-lane road width option is missing.');
  await page.click('.infrastructure-panel .lift-add-btn');
  await page.mouse.click(520, 540);
  await page.mouse.click(650, 460);
  await page.mouse.click(790, 500);
  await page.click('.infrastructure-panel >> text=Finish route');
  await page.fill('.infrastructure-panel .name-entry-input', 'Service Road');
  await page.click('.infrastructure-panel .site-btn-primary');
  await page.waitForSelector('.infrastructure-panel >> text=Service Road');

  const built = await page.evaluate(() => {
    const map = globalThis.appMap;
    const feature = map.getSource('local-context')?._data?.features
      ?.find((item) => item.properties?.playerBuilt === true);
    return { feature, visibility: map.getLayoutProperty('local-roads', 'visibility') ?? 'visible' };
  });
  if (!built.feature || built.feature.properties.class !== 'minor')
    throw new Error('Built road was not merged into local road context.');

  await page.click('.dock-circle-layers');
  await page.waitForSelector('.dock-layers');
  const roadsToggle = page.locator('.dock-layers .layer-row', { hasText: 'Roads' }).locator('input');
  await roadsToggle.click();
  const hidden = await page.evaluate(() => globalThis.appMap.getLayoutProperty('local-roads', 'visibility'));
  if (hidden !== 'none') throw new Error('Roads toggle did not hide player roads.');
  await roadsToggle.click();

  console.log(JSON.stringify({ option, builtRoad: built.feature.properties, originalVisibility: built.visibility }, null, 2));
} catch (error) {
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}
