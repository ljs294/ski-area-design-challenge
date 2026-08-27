import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

const roots = ['weather-engine', 'weather-lab'];
const forbidden = [
  /from\s+['"][^'"]*src\/app\//,
  /from\s+['"][^'"]*src\/weather\//,
  /from\s+['"][^'"]*electron\//,
  /from\s+['"](?:react|react-dom)['"]/,
];
async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(entries.filter((entry) => !['node_modules', 'dist'].includes(entry.name)).map((entry) => {
    const target = path.join(directory, entry.name); return entry.isDirectory() ? files(target) : [target];
  }));
  return nested.flat();
}
const violations = [];
for (const root of roots) for (const file of await files(root)) {
  if (!/\.(?:ts|tsx|js|mjs)$/.test(file)) continue;
  const source = await readFile(file, 'utf8');
  for (const rule of forbidden) {
    if (root === 'weather-lab' && rule.source.includes('react')) continue;
    if (rule.test(source)) violations.push(`${file}: ${rule}`);
  }
}
if (violations.length) { console.error(`Weather standalone architecture violations:\n${violations.join('\n')}`); process.exit(1); }
console.log('Weather engine and standalone lab do not import game, Electron, or legacy weather modules.');
