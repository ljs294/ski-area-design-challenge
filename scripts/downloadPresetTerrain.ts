// One-off dev tool — NOT shipped with the app. Downloads real elevation
// data for a curated preset mountain and writes it to
// src/presetTerrain/<id>.json, where terrainIngest.ts picks it up at build
// time to replace that preset's procedurally-generated placeholder terrain.
//
// Usage: npm run download-preset -- <preset-id> [open-meteo|usgs]
//   open-meteo (default) — same provider/pacing as the live in-app picker.
//   usgs — USGS EPQS, US-coverage only. Fallback for when Open-Meteo is
//   throttling this machine hard; a separate service with its own quota.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { NA_MOUNTAIN_PRESETS } from '../src/mountainPresets';
import { boundsForSquareMeters } from '../src/geo';
import { fetchElevationGrid, SAMPLE_GRID_SIZE } from '../src/elevation';
import { fetchUsgsElevationGrid } from './usgsElevation';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

async function main() {
  const presetId = process.argv[2];
  const source = process.argv[3] === 'usgs' ? 'usgs' : 'open-meteo';

  if (!presetId) {
    console.error('Usage: npm run download-preset -- <preset-id> [open-meteo|usgs]');
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

  console.log(`Downloading real elevation data for ${preset.name} (${presetId}) via ${source}`);
  console.log(
    `  center: ${preset.latitude}, ${preset.longitude}  area: ${areaSizeMeters}m  grid: ${SAMPLE_GRID_SIZE}x${SAMPLE_GRID_SIZE}`
  );

  const startTime = Date.now();
  let sampleHeights: number[];

  if (source === 'usgs') {
    console.log('  (USGS EPQS, concurrent point queries — expect several minutes)');
    sampleHeights = await fetchUsgsElevationGrid(bounds, SAMPLE_GRID_SIZE, (p) => {
      const pct = Math.round((p.completed / p.total) * 100);
      if (p.completed % 50 === 0 || p.completed === p.total) {
        console.log(`  ${p.completed}/${p.total} points (${pct}%)`);
      }
    });
  } else {
    console.log('  (Open-Meteo, paced download — expect a couple of minutes)');
    sampleHeights = await fetchElevationGrid(bounds, (p) => {
      const pct = Math.round((p.completedBatches / p.totalBatches) * 100);
      const suffix = p.rateLimited ? ' (rate-limited, backing off...)' : '';
      console.log(`  ${p.completedBatches}/${p.totalBatches} batches (${pct}%)${suffix}`);
    });
  }

  const elapsedSec = Math.round((Date.now() - startTime) / 1000);
  const min = Math.min(...sampleHeights);
  const max = Math.max(...sampleHeights);
  console.log(`Done in ${elapsedSec}s. Elevation range: ${min}m - ${max}m`);

  const outDir = path.join(__dirname, '..', 'src', 'presetTerrain');
  fs.mkdirSync(outDir, { recursive: true });
  const outFile = path.join(outDir, `${presetId}.json`);
  fs.writeFileSync(outFile, JSON.stringify({ sampleGridSize: SAMPLE_GRID_SIZE, sampleHeights }));
  console.log(`Wrote ${outFile}`);
}

main().catch((e) => {
  console.error('FAILED:', e);
  process.exit(1);
});
