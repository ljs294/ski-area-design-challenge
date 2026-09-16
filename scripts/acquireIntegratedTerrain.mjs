import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { createServer as createViteServer } from 'vite';
import { listenWeatherService } from '../weather-service/server.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const outputRoot = path.resolve(repositoryRoot, 'test-results', 'integrated-fixtures', 'jackson');
const relativeOutputRoot = 'test-results/integrated-fixtures/jackson';
const host = '127.0.0.1';
const vitePort = 44581;
const weatherPort = 8789;
const elevationEndpoint = 'https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage';
const naipExportEndpoint = 'https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPPlus/ImageServer/exportImage';
const glyphBaseUrl = 'https://tiles.openfreemap.org/fonts/Noto%20Sans%20Regular';
const glyphRanges = ['0-255', '8192-8447'];
const notoLicenseUrl = 'https://raw.githubusercontent.com/notofonts/latin-greek-cyrillic/main/OFL.txt';

// A six-kilometre square centered on the Black Mountain/Jackson, NH slopes.
// Longitude span accounts for latitude so the requested footprint is square in metres.
const site = Object.freeze({
  bounds: [[-71.20255, 44.13905], [-71.12745, 44.19295]],
  widthKm: 6,
  heightKm: 6,
});
const mountainName = 'Jackson Black Mountain Benchmark';

function assertSafeOutput(filePath) {
  const resolved = path.resolve(filePath);
  const relative = path.relative(outputRoot, resolved);
  if (relative.startsWith('..') || path.isAbsolute(relative) || relative === '') {
    throw new Error(`Refusing to write outside the Jackson fixture directory: ${resolved}`);
  }
  return resolved;
}

function relativeFixturePath(filePath) {
  return path.relative(repositoryRoot, filePath).replaceAll(path.sep, '/');
}

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

async function artifact(filePath) {
  const bytes = await readFile(filePath);
  return { path: relativeFixturePath(filePath), bytes: bytes.byteLength, sha256: sha256(bytes) };
}

async function writeJson(filePath, value) {
  const target = assertSafeOutput(filePath);
  await writeFile(target, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

async function closeServer(server) {
  if (!server?.listening) return;
  server.closeAllConnections?.();
  await new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
}

async function fetchRequiredAsset(url, filePath) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Required presentation asset returned ${response.status}: ${url}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength === 0) throw new Error(`Required presentation asset was empty: ${url}`);
  await writeFile(assertSafeOutput(filePath), bytes);
  return bytes;
}

async function triggerJsonDownload(page, fileName, expression) {
  const downloadPromise = page.waitForEvent('download');
  await page.evaluate(({ fileName: name, expression: source }) => {
    const value = Function(`return (${source})`)();
    const blob = new Blob([JSON.stringify(value)], { type: 'application/json' });
    const anchor = document.createElement('a');
    anchor.href = URL.createObjectURL(blob);
    anchor.download = name;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(anchor.href), 1_000);
  }, { fileName, expression });
  const download = await downloadPromise;
  const failure = await download.failure();
  if (failure) throw new Error(`Browser export ${fileName} failed: ${failure}`);
  await download.saveAs(assertSafeOutput(path.join(outputRoot, fileName)));
}

async function triggerByteDownload(page, fileName, expression) {
  const downloadPromise = page.waitForEvent('download');
  await page.evaluate(({ fileName: name, expression: source }) => {
    const value = Function(`return (${source})`)();
    const blob = new Blob([value], { type: 'application/octet-stream' });
    const anchor = document.createElement('a');
    anchor.href = URL.createObjectURL(blob);
    anchor.download = name;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(anchor.href), 1_000);
  }, { fileName, expression });
  const download = await downloadPromise;
  const failure = await download.failure();
  if (failure) throw new Error(`Browser export ${fileName} failed: ${failure}`);
  await download.saveAs(assertSafeOutput(path.join(outputRoot, fileName)));
}

async function writePartialManifest(provenance, roles = {}, presentation = []) {
  const manifest = {
    schemaVersion: 1,
    fixtureId: 'jackson-black-mountain-live-v1',
    region: 'Jackson / Black Mountain, White Mountains, New Hampshire, USA',
    requestedSite: site,
    artifacts: {
      terrainRecord: roles.terrainRecord ?? { path: `${relativeOutputRoot}/terrain.json`, status: 'pending' },
      imagery: roles.imagery ?? { path: `${relativeOutputRoot}/imagery.jpg`, status: 'pending' },
      weatherPackage: roles.weatherPackage ?? { path: `${relativeOutputRoot}/weather-package.json`, status: 'pending' },
      checkpoint1000: { path: `${relativeOutputRoot}/save-1000.json`, status: 'pending' },
      checkpoint3000: { path: `${relativeOutputRoot}/save-3000.json`, status: 'pending' },
    },
    requiredPresentationAssets: presentation,
    acquisition: provenance,
  };
  await writeJson(path.join(outputRoot, 'fixture-manifest.json'), manifest);
}

await mkdir(outputRoot, { recursive: true });
await mkdir(path.join(outputRoot, 'fonts', 'Noto Sans Regular'), { recursive: true });
await mkdir(path.join(outputRoot, 'provider-cache'), { recursive: true });

const provenance = {
  startedAt: new Date().toISOString(),
  status: 'running',
  sourceTypeRequired: 'live',
  syntheticInputsAllowed: false,
  requestedSite: site,
  elevationRequestOverride: {
    endpoint: elevationEndpoint,
    appliedFlag: 'adjustAspectRatio=false',
    requests: [],
  },
  sources: {
    elevation: 'USGS 3DEP ImageServer',
    imagery: 'USDA/USGS NAIP public-domain orthoimagery',
    worldCover: {
      id: 'esa-worldcover-2021-v200',
      license: 'CC-BY-4.0',
      attribution: '© ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium',
    },
    weather: 'Daymet V4 R1 and NASA POWER hourly, source policy 2001-2025',
  },
  naipExportRequests: [],
  progress: [],
};
await writePartialManifest(provenance);

let vite;
let weather;
let browser;
const roles = {};
const presentation = [];

try {
  process.env.VITE_WEATHER_SERVICE_URL = `http://${host}:${weatherPort}`;
  weather = listenWeatherService({
    mode: 'live',
    port: weatherPort,
    host,
    cacheDirectory: path.join(outputRoot, 'provider-cache'),
  });
  await new Promise((resolve, reject) => {
    weather.server.once('listening', resolve);
    weather.server.once('error', reject);
  });

  vite = await createViteServer({
    configFile: path.join(repositoryRoot, 'vite.config.web.ts'),
    root: repositoryRoot,
    server: { host, port: vitePort, strictPort: true },
  });
  await vite.listen();

  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ acceptDownloads: true });
  const page = await context.newPage();
  page.setDefaultTimeout(30 * 60_000);
  page.on('console', (message) => {
    const text = message.text();
    if (message.type() === 'error' || /\[(terrain-cover|worldcover)\]/.test(text)) {
      process.stderr.write(`[browser:${message.type()}] ${text}\n`);
    }
  });
  await page.route(`${elevationEndpoint}**`, async (route) => {
    const originalUrl = route.request().url();
    const adjusted = new URL(originalUrl);
    adjusted.searchParams.set('adjustAspectRatio', 'false');
    provenance.elevationRequestOverride.requests.push({ originalUrl, appliedUrl: adjusted.href });
    await writePartialManifest(provenance, roles, presentation);
    await route.continue({ url: adjusted.href });
  });
  await page.route(`${naipExportEndpoint}**`, async (route) => {
    const url = route.request().url();
    let response;
    let body;
    for (let attempt = 1; attempt <= 4; attempt++) {
      const candidate = new URL(url);
      // ArcGIS repeatedly times out mosaicking this six-scene crop at the
      // product's 2 m request. Preserve the scene lock and full bounds while
      // retrying at a smaller raster after the unchanged request fails once.
      if (attempt > 1) candidate.searchParams.set('size', '1999,2000');
      response = await route.fetch({ url: candidate.href, timeout: 10 * 60_000 });
      body = await response.body();
      provenance.naipExportRequests.push({
        originalUrl: url,
        appliedUrl: candidate.href,
        attempt,
        status: response.status(),
        bytes: body.byteLength,
        sha256: sha256(body),
        browserAccessHeaderApplied: 'access-control-allow-origin: *',
        ...(attempt > 1 ? { rasterRetryReason: 'USGS gateway timeout at the product 2 m export dimensions' } : {}),
      });
      if (response.status() < 500) break;
      await new Promise((resolve) => setTimeout(resolve, attempt * 1_500));
    }
    await writePartialManifest(provenance, roles, presentation);
    await route.fulfill({
      response,
      body,
      headers: { ...response.headers(), 'access-control-allow-origin': '*' },
    });
  });
  await page.goto(`http://${host}:${vitePort}/ski-area-design-challenge/`, { waitUntil: 'domcontentloaded' });

  provenance.progress.push({ stage: 'terrain', startedAt: new Date().toISOString() });
  const terrainSummary = await page.evaluate(async ({ site: requestedSite, mountainName: requestedName }) => {
    const [{ prepareResortPackage }, { sampleSiteCoverGrid }, { validateTerrainPackage }, { generateBareSnowGrid }] = await Promise.all([
      import('/ski-area-design-challenge/src/terrainIngest.ts'),
      import('/ski-area-design-challenge/src/app/worldcoverProtocol.ts'),
      import('/ski-area-design-challenge/src/terrainPackage.ts'),
      import('/ski-area-design-challenge/src/snow.ts'),
    ]);
    const mapContextFailures = [];
    const terrain = await prepareResortPackage(requestedSite, requestedName, { sampleSiteCoverGrid }, {
      onProgress: (entry) => console.info(`[acquisition:${entry.phase}] ${entry.completed}/${entry.total} ${entry.message}`),
      onMapContextFailure: async (error) => {
        mapContextFailures.push({ message: error instanceof Error ? error.message : String(error) });
        return 'continue';
      },
    });
    const validation = validateTerrainPackage(terrain);
    if (!validation.ok) throw new Error(`Terrain validation failed: ${validation.errors.join(' ')}`);
    if (terrain.sourceType !== 'live') throw new Error(`Expected live terrain source, received ${terrain.sourceType}`);
    if (!terrain.localImagery || !terrain.localImageryMetadata || terrain.localImagery.byteLength === 0) {
      throw new Error('NAIP imagery is required for integrated benchmark qualification.');
    }
    if (terrain.coverGrid?.provenance?.method !== 'naip-worldcover') {
      throw new Error(`NAIP-derived terrain cover is required; received ${terrain.coverGrid?.provenance?.method ?? 'missing provenance'}.`);
    }
    const snow = generateBareSnowGrid(terrain);
    if (snow.width !== 512 || snow.height !== 512) {
      throw new Error(`Production bare-snow grid must be 512x512; received ${snow.width}x${snow.height}.`);
    }
    window.__integratedTerrain = terrain;
    window.__integratedTerrainJson = {
      ...terrain,
      sampleHeights: Array.from(terrain.sampleHeights),
      coverGrid: terrain.coverGrid ? { ...terrain.coverGrid, data: Array.from(terrain.coverGrid.data) } : undefined,
      originalCoverGrid: terrain.originalCoverGrid
        ? { ...terrain.originalCoverGrid, data: Array.from(terrain.originalCoverGrid.data) }
        : undefined,
      coverBoundarySegments: terrain.coverBoundarySegments ? Array.from(terrain.coverBoundarySegments) : undefined,
      coverDisplayGeometry: terrain.coverDisplayGeometry ? Array.from(terrain.coverDisplayGeometry) : undefined,
      contourSegments: terrain.contourSegments ? Array.from(terrain.contourSegments) : undefined,
      localImagery: undefined,
    };
    return {
      key: terrain.key,
      bounds: terrain.bounds,
      requestedBounds: requestedSite.bounds,
      sampleGridSize: terrain.sampleGridSize,
      heightCount: terrain.sampleHeights.length,
      coverDimensions: terrain.coverGrid ? [terrain.coverGrid.width, terrain.coverGrid.height] : null,
      originalCoverDimensions: terrain.originalCoverGrid
        ? [terrain.originalCoverGrid.width, terrain.originalCoverGrid.height]
        : null,
      imageryBytes: terrain.localImagery.byteLength,
      imageryMetadata: terrain.localImageryMetadata,
      coverMethod: terrain.coverGrid.provenance.method,
      sourceType: terrain.sourceType,
      snowDimensions: [snow.width, snow.height],
      validation,
      mapContextFailures,
    };
  }, { site, mountainName });
  provenance.terrain = terrainSummary;
  provenance.progress.at(-1).completedAt = new Date().toISOString();

  await triggerJsonDownload(page, 'terrain.json', 'window.__integratedTerrainJson');
  await triggerByteDownload(page, 'imagery.jpg', 'window.__integratedTerrain.localImagery');
  roles.terrainRecord = await artifact(path.join(outputRoot, 'terrain.json'));
  roles.imagery = await artifact(path.join(outputRoot, 'imagery.jpg'));
  if (roles.imagery.bytes !== terrainSummary.imageryMetadata.byteLength) {
    throw new Error('Exported imagery byte length differs from terrain metadata.');
  }

  provenance.progress.push({ stage: 'weather', startedAt: new Date().toISOString() });
  const weatherSummary = await page.evaluate(async () => {
    const [{ prepareWeatherPackage }, { validateWeatherPackage }] = await Promise.all([
      import('/ski-area-design-challenge/src/weatherServiceClient.ts'),
      import('/ski-area-design-challenge/src/weatherStorageClient.ts'),
    ]);
    const weatherPackage = await prepareWeatherPackage(window.__integratedTerrain, {
      onProgress: (job) => console.info(`[acquisition:weather:${job.progress.stage}] ${job.progress.message}`),
    });
    if (!validateWeatherPackage(weatherPackage)) throw new Error('Production weather package validation failed.');
    window.__integratedWeatherPackage = weatherPackage;
    return {
      schemaVersion: weatherPackage.manifest.schemaVersion,
      contentHash: weatherPackage.manifest.contentHash,
      quality: weatherPackage.manifest.quality,
      sourceSummary: weatherPackage.manifest.sourceSummary,
      historicalYears: [weatherPackage.manifest.historicalStartYear, weatherPackage.manifest.historicalEndYear],
      chunkCount: weatherPackage.manifest.chunks?.length ?? 0,
      sources: weatherPackage.manifest.sources,
    };
  });
  provenance.weather = weatherSummary;
  provenance.progress.at(-1).completedAt = new Date().toISOString();
  await triggerJsonDownload(page, 'weather-package.json', 'window.__integratedWeatherPackage');
  roles.weatherPackage = await artifact(path.join(outputRoot, 'weather-package.json'));

  provenance.progress.push({ stage: 'presentation-assets', startedAt: new Date().toISOString() });
  for (const range of glyphRanges) {
    const sourceUrl = `${glyphBaseUrl}/${range}.pbf`;
    const filePath = path.join(outputRoot, 'fonts', 'Noto Sans Regular', `${range}.pbf`);
    await fetchRequiredAsset(sourceUrl, filePath);
    presentation.push({
      role: `glyph:Noto Sans Regular:${range}`,
      ...await artifact(filePath),
      sourceUrl,
      license: 'SIL-OFL-1.1',
    });
  }
  const licensePath = path.join(outputRoot, 'fonts', 'OFL.txt');
  await fetchRequiredAsset(notoLicenseUrl, licensePath);
  presentation.push({
    role: 'glyph-license:Noto Sans',
    ...await artifact(licensePath),
    sourceUrl: notoLicenseUrl,
    license: 'SIL-OFL-1.1',
  });
  provenance.progress.at(-1).completedAt = new Date().toISOString();

  provenance.status = 'acquired';
  provenance.completedAt = new Date().toISOString();
  // A failed attempt is useful diagnosis, but must not look like the status of
  // a later successful fixture. Preserve it separately and mark recovery.
  try {
    const failurePath = path.join(outputRoot, 'acquisition-failure.json');
    const previousFailure = JSON.parse(await readFile(failurePath, 'utf8'));
    if (previousFailure?.status !== 'recovered') {
      await writeJson(path.join(outputRoot, 'prior-acquisition-failure.json'), previousFailure);
    }
    await writeJson(failurePath, {
      status: 'recovered',
      recoveredAt: provenance.completedAt,
      note: 'The current fixture acquired successfully; transient provider responses are retained in fixture-manifest.json.',
    });
  } catch {
    // No prior failed attempt exists.
  }
  await writePartialManifest(provenance, roles, presentation);
  process.stdout.write(`${JSON.stringify({ outputRoot, terrain: terrainSummary, weather: weatherSummary, roles, presentation }, null, 2)}\n`);
} catch (error) {
  const failure = {
    stage: provenance.progress.at(-1)?.stage ?? 'startup',
    name: error instanceof Error ? error.name : 'Error',
    message: error instanceof Error ? error.message : String(error),
    stack: error instanceof Error ? error.stack : undefined,
    failedAt: new Date().toISOString(),
  };
  provenance.status = 'failed';
  provenance.failure = failure;
  await writeJson(path.join(outputRoot, 'acquisition-failure.json'), failure);
  await writePartialManifest(provenance, roles, presentation);
  process.stderr.write(`[acquisition:${failure.stage}] ${failure.message}\n`);
  process.exitCode = 1;
} finally {
  await browser?.close().catch(() => undefined);
  await vite?.close().catch(() => undefined);
  await closeServer(weather?.server).catch(() => undefined);
}
