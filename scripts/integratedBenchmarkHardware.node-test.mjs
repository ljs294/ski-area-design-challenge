import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  hashDirectory,
  collectHardwareMetadata,
  normalizeWindowsInventory,
  qualificationInventoryErrors,
} from './integratedBenchmarkHardware.mjs';
import { cleanupPackagedElectronFromUserData, launchPackagedElectron,
  cdpPageTargets, packagedElectronArguments, reserveLoopbackPort, validateDebugPort } from './packagedElectronControl.mjs';
import { diagnosticArtifactErrors, orderedReactProfileEntries } from './integratedDiagnosticProfiler.mjs';
import { checkpointSeed, checkpointTarget, emittedTrialErrors, integratedBenchmarkScenarios, parseArguments, planTrials,
  playwrightCollectionErrors, qualificationSourceErrors, scenarioSpeed, summarizeMemoryGate, mergeFailedTrial,
  summarizeTrialEvidence, trialDeadlineMs } from './runIntegratedBenchmark.mjs';

const rawInventory = {
  cpu: { Name: 'AMD Ryzen 5 5600X', NumberOfCores: 6, NumberOfLogicalProcessors: 12 },
  os: { Caption: 'Windows 11 Home', Version: '10.0.26200', BuildNumber: '26200', TotalVisibleMemorySize: 33_000_000 },
  gpus: [{ Name: 'NVIDIA GeForce RTX 3060 Ti', DriverVersion: '32.0.15.9186',
    CurrentHorizontalResolution: 2560, CurrentVerticalResolution: 1080, CurrentRefreshRate: 60,
    InventorySource: 'Win32_VideoController' }],
  power: 'Power Scheme GUID: balanced', thermal: [],
};

const validRuntimeEvidence = {
  simulation: { targetOccupancy: 0, requestedSpeed: 1,
    publications: [
      { macroSecond: 0, microSecond: 0, active: 0, movementCount: 0, committedRevision: 1 },
      { macroSecond: 120, microSecond: 120, active: 0, movementCount: 0, committedRevision: 1 },
    ], backlog: { availability: 'unavailable', reason: 'worker target timestamps unavailable in fixture test' } },
  presentation: { lastDrawnRevision: { availability: 'unavailable', reason: 'empty workload has no guest draw revision' } },
  sourceState: { terrain: true, terrainLoaded: true, snow: true, snowLoaded: true, mapLoaded: true },
  telemetry: { entries: [] },
  cacheState: { policy: 'warm', profileIsolated: true, checkpointRestored: true,
    fixtureManifestHash: undefined, cleared: true, primed: true, primeCompleted: true },
};

test('normalizes hardware inventory and records unavailable thermal observation', () => {
  const hardware = normalizeWindowsInventory(rawInventory, 'benchmark-pc');
  assert.equal(hardware.id, 'benchmark-pc');
  assert.equal(hardware.cpuCores, 6);
  assert.equal(hardware.ramBytes, 33_000_000 * 1024);
  assert.deepEqual(hardware.display, { width: 2560, height: 1080, refreshHz: 60, source: 'Win32_VideoController' });
  assert.deepEqual(hardware.thermalObservation, { availability: 'unavailable', celsius: [] });
  assert.deepEqual(qualificationInventoryErrors(hardware), []);
});

test('reserves a dedicated loopback CDP port and rejects unsafe port values', async () => {
  const port = await reserveLoopbackPort();
  assert.equal(validateDebugPort(port), port);
  assert.throws(() => validateDebugPort(80), /1024 through 65535/);
  assert.throws(() => validateDebugPort('not-a-port'), /1024 through 65535/);
});

test('requires a real CDP page target before handing a packaged browser endpoint to Playwright', () => {
  assert.deepEqual(cdpPageTargets([{ type: 'browser', webSocketDebuggerUrl: 'ws://browser' },
    { type: 'worker', webSocketDebuggerUrl: 'ws://worker' }]), []);
  assert.deepEqual(cdpPageTargets([{ type: 'page', title: 'Mountain Planner', url: 'file:///index.html',
    webSocketDebuggerUrl: 'ws://page' }]), [{ type: 'page', title: 'Mountain Planner', url: 'file:///index.html' }]);
});

test('keeps hardware GPU default and makes a disabled-GPU diagnostic explicit', () => {
  const options = { userDataDir: 'C:/benchmark-profile', debugPort: 45123, scenario: { id: 'fixture' },
    telemetryEnabled: false, devConsole: false, deviceScaleFactor: 1 };
  assert.ok(!packagedElectronArguments(options).includes('--disable-gpu'));
  assert.ok(packagedElectronArguments({ ...options, gpuMode: 'disabled' }).includes('--disable-gpu'));
  assert.throws(() => packagedElectronArguments({ ...options, gpuMode: 'software' }), /hardware or disabled/);
});

test('React diagnostic ring rejects empty data and restores wrapped chronological order', () => {
  assert.deepEqual(orderedReactProfileEntries({ entries: new Array(4), size: 0, writeIndex: 0 }), []);
  const entry = (id, commitTime) => ({ id, phase: 'update', actualDuration: 1, baseDuration: 2,
    startTime: commitTime - 1, commitTime });
  assert.deepEqual(orderedReactProfileEntries({ entries: [entry('c', 3), entry('d', 4), entry('a', 1), entry('b', 2)],
    size: 4, writeIndex: 2 }).map(value => value.id), ['a', 'b', 'c', 'd']);
  assert.deepEqual(orderedReactProfileEntries({ entries: [{ ...entry('bad', 1), actualDuration: Number.NaN }],
    size: 1, writeIndex: 0 }), []);
});

test('diagnostic profile manifests require hashed output or a truthful unavailable reason for every role', () => {
  const available = role => ({ role, availability: 'available', path: `test-results/${role}.json`, bytes: 1,
    sha256: 'a'.repeat(64) });
  assert.deepEqual(diagnosticArtifactErrors([available('allocationProfile'), available('gcTrace'),
    available('heapSnapshot'), { role: 'reactProfile', availability: 'unavailable', reason: 'Profiling build unavailable.' }]), []);
  assert.deepEqual(diagnosticArtifactErrors([available('allocationProfile'), available('gcTrace'),
    { ...available('heapSnapshot'), bytes: 0 }, { role: 'reactProfile', availability: 'unavailable' }]), [
    'Available heapSnapshot lacks a nonempty hashed artifact.', 'Unavailable reactProfile lacks a reason.',
  ]);
});

test('packaged control refuses the development Electron executable', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'integrated-electron-reject-'));
  try {
    const developmentDirectory = path.join(directory, 'node_modules', 'electron');
    await mkdir(developmentDirectory, { recursive: true });
    const developmentElectron = path.join(developmentDirectory, process.platform === 'win32' ? 'electron.exe' : 'electron');
    await writeFile(developmentElectron, 'not an executable');
    await assert.rejects(launchPackagedElectron({ executablePath: developmentElectron,
      userDataDir: directory, scenario: {}, telemetryEnabled: false }), /refuses the development Electron executable/);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('failed packaged launch removes its owned marker', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'integrated-electron-failed-'));
  try {
    const userDataDir = path.join(directory, 'nested-profile');
    await assert.rejects(launchPackagedElectron({ executablePath: process.execPath, userDataDir,
      scenario: { test: true }, telemetryEnabled: false, timeoutMs: 2_000 }), /exited|CDP did not become ready/);
    assert.equal((await stat(userDataDir)).isDirectory(), true);
    await assert.rejects(readFile(path.join(userDataDir, '.integrated-owned-electron.json')), /ENOENT/);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('retains spec failure evidence and merges bounded runner diagnostics', () => {
  const emitted = { id: 'diagnostic-browser-empty-telemetry-overhead-p1-a', status: 'invalid',
    invalidReasons: ['Weather readiness did not settle.'], stage: 'game-ready',
    domSnapshotPath: 'test-results/dom.html', stdoutPath: 'test-results/spec.stdout', stderrPath: 'test-results/spec.stderr',
    evidence: { worker: { publication: true } }, diagnostics: { screenshotPath: 'test-results/failure.png' } };
  const merged = mergeFailedTrial({ id: emitted.id, condition: 'A' }, { supplied: true }, emitted,
    { exitCode: 1, logs: { stdout: 'playwright stdout', stderr: 'playwright stderr' } });
  assert.notEqual(merged, emitted, 'merge returns a new record without mutating the emitted diagnostic');
  assert.equal(merged.stage, 'game-ready');
  assert.equal(merged.domSnapshotPath, 'test-results/dom.html');
  assert.equal(merged.stdoutPath, 'test-results/spec.stdout');
  assert.equal(merged.stderrPath, 'test-results/spec.stderr');
  assert.deepEqual(merged.evidence, { worker: { publication: true } });
  assert.deepEqual(merged.diagnostics, { screenshotPath: 'test-results/failure.png',
    runner: { exitCode: 1, stdout: 'playwright stdout', stderr: 'playwright stderr' } });
  assert.deepEqual(merged.invalidReasons, ['Weather readiness did not settle.', 'Playwright trial exited 1.']);
});

test('records bounded runner output when a scenario emits no trial record', () => {
  const result = mergeFailedTrial({ id: 'missing-trial', condition: 'A' }, { supplied: true }, null,
    { exitCode: 124, logs: { stdout: 'last stdout', stderr: 'last stderr' } });
  assert.deepEqual(result, { id: 'missing-trial', condition: 'A', status: 'invalid',
    invalidReasons: ['Playwright trial exited 124.'], identity: { supplied: true },
    diagnostics: { stage: 'runner-no-scenario-result', exitCode: 124, stdout: 'last stdout', stderr: 'last stderr' },
    recordedAt: result.recordedAt });
  assert.match(result.recordedAt, /^\d{4}-\d{2}-\d{2}T/);
});

test('stale marker cannot terminate a process with a different executable identity', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'integrated-electron-stale-'));
  try {
    await writeFile(path.join(directory, '.integrated-owned-electron.json'), JSON.stringify({ pid: process.pid,
      executable: process.execPath, launchedAt: new Date().toISOString() }));
    await cleanupPackagedElectronFromUserData(directory, path.join(directory, 'different.exe'));
    assert.equal(process.pid > 0, true);
    await assert.rejects(readFile(path.join(directory, '.integrated-owned-electron.json')), /ENOENT/);
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('rejects unknown GPU inventory and a nonqualifying display', () => {
  const hardware = normalizeWindowsInventory({ ...rawInventory,
    gpus: [{ Name: 'unknown', DriverVersion: '', CurrentHorizontalResolution: 1280,
      CurrentVerticalResolution: 720, CurrentRefreshRate: 59 }] });
  assert.deepEqual(qualificationInventoryErrors(hardware), [
    'Hardware gpu is unavailable.',
    'Hardware driver is unavailable.',
    'A detected display of at least 1920x1080 is required.',
  ]);
});

test('turns inventory command denial into explicit unavailable evidence', () => {
  const hardware = collectHardwareMetadata({ platform: 'win32', execFileSyncImpl: () => { throw new Error('access denied'); } });
  assert.match(hardware.collectionError, /access denied/);
  assert.ok(qualificationInventoryErrors(hardware).some((error) => /gpu is unavailable/.test(error)));
});

test('directory hashes are deterministic, path-sensitive, and content-sensitive', async () => {
  const directory = await mkdtemp(path.join(tmpdir(), 'integrated-build-hash-'));
  try {
    await mkdir(path.join(directory, 'assets'));
    await writeFile(path.join(directory, 'index.html'), '<main>benchmark</main>');
    await writeFile(path.join(directory, 'assets', 'app.js'), 'export default 1;');
    const first = await hashDirectory(directory);
    assert.equal(await hashDirectory(directory), first);
    await writeFile(path.join(directory, 'assets', 'app.js'), 'export default 2;');
    assert.notEqual(await hashDirectory(directory), first);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('plans five sequential alternating pairs with condition independent of telemetry', () => {
  const options = parseArguments(['--runtime', 'browser', '--fixture', 'fixture', '--output', 'test-results/out',
    '--scenario', 'detailed-3000', '--comparison', 'optimization-regression', '--telemetry', '0']);
  const trials = planTrials(options);
  assert.equal(trials.length, 10);
  assert.deepEqual(trials.slice(0, 4).map((trial) => [trial.pairIndex, trial.order, trial.condition, trial.telemetryEnabled]), [
    [1, 'AB', 'A', false], [1, 'AB', 'B', false], [2, 'BA', 'B', false], [2, 'BA', 'A', false],
  ]);
});

test('telemetry pairs use identical conditions with disabled A and enabled B', () => {
  const options = parseArguments(['--runtime', 'electron', '--fixture', 'fixture', '--output', 'test-results/out',
    '--scenario', 'empty', '--pairs', '10']);
  const trials = planTrials(options);
  assert.equal(trials.length, 20);
  assert.equal(trials.every((trial) => trial.telemetryEnabled === (trial.condition === 'B')), true);
  assert.deepEqual([...new Set(trials.map((trial) => trial.order))], ['AB', 'BA']);
});

test('maps every runner scenario to an explicit fixture checkpoint target', () => {
  assert.equal(checkpointTarget('empty'), 0);
  assert.equal(checkpointTarget('detailed-1000'), 1000);
  for (const scenario of ['detailed-3000', 'aggregate-8', 'aggregate-16', 'aggregate-64',
    'interactive', 'visibility-near', 'visibility-wide', 'recovery', 'soak']) assert.equal(checkpointTarget(scenario), 3000);
});

test('separates detailed population from requested clock speed', () => {
  const expected = {
    'ci-diagnostic': [32, 1, 'detailed', 'steady'],
    empty: [0, 1, 'empty', 'steady'],
    'detailed-1000': [1000, 1, 'detailed', 'steady'],
    'detailed-1000-1x': [1000, 1, 'detailed', 'steady'],
    'detailed-1000-2x': [1000, 2, 'detailed', 'steady'],
    'detailed-1000-4x': [1000, 4, 'detailed', 'steady'],
    'detailed-3000': [3000, 1, 'detailed', 'steady'],
    'detailed-3000-1x': [3000, 1, 'detailed', 'steady'],
    'detailed-3000-2x': [3000, 2, 'detailed', 'steady'],
    'detailed-3000-4x': [3000, 4, 'detailed', 'steady'],
    'aggregate-8': [3000, 8, 'aggregate', 'steady'],
    'aggregate-16': [3000, 16, 'aggregate', 'steady'],
    'aggregate-64': [3000, 64, 'aggregate', 'steady'],
    interactive: [3000, 1, 'detailed', 'interactive'],
    'visibility-near': [3000, 1, 'detailed', 'visibility-near'],
    'visibility-wide': [3000, 1, 'detailed', 'visibility-wide'],
    'profile-high': [3000, 1, 'detailed', 'profile-high'],
    'high-dpr': [3000, 1, 'detailed', 'high-dpr'],
    recovery: [3000, 1, 'detailed', 'recovery'],
    soak: [3000, 1, 'detailed', 'soak'],
  };
  assert.deepEqual(Object.fromEntries(Object.entries(integratedBenchmarkScenarios).map(([name, definition]) =>
    [name, [checkpointTarget(name), scenarioSpeed(name), definition.workload, definition.workflow]])), expected);
});

test('uses the immutable production checkpoint seed and rejects a missing seed', () => {
  assert.equal(checkpointSeed({ dualClock: { seed: 'jackson:domain-v1' } }), 'jackson:domain-v1');
  assert.throws(() => checkpointSeed({ dualClock: {} }), /lacks its production dual-clock seed/);
});

test('requires the selected Playwright project to collect a workflow before paired attempts', () => {
  assert.deepEqual(playwrightCollectionErrors(0, 'Total: 2 tests in 1 file'), []);
  assert.deepEqual(playwrightCollectionErrors(1, 'Error: module import failed\nTotal: 0 tests'), [
    'Playwright collection exited 1.', 'Playwright collection found no workflow tests.',
  ]);
});

test('parent trial deadline exceeds runtime child budget and includes reopen allowance', () => {
  const electron = { runtime: 'electron', tier: 'qualification', warmupMs: 30_000, measureMs: 120_000, reopenCycles: 10 };
  assert.equal(trialDeadlineMs(electron), 710_000);
  assert.ok(trialDeadlineMs(electron) > electron.warmupMs + electron.measureMs + 480_000);
  const soak = { ...electron, measureMs: 1_200_000 };
  assert.ok(trialDeadlineMs(soak) > soak.warmupMs + soak.measureMs + 480_000);
  assert.equal(trialDeadlineMs({ ...electron, reopenCycles: 1 }) + 45_000, trialDeadlineMs(electron));
});

test('requires immutable clean source and build identity for qualification', () => {
  const source = { commit: 'a'.repeat(40), workingTreeStatus: 'clean', lockfileHash: 'b'.repeat(64),
    buildHash: 'c'.repeat(64) };
  assert.deepEqual(qualificationSourceErrors(source), []);
  assert.deepEqual(qualificationSourceErrors({ ...source, commit: 'short', workingTreeStatus: 'dirty' }), [
    'Source commit must be a full immutable Git SHA.',
    'Qualification source must have a clean working tree.',
  ]);
});

test('enforces qualification windows, pair blocks, and soak duration', () => {
  const base = ['--runtime', 'browser', '--fixture', 'fixture', '--output', 'test-results/out', '--scenario', 'interactive'];
  assert.throws(() => parseArguments([...base, '--pairs', '6']), /5 or 10/);
  assert.throws(() => parseArguments([...base, '--measure-ms', '119999']), /at least 120000/);
  assert.throws(() => parseArguments([...base, '--warmup-ms', '29999']), /exactly 30000/);
  assert.throws(() => parseArguments([...base.slice(0, -1), 'soak', '--measure-ms', '1199999']), /at least 1200000/);
  const diagnostic = parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '1', '--profiling', 'heavy']);
  assert.deepEqual({ tier: diagnostic.tier, pairs: diagnostic.pairs, warmupMs: diagnostic.warmupMs,
    measureMs: diagnostic.measureMs, profiling: diagnostic.profiling },
  { tier: 'diagnostic', pairs: 1, warmupMs: 1, measureMs: 1, profiling: 'heavy' });
  assert.throws(() => parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '30001',
    '--measure-ms', '1', '--profiling', 'heavy']), /limited to a 30s warm-up and 60s measurement/);
  assert.throws(() => parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '60001', '--profiling', 'heavy']), /limited to a 30s warm-up and 60s measurement/);
  assert.throws(() => parseArguments([...base.slice(0, -1), 'soak', '--tier', 'diagnostic', '--pairs', '1',
    '--warmup-ms', '1', '--measure-ms', '1', '--profiling', 'heavy']), /cannot run as soak/);
  assert.throws(() => parseArguments([...base, '--tier', 'diagnostic', '--pairs', '5']), /exactly one pair/);
  assert.throws(() => parseArguments([...base, '--profiling', 'heavy']), /diagnostic-only/);
  const react = parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '1', '--profiling', 'react']);
  assert.equal(react.profiling, 'react');
  assert.throws(() => parseArguments([...base.slice(0, 1), 'electron', ...base.slice(2),
    '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1', '--measure-ms', '1', '--profiling', 'react']),
  /dedicated browser renderer/);
  const aOnly = parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '1', '--condition', 'A']);
  assert.deepEqual(planTrials(aOnly).map(trial => trial.condition), ['A']);
  assert.throws(() => parseArguments([...base, '--condition', 'A']), /diagnostic-only/);
  const snowDemand = [...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '1', '--snow-demand', '1', '--fixture-kind', 'ci', '--scenario', 'ci-diagnostic'];
  assert.throws(() => parseArguments(snowDemand), /use --condition B/);
  assert.throws(() => parseArguments([...snowDemand, '--condition', 'A']), /use --condition B/);
  assert.throws(() => parseArguments([...base, '--tier', 'diagnostic', '--pairs', '1', '--warmup-ms', '1',
    '--measure-ms', '1', '--snow-demand', '1', '--condition', 'B']), /fixture-kind ci/);
  assert.equal(parseArguments([...snowDemand, '--condition', 'B']).snowDemand, true);
});

test('accepts a single-attempt CI diagnostic without a Jackson fixture path', () => {
  const options = parseArguments(['--runtime', 'browser', '--scenario', 'ci-diagnostic', '--comparison',
    'telemetry-overhead', '--tier', 'diagnostic', '--pairs', '1', '--fixture-kind', 'ci', '--output',
    'test-results/integrated-benchmark/ci-diagnostic']);
  assert.equal(options.fixtureKind, 'ci'); assert.equal(options.fixture, null); assert.equal(options.tier, 'diagnostic');
  assert.throws(() => parseArguments(['--runtime', 'electron', '--scenario', 'ci-diagnostic', '--comparison',
    'telemetry-overhead', '--tier', 'diagnostic', '--pairs', '1', '--fixture-kind', 'ci', '--output',
    'test-results/integrated-benchmark/ci-electron']), /CI diagnostics are browser-only/);
});

test('accepts physical GL evidence and rejects software rendering or wrong canvas', () => {
  const expected = { id: 'run', pairIndex: 1, order: 'AB', condition: 'A', telemetryEnabled: false };
  const result = { ...expected, status: 'valid', identity: {
    profile: 'Standard', cssViewport: { width: 1920, height: 1080 }, canvas: { width: 1920, height: 1080 },
    devicePixelRatio: 1, refreshHz: 60, runtime: { kind: 'browser', version: 'Chromium 140',
      glVendor: 'Google Inc. (NVIDIA)', glRenderer: 'ANGLE (NVIDIA GeForce RTX 3060 Ti)', glBackend: 'D3D11',
      executableHash: 'a'.repeat(64) },
  }, measurement: { windowDurationMs: 120_000, expectedFrames: 7_200, deliveredFrames: 7_000,
    trailingGapAccounted: true, livenessPassed: true }, metrics: { frameIntervalMs: 16.7 },
  samples: { frameIntervalMs: Array.from({ length: 30 }, () => 16.7),
    mapRenderIntervalMs: Array.from({ length: 30 }, () => 16.7) }, evidence: structuredClone(validRuntimeEvidence) };
  assert.deepEqual(emittedTrialErrors(result, expected, { runtime: 'browser', measureMs: 120_000, scenario: 'empty' }), []);
  const software = structuredClone(result);
  software.identity.runtime.glRenderer = 'ANGLE SwiftShader Device';
  software.identity.canvas.width = 1919;
  assert.deepEqual(emittedTrialErrors(software, expected, { runtime: 'browser', measureMs: 120_000, scenario: 'empty' }), [
    'Software or unknown GL cannot qualify a hardware lane.',
    'physical canvas must be exactly 1920x1080.',
  ]);
  const wrongCache = structuredClone(result);
  assert.ok(emittedTrialErrors(wrongCache, expected,
    { runtime: 'browser', measureMs: 120_000, scenario: 'empty', cacheState: 'cold' })
    .includes('Trial did not prove the requested cold/warm cache-state policy.'));
  const soak = structuredClone(result);
  soak.evidence.simulation.targetOccupancy = 3000;
  soak.evidence.simulation.publications = soak.evidence.simulation.publications.map(publication =>
    ({ ...publication, active: 3000, movementCount: 3000 }));
  soak.evidence.simulation.backlog = { availability: 'available', samples: [0, 1] };
  soak.evidence.presentation.lastDrawnRevision = { availability: 'available', value: 7 };
  soak.evidence.memory = Object.fromEntries(['process', 'renderer', 'worker', 'gpu'].map(lane =>
    [lane, { availability: 'available', beforeBytes: 100, afterBytes: 105, method: `${lane} test API` }]));
  soak.evidence.memory.boundedCounts = Object.fromEntries(['objects', 'caches', 'workers'].map(count =>
    [count, { availability: 'available', before: 1, after: 1, maximum: 2 }]));
  assert.deepEqual(emittedTrialErrors(soak, expected,
    { runtime: 'browser', measureMs: 120_000, scenario: 'soak', cacheState: 'warm' }), []);
  soak.evidence.memory.gpu = { availability: 'unavailable', reason: 'browser GPU API unavailable' };
  assert.deepEqual(emittedTrialErrors(soak, expected,
    { runtime: 'browser', measureMs: 120_000, scenario: 'soak', cacheState: 'warm' }), []);
  assert.equal(summarizeMemoryGate(soak.evidence.memory).status, 'incomplete');
  assert.match(summarizeMemoryGate(soak.evidence.memory).unavailable[0], /gpu/);
  assert.equal(summarizeTrialEvidence(soak).qualification.status, 'incomplete');
  soak.evidence.memory.gpu = { availability: 'available', beforeBytes: 100, afterBytes: 105, method: 'gpu test API' };
  soak.evidence.memory.process = { availability: 'available', beforeBytes: 100, afterBytes: 111, method: 'test' };
  assert.deepEqual(emittedTrialErrors(soak, expected,
    { runtime: 'browser', measureMs: 120_000, scenario: 'soak', cacheState: 'warm' }), []);
  assert.equal(summarizeMemoryGate(soak.evidence.memory).status, 'fail');
  assert.match(summarizeMemoryGate(soak.evidence.memory).failures[0], /process retained memory grew 11\.00%/);
  assert.equal(summarizeTrialEvidence(soak).qualification.status, 'fail');
  const highDpr = structuredClone(soak);
  highDpr.identity.cssViewport = { width: 960, height: 540 };
  highDpr.identity.devicePixelRatio = 2;
  delete highDpr.evidence.memory;
  assert.deepEqual(emittedTrialErrors(highDpr, expected,
    { runtime: 'browser', measureMs: 120_000, scenario: 'high-dpr', cacheState: 'warm' }), []);
});

test('rejects a trial that rewrites runner-supplied comparison identity', () => {
  const expected = { id: 'run', pairIndex: 1, order: 'AB', condition: 'A', telemetryEnabled: false };
  const identity = { scenarioId: 'detailed-3000', workloadId: 'fixture', seed: 'pair-1',
    checkpointHash: 'checkpoint', fixtureManifestHash: 'manifest', assetManifestHash: 'assets',
    cacheStatePolicy: 'warm', profile: 'Standard', refreshHz: 60,
    cssViewport: { width: 1920, height: 1080 }, canvas: { width: 1920, height: 1080 }, devicePixelRatio: 1,
    machine: { id: 'pc', cpu: 'cpu', gpu: 'gpu', driver: 'driver', os: 'os' },
    source: { commit: 'abc', workingTreeStatus: 'clean', lockfileHash: 'lock', buildHash: 'build' },
    runtime: { kind: 'browser', version: 'Chromium', glVendor: 'NVIDIA', glRenderer: 'RTX 3060 Ti',
      glBackend: 'D3D11', executableHash: 'b'.repeat(64) } };
  const result = { ...expected, status: 'valid', identity: structuredClone(identity),
    metrics: { frameIntervalMs: 16.7 }, samples: { frameIntervalMs: Array.from({ length: 30 }, () => 16.7),
      mapRenderIntervalMs: Array.from({ length: 30 }, () => 16.7) },
    measurement: { windowDurationMs: 120_000, expectedFrames: 7_200, deliveredFrames: 7_000,
      trailingGapAccounted: true, livenessPassed: true }, evidence: structuredClone(validRuntimeEvidence) };
  result.identity.source.commit = 'different';
  assert.ok(emittedTrialErrors(result, expected, { runtime: 'browser', measureMs: 120_000, scenario: 'empty' }, identity)
    .includes('Valid trial identity source does not match the runner input.'));
});

test('derives achieved clock, occupancy, revision, and unavailable backlog evidence from retained samples', () => {
  const result = { measurement: { windowDurationMs: 120_000 }, evidence: structuredClone(validRuntimeEvidence) };
  assert.deepEqual(summarizeTrialEvidence(result), {
    publicationCount: 2, requestedSpeed: 1, macroSecondsAdvanced: 120, microSecondsAdvanced: 120,
    achievedMacroSecondsPerWallSecond: 1, achievedMicroSecondsPerWallSecond: 1,
    activeOccupancy: { min: 0, median: 0, max: 0 }, movementOccupancy: { min: 0, median: 0, max: 0 },
    finalCommittedRevision: 1, finalPublicationSequence: null,
    backlog: { availability: 'unavailable', reason: 'worker target timestamps unavailable in fixture test' },
    lastDrawnRevision: { availability: 'unavailable', reason: 'empty workload has no guest draw revision' },
    backlogGate: { status: 'incomplete', observation: {
      availability: 'unavailable', reason: 'worker target timestamps unavailable in fixture test' } },
    presentationGate: { status: 'incomplete', observation: {
      availability: 'unavailable', reason: 'empty workload has no guest draw revision' } },
    cacheState: { policy: 'warm', profileIsolated: true, checkpointRestored: true,
      fixtureManifestHash: undefined, cleared: true, primed: true, primeCompleted: true },
    qualification: { status: 'incomplete', gates: {
      backlog: { status: 'incomplete', observation: {
        availability: 'unavailable', reason: 'worker target timestamps unavailable in fixture test' } },
      presentation: { status: 'incomplete', observation: {
        availability: 'unavailable', reason: 'empty workload has no guest draw revision' } },
    } },
  });
});

test('CLI exits nonzero and retains an invalid trial when packaged launch preflight fails', async () => {
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const resultRoot = path.join(repositoryRoot, 'test-results');
  await mkdir(resultRoot, { recursive: true });
  const temporary = await mkdtemp(path.join(resultRoot, 'integrated-runner-failure-'));
  try {
    const fixture = path.join(temporary, 'fixture'), output = path.join(temporary, 'output');
    await mkdir(fixture);
    const checkpoint = Buffer.from(JSON.stringify({ dualClock: { seed: 'failure-test-seed' } }));
    const digest = createHash('sha256').update(checkpoint).digest('hex');
    await writeFile(path.join(fixture, 'save-0.json'), checkpoint);
    await writeFile(path.join(fixture, 'fixture-manifest.json'), JSON.stringify({ fixtureId: 'failure-test',
      artifacts: { checkpointEmpty: { path: 'save-0.json', bytes: checkpoint.byteLength, sha256: digest } } }));
    const missing = path.join(temporary, 'missing-packaged.exe');
    const run = spawnSync(process.execPath, [path.join(repositoryRoot, 'scripts/runIntegratedBenchmark.mjs'),
      '--runtime', 'electron', '--fixture', fixture, '--output', output, '--scenario', 'empty',
      '--condition-a-target', missing, '--condition-b-target', missing],
    { cwd: repositoryRoot, encoding: 'utf8', windowsHide: true });
    assert.equal(run.status, 1);
    const trial = JSON.parse(await readFile(path.join(output,
      'electron-empty-telemetry-overhead-p1-a.trial.json'), 'utf8'));
    const summary = JSON.parse(await readFile(path.join(output, 'run-summary.json'), 'utf8'));
    assert.equal(trial.status, 'invalid');
    assert.match(trial.invalidReasons[0], /missing-packaged\.exe|ENOENT/);
    assert.deepEqual({ status: summary.status, attemptedTrials: summary.attemptedTrials },
      { status: 'incomplete', attemptedTrials: 1 });
  } finally {
    const resolved = path.resolve(temporary), allowed = path.resolve(resultRoot) + path.sep;
    if (!resolved.startsWith(allowed)) throw new Error('Refusing to remove a test directory outside test-results.');
    await rm(resolved, { recursive: true, force: true });
  }
});
