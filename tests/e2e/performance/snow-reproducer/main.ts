import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { registerSnowProtocol, setActiveSnowGrid, snowProtocolUrl } from '../../../../src/app/snowProtocol';
import { registerResortProtocols, requestResortTile, resortDemBounds, resortProtocolUrl, RESORT_DEM_PROTOCOL,
  setActiveResortTerrain } from '../../../../src/app/resortProtocols';
import { generateBareSnowGrid, sampleSnowGrid } from '../../../../src/snow';
import type { TerrainRecord } from '../../../../src/types';
import { benchmarkTelemetrySnapshot } from '../../../../src/app/integratedBenchmarkTelemetry';
import { preparedTerrainFixture } from '../../support/preparedResort';
import { setupAnalysisLayers } from '../../../../src/app/analysisLayers';
import { createGameBasemapStyle } from '../../../../src/app/masterPlanStyle';
import { applyTileLod } from '../../../../src/app/terrainLod';
import { mountTerrain, TERRAIN_DEM_SOURCE } from '../../../../src/app/terrain3d';

type ReproducerCase = 'known-raster' | 'snow-flat' | 'snow-dem' | 'snow-ci-dem' | 'analysis-composition';
type EventRecord = { type: string; detail?: unknown; at: number };
const events: EventRecord[] = [];
let knownRegistered = false;
let activeMap: maplibregl.Map | null = null;
let demInspection: Record<string, unknown> | null = null;

async function inspectDemAt(lng: number, lat: number, terrain: TerrainRecord, z: number) {
  const n = 2 ** z;
  const xf = (lng + 180) / 360 * n;
  const yf = (1 - Math.asinh(Math.tan(lat * Math.PI / 180)) / Math.PI) / 2 * n;
  const x = Math.floor(xf), y = Math.floor(yf), px = Math.floor((xf - x) * 256), py = Math.floor((yf - y) * 256);
  const url = resortProtocolUrl(RESORT_DEM_PROTOCOL, terrain).replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y));
  const data = await requestResortTile('dem', url);
  const bitmap = await createImageBitmap(new Blob([data], { type: 'image/png' }));
  const canvas = new OffscreenCanvas(256, 256), context = canvas.getContext('2d')!;
  context.drawImage(bitmap, 0, 0); const rgba = [...context.getImageData(px, py, 1, 1).data]; bitmap.close();
  return { url, pixel: { x: px, y: py }, rgba,
    decodedElevation: rgba[0]! * 256 + rgba[1]! + rgba[2]! / 256 - 32768 };
}

function solidPng(red: number, green: number, blue: number): Promise<ArrayBuffer> {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
  const context = canvas.getContext('2d')!; context.fillStyle = `rgb(${red},${green},${blue})`; context.fillRect(0, 0, 256, 256);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob?.arrayBuffer().then(resolve, reject)
    ?? reject(new Error('PNG encoding failed')), 'image/png'));
}

async function waitForIdle(map: maplibregl.Map): Promise<void> {
  await new Promise<void>((resolve, reject) => { const timeout = window.setTimeout(() => reject(new Error('map idle deadline')), 30_000);
    map.once('idle', () => { window.clearTimeout(timeout); resolve(); }); });
}

async function runCase(kind: ReproducerCase, terrainUrl?: string) {
  activeMap?.remove(); activeMap = null;
  events.length = 0; demInspection = null; document.querySelector('#map')!.replaceChildren(); setActiveResortTerrain(null); setActiveSnowGrid(null);
  (globalThis as typeof globalThis & Record<string, unknown>).__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__ = { enabled: true, maxEntries: 512 };
  if (!knownRegistered) { knownRegistered = true; maplibregl.addProtocol('known-raster', async params => {
    events.push({ type: 'known-request', detail: params.url, at: performance.now() }); return { data: await solidPng(230, 32, 72) }; }); }
  const ciTerrain = kind !== 'snow-dem';
  const center: [number, number] = ciTerrain ? [-121.495, 46.902] : [-71.165, 44.166];
  let terrain: TerrainRecord | null = null;
  let snowGrid: ReturnType<typeof generateBareSnowGrid> | null = null;
  if (ciTerrain) terrain = preparedTerrainFixture() as unknown as TerrainRecord;
  else if (terrainUrl) terrain = await fetch(terrainUrl).then(response => response.json()) as TerrainRecord;
  if (terrain) { setActiveResortTerrain(terrain); registerResortProtocols(); }
  const bounds = terrain ? resortDemBounds(terrain)! : [-71.20255, 44.13905, -71.12745, 44.19295] as [number, number, number, number];
  const style: maplibregl.StyleSpecification = kind === 'analysis-composition' ? createGameBasemapStyle()
    : { version: 8, sources: {}, layers: [{ id: 'paper', type: 'background', paint: { 'background-color': '#18212a' } }] };
  const map = new maplibregl.Map({ container: 'map', style, center, zoom: 17.0426, bearing: 0,
    pitch: ciTerrain ? 35 : 0,
    interactive: false, attributionControl: false, pixelRatio: 1 });
  activeMap = map;
  map.on('error', event => events.push({ type: 'map-error', detail: String(event.error), at: performance.now() }));
  map.on('sourcedata', event => { if (event.sourceId) events.push({ type: 'source-data', detail: event.sourceId, at: performance.now() }); });
  await new Promise<void>(resolve => map.once('load', resolve));
  if (kind === 'known-raster') {
    map.addSource('case', { type: 'raster', tiles: ['known-raster://tile/{z}/{x}/{y}'], tileSize: 256, maxzoom: 18, bounds });
  } else if (kind === 'analysis-composition') {
    const grid = generateBareSnowGrid(terrain!); grid.depthM.fill(.8); grid.surface.fill(1); snowGrid = grid; setActiveSnowGrid(grid);
    setupAnalysisLayers(map, terrain, 'imperial', null, null, {}, {}, grid, 'depth', 'standard');
    applyTileLod(map, 'standard');
    mountTerrain(map, 'standard');
    // Match App hydration: the analysis source stays hidden while terrain boots,
    // then the player reveals Snow only after the initial scene is ready.
    await new Promise<void>((resolve, reject) => {
      const deadline = performance.now() + 30_000;
      const check = () => {
        if (benchmarkTelemetrySnapshot()?.entries.some(entry => entry.stage === 'terrain-dem-generated')) resolve();
        else if (performance.now() >= deadline) reject(new Error('hidden-snow terrain readiness deadline'));
        else window.setTimeout(check, 50);
      }; check();
    });
    map.setLayoutProperty('snow', 'visibility', 'visible');
  } else {
    const grid = terrain ? generateBareSnowGrid(terrain) : { bounds: { west: bounds[0], south: bounds[1], east: bounds[2], north: bounds[3] },
      width: 128, height: 128, depthM: new Float32Array(128 * 128), surface: new Uint8Array(128 * 128) };
    grid.depthM.fill(.8); grid.surface.fill(1); snowGrid = grid; setActiveSnowGrid(grid); registerSnowProtocol();
    map.addSource('case', { type: 'raster', tiles: [snowProtocolUrl('depth')], tileSize: 256, maxzoom: 18, bounds });
    if (terrain && (kind === 'snow-dem' || kind === 'snow-ci-dem')) {
      applyTileLod(map, 'standard');
      mountTerrain(map, 'standard');
    }
  }
  if (kind !== 'analysis-composition') map.addLayer({ id: 'case', type: 'raster', source: 'case',
    paint: { 'raster-opacity': 1, 'raster-fade-duration': 0 } });
  const sourceId = kind === 'analysis-composition' ? 'snow' : 'case';
  if (kind === 'snow-dem' || kind === 'snow-ci-dem' || kind === 'analysis-composition') await new Promise<void>((resolve, reject) => {
    const deadline = performance.now() + 45_000;
    const check = () => {
      const telemetry = benchmarkTelemetrySnapshot();
      if (telemetry?.entries.some(entry => entry.stage === 'snow-tile-generated')
        && telemetry.entries.some(entry => entry.stage === 'terrain-dem-generated')) resolve();
      else if (performance.now() >= deadline) reject(new Error('snow+DEM public readiness deadline'));
      else window.setTimeout(check, 50);
    }; check();
  }); else await waitForIdle(map);
  if (kind === 'snow-dem' || kind === 'snow-ci-dem' || kind === 'analysis-composition') await new Promise<void>((resolve, reject) => {
    const deadline = performance.now() + 45_000;
    const check = () => {
      if (map.isSourceLoaded(sourceId)) resolve();
      else if (performance.now() >= deadline) reject(new Error('visible snow source readiness deadline'));
      else window.setTimeout(check, 50);
    }; check();
  });
  if (terrain && (kind === 'snow-dem' || kind === 'snow-ci-dem' || kind === 'analysis-composition')) {
    demInspection = { tiles: await Promise.all([15, 14, 5].map(z => inspectDemAt(center[0], center[1], terrain, z))) };
  }
  if (kind === 'snow-dem' || kind === 'snow-ci-dem' || kind === 'analysis-composition') await new Promise<void>((resolve, reject) => {
    const deadline = performance.now() + 45_000;
    const check = () => {
      const elevation = map.queryTerrainElevation(map.getCenter());
      if (elevation !== null && Number.isFinite(elevation) && elevation > -10_000) resolve();
      else if (performance.now() >= deadline) reject(new Error('terrain elevation readiness deadline'));
      else { map.triggerRepaint(); window.setTimeout(check, 50); }
    }; check();
  });
  await new Promise<void>(resolve => { map.once('render', resolve); map.triggerRepaint(); });
  const gl = map.getCanvas().getContext('webgl2')!;
  const centerPixel = new Uint8Array(4), controlPixel = new Uint8Array(4);
  gl.readPixels(Math.floor(gl.drawingBufferWidth / 2), Math.floor(gl.drawingBufferHeight / 2),
    1, 1, gl.RGBA, gl.UNSIGNED_BYTE, centerPixel);
  gl.readPixels(40, 40, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, controlPixel);
  const frame = new Uint8Array(gl.drawingBufferWidth * gl.drawingBufferHeight * 4);
  gl.readPixels(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight, gl.RGBA, gl.UNSIGNED_BYTE, frame);
  let bluePixelCount = 0;
  for (let index = 0; index < frame.length; index += 4) {
    if (frame[index + 2]! > frame[index]! + 20) bluePixelCount++;
  }
  let withoutCoverPixel: number[] | undefined;
  if (kind === 'analysis-composition') {
    map.setLayoutProperty('groundcover', 'visibility', 'none');
    await new Promise<void>(resolve => { map.once('render', resolve); map.triggerRepaint(); });
    const pixel = new Uint8Array(4); gl.readPixels(Math.floor(gl.drawingBufferWidth / 2),
      Math.floor(gl.drawingBufferHeight / 2), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
    withoutCoverPixel = [...pixel];
  }
  const source = (map.getSource(sourceId) as maplibregl.RasterTileSource).serialize();
  return { kind, source, demSource: map.getSource(TERRAIN_DEM_SOURCE)?.serialize() ?? null,
    terrain: map.getTerrain(), center: map.getCenter(), zoom: map.getZoom(),
    pitch: map.getPitch(), elevation: map.getCenter().lat === center[1] ? map.queryTerrainElevation(map.getCenter()) : null,
    loaded: map.loaded(), events: [...events],
    telemetry: benchmarkTelemetrySnapshot(), framebuffer: { width: gl.drawingBufferWidth, height: gl.drawingBufferHeight },
    layers: map.getStyle().layers.map(layer => ({ id: layer.id, type: layer.type,
      visibility: map.getLayoutProperty(layer.id, 'visibility') ?? 'visible' })),
    pixels: { center: [...centerPixel], control: [...controlPixel], bluePixelCount,
      gridAtCameraCenter: snowGrid ? sampleSnowGrid(snowGrid, center[0], center[1]) : null,
      ...(withoutCoverPixel ? { withoutCover: withoutCoverPixel } : {}) } };
}

function snapshot() {
  const map = activeMap;
  return { source: map?.getSource('case')?.serialize(), dem: map?.getSource('dem')?.serialize(),
    loaded: map?.loaded(), snowLoaded: map?.isSourceLoaded('case'), demLoaded: map?.getSource('dem') ? map.isSourceLoaded('dem') : null,
    center: map?.getCenter(), zoom: map?.getZoom(), demInspection, events: [...events], telemetry: benchmarkTelemetrySnapshot() };
}

(globalThis as typeof globalThis & { snowReproducer?: unknown }).snowReproducer = { runCase, snapshot };
