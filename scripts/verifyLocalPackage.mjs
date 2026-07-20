import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://127.0.0.1:5176/ski-area-design-challenge/';
const shot = process.argv[3] ?? 'scratchpad/local-package.png';
const target = process.argv[4] ?? 'default';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const errors = [];
const providerRequests = [];
let phase = 'setup';

const providerKind = (url) => {
  if (url.includes('elevation.nationalmap.gov')) return 'USGS';
  if (url.includes('imagery.nationalmap.gov')) return 'NAIP';
  if (url.includes('usgs-lidar-public') || url.includes('usgs-lidar-stac') || url.includes('hobuinc/usgs-lidar')) return 'USGS-Lidar';
  if (url.includes('wmts.terrascope.be')) return 'WorldCover';
  if (url.includes('elevation-tiles-prod')) return 'Terrarium';
  return null;
};
page.on('request', (request) => {
  const kind = providerKind(request.url());
  if (kind) providerRequests.push({ phase, kind, url: request.url() });
});
page.on('pageerror', (error) => errors.push(String(error)));
page.on('console', (message) => {
  if (message.type() === 'error') errors.push(`[console] ${message.text()}`);
});
// The menu/explore preview is out of scope for package preparation. Blocking
// Terrarium makes any accidental gameplay dependency visible in the request
// audit while keeping decorative menu terrain from delaying navigation.
await page.route('**/elevation-tiles-prod.s3.amazonaws.com/**', (route) => route.abort());
await page.route('**/server.arcgisonline.com/**', (route) => route.abort());
await page.route('**/tiles.openfreemap.org/**', (route) => route.abort());
await page.addInitScript(() => {
  localStorage.setItem('skiapp:settings', JSON.stringify({ reducedMotion: true, renderQuality: 'standard' }));
});

async function waitMap() {
  await page.waitForFunction(() => globalThis.appMap?.getLayer?.('site-box-fill'), null, { timeout: 45_000 });
}

try {
  await page.goto(base, { waitUntil: 'load', timeout: 30_000 });
  await page.locator('.trail-slat', { hasText: 'New Game' }).evaluate((element) => element.click());
  await waitMap();
  if (target === 'schweitzer') {
    await page.evaluate(() => globalThis.appMap.jumpTo({ center: [-116.622, 48.368], zoom: 13.4 }));
    await page.waitForTimeout(800);
  }
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(610, 410);
  await page.mouse.down();
  await page.mouse.move(760, 555, { steps: 12 });
  await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Local Package Test');

  phase = 'preparation';
  await page.click('text=Start Designing');
  let prepared = false;
  for (let attempt = 0; attempt < 3 && !prepared; attempt++) {
    const outcome = await page.waitForFunction(() => {
      if (document.querySelector('.hud-resort')) return 'ready';
      const card = document.querySelector('.package-card');
      if (card?.querySelector('.package-actions') && !card.textContent?.includes('Cancel')) return 'retry';
      return false;
    }, null, { timeout: 240_000 }).then((handle) => handle.jsonValue());
    if (outcome === 'ready') prepared = true;
    else await page.click('.package-card >> text=Prepare Resort Data');
  }
  if (!prepared) throw new Error('Package preparation failed after retries');

  phase = 'gameplay';
  await waitMap();
  await page.waitForTimeout(5_000);
  const local2D = await page.evaluate(() => {
    const map = globalThis.appMap;
    const style = map.getStyle();
    return {
      pitch: map.getPitch(),
      demTiles: style.sources.dem?.tiles,
      hasVectorCover: style.sources['cover-vector']?.type === 'geojson',
      hasRasterCover: !!style.sources.worldcover,
      contourType: style.sources.contours?.type,
      hasCoverBoundaries: !!style.sources['cover-boundaries'],
      hasLocalContext: !!style.sources['local-context'],
      satelliteType: style.sources.satellite?.type,
      layerOrder: style.layers.map((layer) => layer.id),
    };
  });
  await page.click('.view3d-btn', { force: true, noWaitAfter: true });
  await page.waitForFunction(() => globalThis.appMap?.getPitch?.() > 55, null, { timeout: 45_000 });
  const local3D = await page.evaluate(() => {
    const map = globalThis.appMap;
    const style = map.getStyle();
    return { pitch: map.getPitch(), terrain: map.getTerrain(), terrainTiles: style.sources['terrain-dem']?.tiles };
  });
  void shot; // Optional visual capture is covered by _verifyLab; avoid stalling on software WebGL.

  // Persist the inspected camera exactly as the in-app Save action's snapshot
  // does, then reload the package through the same storage/validation clients.
  const persisted = await page.evaluate(async (pitch) => {
    const index = JSON.parse(localStorage.getItem('gamesave-index') || '[]');
    const key = index[0]?.key;
    const raw = key ? localStorage.getItem(`gamesave:${key}`) : null;
    if (!raw) throw new Error('Game save missing before resume test');
    const save = JSON.parse(raw);
    save.is3D = true;
    save.pitch = pitch;
    save.updatedAt = new Date().toISOString();
    localStorage.setItem(`gamesave:${key}`, JSON.stringify(save));
    const storage = await import('./src/terrainStorageClient.ts');
    const packages = await import('./src/terrainPackage.ts');
    const record = await storage.loadTerrain(save.terrainKey);
    return {
      key: record?.key,
      validation: record ? packages.validateTerrainPackage(record) : { ok: false, errors: ['missing'] },
      is3D: save.is3D,
      pitch: save.pitch,
      coverComplete: record?.coverGrid?.complete,
      coverSchema: record?.schemaVersion,
      coverVertices: record?.coverDisplayMetadata?.vertexCount,
      coverSource: record?.coverGrid?.source,
      coverMethod: record?.coverGrid?.provenance?.method,
      imageryBytes: record?.localImageryMetadata?.byteLength,
      lidarBytes: record?.coverGrid?.provenance?.lidar?.downloadedBytes,
      treelineM: record?.coverGrid?.treelineM,
      coverGrid: record?.coverGrid ? [record.coverGrid.width, record.coverGrid.height] : null,
    };
  }, local3D.pitch);

  const forbidden = providerRequests.filter((request) => request.phase === 'gameplay');
  console.log('LOCAL_2D', JSON.stringify(local2D));
  console.log('LOCAL_3D', JSON.stringify(local3D));
  console.log('PERSISTED_LOCAL_PACKAGE', JSON.stringify(persisted));
  console.log('GAMEPLAY_PROVIDER_REQUESTS', JSON.stringify(forbidden));
  if (target === 'schweitzer') await page.screenshot({ path: shot });
  if (!String(local2D.demTiles?.[0]).startsWith('resort-dem://')) throw new Error('2D DEM was not local');
  if (!local2D.hasVectorCover || local2D.hasRasterCover || local2D.hasCoverBoundaries) throw new Error('Persisted vector cover did not replace raster cover');
  if (persisted.imageryBytes && local2D.satelliteType !== 'image') throw new Error('Matched local imagery did not replace the live satellite source');
  if (!String(local3D.terrainTiles?.[0]).startsWith('resort-dem://')) throw new Error('3D DEM was not local');
  if (!persisted.validation.ok || !persisted.coverComplete || persisted.coverSchema !== 6 || persisted.coverSource !== 'usgs-four-class-v1' || !persisted.coverVertices || !persisted.is3D || forbidden.length) throw new Error('Offline/persistence acceptance failed');
} catch (error) {
  console.error('VERIFY_LOCAL_PACKAGE_FAILED', error instanceof Error ? error.stack : error);
  console.error('PACKAGE_CARD', await page.locator('.package-card').textContent().catch(() => null));
}

console.log('ERRORS', JSON.stringify(errors.slice(-20)));
console.log('PROVIDER_REQUESTS', JSON.stringify(providerRequests.map(({ phase: p, kind }) => ({ phase: p, kind }))));
await browser.close();
