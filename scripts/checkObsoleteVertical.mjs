import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const root = process.cwd();
const removedPaths = [
  'menu.html',
  'scripts/downloadPresetTerrain.ts',
  'src/main.ts',
  'src/renderer.ts',
  'src/gisSelector.ts',
  'src/contentManager.ts',
  'src/selectionBox.ts',
  'src/style.css',
  'src/mountainPresets.ts',
  'src/labels.ts',
  'src/contours.ts',
  'src/hillshade.ts',
  'src/tileIndex.ts',
  'public/presetTerrain',
];
const errors = [];

for (const relativePath of removedPaths) {
  if (fs.existsSync(path.join(root, relativePath))) {
    errors.push(`obsolete path exists: ${relativePath}`);
  }
}

const packageJson = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
if (packageJson.scripts?.['download-preset']) errors.push('obsolete package script exists: download-preset');
for (const dependency of ['leaflet', '@types/leaflet', 'tsx']) {
  if (packageJson.dependencies?.[dependency] || packageJson.devDependencies?.[dependency]) {
    errors.push(`obsolete direct dependency exists: ${dependency}`);
  }
}

const indexHtml = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
for (const obsoleteReference of ['menu.html', 'src/main.ts']) {
  if (indexHtml.includes(obsoleteReference)) errors.push(`index.html references ${obsoleteReference}`);
}

function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const entryPath = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(entryPath) : [entryPath];
  });
}

for (const output of ['dist', 'dist-web']) {
  const outputRoot = path.join(root, output);
  if (!fs.existsSync(outputRoot)) {
    errors.push(`production output is missing: ${output}`);
    continue;
  }
  for (const filePath of walk(outputRoot)) {
    const relativePath = path.relative(outputRoot, filePath).replaceAll('\\', '/');
    if (/presetTerrain|crystal-mountain|palisades|stowe|vail/.test(relativePath)) {
      errors.push(`obsolete preset payload found in ${output}: ${relativePath}`);
    }
  }
}

if (errors.length > 0) {
  console.error('Obsolete-vertical checks failed:\n');
  for (const error of errors.sort()) console.error(`- ${error}`);
  process.exitCode = 1;
} else {
  console.log('Obsolete-vertical checks passed (source, package metadata, and production outputs).');
}
