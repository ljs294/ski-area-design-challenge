// One-off dev tool — NOT shipped with the app. Downloads real elevation
// data for a curated preset mountain and writes it to
// public/presetTerrain/<id>.heights.bin, where terrainIngest.ts fetches it
// at runtime to replace that preset's procedurally-generated placeholder
// terrain.
//
// Usage: npm run download-preset -- <preset-id>
//
// Written as raw Float32 binary (row-major, square grid — grid size is
// recovered from the file's byte length, sqrt(bytes/4)), not JSON. At the
// grid sizes this app now requests (up to ~4M points), a plain JSON number
// array runs ~18 bytes/point vs 4 bytes/point raw binary — and, more
// importantly, serving/transforming a 70MB+ JSON file through Vite's dev
// middleware was found to crash the dev server outright. Living in
// public/ means Vite serves it as a static passthrough (copied verbatim,
// no transform) in both dev and production, sidestepping that entirely.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { NA_MOUNTAIN_PRESETS } from '../src/mountainPresets';
import { boundsForSquareMeters } from '../src/geo';
import { fetchElevationBuffer, fetchElevationGrid, sampleGridSizeFor } from '../src/elevation';
import { fetchVectorFeatures } from '../src/vectorFeatures';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

async function main() {
  const presetId = process.argv[2];

  if (!presetId) {
    console.error('Usage: npm run download-preset -- <preset-id>');
    console.error('Available:', NA_MOUNTAIN_PRESETS.map((p) => p.id).join(', '));
    process.exit(1);
  }

  const preset = NA_MOUNTAIN_PRESETS.find((p) => p.id === presetId);
  if (!preset) {
    console.error(`Unknown preset id: ${presetId}`);
    console.error('Available:', NA_MOUNTAIN_PRESETS.map((p) => p.id).join(', '));
    process.exit(1);
  }

  const areaSizeMeters = preset.areaSizeMeters ?? 4000;
  const bounds = boundsForSquareMeters(preset.latitude, preset.longitude, areaSizeMeters);
  const gridSize = sampleGridSizeFor(areaSizeMeters);

  console.log(`Downloading real elevation data for ${preset.name} (${presetId}) via USGS 3DEP`);
  console.log(
    `  center: ${preset.latitude}, ${preset.longitude}  area: ${areaSizeMeters}m  grid: ${gridSize}x${gridSize}`
  );

  const startTime = Date.now();
  const elevation = await fetchElevationGrid(bounds, areaSizeMeters, (p) => {
    console.log(`  ${p.phase}...`);
  });
  const sampleHeights = elevation.heights;

  const elapsedSec = (Date.now() - startTime) / 1000;
  let min = Infinity;
  let max = -Infinity;
  for (const h of sampleHeights) {
    if (h < min) min = h;
    if (h > max) max = h;
  }
  console.log(`Done in ${elapsedSec.toFixed(1)}s. Elevation range: ${min.toFixed(1)}m - ${max.toFixed(1)}m`);
  console.log(`  requested bounds: ${JSON.stringify(bounds)}`);
  console.log(`  actual raster extent: ${JSON.stringify(elevation.bounds)}`);

  const outDir = path.join(__dirname, '..', 'public', 'presetTerrain');
  fs.mkdirSync(outDir, { recursive: true });
  const outFile = path.join(outDir, `${presetId}.heights.bin`);
  fs.writeFileSync(outFile, Buffer.from(Float32Array.from(sampleHeights).buffer));
  console.log(`Wrote ${outFile} (${(fs.statSync(outFile).size / 1e6).toFixed(1)} MB)`);

  // Sidecar so ingestPreset places the heights at the extent the service
  // actually rendered (see ElevationGrid.bounds), not the requested square.
  const metaFile = path.join(outDir, `${presetId}.meta.json`);
  fs.writeFileSync(metaFile, JSON.stringify({ bounds: elevation.bounds, width: elevation.width, height: elevation.height }), 'utf-8');
  console.log(`Wrote ${metaFile}`);

  // Offline perimeter ring so this preset gets neighbouring hillshaded relief in
  // 3D (out to 3 km, floating-clip edge) instead of a cliff at the property line
  // (see TerrainRecord.surround).
  console.log(`Downloading offline perimeter ring via USGS 3DEP...`);
  try {
    const surround = await fetchElevationBuffer(elevation.bounds);
    if (surround) {
      const surroundFile = path.join(outDir, `${presetId}.surround.json`);
      fs.writeFileSync(surroundFile, JSON.stringify(surround), 'utf-8');
      console.log(
        `Wrote ${surroundFile} (${(fs.statSync(surroundFile).size / 1e6).toFixed(1)} MB) — ` +
          `${surround.width}x${surround.height} over ${JSON.stringify(surround.bounds)}`
      );
    } else {
      console.warn('Perimeter ring unavailable (service returned no data); preset saved without one.');
    }
  } catch (e) {
    console.error('Failed to download perimeter ring (preset was still saved successfully):', e);
  }

  console.log(`Downloading map features (roads/water/peaks/land cover) via Overpass...`);
  try {
    const vectorFeatures = await fetchVectorFeatures(elevation.bounds);
    const vectorsFile = path.join(outDir, `${presetId}.vectors.json`);
    fs.writeFileSync(vectorsFile, JSON.stringify(vectorFeatures), 'utf-8');
    console.log(
      `Wrote ${vectorsFile} (${(fs.statSync(vectorsFile).size / 1e3).toFixed(1)} KB) — ` +
        `${vectorFeatures.roads.length} roads, ${vectorFeatures.waterLines.length} water lines, ` +
        `${vectorFeatures.waterPolygons.length} water polygons, ${vectorFeatures.landCover.length} land cover polygons, ` +
        `${vectorFeatures.peaks.length} named peaks`
    );
  } catch (e) {
    console.error(`Failed to download map features (preset heightmap was still saved successfully):`, e);
  }
}

main().catch((e) => {
  console.error('FAILED:', e);
  process.exit(1);
});
