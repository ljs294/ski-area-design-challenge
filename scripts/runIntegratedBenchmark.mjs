import { execFile, spawn } from 'node:child_process';
import { access, mkdir, mkdtemp, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { build, preview } from 'vite';
import {
  assertRegularFile,
  collectHardwareMetadata,
  gitSourceIdentity,
  hashDirectory,
  hashFile,
  qualificationInventoryErrors,
  sha256,
} from './integratedBenchmarkHardware.mjs';
import { cleanupPackagedElectronFromUserData } from './packagedElectronControl.mjs';
import { integratedBenchmarkScenarios, resolveIntegratedBenchmarkConfiguration } from './integratedBenchmarkConfiguration.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const playwrightCli = path.join(path.dirname(require.resolve('@playwright/test/package.json')), 'cli.js');
const browserExecutable = require('@playwright/test').chromium.executablePath();
export { integratedBenchmarkScenarios };
const MAX_PLAYWRIGHT_LOG_BYTES = 128 * 1024;

function required(value, message) {
  if (!value) throw new Error(message);
  return value;
}

function integer(value, label) {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) throw new Error(`${label} must be a positive integer.`);
  return parsed;
}

export function parseArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) {
    const flag = argv[index];
    if (!flag?.startsWith('--') || !argv[index + 1]) throw new Error(`Invalid argument near '${flag ?? ''}'.`);
    values[flag.slice(2)] = argv[index + 1];
  }
  const runtime = required(values.runtime, '--runtime browser|electron is required.');
  if (runtime !== 'browser' && runtime !== 'electron') throw new Error('--runtime must be browser or electron.');
  const scenario = required(values.scenario, '--scenario is required.');
  if (!Object.hasOwn(integratedBenchmarkScenarios, scenario)) throw new Error(`Unsupported scenario '${scenario}'.`);
  const comparison = values.comparison ?? 'telemetry-overhead';
  if (comparison !== 'telemetry-overhead' && comparison !== 'optimization-regression') {
    throw new Error('--comparison must be telemetry-overhead or optimization-regression.');
  }
  const tier = values.tier ?? 'qualification';
  if (tier !== 'qualification' && tier !== 'diagnostic') throw new Error('--tier must be qualification or diagnostic.');
  const fixtureKind = values['fixture-kind'] ?? 'jackson';
  resolveIntegratedBenchmarkConfiguration({ INTEGRATED_FIXTURE_KIND: fixtureKind, INTEGRATED_TIER: tier,
    INTEGRATED_SCENARIO: scenario });
  if (runtime === 'electron' && fixtureKind === 'ci') {
    throw new Error('CI diagnostics are browser-only; packaged Electron requires the Jackson fixture.');
  }
  const pairs = integer(values.pairs ?? (tier === 'diagnostic' ? '1' : '5'), '--pairs');
  if (tier === 'qualification' ? pairs !== 5 && pairs !== 10 : pairs !== 1) {
    throw new Error(tier === 'qualification' ? '--pairs must be 5 or 10.' : 'Diagnostic smoke requires exactly one pair.');
  }
  const cacheState = values['cache-state'] ?? 'warm';
  if (cacheState !== 'cold' && cacheState !== 'warm') throw new Error('--cache-state must be cold or warm.');
  const optimizationTelemetry = values.telemetry ?? '1';
  if (optimizationTelemetry !== '0' && optimizationTelemetry !== '1') throw new Error('--telemetry must be 0 or 1.');
  const warmupMs = integer(values['warmup-ms'] ?? '30000', '--warmup-ms');
  const profiling = values.profiling ?? 'none';
  if (!['none', 'heavy', 'react'].includes(profiling)) throw new Error('--profiling must be none, heavy, or react.');
  if (profiling !== 'none' && tier !== 'diagnostic') throw new Error('Profiling is diagnostic-only.');
  if (profiling === 'react' && runtime !== 'browser') throw new Error('React profiling requires the dedicated browser renderer build.');
  if (tier === 'qualification' && warmupMs !== 30_000) throw new Error('Qualification warm-up must be exactly 30000 ms.');
  const measureDefault = scenario === 'soak' ? '1200000' : '120000';
  const measureMs = integer(values['measure-ms'] ?? measureDefault, '--measure-ms');
  if (profiling !== 'none' && (scenario === 'soak' || warmupMs > 30_000 || measureMs > 60_000)) {
    throw new Error('Profiling diagnostics are limited to a 30s warm-up and 60s measurement and cannot run as soak.');
  }
  if (tier === 'qualification' && scenario !== 'soak' && measureMs < 120_000) {
    throw new Error('Qualification measurement must be at least 120000 ms.');
  }
  if (tier === 'qualification' && scenario === 'soak' && measureMs < 1_200_000) {
    throw new Error('Soak measurement must be at least 1200000 ms.');
  }
  const condition = values.condition?.toUpperCase();
  if (condition && condition !== 'A' && condition !== 'B') throw new Error('--condition must be A or B.');
  if (condition && tier !== 'diagnostic') throw new Error('--condition is diagnostic-only.');
  const snowDemand = values['snow-demand'] === '1';
  if (snowDemand && tier !== 'diagnostic') throw new Error('--snow-demand is diagnostic-only.');
  if (snowDemand && fixtureKind !== 'ci') throw new Error('--snow-demand currently requires --fixture-kind ci.');
  if (snowDemand && comparison === 'telemetry-overhead' && condition !== 'B') {
    throw new Error('--snow-demand requires telemetry; use --condition B for telemetry-overhead diagnostics.');
  }
  if (snowDemand && comparison === 'optimization-regression' && optimizationTelemetry !== '1') {
    throw new Error('--snow-demand requires --telemetry 1.');
  }
  return {
    runtime, scenario, comparison, tier, fixtureKind, profiling, pairs, cacheState, warmupMs, measureMs, snowDemand,
    fixture: fixtureKind === 'jackson' ? path.resolve(required(values.fixture, '--fixture is required for Jackson.')) : null,
    output: path.resolve(required(values.output, '--output is required.')),
    port: integer(values.port ?? '44582', '--port'),
    reopenCycles: integer(values['reopen-cycles'] ?? '10', '--reopen-cycles'),
    optimizationTelemetry: optimizationTelemetry === '1',
    conditionATarget: values['condition-a-target'] ? path.resolve(values['condition-a-target']) : null,
    conditionBTarget: values['condition-b-target'] ? path.resolve(values['condition-b-target']) : null,
    conditionASource: values['condition-a-source'] ? path.resolve(values['condition-a-source']) : null,
    conditionBSource: values['condition-b-source'] ? path.resolve(values['condition-b-source']) : null,
    hardware: values.hardware ? path.resolve(values.hardware) : null,
    condition,
  };
}

export function planTrials(options) {
  const trials = [];
  for (let pairIndex = 1; pairIndex <= options.pairs; pairIndex++) {
    const order = pairIndex % 2 === 1 ? 'AB' : 'BA';
    for (const condition of order) {
      const telemetryEnabled = options.comparison === 'telemetry-overhead'
        ? condition === 'B' : options.optimizationTelemetry;
      const runId = `${options.tier === 'diagnostic' ? 'diagnostic-' : ''}${options.runtime}-${options.scenario}-${options.comparison}-p${pairIndex}-${condition.toLowerCase()}`;
      trials.push({ runId, id: runId, pairIndex, order, condition, telemetryEnabled });
    }
  }
  return options.condition ? trials.filter(trial => trial.condition === options.condition) : trials;
}

export function trialDeadlineMs(options) {
  const childBudget = options.runtime === 'electron'
    ? Math.max(600_000, options.warmupMs + options.measureMs + 480_000)
    : Math.max(300_000, options.warmupMs + options.measureMs + (options.tier === 'qualification' ? 420_000 : 240_000));
  return childBudget + 30_000 + options.reopenCycles * 5_000;
}

function ensureOutputPath(output) {
  const relative = path.relative(root, output);
  if (relative.startsWith('..') || path.isAbsolute(relative) || !relative.replaceAll('\\', '/').startsWith('test-results/')) {
    throw new Error('Benchmark output must be beneath this repository\'s test-results directory.');
  }
}

async function ensureResolvedOutputPath(output) {
  ensureOutputPath(output);
  const resultRoot = path.join(root, 'test-results');
  await mkdir(resultRoot, { recursive: true });
  const [actualRoot, actualResultRoot] = await Promise.all([realpath(root), realpath(resultRoot)]);
  const rootRelative = path.relative(actualRoot, actualResultRoot);
  if (rootRelative.startsWith('..') || path.isAbsolute(rootRelative)) {
    throw new Error('Repository test-results resolves outside the repository root.');
  }
  await mkdir(output, { recursive: true });
  const actualOutput = await realpath(output);
  const outputRelative = path.relative(actualResultRoot, actualOutput);
  if (outputRelative.startsWith('..') || path.isAbsolute(outputRelative)) {
    throw new Error('Benchmark output resolves outside the repository test-results directory.');
  }
}

async function sourceFromFile(filePath, fallback) {
  if (!filePath) return fallback;
  const value = JSON.parse(await readFile(filePath, 'utf8'));
  for (const field of ['commit', 'workingTreeStatus', 'lockfileHash']) {
    if (typeof value[field] !== 'string' || !value[field]) throw new Error(`Source identity ${filePath} lacks ${field}.`);
  }
  return value;
}

function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  return JSON.stringify(value);
}

function checkpointRole(scenario) {
  const target = checkpointTarget(scenario);
  if (target === 0) return 'checkpointEmpty';
  if (target === 1000) return 'checkpoint1000';
  return 'checkpoint3000';
}

export function checkpointTarget(scenario) {
  const definition = integratedBenchmarkScenarios[scenario];
  if (!definition) throw new Error(`Unsupported scenario '${scenario}'.`);
  return definition.checkpointTarget;
}

export function scenarioSpeed(scenario) {
  const definition = integratedBenchmarkScenarios[scenario];
  if (!definition) throw new Error(`Unsupported scenario '${scenario}'.`);
  return definition.requestedSpeed;
}

export function checkpointSeed(save) {
  if (typeof save?.dualClock?.seed !== 'string' || !save.dualClock.seed) {
    throw new Error('Fixture checkpoint lacks its production dual-clock seed.');
  }
  return save.dualClock.seed;
}

async function fixtureIdentity(fixtureRoot, scenario) {
  const manifestPath = path.join(fixtureRoot, 'fixture-manifest.json');
  const manifestBytes = await readFile(manifestPath);
  const manifest = JSON.parse(manifestBytes.toString('utf8'));
  const checkpoint = manifest.artifacts?.[checkpointRole(scenario)];
  if (!checkpoint?.sha256 || typeof checkpoint.path !== 'string') {
    throw new Error(`Fixture manifest lacks ${checkpointRole(scenario)}.`);
  }
  const checkpointPath = path.resolve(fixtureRoot, checkpoint.path);
  const checkpointRelative = path.relative(fixtureRoot, checkpointPath);
  if (checkpointRelative.startsWith('..') || path.isAbsolute(checkpointRelative)) {
    throw new Error('Fixture checkpoint path escapes its fixture root.');
  }
  const checkpointBytes = await readFile(checkpointPath);
  if (sha256(checkpointBytes) !== checkpoint.sha256) throw new Error('Fixture checkpoint SHA-256 does not match its manifest.');
  return {
    manifest,
    fixtureManifestHash: sha256(manifestBytes),
    assetManifestHash: sha256(Buffer.from(canonical(manifest.artifacts))),
    checkpointHash: checkpoint.sha256,
    checkpointSeed: checkpointSeed(JSON.parse(checkpointBytes.toString('utf8'))),
  };
}

async function closePreview(server) {
  if (!server) return;
  server.httpServer.closeAllConnections?.();
  if (server.httpServer.listening) await new Promise((resolve, reject) =>
    server.httpServer.close((error) => error ? reject(error) : resolve()));
}

async function runPlaywright(project, environment) {
  const focusedDiagnostic = environment.INTEGRATED_TIER === 'diagnostic'
    ? ['--grep', 'integrated App to MapLibre diagnostic or qualification workflow'] : [];
  const child = spawn(process.execPath, [playwrightCli, 'test', '--config=playwright.integratedBenchmark.config.ts',
    `--project=${project}`, ...focusedDiagnostic], { cwd: root, env: { ...process.env, ...environment, PW_TEST_HTML_REPORT_OPEN: 'never' },
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  const logs = { stdout: '', stderr: '' };
  const append = (field, chunk) => {
    logs[field] = `${logs[field]}${chunk}`.slice(-MAX_PLAYWRIGHT_LOG_BYTES);
  };
  child.stdout.on('data', chunk => { append('stdout', chunk); process.stdout.write(chunk); });
  child.stderr.on('data', chunk => { append('stderr', chunk); process.stderr.write(chunk); });
  const configuredDeadline = Number(environment.INTEGRATED_TRIAL_DEADLINE_MS);
  return await new Promise((resolve, reject) => {
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      if (process.platform === 'win32') execFile('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'],
        { windowsHide: true }, () => undefined);
      else child.kill('SIGKILL');
    }, configuredDeadline);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', (code, signal) => { clearTimeout(timer); resolve({
      exitCode: timedOut ? 124 : code ?? (signal ? 1 : 0), logs,
    }); });
  });
}

export function playwrightCollectionErrors(exitCode, output) {
  const errors = [];
  if (exitCode !== 0) errors.push(`Playwright collection exited ${exitCode}.`);
  if (!/\b(?:[1-9]\d*) tests?\b/i.test(output)) errors.push('Playwright collection found no workflow tests.');
  return errors;
}

async function preflightPlaywright(project, environment) {
  const focusedDiagnostic = environment.INTEGRATED_TIER === 'diagnostic'
    ? ['--grep', 'integrated App to MapLibre diagnostic or qualification workflow'] : [];
  const child = spawn(process.execPath, [playwrightCli, 'test', '--config=playwright.integratedBenchmark.config.ts',
    `--project=${project}`, ...focusedDiagnostic, '--list'], { cwd: root,
    env: { ...process.env, ...environment, PW_TEST_HTML_REPORT_OPEN: 'never' },
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  let output = '';
  child.stdout.on('data', chunk => { output += chunk; });
  child.stderr.on('data', chunk => { output += chunk; });
  const exitCode = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => resolve(code ?? (signal ? 1 : 0)));
  });
  const errors = playwrightCollectionErrors(exitCode, output);
  if (errors.length) throw new Error(`${errors.join(' ')}\n${output.trim()}`);
  return { exitCode, output: output.trim() };
}

async function writeInvalid(filePath, trial, identity, reason, diagnostics = {}) {
  const value = { ...trial, status: 'invalid', invalidReasons: [reason], identity, diagnostics,
    recordedAt: new Date().toISOString() };
  await writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
  return value;
}

/** Retain the workflow's diagnostic object instead of replacing it on runner failure. */
export function mergeFailedTrial(trial, identity, emitted, { exitCode, logs = {} }) {
  const runnerDiagnostics = {
    exitCode,
    ...(logs.stdout ? { stdout: logs.stdout } : {}),
    ...(logs.stderr ? { stderr: logs.stderr } : {}),
  };
  const runnerReason = exitCode !== 0 ? `Playwright trial exited ${exitCode}.` : null;
  if (!emitted || typeof emitted !== 'object') {
    return { ...trial, status: 'invalid', invalidReasons: [runnerReason ?? 'Scenario did not emit a trial result.'], identity,
      diagnostics: { stage: 'runner-no-scenario-result', ...runnerDiagnostics }, recordedAt: new Date().toISOString() };
  }
  const existingReasons = Array.isArray(emitted.invalidReasons) ? emitted.invalidReasons.filter(reason => typeof reason === 'string') : [];
  return {
    ...emitted,
    status: 'invalid',
    invalidReasons: [...new Set([...existingReasons, ...(runnerReason ? [runnerReason] : [])])],
    diagnostics: { ...(emitted.diagnostics && typeof emitted.diagnostics === 'object' ? emitted.diagnostics : {}),
      runner: runnerDiagnostics },
  };
}

export function emittedTrialErrors(result, expected, options, expectedIdentity = null) {
  const errors = [];
  if (!result || typeof result !== 'object') return ['Scenario result is not an object.'];
  for (const field of ['id', 'pairIndex', 'order', 'condition', 'telemetryEnabled']) {
    if (result[field] !== expected[field]) errors.push(`Scenario result ${field} does not match the planned trial.`);
  }
  if (result.status !== 'valid') return errors;
  const identity = result.identity;
  const runtime = identity?.runtime;
  const display = integratedBenchmarkScenarios[options.scenario];
  if (options.tier === 'diagnostic') {
    if (runtime?.kind !== options.runtime) errors.push('Diagnostic runtime kind does not match the requested lane.');
    const gl = [runtime?.glVendor, runtime?.glRenderer, runtime?.glBackend].join(' ');
    if (!runtime?.version || !runtime?.glVendor || !runtime?.glRenderer || !runtime?.glBackend
      || /swiftshader|llvmpipe|software|unknown|unavailable/i.test(gl)) {
      errors.push('Diagnostic requires an identified hardware GL runtime.');
    }
    if (identity?.profile !== display.profile) errors.push(`Diagnostic must use the ${display.profile} profile.`);
    if (!Number.isFinite(identity?.refreshHz) || Math.abs(identity.refreshHz - 60) > 1) {
      errors.push('Diagnostic refresh must be 60 Hz.');
    }
    for (const [label, size, expectedSize] of [['CSS viewport', identity?.cssViewport, display.cssViewport],
      ['physical canvas', identity?.canvas, display.canvas]]) {
      if (size?.width !== expectedSize.width || size?.height !== expectedSize.height) {
        errors.push(`Diagnostic ${label} must be exactly ${expectedSize.width}x${expectedSize.height}.`);
      }
    }
    if (identity?.devicePixelRatio !== display.devicePixelRatio) errors.push('Diagnostic DPR does not match the scenario.');
    const measurement = result.measurement;
    if (!Number.isFinite(measurement?.windowDurationMs) || measurement.windowDurationMs < options.measureMs
      || measurement?.livenessPassed !== true) errors.push('Diagnostic measurement failed duration or liveness.');
    if (!Array.isArray(result.samples?.frameIntervalMs) || result.samples.frameIntervalMs.length < 30
      || !Array.isArray(result.samples?.mapRenderIntervalMs) || result.samples.mapRenderIntervalMs.length < 30) {
      errors.push('Diagnostic requires frame and map-render samples.');
    }
    const sourceState = result.evidence?.sourceState;
    if (!sourceState?.terrain || !sourceState?.terrainLoaded || !sourceState?.snow || !sourceState?.snowLoaded
      || !sourceState?.mapLoaded) errors.push('Diagnostic terrain, snow, or map readiness failed.');
    if (options.fixtureKind === 'jackson' && (!sourceState?.imagery || !sourceState?.imageryLoaded)) {
      errors.push('Jackson diagnostic imagery readiness failed.');
    }
    const publications = result.evidence?.simulation?.publications;
    const target = Number(display.checkpointTarget);
    const threshold = Math.ceil(target * .95);
    const adequatePopulation = Array.isArray(publications) && publications.every(publication => {
      const active = Number(publication?.active), movement = Number(publication?.movementCount);
      if (!Number.isFinite(active) || !Number.isFinite(movement)) return false;
      if (display.workload === 'empty') return active === 0 && movement === 0;
      if (display.workload === 'aggregate') return active >= threshold && movement === 0;
      return active >= threshold && movement >= threshold;
    });
    if (!Array.isArray(publications) || publications.length < 2 || !adequatePopulation) {
      errors.push('Diagnostic population or simulation publication evidence failed.');
    }
    for (const field of ['pauseResponseMs', 'workerCancellationMs', 'sourceRevisionResponseMs', 'gradingConfirmationMs']) {
      if (!Number.isFinite(result.metrics?.[field])) errors.push(`Diagnostic metric ${field} is unavailable.`);
    }
    return errors;
  }
  if (expectedIdentity) {
    for (const field of ['tier', 'appQuery', 'scenarioId', 'workloadId', 'seed', 'checkpointHash', 'fixtureManifestHash',
      'assetManifestHash', 'cacheStatePolicy', 'profile', 'refreshHz']) {
      if (canonical(identity?.[field]) !== canonical(expectedIdentity[field])) {
        errors.push(`Valid trial identity ${field} does not match the runner input.`);
      }
    }
    for (const field of ['machine', 'source']) if (canonical(identity?.[field]) !== canonical(expectedIdentity[field])) {
      errors.push(`Valid trial identity ${field} does not match the runner input.`);
    }
    if (runtime?.executableHash !== expectedIdentity.runtime?.executableHash) {
      errors.push('Valid trial runtime executable hash does not match the runner input.');
    }
  }
  for (const field of ['version', 'glVendor', 'glRenderer', 'glBackend']) {
    if (typeof runtime?.[field] !== 'string' || !runtime[field] || runtime[field] === 'captured-by-spec') {
      errors.push(`Valid trial is missing runtime ${field}.`);
    }
  }
  if (typeof runtime?.executableHash !== 'string' || !/^[a-f0-9]{64}$/.test(runtime.executableHash)) {
    errors.push('Valid trial is missing the runtime executable SHA-256.');
  }
  const gl = [runtime?.glVendor, runtime?.glRenderer, runtime?.glBackend].join(' ');
  if (/swiftshader|llvmpipe|software|unknown|unavailable/i.test(gl)) errors.push('Software or unknown GL cannot qualify a hardware lane.');
  if (runtime?.kind !== options.runtime) errors.push('Runtime kind does not match the requested lane.');
  if (identity?.profile !== display.profile) errors.push(`Valid trial did not use the ${display.profile} profile.`);
  for (const [label, size, expectedSize] of [['CSS viewport', identity?.cssViewport, display.cssViewport],
    ['physical canvas', identity?.canvas, display.canvas]]) {
    if (size?.width !== expectedSize.width || size?.height !== expectedSize.height) {
      errors.push(`${label} must be exactly ${expectedSize.width}x${expectedSize.height}.`);
    }
  }
  if (identity?.devicePixelRatio !== display.devicePixelRatio) {
    errors.push(`Valid trial DPR must be exactly ${display.devicePixelRatio}.`);
  }
  if (!Number.isFinite(identity?.refreshHz) || Math.abs(identity.refreshHz - 60) > 1) errors.push('Valid trial refresh must be 60 Hz.');
  const measurement = result.measurement;
  if (!Number.isFinite(measurement?.windowDurationMs) || measurement.windowDurationMs < options.measureMs) {
    errors.push(`Valid trial did not cover the requested ${options.measureMs} ms window.`);
  }
  if (!Number.isInteger(measurement?.expectedFrames) || measurement.expectedFrames <= 0 ||
    !Number.isInteger(measurement?.deliveredFrames) || measurement.deliveredFrames <= 0) {
    errors.push('Valid trial requires expected and delivered frame counts.');
  }
  if (measurement?.trailingGapAccounted !== true) errors.push('Valid trial did not account for its trailing window gap.');
  if (measurement?.livenessPassed !== true) errors.push('Valid trial failed liveness checks.');
  if (!result.metrics || typeof result.metrics !== 'object' || !Object.keys(result.metrics).length ||
    Object.values(result.metrics).some((value) => !Number.isFinite(value))) {
    errors.push('Valid trial requires finite numeric metrics.');
  }
  if (!Array.isArray(result.samples?.frameIntervalMs) || result.samples.frameIntervalMs.length < 30 ||
    result.samples.frameIntervalMs.some((value) => !Number.isFinite(value))) {
    errors.push('Valid trial requires at least 30 finite frame interval samples.');
  }
  if (!Array.isArray(result.samples?.mapRenderIntervalMs) || result.samples.mapRenderIntervalMs.length < 30 ||
    result.samples.mapRenderIntervalMs.some((value) => !Number.isFinite(value))) {
    errors.push('Valid trial requires at least 30 finite map render interval samples.');
  }
  const evidence = result.evidence;
  const publications = evidence?.simulation?.publications;
  if (!Array.isArray(publications) || publications.length < 2) {
    errors.push('Valid trial requires original simulation publication evidence.');
  } else {
    const numericFields = ['macroSecond', 'microSecond', 'active', 'movementCount', 'committedRevision'];
    if (publications.some((publication) => numericFields.some((field) => !Number.isFinite(publication?.[field])))) {
      errors.push('Simulation publications contain unavailable required clock, occupancy, movement, or revision data.');
    } else {
      const first = publications[0], last = publications.at(-1);
      if (last.macroSecond <= first.macroSecond || last.microSecond <= first.microSecond) {
        errors.push('Simulation clocks did not advance during the measurement window.');
      }
      const target = checkpointTarget(options.scenario);
      if (evidence.simulation.targetOccupancy !== target) errors.push('Simulation evidence target occupancy is incorrect.');
      const minimum = Math.ceil(target * 0.95);
      if (target === 0 ? publications.some((entry) => entry.active !== 0)
        : publications.some((entry) => entry.active < minimum)) {
        errors.push('Simulation evidence did not sustain the required active occupancy.');
      }
      const workload = integratedBenchmarkScenarios[options.scenario]?.workload;
      if (workload === 'detailed' && target > 0 && publications.some((entry) => entry.movementCount < minimum)) {
        errors.push('Detailed simulation evidence did not sustain the required representative occupancy.');
      }
      if (workload === 'aggregate' && publications.some((entry) => entry.movementCount !== 0)) {
        errors.push('Aggregate simulation unexpectedly retained detailed movement uploads.');
      }
    }
  }
  if (evidence?.simulation?.requestedSpeed !== scenarioSpeed(options.scenario)) {
    errors.push('Simulation evidence requested speed is incorrect.');
  }
  const backlog = evidence?.simulation?.backlog;
  if (backlog?.availability === 'available') {
    if (!Array.isArray(backlog.samples) || !backlog.samples.length ||
      backlog.samples.some((value) => !Number.isFinite(value) || value < 0)) {
      errors.push('Available backlog evidence requires original finite nonnegative samples.');
    }
  } else if (backlog?.availability !== 'unavailable' || typeof backlog.reason !== 'string' || !backlog.reason) {
    errors.push('Simulation backlog evidence must be measured or explicitly unavailable.');
  }
  const drawnRevision = evidence?.presentation?.lastDrawnRevision;
  if (!(Number.isInteger(drawnRevision?.value) && drawnRevision.value >= 0) &&
    !(drawnRevision?.availability === 'unavailable' && typeof drawnRevision.reason === 'string' && drawnRevision.reason)) {
    errors.push('Last drawn revision must be recorded or explicitly unavailable.');
  }
  const sourceState = evidence?.sourceState;
  if (sourceState?.terrain !== true || sourceState?.terrainLoaded !== true || sourceState?.snow !== true ||
    sourceState?.snowLoaded !== true || sourceState?.mapLoaded !== true) {
    errors.push('Valid trial requires loaded local terrain, snow, and map sources.');
  }
  const telemetryEntries = evidence?.telemetry?.entries;
  if (!Array.isArray(telemetryEntries) || (expected.telemetryEnabled ? telemetryEntries.length === 0 : telemetryEntries.length !== 0)) {
    errors.push(expected.telemetryEnabled
      ? 'Telemetry-enabled trial requires instrumentation evidence.'
      : 'Telemetry-disabled trial retained instrumentation evidence.');
  }
  const cache = evidence?.cacheState;
  const requestedCacheState = options.cacheState ?? 'warm';
  if (cache?.policy !== requestedCacheState || cache?.profileIsolated !== true || cache?.checkpointRestored !== true ||
    cache?.fixtureManifestHash !== identity?.fixtureManifestHash ||
    (requestedCacheState === 'cold' ? cache.cleared !== true || cache.primed !== false
      : cache.primed !== true || cache.primeCompleted !== true)) {
    errors.push('Trial did not prove the requested cold/warm cache-state policy.');
  }
  if (options.scenario === 'soak') {
    const memory = evidence?.memory;
    for (const lane of ['process', 'renderer', 'worker', 'gpu']) {
      const observation = memory?.[lane];
      if (observation?.availability === 'available') {
        if (!Number.isFinite(observation.beforeBytes) || observation.beforeBytes <= 0 ||
          !Number.isFinite(observation.afterBytes) || observation.afterBytes < 0 ||
          typeof observation.method !== 'string' || !observation.method) {
          errors.push(`Soak ${lane} memory evidence is malformed.`);
        }
      } else if (observation?.availability !== 'unavailable' || typeof observation.reason !== 'string' || !observation.reason) {
        errors.push(`Soak ${lane} memory must be measured or explicitly unavailable.`);
      }
    }
    for (const count of ['objects', 'caches', 'workers']) {
      const observation = memory?.boundedCounts?.[count];
      if (observation?.availability === 'available') {
        if (!Number.isInteger(observation.before) || observation.before < 0 ||
          !Number.isInteger(observation.after) || observation.after < 0 ||
          !Number.isInteger(observation.maximum) || observation.maximum < Math.max(observation.before, observation.after)) {
          errors.push(`Soak ${count} count evidence is malformed.`);
        }
      } else if (observation?.availability !== 'unavailable' || typeof observation.reason !== 'string' || !observation.reason) {
        errors.push(`Soak ${count} counts must be measured or explicitly unavailable.`);
      }
    }
  }
  return errors;
}

function sampleSummary(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return { min: sorted[0], median: sorted.length % 2 ? sorted[middle]
    : (sorted[middle - 1] + sorted[middle]) / 2, max: sorted.at(-1) };
}

export function summarizeMemoryGate(memory) {
  const unavailable = [], failures = [], observations = {};
  for (const lane of ['process', 'renderer', 'worker', 'gpu']) {
    const observation = memory?.[lane];
    if (observation?.availability !== 'available') {
      unavailable.push(`${lane}: ${observation?.reason ?? 'unavailable'}`);
      observations[lane] = observation;
      continue;
    }
    const growthPercent = (observation.afterBytes - observation.beforeBytes) / observation.beforeBytes * 100;
    observations[lane] = { ...observation, growthPercent };
    if (growthPercent > 10) failures.push(`${lane} retained memory grew ${growthPercent.toFixed(2)}%.`);
  }
  for (const count of ['objects', 'caches', 'workers']) {
    const observation = memory?.boundedCounts?.[count];
    if (observation?.availability !== 'available') unavailable.push(`${count}: ${observation?.reason ?? 'unavailable'}`);
  }
  return { status: failures.length ? 'fail' : unavailable.length ? 'incomplete' : 'pass',
    limitPercent: 10, failures, unavailable, observations, boundedCounts: memory?.boundedCounts };
}

function summarizeAvailabilityGate(observation) {
  return observation?.availability === 'available' || Number.isInteger(observation?.value)
    ? { status: 'pass', observation }
    : { status: 'incomplete', observation };
}

export function summarizeTrialEvidence(result) {
  const publications = result.evidence.simulation.publications;
  const first = publications[0], last = publications.at(-1);
  const wallSeconds = result.measurement.windowDurationMs / 1000;
  const macroSecondsAdvanced = last.macroSecond - first.macroSecond;
  const microSecondsAdvanced = last.microSecond - first.microSecond;
  const backlog = result.evidence.simulation.backlog;
  const backlogGate = summarizeAvailabilityGate(backlog);
  const presentationGate = summarizeAvailabilityGate(result.evidence.presentation.lastDrawnRevision);
  const memoryGate = result.evidence.memory ? summarizeMemoryGate(result.evidence.memory) : undefined;
  const qualificationGates = { backlog: backlogGate, presentation: presentationGate,
    ...(memoryGate ? { memory: memoryGate } : {}) };
  const gateStatuses = Object.values(qualificationGates).map((gate) => gate.status);
  const qualification = { status: gateStatuses.includes('fail') ? 'fail'
    : gateStatuses.includes('incomplete') ? 'incomplete' : 'pass', gates: qualificationGates };
  return {
    publicationCount: publications.length,
    requestedSpeed: result.evidence.simulation.requestedSpeed,
    macroSecondsAdvanced,
    microSecondsAdvanced,
    achievedMacroSecondsPerWallSecond: macroSecondsAdvanced / wallSeconds,
    achievedMicroSecondsPerWallSecond: microSecondsAdvanced / wallSeconds,
    activeOccupancy: sampleSummary(publications.map((entry) => entry.active)),
    movementOccupancy: sampleSummary(publications.map((entry) => entry.movementCount)),
    finalCommittedRevision: last.committedRevision,
    finalPublicationSequence: Number.isInteger(last.publicationSequence) ? last.publicationSequence : null,
    backlog: backlog.availability === 'available'
      ? { availability: 'available', ...sampleSummary(backlog.samples) }
      : { availability: 'unavailable', reason: backlog.reason },
    lastDrawnRevision: result.evidence.presentation.lastDrawnRevision,
    backlogGate,
    presentationGate,
    cacheState: result.evidence.cacheState,
    ...(memoryGate ? { memoryGate } : {}),
    qualification,
  };
}

async function targetMetadata(options, currentSource) {
  if (options.runtime === 'electron') {
    const fallback = process.env.ELECTRON_RELEASE_PATH ? path.resolve(process.env.ELECTRON_RELEASE_PATH) : null;
    const a = options.conditionATarget ?? fallback;
    const b = options.conditionBTarget ?? fallback;
    if (!a || !b) throw new Error('ELECTRON_RELEASE_PATH or both condition executable targets are required.');
    for (const executable of [a, b]) {
      await assertRegularFile(executable);
      if (executable.replaceAll('\\', '/').toLowerCase().includes('/node_modules/electron/')) {
        throw new Error('Packaged qualification cannot use the development Electron executable.');
      }
    }
    return {
      A: { target: a, buildHash: await hashFile(a), source: await sourceFromFile(options.conditionASource, currentSource) },
      B: { target: b, buildHash: await hashFile(b), source: await sourceFromFile(options.conditionBSource, currentSource) },
    };
  }

  let a = options.conditionATarget;
  let b = options.conditionBTarget;
  if (!a && !b) {
    const outputDirectory = path.join(options.output, 'production-web');
    await build({ configFile: path.join(root, options.profiling === 'react' || options.profiling === 'heavy'
      ? 'vite.config.integratedProfiling.ts' : 'vite.config.web.ts'), root,
      build: { outDir: outputDirectory, emptyOutDir: true } });
    a = outputDirectory;
    b = outputDirectory;
  }
  if (!a || !b) throw new Error('Supply both condition browser build targets or neither.');
  for (const directory of [a, b]) if (!(await stat(directory)).isDirectory()) throw new Error(`Expected browser build directory: ${directory}`);
  await assertRegularFile(browserExecutable);
  const executableHash = await hashFile(browserExecutable);
  return {
    A: { target: a, buildHash: await hashDirectory(a), executableHash,
      source: await sourceFromFile(options.conditionASource, currentSource) },
    B: { target: b, buildHash: await hashDirectory(b), executableHash,
      source: await sourceFromFile(options.conditionBSource, currentSource) },
  };
}

function sourceWithBuild(target) {
  return { ...target.source, buildHash: target.buildHash };
}

export function qualificationSourceErrors(source) {
  const errors = [];
  if (typeof source?.commit !== 'string' || !/^[a-f0-9]{40,64}$/i.test(source.commit)) {
    errors.push('Source commit must be a full immutable Git SHA.');
  }
  if (source?.workingTreeStatus !== 'clean') errors.push('Qualification source must have a clean working tree.');
  if (typeof source?.lockfileHash !== 'string' || !/^[a-f0-9]{64}$/i.test(source.lockfileHash)) {
    errors.push('Source dependency lockfile SHA-256 is invalid.');
  }
  if (typeof source?.buildHash !== 'string' || !/^[a-f0-9]{64}$/i.test(source.buildHash)) {
    errors.push('Source production build SHA-256 is invalid.');
  }
  return errors;
}

async function main(argv = process.argv.slice(2)) {
  const options = parseArguments(argv);
  await ensureResolvedOutputPath(options.output);
  if (options.fixture) await access(options.fixture);
  const planned = planTrials(options);
  const hardware = options.hardware
    ? JSON.parse(await readFile(options.hardware, 'utf8'))
    : collectHardwareMetadata();
  const inventoryErrors = qualificationInventoryErrors(hardware);
  const fixture = options.fixtureKind === 'jackson' ? await fixtureIdentity(options.fixture, options.scenario) : {
    manifest: { fixtureId: 'integrated-diagnostic-v1', artifacts: {} }, fixtureManifestHash: 'ci-diagnostic',
    assetManifestHash: 'ci-diagnostic', checkpointHash: 'ci-diagnostic', checkpointSeed: 'diagnostic-v1' };
  const currentSource = await gitSourceIdentity(root);
  let targets;
  try {
    targets = await targetMetadata(options, currentSource);
    if (options.comparison === 'telemetry-overhead' &&
      canonical(sourceWithBuild(targets.A)) !== canonical(sourceWithBuild(targets.B))) {
      throw new Error('Telemetry overhead requires identical source and build identities for A and B.');
    }
    if (options.comparison === 'optimization-regression' &&
      (!options.conditionASource || !options.conditionBSource || targets.A.buildHash === targets.B.buildHash)) {
      throw new Error('Optimization regression requires explicit A/B source identities and distinct production build hashes.');
    }
    if (options.tier === 'qualification') {
      const sourceErrors = [...qualificationSourceErrors(sourceWithBuild(targets.A)),
        ...qualificationSourceErrors(sourceWithBuild(targets.B))];
      if (sourceErrors.length) throw new Error([...new Set(sourceErrors)].join(' '));
    }
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    const trial = planned[0];
    await writeInvalid(path.join(options.output, `${trial.runId}.trial.json`), trial, { rawHardware: hardware }, reason);
    await writeFile(path.join(options.output, 'run-summary.json'), `${JSON.stringify({ status: 'incomplete', reason,
      plannedTrials: planned.length, attemptedTrials: 1, hardware }, null, 2)}\n`, 'utf8');
    throw error;
  }

  if (options.tier === 'qualification' && inventoryErrors.length) {
    const reason = inventoryErrors.join(' ');
    const trial = planned[0];
    await writeInvalid(path.join(options.output, `${trial.runId}.trial.json`), trial, { rawHardware: hardware }, reason);
    await writeFile(path.join(options.output, 'run-summary.json'), `${JSON.stringify({ status: 'incomplete', reason,
      plannedTrials: planned.length, attemptedTrials: 1, hardware }, null, 2)}\n`, 'utf8');
    throw new Error(reason);
  }

  const project = options.runtime === 'browser' ? 'integrated-browser' : 'integrated-electron';
  try {
    await preflightPlaywright(project, {
      INTEGRATED_BENCHMARK_RUNTIME: options.runtime,
      ...(options.fixture ? { INTEGRATED_FIXTURE_ROOT: options.fixture } : {}),
      INTEGRATED_FIXTURE_KIND: options.fixtureKind,
      INTEGRATED_TIER: options.tier,
      INTEGRATED_SCENARIO: options.scenario,
      ...(options.runtime === 'electron' ? { RUN_INTEGRATED_ELECTRON: '1' } : {}),
    });
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    const trial = planned[0];
    await writeInvalid(path.join(options.output, `${trial.runId}.trial.json`), trial,
      { rawHardware: hardware, fixtureManifestHash: fixture.fixtureManifestHash }, reason,
      { stage: 'playwright-collection' });
    await writeFile(path.join(options.output, 'run-summary.json'), `${JSON.stringify({ status: 'incomplete',
      qualificationStatus: 'incomplete', reason, plannedTrials: planned.length, attemptedTrials: 1, hardware }, null, 2)}\n`, 'utf8');
    throw error;
  }

  const completed = [];
  let failed = false;
  for (const trial of planned) {
    const target = targets[trial.condition];
    const display = integratedBenchmarkScenarios[options.scenario];
    const identity = {
      runId: trial.runId,
      tier: options.tier,
      appQuery: '?dev-console',
      profiling: options.profiling,
      scenarioId: options.scenario,
      workloadId: fixture.manifest.fixtureId,
      seed: fixture.checkpointSeed,
      checkpointHash: fixture.checkpointHash,
      fixtureManifestHash: fixture.fixtureManifestHash,
      assetManifestHash: fixture.assetManifestHash,
      cacheStatePolicy: options.cacheState,
      profile: display.profile,
      cssViewport: display.cssViewport,
      canvas: display.canvas,
      devicePixelRatio: display.devicePixelRatio,
      refreshHz: hardware.display.refreshHz,
      machine: hardware,
      runtime: { kind: options.runtime, version: 'captured-by-spec',
        executableHash: target.executableHash ?? target.buildHash },
      source: sourceWithBuild(target),
    };
    const identityPath = path.join(options.output, `${trial.runId}.identity.json`);
    const trialPath = path.join(options.output, `${trial.runId}.trial.json`);
    // A skipped or aborted Playwright process must never make an earlier result
    // with the same deterministic run id look like the current attempt.
    await rm(trialPath, { force: true });
    await writeFile(identityPath, `${JSON.stringify(identity, null, 2)}\n`, 'utf8');
    const userData = await mkdtemp(path.join(tmpdir(), 'mountain-planner-integrated-'));
    let server;
    let playwright = { exitCode: 1, logs: { stdout: '', stderr: '' } };
    try {
      if (options.runtime === 'browser') {
        server = await preview({ configFile: path.join(root, 'vite.config.web.ts'), root,
          build: { outDir: target.target }, preview: { host: '127.0.0.1', port: options.port, strictPort: true } });
      }
      const environment = {
        INTEGRATED_BENCHMARK_RUNTIME: options.runtime,
        INTEGRATED_BENCHMARK_BASE_URL: `http://127.0.0.1:${options.port}/ski-area-design-challenge/`,
        INTEGRATED_APP_QUERY: '?dev-console',
        ...(options.fixture ? { INTEGRATED_FIXTURE_ROOT: options.fixture } : {}),
        INTEGRATED_FIXTURE_KIND: options.fixtureKind,
        INTEGRATED_TIER: options.tier,
        INTEGRATED_RESULTS_DIR: options.output,
        INTEGRATED_PLAYWRIGHT_OUTPUT_DIR: path.join(options.output, 'playwright', trial.runId),
        INTEGRATED_SCENARIO: options.scenario,
        INTEGRATED_TARGET: String(checkpointTarget(options.scenario)),
        INTEGRATED_REQUESTED_SPEED: String(scenarioSpeed(options.scenario)),
        INTEGRATED_CONDITION: trial.condition,
        INTEGRATED_TELEMETRY: trial.telemetryEnabled ? '1' : '0',
        INTEGRATED_PAIR_INDEX: String(trial.pairIndex),
        INTEGRATED_PAIR_ORDER: trial.order,
        INTEGRATED_WARMUP_MS: String(options.warmupMs),
        INTEGRATED_MEASURE_MS: String(options.measureMs),
        INTEGRATED_RUN_IDENTITY_PATH: identityPath,
        INTEGRATED_RUN_ID: trial.runId,
        INTEGRATED_USER_DATA_DIR: userData,
        INTEGRATED_CACHE_STATE: options.cacheState,
        INTEGRATED_REOPEN_CYCLES: String(options.reopenCycles),
        INTEGRATED_PROFILING: options.profiling,
        ...(options.snowDemand ? { INTEGRATED_SNOW_DEMAND_DIAGNOSTIC: '1' } : {}),
        INTEGRATED_TRIAL_DEADLINE_MS: String(trialDeadlineMs(options)),
        ...(options.runtime === 'electron' ? {
          ELECTRON_RELEASE_PATH: target.target,
          RUN_INTEGRATED_ELECTRON: '1',
        } : {}),
      };
      playwright = await runPlaywright(project, environment);
      if (options.snowDemand) {
        const diagnosticPath = path.join(options.output, `${trial.runId}.snow-demand-diagnostic.json`);
        const evidence = JSON.parse(await readFile(diagnosticPath, 'utf8'));
        const result = { ...trial, status: playwright.exitCode === 0 ? 'diagnostic-complete' : 'invalid',
          tier: 'diagnostic', qualificationEligible: false, identity,
          invalidReasons: playwright.exitCode === 0 ? [] : ['Snow-demand diagnostic failed.'],
          evidence: { snowDemand: evidence }, runnerDiagnostics: playwright.logs };
        await writeFile(trialPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
        completed.push(result);
        if (playwright.exitCode !== 0) failed = true;
        continue;
      }
      let result;
      try { result = JSON.parse(await readFile(trialPath, 'utf8')); } catch { /* materialized below */ }
      const resultErrors = result ? emittedTrialErrors(result, trial, options, identity) : [];
      const emittedInvalid = result && result.status !== 'valid';
      const attemptFailed = playwright.exitCode !== 0 || !result || emittedInvalid || resultErrors.length > 0;
      if (attemptFailed) {
        result = mergeFailedTrial(trial, identity, result, playwright);
        if (playwright.exitCode === 0 && resultErrors.length > 0) {
          result.invalidReasons = [...new Set([...(result.invalidReasons ?? []), ...resultErrors])];
        }
        await writeFile(trialPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
      } else {
        if (options.tier === 'diagnostic') {
          result.status = 'diagnostic-complete';
          result.tier = 'diagnostic';
          result.qualificationEligible = false;
          result.invalidReasons = [];
          result.runnerValidation = { functional: { status: 'pass' }, qualification: { status: 'ineligible' } };
        } else {
          result.runnerValidation = summarizeTrialEvidence(result);
        }
        await writeFile(trialPath, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
      }
      completed.push(result);
      if (attemptFailed) failed = true;
    } catch (error) {
      failed = true;
      const reason = error instanceof Error ? error.message : String(error);
      completed.push(await writeInvalid(trialPath, trial, identity, reason));
      // A launch/server failure is systemic. Retain the real failed attempt and
      // stop rather than manufacturing unattempted trial records.
      break;
    } finally {
      await closePreview(server).catch(() => undefined);
      const resolvedTemp = path.resolve(userData);
      const resolvedTempReal = await realpath(resolvedTemp).catch(() => null);
      const tempRootReal = await realpath(tmpdir());
      if (resolvedTempReal && path.dirname(resolvedTempReal) === tempRootReal
        && path.basename(resolvedTempReal).startsWith('mountain-planner-integrated-')) {
        if (options.runtime === 'electron') {
          await cleanupPackagedElectronFromUserData(resolvedTempReal, target.target).catch(() => undefined);
        }
        await rm(resolvedTempReal, { recursive: true, force: true });
      }
    }
  }

  const validQualificationStatuses = completed.filter((trial) => trial.status === 'valid')
    .map((trial) => trial.runnerValidation?.qualification?.status);
  const qualificationStatus = options.tier === 'diagnostic' || failed || completed.length !== planned.length ? 'incomplete'
    : validQualificationStatuses.includes('fail') ? 'fail'
      : validQualificationStatuses.includes('incomplete') ? 'incomplete' : 'pass';
  const summary = {
    status: failed || completed.length !== planned.length ? 'incomplete'
      : options.tier === 'diagnostic' ? 'diagnostic-complete' : 'complete',
    tier: options.tier,
    runtime: options.runtime,
    scenario: options.scenario,
    comparison: options.comparison,
    plannedTrials: planned.length,
    attemptedTrials: completed.length,
    validTrials: completed.filter((trial) => trial.status === 'valid' || trial.status === 'diagnostic-complete').length,
    invalidTrials: completed.filter((trial) => trial.status === 'invalid').length,
    qualificationStatus,
    hardware,
    fixtureManifestHash: fixture.fixtureManifestHash,
    conditionBuilds: { A: sourceWithBuild(targets.A), B: sourceWithBuild(targets.B) },
    completedAt: new Date().toISOString(),
  };
  await writeFile(path.join(options.output, 'run-summary.json'), `${JSON.stringify(summary, null, 2)}\n`, 'utf8');
  if (summary.status === 'incomplete') process.exitCode = 1;
}

const entrypoint = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (entrypoint) main().catch((error) => { process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`); process.exitCode = 1; });

export const runIntegratedBenchmark = main;
