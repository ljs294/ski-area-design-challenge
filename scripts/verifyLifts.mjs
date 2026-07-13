// E2E: draw a fixed-grip chairlift, confirm it, and check render + persistence.
// Best-effort under headless SwiftShader (2D only — no terrain mesh).
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
  // Create a resort (site box -> name -> start).
  await page.click('.site-btn >> text=Select site');
  await page.waitForTimeout(300);
  await page.mouse.move(600,380); await page.mouse.down();
  await page.mouse.move(780,540,{steps:15}); await page.mouse.up();
  await page.waitForTimeout(400);
  await page.click('.site-btn >> text=View this area');
  await page.waitForSelector('.name-entry-input',{timeout:5000});
  await page.fill('.name-entry-input','Lift Test Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort',{timeout:8000});
  await page.waitForTimeout(1000); // let fitBounds settle

  // Draw a lift: arm the tool, click two terminals.
  await page.click('.site-btn >> text=New lift');
  await page.waitForTimeout(300);
  await page.mouse.click(500,500);
  await page.waitForTimeout(300);
  await page.mouse.click(720,340);
  await page.waitForSelector('.lift-panel',{timeout:5000});
  console.log('PANEL_OPEN: true');
  // Stats resolve via real DEM fetch; accept either the Vertical row or the error+Retry path.
  const outcome = await Promise.race([
    page.waitForSelector('text=Vertical',{timeout:25000}).then(()=> 'elevation-ok'),
    page.waitForSelector('.lift-link-btn',{timeout:25000}).then(()=> 'elevation-error'),
  ]).catch(()=> 'timeout');
  console.log('ELEVATION:', outcome);
  const panelText = await page.$eval('.lift-panel', el=>el.textContent);
  console.log('PANEL_TEXT:', panelText.replace(/\s+/g,' ').slice(0,300));
  await page.screenshot({path:`${outDir}/lift-review.png`});
  // New lifts default to Planning; the primary button reads "Add to plan".
  await page.click('.lift-panel .site-btn-primary');
  await page.waitForTimeout(500);

  // Rendered features: 1 line + 2 terminals.
  const featureCount = await page.evaluate(()=>{
    const src = globalThis.appMap.getSource('lifts');
    return src.serialize().data.features.length;
  });
  console.log('LIFT_FEATURES:', featureCount, '(expect 3)');
  const rowText = await page.$eval('.lift-row', el=>el.textContent).catch(()=>null);
  console.log('LIST_ROW:', rowText);

  // Capacity badge marker at the base terminal.
  const badgeText = await page.$eval('.lift-badge .lift-badge-cap', el=>el.textContent).catch(()=>null);
  const badgeSeats = await page.$$eval('.lift-badge svg circle', els=>els.length).catch(()=>0);
  console.log('BADGE_CAP:', badgeText, '| BADGE_CIRCLES:', badgeSeats, '(double = 3: grip+2 heads)');
  const planningDash = await page.$eval('.lift-badge', el=>el.classList.contains('lift-badge--planning')).catch(()=>null);
  console.log('BADGE_PLANNING_CLASS:', planningDash);

  // Edit flow: open the lift, flip it to Complete, confirm the line restyles.
  await page.click('.lift-row-btn');
  await page.waitForSelector('.lift-status-toggle',{timeout:5000});
  await page.click('.lift-status-btn >> text=Complete');
  await page.waitForTimeout(400);
  const statusAfter = await page.evaluate(()=>{
    const src = globalThis.appMap.getSource('lifts');
    const line = src.serialize().data.features.find(f=>f.properties.kind==='line');
    return line && line.properties.status;
  });
  console.log('STATUS_AFTER_EDIT:', statusAfter, '(expect complete)');
  const badgeSolid = await page.$eval('.lift-badge', el=>!el.classList.contains('lift-badge--planning')).catch(()=>null);
  console.log('BADGE_SOLID_AFTER_COMPLETE:', badgeSolid);
  await page.click('.lift-panel .site-btn-primary'); // Done
  await page.waitForTimeout(300);

  // Persist + verify the save payload.
  await page.click('.hud-save');
  await page.waitForTimeout(800);
  const savedLift = await page.evaluate(()=>{
    const idx = JSON.parse(localStorage.getItem('gamesave-index')||'[]');
    if (!idx.length) return null;
    const save = JSON.parse(localStorage.getItem('gamesave:'+idx[0].key));
    return save.lifts[0] ?? null;
  });
  console.log('SAVED_LIFT:', JSON.stringify(savedLift, null, 1));
  const ok = savedLift && savedLift.liftClass==='fixed-grip' && savedLift.chairSize===2
    && typeof savedLift.lengthM==='number' && savedLift.lengthM>0
    && typeof savedLift.capacityPph==='number' && savedLift.points.length===2;
  console.log('SAVE_SHAPE_OK:', !!ok);
  await page.screenshot({path:`${outDir}/lift-built.png`});
} catch(e){ console.error('FAIL:', e.message); }
console.log('=== ERRORS ==='); console.log(errors.join('\n')||'(none)');
await browser.close();
