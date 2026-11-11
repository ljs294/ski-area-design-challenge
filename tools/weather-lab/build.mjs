#!/usr/bin/env node
// The Weather Lab page (task 4.05, owner 2026-11-09): fills tools/weather-lab/lab.html with the data that
// WeatherLabTests.WriteTheLabData wrote, plus a baseline to compare against. See README.md.
//
// Usage: node tools/weather-lab/build.mjs [--data <json>] [--out <html>] [--baseline <json>] [--pin]
//   --data      the lab data (default test-results/weather-lab/data.json)
//   --out       the page (default test-results/weather-lab/index.html)
//   --baseline  the run to compare against (default test-results/weather-lab/baseline.json; the first run pins itself)
//   --pin       make this run the baseline from now on
import { readFile, writeFile, copyFile, access } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const args = process.argv.slice(2);
const option = (name, fallback) => { const i = args.indexOf(name); return i >= 0 && args[i + 1] ? path.resolve(args[i + 1]) : fallback; };
const dataPath = option('--data', path.join(root, 'test-results', 'weather-lab', 'data.json'));
const outPath = option('--out', path.join(root, 'test-results', 'weather-lab', 'index.html'));
const baselinePath = option('--baseline', path.join(root, 'test-results', 'weather-lab', 'baseline.json'));
const exists = async (file) => { try { await access(file); return true; } catch { return false; } };

function git(...rest) {
  try { return execFileSync('git', rest, { cwd: root, encoding: 'utf8' }).trim(); } catch { return ''; }
}
const commit = git('rev-parse', '--short', 'HEAD') || 'an unknown commit';
const dirty = git('status', '--porcelain', '--untracked-files=no') !== '';
const now = new Date();
const pad = (n) => String(n).padStart(2, '0');
const build = {
  date: `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}`,
  commit: commit + (dirty ? ' with uncommitted changes' : ''),
};

const data = JSON.parse(await readFile(dataPath, 'utf8'));
data.build = build;

let baseline = null;
if (args.includes('--pin') || !(await exists(baselinePath))) {
  await writeFile(baselinePath, JSON.stringify(data));
  console.log(`Pinned this run as the baseline: ${baselinePath}`);
} else {
  baseline = JSON.parse(await readFile(baselinePath, 'utf8'));
}

// JSON inside a <script> must not close it.
const embed = (value) => JSON.stringify(value).replace(/<\//g, '<\\/');
const template = await readFile(path.join(here, 'lab.html'), 'utf8');
const page = template
  .replace('const LAB = __DATA__;', () => `const LAB = ${embed(data)};`)
  .replace('const BASE = __BASELINE__;', () => `const BASE = ${embed(baseline)};`)
  .replace('const BUILD = __BUILD__;', () => `const BUILD = ${embed(build)};`);
if (/const (LAB|BASE|BUILD) = __/.test(page)) throw new Error('lab.html lost a data constant; nothing written.');
await writeFile(outPath, page);
console.log(`Weather Lab: ${outPath}${baseline ? ` (compared with the baseline from ${baseline.build ? baseline.build.date : 'earlier'})` : ''}`);
