#!/usr/bin/env node
// Compares a benchmark run with the stored baseline (0.3 §8.1; task 15).
//
//   node tools/perf/compare.mjs <baseline.json> <run.json> [--tolerance <percent>]
//
// Both files are written by the game's -benchmark (BenchmarkReport, schema "mountain-planner-benchmark/1").
// Prints the totals and every leg side by side, then exits non-zero when the run misses its budget or is
// slower than the baseline by more than the tolerance (10% by default, and at least 0.5 ms, run-to-run noise).
// No dependencies: plain Node.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const SCHEMA = 'mountain-planner-benchmark/1';
const NOISE_MS = 0.5;

export function compare(baseline, run, tolerancePercent = 10) {
  const problems = [];
  const notes = [];
  for (const [name, report] of [['baseline', baseline], ['run', run]]) {
    if (report.schema !== SCHEMA) problems.push(`${name} has schema ${report.schema}, expected ${SCHEMA}`);
  }
  for (const key of ['quality', 'site', 'width', 'height', 'hud', 'development']) {
    if (baseline[key] !== run[key]) notes.push(`${key} differs: baseline ${baseline[key]}, run ${run[key]} (numbers may not be comparable)`);
  }

  // The run's own budget (frame time, garbage, memory where measured).
  const r = run.result ?? {};
  if (run.budget?.isChecked && !r.frameTime) problems.push(`frame time over budget: ${r.summary}`);
  if (run.budget?.isChecked && r.garbageMeasured && !r.garbage) problems.push(`garbage per frame is not 0: ${r.summary}`);
  if (run.budget?.isChecked && r.memoryMeasured && !r.memory) problems.push(`graphics memory over budget: ${r.summary}`);

  // Regressions against the baseline.
  const rows = [];
  const check = (label, before, after) => {
    if (typeof before !== 'number' || typeof after !== 'number' || before <= 0 || after <= 0) {
      rows.push([label, fmt(before), fmt(after), '']);
      return;
    }
    const delta = after - before;
    const percent = (100 * delta) / before;
    const worse = delta > NOISE_MS && percent > tolerancePercent;
    rows.push([label, fmt(before), fmt(after), `${delta >= 0 ? '+' : ''}${delta.toFixed(2)} ms (${percent >= 0 ? '+' : ''}${percent.toFixed(1)}%)${worse ? '  REGRESSION' : ''}`]);
    if (worse) problems.push(`${label} regressed ${percent.toFixed(1)}% (${fmt(before)} → ${fmt(after)} ms)`);
  };
  const bt = baseline.total ?? {}, rt = run.total ?? {};
  check('total frame p50', bt.p50Ms, rt.p50Ms);
  check('total frame p95', bt.p95Ms, rt.p95Ms);
  check('total frame p99', bt.p99Ms, rt.p99Ms);
  check('total GPU p95', bt.gpuP95Ms, rt.gpuP95Ms);
  const legs = new Map((baseline.legs ?? []).map((l) => [l.name, l.stats]));
  for (const leg of run.legs ?? []) {
    const before = legs.get(leg.name);
    if (!before) { notes.push(`leg ${leg.name} is new (not in the baseline)`); continue; }
    check(`${leg.name} frame p95`, before.p95Ms, leg.stats.p95Ms);
  }
  return { problems, notes, rows };
}

function fmt(v) {
  return typeof v === 'number' ? v.toFixed(2) : '-';
}

function main(argv) {
  const args = argv.slice(2);
  let tolerance = 10;
  const t = args.indexOf('--tolerance');
  if (t >= 0) { tolerance = Number(args[t + 1]); args.splice(t, 2); }
  if (args.length !== 2 || !Number.isFinite(tolerance)) {
    console.error('Usage: node tools/perf/compare.mjs <baseline.json> <run.json> [--tolerance <percent>]');
    return 2;
  }
  const [baselinePath, runPath] = args;
  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
  const run = JSON.parse(readFileSync(runPath, 'utf8'));
  const { problems, notes, rows } = compare(baseline, run, tolerance);

  console.log(`Baseline: ${path.basename(baselinePath)}  ${baseline.quality}  commit ${String(baseline.commit).slice(0, 7)}${baseline.commitDirty ? '+' : ''}  ${baseline.gpu}`);
  console.log(`Run:      ${path.basename(runPath)}  ${run.quality}  commit ${String(run.commit).slice(0, 7)}${run.commitDirty ? '+' : ''}  ${run.gpu}`);
  console.log(`          ${run.result?.summary ?? ''}`);
  const width = Math.max(...rows.map((row) => row[0].length));
  console.log(`\n${'metric'.padEnd(width)}  baseline      run  change`);
  for (const [label, before, after, change] of rows) console.log(`${label.padEnd(width)}  ${before.padStart(8)} ${after.padStart(8)}  ${change}`);
  for (const note of notes) console.log(`note: ${note}`);
  if (problems.length) {
    console.log(`\nFAIL (${problems.length}):`);
    for (const p of problems) console.log(`  - ${p}`);
    return 1;
  }
  console.log('\nPASS: within budget and within the tolerance of the baseline.');
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv);
}
