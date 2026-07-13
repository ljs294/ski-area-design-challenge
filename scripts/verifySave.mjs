import { chromium } from 'playwright';
const base = process.argv[2] ?? 'http://localhost:4173/';
const outDir = process.argv[3] ?? 'scratchpad';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader','--use-gl=angle','--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors=[]; page.on('pageerror',e=>errors.push(String(e)));
page.on('console',m=>{ if(m.type()==='error') errors.push('[c] '+m.text()); });
async function waitStyle(g){ await page.waitForFunction((k)=>{const m=globalThis[k];return m&&m.isStyleLoaded&&m.isStyleLoaded();}, g, {timeout:30000}); await page.waitForTimeout(1200); }
try {
  await page.goto(base,{waitUntil:'load'});
  await page.evaluate(()=>localStorage.clear());
  await page.reload({waitUntil:'load'});
  await waitStyle('menuMap');
  await page.click('.trail-slat >> text=New Game');
  await waitStyle('appMap');
  // Draw a site box: enter selecting mode, then drag on the map canvas.
  await page.click('.site-btn >> text=Select site');
  await page.waitForTimeout(300);
  await page.mouse.move(600,380); await page.mouse.down();
  await page.mouse.move(780,540,{steps:15}); await page.mouse.up();
  await page.waitForTimeout(400);
  await page.click('.site-btn >> text=View this area');
  await page.waitForSelector('.name-entry-input',{timeout:5000});
  await page.fill('.name-entry-input','Test Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort',{timeout:8000});
  const resortName = await page.$eval('.hud-resort', el=>el.textContent);
  console.log('SAVED_RESORT:', resortName);
  // Confirm it landed in storage.
  const stored = await page.evaluate(()=>JSON.parse(localStorage.getItem('gamesave-index')||'[]'));
  console.log('STORED_COUNT:', stored.length, 'NAMES:', JSON.stringify(stored.map(s=>s.name)));
  // Back to menu; Continue should now be enabled.
  await page.click('.hud-quit');
  await page.waitForSelector('.main-menu',{timeout:8000});
  await waitStyle('menuMap');
  const continueDisabled = await page.$eval('.trail-slat', el=>el.disabled);
  console.log('CONTINUE_DISABLED_AFTER_SAVE:', continueDisabled);
  // Continue should reopen the saved resort.
  await page.click('.trail-slat >> text=Continue Game');
  await waitStyle('appMap');
  const resumed = await page.$eval('.hud-resort', el=>el.textContent).catch(()=>null);
  console.log('RESUMED_RESORT:', resumed);
  await page.screenshot({path:`${outDir}/resumed.png`});
} catch(e){ console.error('FAIL:', e.message); }
console.log('=== ERRORS ==='); console.log(errors.join('\n')||'(none)');
await browser.close();
