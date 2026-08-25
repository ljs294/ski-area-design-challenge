import { gzipSync } from 'node:zlib';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const MAX_ENTRY_GZIP = 370 * 1024;

async function checkBuild(directory) {
  const manifestPath = path.join(process.cwd(), directory, '.vite', 'manifest.json');
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const entries = Object.entries(manifest).filter(([, value]) => value.isEntry);
  if (entries.length !== 1) throw new Error(`${directory}: expected one renderer entry, found ${entries.length}.`);
  const [entryKey, entry] = entries[0];
  const bytes = await readFile(path.join(process.cwd(), directory, entry.file));
  const gzipBytes = gzipSync(bytes).byteLength;
  if (gzipBytes > MAX_ENTRY_GZIP) {
    throw new Error(`${directory}: ${entryKey} is ${gzipBytes} gzip bytes; budget is ${MAX_ENTRY_GZIP}.`);
  }
  const dynamic = new Set(entry.dynamicImports ?? []);
  if (![...dynamic].some((key) => key.endsWith('/MapView.tsx'))) {
    throw new Error(`${directory}: MapView must remain a dynamic route chunk.`);
  }
  const entryImports = new Set(entry.imports ?? []);
  if ([...entryImports].some((key) => key.includes('MapView') || key.includes('maplibre-gl'))) {
    throw new Error(`${directory}: MapView/MapLibre leaked into the initial entry graph.`);
  }
  console.log(`${directory}: entry ${gzipBytes} gzip bytes (budget ${MAX_ENTRY_GZIP}).`);
}

for (const directory of ['dist', 'dist-web']) await checkBuild(directory);
