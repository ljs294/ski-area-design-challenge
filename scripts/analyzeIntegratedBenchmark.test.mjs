import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { analyzeIntegratedBenchmark, bootstrapMeanConfidenceInterval, percentile } from './analyzeIntegratedBenchmark.mjs';

function identity(pairIndex, condition, patch = {}) {
  return {
    scenarioId: 'detailed-3000-4x', workloadId: 'jackson-v1', seed: `pair-seed-${pairIndex}`,
    checkpointHash: 'checkpoint-sha', fixtureManifestHash: 'fixture-sha', assetManifestHash: 'assets-sha',
    cacheStatePolicy: 'warm-restored', profile: 'standard', cssViewport: { width: 1920, height: 1080 },
    canvas: { width: 1920, height: 1080 },
    devicePixelRatio: 1, refreshHz: 60,
    machine: { id: 'machine-1', cpu: 'Ryzen 5600X', gpu: 'RTX 3060 Ti', driver: '32.0.15.9186', os: 'Windows 11 26200' },
    runtime: { kind: 'browser', version: 'Chromium 140', glVendor: 'Google Inc. (NVIDIA)',
      glRenderer: 'ANGLE (NVIDIA GeForce RTX 3060 Ti)', glBackend: 'D3D11' },
    source: { commit: condition === 'A' ? 'commit-a' : 'commit-b', workingTreeStatus: 'clean', lockfileHash: 'lock-sha',
      buildHash: condition === 'A' ? 'build-a' : 'build-b' },
    ...patch,
  };
}

function trial(pairIndex, condition, value, options = {}) {
  const order = pairIndex % 2 === 1 ? 'AB' : 'BA';
  return {
    id: `pair-${pairIndex}-${condition}-${options.suffix ?? 'valid'}`, pairIndex, order, condition,
    telemetryEnabled: options.telemetryEnabled ?? false,
    status: options.status ?? 'valid', invalidReasons: options.invalidReasons ?? [],
    identity: options.identity ?? identity(pairIndex, condition),
    metrics: options.metrics ?? { frameIntervalMs: value, activeOccupancy: condition === 'B' ? 970 : 1_000 },
    measurement: options.measurement ?? { windowDurationMs: 120_000, expectedFrames: 7_200, deliveredFrames: 7_100,
      trailingGapAccounted: true, livenessPassed: true },
    ...(options.samples ? { samples: options.samples } : {}),
  };
}

function pairs(count, options = {}) {
  const trials = [];
  for (let pairIndex = 1; pairIndex <= count; pairIndex++) {
    const a = trial(pairIndex, 'A', options.a ?? 10, { telemetryEnabled: options.telemetryA ?? false });
    const b = trial(pairIndex, 'B', options.b ?? 10.2, { telemetryEnabled: options.telemetryB ?? false });
    if (options.sameBuild) b.identity.source = { ...a.identity.source };
    trials.push(...(a.order === 'AB' ? [a, b] : [b, a]));
  }
  return trials;
}

function input(trials, options = {}) {
  return {
    schemaVersion: 1, seed: 'analysis-seed', bootstrapIterations: 1_000,
    comparison: options.comparison ?? { kind: 'optimization-regression', metric: 'frameIntervalMs', direction: 'max' },
    measurementRequirements: { windowDurationMs: 120_000, minimumDeliveredFrameRatio: 0.9, minimumSamples: 30 },
    budgets: options.budgets ?? { frameIntervalMs: { direction: 'max', mode: 'absolute', limit: 20 } },
    trials,
  };
}

test('validates contiguous adjacent AB/BA pairs', () => {
  const malformed = pairs(5);
  malformed.filter((entry) => entry.pairIndex === 2).forEach((entry) => { entry.order = 'AB'; });
  assert.throws(() => analyzeIntegratedBenchmark(input(malformed)), /must use BA/);
  const interleaved = pairs(5);
  [interleaved[1], interleaved[2]] = [interleaved[2], interleaved[1]];
  assert.throws(() => analyzeIntegratedBenchmark(input(interleaved)), /contiguous|sequence/);
});

test('rejects diagnostic trials even when a complete pair set is marked valid', () => {
  const trials = pairs(5);
  for (const entry of trials) entry.identity.tier = 'diagnostic';
  assert.throws(() => analyzeIntegratedBenchmark(input(trials)), /Diagnostic trial .* cannot enter qualification paired analysis/);
});

test('rejects mismatched pair seed, fixture, and hardware identity', () => {
  for (const [field, mutate, message] of [
    ['seed', (value) => { value.identity.seed = 'other-seed'; }, /mismatched seeds/],
    ['fixture', (value) => { value.identity.fixtureManifestHash = 'other-fixture'; }, /mismatched workload, fixture/],
    ['hardware', (value) => { value.identity.machine.gpu = 'other-gpu'; }, /mismatched workload, fixture/],
    ['GL backend', (value) => { value.identity.runtime.glBackend = 'SwiftShader'; }, /mismatched workload, fixture/],
  ]) {
    const trials = pairs(5);
    const candidate = trials.find((entry) => entry.pairIndex === 1 && entry.condition === 'B');
    mutate(candidate);
    assert.throws(() => analyzeIntegratedBenchmark(input(trials)), message, field);
  }
});

test('requires CSS viewport and actual GL identity for valid trials', () => {
  const noViewport = pairs(5);
  delete noViewport[0].identity.cssViewport;
  assert.throws(() => analyzeIntegratedBenchmark(input(noViewport)), /CSS viewport dimensions/);
  const noRenderer = pairs(5);
  delete noRenderer[0].identity.runtime.glRenderer;
  assert.throws(() => analyzeIntegratedBenchmark(input(noRenderer)), /runtime.glRenderer/);
});

test('retains an invalid launch attempt with no metrics and excludes it from valid sequence', () => {
  const trials = pairs(5);
  trials.splice(2, 0, { id: 'failed-launch', pairIndex: 2, order: 'BA', condition: 'B', telemetryEnabled: false,
    status: 'invalid', invalidReasons: ['WebGL adapter unavailable'], identity: { rawGpu: 'unknown' } });
  const report = analyzeIntegratedBenchmark(input(trials));
  assert.equal(report.validPairCount, 5);
  assert.equal(report.invalidTrials.length, 1);
  assert.equal(report.invalidTrials[0].metrics, undefined);
  assert.deepEqual(report.invalidTrials[0].identity, { rawGpu: 'unknown' });
});

test('rejects one-sample series and incomplete liveness metadata', () => {
  const trials = pairs(5);
  trials[0].samples = { frameIntervalMs: [12] };
  assert.throws(() => analyzeIntegratedBenchmark(input(trials)), /at least 30 finite samples/);
  const stopped = pairs(5);
  stopped[0].measurement.trailingGapAccounted = false;
  assert.throws(() => analyzeIntegratedBenchmark(input(stopped)), /trailing window gap/);
});

test('fails an absolute budget when one candidate trial violates it', () => {
  const trials = pairs(5);
  trials.find((entry) => entry.pairIndex === 3 && entry.condition === 'B').metrics.frameIntervalMs = 21;
  const report = analyzeIntegratedBenchmark(input(trials));
  assert.equal(report.decision, 'fail');
  assert.deepEqual(report.metrics[0].violations.map((item) => item.trialId), ['pair-3-B-valid']);
  assert.equal(report.metrics[0].violations[0].condition, 'B');
  assert.equal(report.metrics[0].violations[0].value, 21);
});

test('reports a condition A absolute failure without disqualifying an improved B candidate', () => {
  const trials = pairs(5);
  trials.find((entry) => entry.pairIndex === 4 && entry.condition === 'A').metrics.frameIntervalMs = 21;
  const report = analyzeIntegratedBenchmark(input(trials));
  assert.equal(report.decision, 'pass');
  assert.equal(report.comparisonDecision, 'pass');
  assert.equal(report.qualification.A.status, 'fail');
  assert.equal(report.qualification.B.status, 'pass');
  assert.deepEqual(report.metrics[0].violations, [{
    condition: 'A', trialId: 'pair-4-A-valid', pairIndex: 4, value: 21, limit: 20,
  }]);
  assert.equal(report.metrics[0].conditionQualifications.A.status, 'fail');
});

test('computes per-trial percentiles from adequate samples', () => {
  const trials = pairs(5);
  trials[0].samples = { frameIntervalMs: Array.from({ length: 100 }, (_, index) => index + 1) };
  const report = analyzeIntegratedBenchmark(input(trials));
  assert.deepEqual(report.trialSummaries[0].percentiles.frameIntervalMs,
    { count: 100, p50: 50.5, p95: 95.05, p99: 99.01 });
  assert.equal(percentile([4, 1, 3, 2], 0.5), 2.5);
});

test('keeps telemetry overhead at 3% and optimization regression at 5%', () => {
  const telemetryTrials = pairs(5, { a: 10, b: 10.4, telemetryA: false, telemetryB: true, sameBuild: true });
  const telemetry = analyzeIntegratedBenchmark(input(telemetryTrials, {
    comparison: { kind: 'telemetry-overhead', metric: 'frameIntervalMs', direction: 'max' },
  }));
  assert.equal(telemetry.instrumentationOverhead.limit, 3);
  assert.equal(telemetry.instrumentationOverhead.status, 'fail');
  assert.equal(telemetry.comparisonDecision, 'fail');
  assert.equal(telemetry.qualifyingCondition, 'A');
  assert.equal(telemetry.optimizationRegression, undefined);

  const mismatchedBuild = pairs(5, { a: 10, b: 10.2, telemetryA: false, telemetryB: true });
  assert.throws(() => analyzeIntegratedBenchmark(input(mismatchedBuild, {
    comparison: { kind: 'telemetry-overhead', metric: 'frameIntervalMs', direction: 'max' },
  })), /same source\/build identity/);

  const optimization = analyzeIntegratedBenchmark(input(pairs(5, { a: 10, b: 10.4 }), {
    comparison: { kind: 'optimization-regression', metric: 'frameIntervalMs', direction: 'max' },
  }));
  assert.equal(optimization.optimizationRegression.limit, 5);
  assert.equal(optimization.optimizationRegression.status, 'pass');
  assert.equal(optimization.instrumentationOverhead, undefined);
});

test('uses deterministic bootstrap and permits exactly one five-pair extension', () => {
  const first = bootstrapMeanConfidenceInterval([1, 2, 3, 4, 5], { seed: 'same', iterations: 1_000 });
  assert.deepEqual(first, bootstrapMeanConfidenceInterval([1, 2, 3, 4, 5], { seed: 'same', iterations: 1_000 }));
  const trials = pairs(5);
  const candidate = [10.1, 10.2, 10.3, 10.9, 11.2];
  candidate.forEach((value, index) => { trials.find((entry) => entry.pairIndex === index + 1 && entry.condition === 'B').metrics.frameIntervalMs = value; });
  const report = analyzeIntegratedBenchmark(input(trials));
  assert.equal(report.decision, 'extend-five-pairs');
  assert.equal(report.extension.remainingPairsAllowed, 5);

  const extended = pairs(10);
  [...candidate, ...candidate].forEach((value, index) => {
    extended.find((entry) => entry.pairIndex === index + 1 && entry.condition === 'B').metrics.frameIntervalMs = value;
  });
  const extendedReport = analyzeIntegratedBenchmark(input(extended));
  assert.equal(extendedReport.decision, 'inconclusive');
  assert.equal(extendedReport.extension.remainingPairsAllowed, 0);
  assert.match(extendedReport.reasons[0], /single permitted extension/);
});

test('supports min-direction relative comparisons for occupancy', () => {
  const report = analyzeIntegratedBenchmark(input(pairs(5), {
    comparison: { kind: 'optimization-regression', metric: 'activeOccupancy', direction: 'min' },
    budgets: { activeOccupancy: { direction: 'min', mode: 'absolute', limit: 950 } },
  }));
  assert.equal(report.metrics[0].status, 'pass');
  assert.equal(report.optimizationRegression.status, 'pass');
  assert.equal(report.optimizationRegression.pairResults[0].adversePercent, 3);
});

test('CLI reads input and writes a versioned report', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'integrated-analysis-'));
  try {
    const inputPath = path.join(directory, 'input.json');
    const outputPath = path.join(directory, 'report.json');
    await writeFile(inputPath, JSON.stringify(input(pairs(5))), 'utf8');
    const result = spawnSync(process.execPath, [path.resolve('scripts/analyzeIntegratedBenchmark.mjs'), '--input', inputPath,
      '--output', outputPath], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    const report = JSON.parse(await readFile(outputPath, 'utf8'));
    assert.equal(report.schemaVersion, 1);
    assert.equal(report.validPairCount, 5);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
