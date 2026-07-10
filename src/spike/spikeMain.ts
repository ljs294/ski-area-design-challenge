// De-risking spike (v1): prove OpenFreeMap Liberty basemap + AWS Terrarium
// hillshade + ESA WorldCover ground cover all load keyless & CORS-open inside
// the app's rendering environment. Deliberately isolated from the menu app —
// this is throwaway verification scaffolding, not the real Phase 1 code.
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';

const TERRARIUM_TILES =
  'https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png';

// KVP GetTile form — the RESTful ResourceURL template from GetCapabilities
// 400s; only this KVP form returns tiles. See v1-scope memory.
const WORLDCOVER_TILES =
  'https://wmts.terrascope.be/?service=WMTS&request=GetTile&version=1.0.0' +
  '&layer=esa-worldcover-map-10m-2021-v2_map&style=default&format=image/png' +
  '&tilematrixset=EPSG:3857&TileMatrix={z}&TileCol={x}&TileRow={y}&TIME=2021-01-01';

const logEl = document.getElementById('log')!;
function log(msg: string, cls = ''): void {
  const line = document.createElement('div');
  if (cls) line.className = cls;
  line.textContent = `${new Date().toLocaleTimeString()}  ${msg}`;
  logEl.prepend(line);
  // Mirror to console so headless/automated drivers can read it too.
  console.log('[spike]', msg);
}

log('Booting MapLibre…');

const map = new maplibregl.Map({
  container: 'map',
  style: 'https://tiles.openfreemap.org/styles/liberty',
  center: [-121.474, 46.928], // Crystal Mountain, WA
  zoom: 11,
});
// Expose for automated drivers to poll load state.
(window as unknown as { spikeMap: maplibregl.Map }).spikeMap = map;

// Surface every source/tile error, tagged by which source failed — this is
// the whole point of the spike.
map.on('error', (e) => {
  const src = (e as unknown as { sourceId?: string }).sourceId ?? 'style';
  log(`ERROR [${src}]: ${e.error?.message ?? e.error}`, 'err');
});

function firstSymbolLayerId(): string | undefined {
  for (const layer of map.getStyle().layers ?? []) {
    if (layer.type === 'symbol') return layer.id;
  }
  return undefined;
}

map.on('load', () => {
  log('Basemap style loaded ✓ (OpenFreeMap Liberty)', 'ok');
  const beforeId = firstSymbolLayerId();

  map.addSource('terrarium', {
    type: 'raster-dem',
    tiles: [TERRARIUM_TILES],
    encoding: 'terrarium',
    tileSize: 256,
    maxzoom: 15,
    attribution: 'Terrain: Terrarium tiles, Mapzen/AWS Open Data',
  });
  map.addLayer(
    { id: 'hillshade', type: 'hillshade', source: 'terrarium' },
    beforeId
  );
  log('Added Terrarium hillshade layer');

  map.addSource('worldcover', {
    type: 'raster',
    tiles: [WORLDCOVER_TILES],
    tileSize: 256,
    attribution: '© ESA WorldCover project / Copernicus',
  });
  map.addLayer(
    {
      id: 'worldcover',
      type: 'raster',
      source: 'worldcover',
      paint: { 'raster-opacity': 0.55 },
    },
    beforeId
  );
  log('Added ESA WorldCover layer');

  // Once every currently-needed tile is in, the map goes idle — if we got
  // here with no ERROR lines above, all three sources rendered.
  map.once('idle', () => log('Map idle — all requested tiles settled ✓', 'ok'));
});

// Layer toggles
function bindToggle(btnId: string, layerId: string): void {
  const btn = document.getElementById(btnId) as HTMLButtonElement | null;
  btn?.addEventListener('click', () => {
    const visible = map.getLayoutProperty(layerId, 'visibility') !== 'none';
    map.setLayoutProperty(layerId, 'visibility', visible ? 'none' : 'visible');
    btn.classList.toggle('off', visible);
  });
}
bindToggle('toggle-hillshade', 'hillshade');
bindToggle('toggle-worldcover', 'worldcover');
