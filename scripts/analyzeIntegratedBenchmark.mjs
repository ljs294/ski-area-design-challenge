import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const PAIRS_PER_BLOCK = 5;
const MAX_PAIRS = 10;
const DEFAULT_BOOTSTRAP_ITERATIONS = 10_000;
export const INSTRUMENTATION_OVERHEAD_BUDGET_PERCENT = 3;
export const OPTIMIZATION_REGRESSION_BUDGET_PERCENT = 5;

function invariant(condition, message) {
  if (!condition) throw new Error(message);
}

function finite(value) {
  return typeof value === 'number' && Number.isFinite(value);
}

export function percentile(values, probability) {
  invariant(Array.isArray(values) && values.length > 0 && values.every(finite), 'Percentiles require finite samples.');
  invariant(finite(probability) && probability >= 0 && probability <= 1, 'Percentile probability must be between zero and one.');
  const sorted = [...values].sort((left, right) => left - right);
  const position = (sorted.length - 1) * probability;
  const lower = Math.floor(position);
  const fraction = position - lower;
  return sorted[lower] + (sorted[Math.min(lower + 1, sorted.length - 1)] - sorted[lower]) * fraction;
}

function seedHash(value) {
  let hash = 0x811c9dc5;
  for (const byte of Buffer.from(String(value))) {
    hash ^= byte;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash || 0x6d2b79f5;
}

function seededRandom(seed) {
  let state = seedHash(seed);
  return () => {
    state += 0x6d2b79f5;
    let value = state;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4_294_967_296;
  };
}

export function bootstrapMeanConfidenceInterval(values, options = {}) {
  invariant(Array.isArray(values) && values.length > 0 && values.every(finite), 'Bootstrap requires finite values.');
  const iterations = options.iterations ?? DEFAULT_BOOTSTRAP_ITERATIONS;
  invariant(Number.isInteger(iterations) && iterations >= 100, 'bootstrapIterations must be an integer of at least 100.');
  const random = seededRandom(options.seed ?? 'integrated-benchmark-v1');
  const means = new Array(iterations);
  for (let iteration = 0; iteration < iterations; iteration++) {
    let sum = 0;
    for (let index = 0; index < values.length; index++) sum += values[Math.floor(random() * values.length)];
    means[iteration] = sum / values.length;
  }
  return { level: 0.95, low: percentile(means, 0.025), high: percentile(means, 0.975), iterations,
    seed: String(options.seed ?? 'integrated-benchmark-v1') };
}

function mean(values) {
  return values.reduce((sum, value) => sum + value, 0) / values.length;
}

function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.keys(value).sort()
    .map((key) => `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
  return JSON.stringify(value);
}

function requiredString(value, label) {
  invariant(typeof value === 'string' && value.length > 0, `${label} must be a non-empty string.`);
}

function validateIdentity(identity, trialId) {
  invariant(identity && typeof identity === 'object' && !Array.isArray(identity), `Valid trial '${trialId}' requires identity.`);
  for (const field of ['scenarioId', 'workloadId', 'seed', 'checkpointHash', 'fixtureManifestHash',
    'assetManifestHash', 'cacheStatePolicy', 'profile']) requiredString(identity[field], `Trial '${trialId}' identity.${field}`);
  invariant(identity.cssViewport && Number.isInteger(identity.cssViewport.width) && identity.cssViewport.width > 0 &&
    Number.isInteger(identity.cssViewport.height) && identity.cssViewport.height > 0,
  `Trial '${trialId}' requires CSS viewport dimensions.`);
  invariant(identity.canvas && Number.isInteger(identity.canvas.width) && identity.canvas.width > 0 &&
    Number.isInteger(identity.canvas.height) && identity.canvas.height > 0, `Trial '${trialId}' requires physical canvas dimensions.`);
  invariant(finite(identity.devicePixelRatio) && identity.devicePixelRatio > 0, `Trial '${trialId}' requires a positive DPR.`);
  invariant(finite(identity.refreshHz) && identity.refreshHz > 0, `Trial '${trialId}' requires a positive refresh rate.`);
  invariant(identity.machine && typeof identity.machine === 'object', `Trial '${trialId}' requires machine identity.`);
  for (const field of ['id', 'cpu', 'gpu', 'driver', 'os']) requiredString(identity.machine[field], `Trial '${trialId}' identity.machine.${field}`);
  invariant(identity.runtime && (identity.runtime.kind === 'browser' || identity.runtime.kind === 'electron'),
    `Trial '${trialId}' runtime.kind must be browser or electron.`);
  for (const field of ['version', 'glVendor', 'glRenderer', 'glBackend']) {
    requiredString(identity.runtime[field], `Trial '${trialId}' identity.runtime.${field}`);
  }
  invariant(identity.source && typeof identity.source === 'object', `Trial '${trialId}' requires source/build identity.`);
  for (const field of ['commit', 'workingTreeStatus', 'lockfileHash', 'buildHash']) requiredString(identity.source[field], `Trial '${trialId}' identity.source.${field}`);
}

function sharedIdentity(identity) {
  const { seed: _seed, source: _source, ...shared } = identity;
  return shared;
}

function validateMeasurement(measurement, requirements, trialId) {
  invariant(measurement && typeof measurement === 'object', `Valid trial '${trialId}' requires measurement metadata.`);
  invariant(finite(measurement.windowDurationMs) && measurement.windowDurationMs >= requirements.windowDurationMs,
    `Trial '${trialId}' did not cover the required ${requirements.windowDurationMs} ms window.`);
  invariant(Number.isInteger(measurement.expectedFrames) && measurement.expectedFrames > 0 &&
    Number.isInteger(measurement.deliveredFrames) && measurement.deliveredFrames > 0,
  `Trial '${trialId}' requires positive expected and delivered frame counts.`);
  invariant(measurement.deliveredFrames / measurement.expectedFrames >= requirements.minimumDeliveredFrameRatio,
    `Trial '${trialId}' delivered too few frames for evaluation.`);
  invariant(measurement.trailingGapAccounted === true, `Trial '${trialId}' did not account for the trailing window gap.`);
  invariant(measurement.livenessPassed === true, `Trial '${trialId}' failed runner liveness validation.`);
}

function validateTrial(trial, index, requirements) {
  invariant(trial && typeof trial === 'object', `trials[${index}] must be an object.`);
  requiredString(trial.id, `trials[${index}].id`);
  invariant(Number.isInteger(trial.pairIndex) && trial.pairIndex >= 1, `Trial '${trial.id}' has an invalid pairIndex.`);
  invariant(trial.order === 'AB' || trial.order === 'BA', `Trial '${trial.id}' order must be AB or BA.`);
  invariant(trial.condition === 'A' || trial.condition === 'B', `Trial '${trial.id}' condition must be A or B.`);
  invariant(typeof trial.telemetryEnabled === 'boolean', `Trial '${trial.id}' must declare telemetryEnabled.`);
  invariant(trial.status === 'valid' || trial.status === 'invalid', `Trial '${trial.id}' status must be valid or invalid.`);
  invariant(Array.isArray(trial.invalidReasons) && trial.invalidReasons.every((reason) => typeof reason === 'string' && reason.length > 0),
    `Trial '${trial.id}' invalidReasons must be non-empty strings.`);
  if (trial.status === 'invalid') {
    invariant(trial.invalidReasons.length > 0, `Invalid trial '${trial.id}' must include a reason.`);
    return;
  }
  invariant(trial.invalidReasons.length === 0, `Valid trial '${trial.id}' cannot have invalidReasons.`);
  invariant(trial.metrics && typeof trial.metrics === 'object' && !Array.isArray(trial.metrics), `Valid trial '${trial.id}' requires metrics.`);
  for (const [metric, value] of Object.entries(trial.metrics)) invariant(finite(value), `Trial '${trial.id}' metric '${metric}' must be finite.`);
  validateIdentity(trial.identity, trial.id);
  validateMeasurement(trial.measurement, requirements, trial.id);
  for (const [metric, values] of Object.entries(trial.samples ?? {})) {
    invariant(Array.isArray(values) && values.length >= requirements.minimumSamples && values.every(finite),
      `Trial '${trial.id}' sample series '${metric}' requires at least ${requirements.minimumSamples} finite samples.`);
  }
}

function validateBudget(metric, budget) {
  invariant(budget && typeof budget === 'object', `Budget '${metric}' must be an object.`);
  invariant(budget.direction === 'max' || budget.direction === 'min', `Budget '${metric}' direction must be max or min.`);
  invariant(budget.mode === 'absolute' || budget.mode === 'paired-percent', `Budget '${metric}' mode must be absolute or paired-percent.`);
  invariant(finite(budget.limit), `Budget '${metric}' limit must be finite.`);
  invariant(budget.statistic === undefined || ['value', 'p50', 'p95', 'p99'].includes(budget.statistic),
    `Budget '${metric}' statistic must be value, p50, p95, or p99.`);
}

function metricValue(trial, metric, statistic, minimumSamples) {
  if (!statistic || statistic === 'value') return trial.metrics[metric];
  const samples = trial.samples?.[metric];
  invariant(Array.isArray(samples) && samples.length >= minimumSamples,
    `Trial '${trial.id}' requires ${minimumSamples} samples for ${metric} ${statistic}.`);
  return percentile(samples, Number(statistic.slice(1)) / 100);
}

function samplePercentiles(trial) {
  const result = {};
  for (const [metric, values] of Object.entries(trial.samples ?? {})) {
    if (!Array.isArray(values) || values.length === 0 || !values.every(finite)) result[metric] = { status: 'unavailable' };
    else result[metric] = { count: values.length, p50: percentile(values, 0.5), p95: percentile(values, 0.95), p99: percentile(values, 0.99) };
  }
  return result;
}

function pairedDelta(candidate, baseline, direction) {
  if (baseline === 0) return null;
  const candidateVsBaselinePercent = ((candidate - baseline) / Math.abs(baseline)) * 100;
  return { candidateVsBaselinePercent, adversePercent: direction === 'max' ? candidateVsBaselinePercent : -candidateVsBaselinePercent };
}

function validateComparison(input) {
  const comparison = input.comparison;
  invariant(comparison && (comparison.kind === 'telemetry-overhead' || comparison.kind === 'optimization-regression'),
    'comparison.kind must be telemetry-overhead or optimization-regression.');
  requiredString(comparison.metric, 'comparison.metric');
  invariant(comparison.direction === 'max' || comparison.direction === 'min', 'comparison.direction must be max or min.');
  invariant(comparison.statistic === undefined || ['value', 'p50', 'p95', 'p99'].includes(comparison.statistic),
    'comparison.statistic must be value, p50, p95, or p99.');
  return comparison;
}

function buildPairs(validTrials, comparison) {
  invariant(validTrials.length % 2 === 0, 'Valid trials must form adjacent A/B pairs.');
  const pairs = [];
  for (let offset = 0; offset < validTrials.length; offset += 2) {
    const first = validTrials[offset];
    const second = validTrials[offset + 1];
    const pairIndex = offset / 2 + 1;
    invariant(first.pairIndex === pairIndex && second.pairIndex === pairIndex,
      `Valid pair sequence must be contiguous; expected adjacent pair ${pairIndex}.`);
    invariant(first.order === second.order, `Pair ${pairIndex} has inconsistent order metadata.`);
    const expectedOrder = pairIndex % 2 === 1 ? 'AB' : 'BA';
    invariant(first.order === expectedOrder, `Pair ${pairIndex} must use ${expectedOrder} to preserve alternating order.`);
    invariant(first.condition !== second.condition, `Pair ${pairIndex} must contain one A and one B condition.`);
    invariant(first.condition === first.order[0] && second.condition === first.order[1],
      `Pair ${pairIndex} trial sequence does not match ${first.order} order.`);
    const conditionA = first.condition === 'A' ? first : second;
    const conditionB = first.condition === 'B' ? first : second;
    invariant(conditionA.identity.seed === conditionB.identity.seed, `Pair ${pairIndex} uses mismatched seeds.`);
    invariant(stableJson(sharedIdentity(conditionA.identity)) === stableJson(sharedIdentity(conditionB.identity)),
      `Pair ${pairIndex} has mismatched workload, fixture, cache, profile, canvas, or hardware identity.`);
    if (comparison.kind === 'telemetry-overhead') {
      invariant(conditionA.telemetryEnabled === false && conditionB.telemetryEnabled === true,
        `Telemetry pair ${pairIndex} must use A=disabled and B=enabled.`);
      invariant(stableJson(conditionA.identity.source) === stableJson(conditionB.identity.source),
        `Telemetry pair ${pairIndex} must use the same source/build identity.`);
    } else {
      invariant(conditionA.telemetryEnabled === conditionB.telemetryEnabled,
        `Optimization pair ${pairIndex} must use the same telemetry setting in A and B.`);
    }
    pairs.push({ pairIndex, order: first.order, conditionA, conditionB });
  }
  if (pairs.length > 0) {
    const shared = stableJson(sharedIdentity(pairs[0].conditionA.identity));
    const sourceA = stableJson(pairs[0].conditionA.identity.source);
    const sourceB = stableJson(pairs[0].conditionB.identity.source);
    for (const pair of pairs) {
      invariant(stableJson(sharedIdentity(pair.conditionA.identity)) === shared && stableJson(sharedIdentity(pair.conditionB.identity)) === shared,
        `Pair ${pair.pairIndex} does not match the decision-set comparability identity.`);
      invariant(stableJson(pair.conditionA.identity.source) === sourceA, `Pair ${pair.pairIndex} changes condition A source/build identity.`);
      invariant(stableJson(pair.conditionB.identity.source) === sourceB, `Pair ${pair.pairIndex} changes condition B source/build identity.`);
    }
  }
  return pairs;
}

function analyzeMetric(metric, budget, pairs, bootstrap, minimumSamples) {
  const statistic = budget.statistic ?? 'value';
  const pairResults = pairs.map((pair) => {
    const baseline = metricValue(pair.conditionA, metric, statistic, minimumSamples);
    const candidate = metricValue(pair.conditionB, metric, statistic, minimumSamples);
    invariant(finite(baseline) && finite(candidate), `Metric '${metric}' is missing from pair ${pair.pairIndex}.`);
    const delta = pairedDelta(candidate, baseline, budget.direction);
    invariant(delta, `Metric '${metric}' has a zero condition A denominator in pair ${pair.pairIndex}.`);
    return { pairIndex: pair.pairIndex, conditionATrialId: pair.conditionA.id, conditionBTrialId: pair.conditionB.id,
      baseline, candidate, ...delta };
  });
  const deltas = pairResults.map((pair) => pair.adversePercent);
  const deltaCi = bootstrapMeanConfidenceInterval(deltas, { ...bootstrap, seed: `${bootstrap.seed}:${metric}:delta` });
  const violates = (value) => budget.direction === 'max' ? value > budget.limit : value < budget.limit;
  const violations = budget.mode === 'absolute' ? pairResults.flatMap((pair) => [
    ...(violates(pair.baseline) ? [{ condition: 'A', trialId: pair.conditionATrialId,
      pairIndex: pair.pairIndex, value: pair.baseline, limit: budget.limit }] : []),
    ...(violates(pair.candidate) ? [{ condition: 'B', trialId: pair.conditionBTrialId,
      pairIndex: pair.pairIndex, value: pair.candidate, limit: budget.limit }] : []),
  ]) : [];
  let status;
  const conditionQualifications = budget.mode === 'absolute' ? Object.fromEntries(['A', 'B'].map((condition) => {
    const conditionViolations = violations.filter((violation) => violation.condition === condition);
    return [condition, { status: conditionViolations.length > 0 ? 'fail' : 'pass', violations: conditionViolations }];
  })) : undefined;
  if (budget.mode === 'absolute') status = conditionQualifications.B.status;
  else if (deltaCi.high <= budget.limit) status = 'pass';
  else if (deltaCi.low > budget.limit) status = 'fail';
  else status = 'crosses-budget';
  return { metric, ...budget, statistic, status, violations, conditionQualifications, pairResults,
    pairedAdversePercent: { mean: mean(deltas), ci95: deltaCi } };
}

export function analyzeIntegratedBenchmark(input) {
  const diagnosticTrial = input?.trials?.find((trial) => trial?.identity?.tier === 'diagnostic');
  invariant(!diagnosticTrial, `Diagnostic trial '${diagnosticTrial?.id}' cannot enter qualification paired analysis.`);
  invariant(input && typeof input === 'object', 'Input must be an object.');
  invariant(input.schemaVersion === 1, 'Input schemaVersion must be 1.');
  invariant(Array.isArray(input.trials), 'Input trials must be an array.');
  invariant(input.budgets && typeof input.budgets === 'object' && !Array.isArray(input.budgets), 'Input budgets must be an object.');
  const comparison = validateComparison(input);
  const requirements = input.measurementRequirements;
  invariant(requirements && finite(requirements.windowDurationMs) && requirements.windowDurationMs >= 120_000,
    'measurementRequirements.windowDurationMs must be at least 120000.');
  invariant(finite(requirements.minimumDeliveredFrameRatio) && requirements.minimumDeliveredFrameRatio > 0 &&
    requirements.minimumDeliveredFrameRatio <= 1, 'measurementRequirements.minimumDeliveredFrameRatio must be in (0, 1].');
  invariant(Number.isInteger(requirements.minimumSamples) && requirements.minimumSamples >= 30,
    'measurementRequirements.minimumSamples must be an integer of at least 30.');
  input.trials.forEach((trial, index) => validateTrial(trial, index, requirements));
  invariant(new Set(input.trials.map((trial) => trial.id)).size === input.trials.length, 'Trial ids must be unique.');
  for (const [metric, budget] of Object.entries(input.budgets)) validateBudget(metric, budget);

  const invalidTrials = input.trials.filter((trial) => trial.status === 'invalid').map((trial) => structuredClone(trial));
  const validTrials = input.trials.filter((trial) => trial.status === 'valid');
  const pairs = buildPairs(validTrials, comparison);
  const reasons = [];
  if (![PAIRS_PER_BLOCK, MAX_PAIRS].includes(pairs.length)) reasons.push(`Expected one complete 5-pair block or one 10-pair extended set; found ${pairs.length} pairs.`);
  if (pairs.length > MAX_PAIRS) reasons.push('Only one additional five-pair extension block is permitted.');
  const bootstrap = { seed: String(input.seed ?? 'integrated-benchmark-v1'), iterations: input.bootstrapIterations ?? DEFAULT_BOOTSTRAP_ITERATIONS };
  const metrics = pairs.length > 0 ? Object.entries(input.budgets).map(([metric, budget]) =>
    analyzeMetric(metric, budget, pairs, bootstrap, requirements.minimumSamples)) : [];
  const relativeLimit = comparison.kind === 'telemetry-overhead'
    ? INSTRUMENTATION_OVERHEAD_BUDGET_PERCENT : OPTIMIZATION_REGRESSION_BUDGET_PERCENT;
  const relativeComparison = pairs.length > 0 ? analyzeMetric(comparison.metric, {
    direction: comparison.direction, mode: 'paired-percent', limit: relativeLimit,
    statistic: comparison.statistic ?? 'value',
  }, pairs, bootstrap, requirements.minimumSamples) : {
    metric: comparison.metric, direction: comparison.direction, mode: 'paired-percent', limit: relativeLimit,
    statistic: comparison.statistic ?? 'value', status: 'inconclusive', violations: [], pairResults: [],
    reason: 'No complete valid pairs are available.',
  };
  const qualification = Object.fromEntries(['A', 'B'].map((condition) => {
    const violations = metrics.filter((metric) => metric.mode === 'absolute')
      .flatMap((metric) => metric.conditionQualifications[condition].violations.map((violation) => ({ metric: metric.metric, ...violation })));
    return [condition, { status: violations.length > 0 ? 'fail' : 'pass', violations }];
  }));
  const comparisonChecks = [...metrics.filter((metric) => metric.mode === 'paired-percent'), relativeComparison];
  let comparisonDecision;
  if (reasons.length > 0) comparisonDecision = 'inconclusive';
  else if (comparisonChecks.some((check) => check.status === 'fail')) comparisonDecision = 'fail';
  else if (comparisonChecks.some((check) => check.status === 'crosses-budget')) {
    comparisonDecision = pairs.length === PAIRS_PER_BLOCK ? 'extend-five-pairs' : 'inconclusive';
    reasons.push(pairs.length === PAIRS_PER_BLOCK
      ? 'At least one paired-change 95% confidence interval crosses its budget; collect exactly five additional alternating pairs.'
      : 'At least one paired-change 95% confidence interval remains inconclusive after the single permitted extension.');
  } else comparisonDecision = 'pass';
  const qualifyingCondition = comparison.kind === 'optimization-regression' ? 'B' : 'A';
  const decision = qualification[qualifyingCondition].status === 'fail' ? 'fail' : comparisonDecision;

  return {
    schemaVersion: 1, decision, comparisonDecision, qualification, qualifyingCondition, reasons, comparison, validPairCount: pairs.length,
    extension: { initialPairs: PAIRS_PER_BLOCK, maximumPairs: MAX_PAIRS, supplied: pairs.length === MAX_PAIRS,
      remainingPairsAllowed: pairs.length === PAIRS_PER_BLOCK ? PAIRS_PER_BLOCK : 0 },
    bootstrap, measurementRequirements: { ...requirements }, invalidTrials,
    comparability: pairs.length === 0 ? null : {
      sharedIdentity: structuredClone(sharedIdentity(pairs[0].conditionA.identity)),
      conditionSources: {
        A: structuredClone(pairs[0].conditionA.identity.source),
        B: structuredClone(pairs[0].conditionB.identity.source),
      },
      pairSeeds: pairs.map((pair) => ({ pairIndex: pair.pairIndex, seed: pair.conditionA.identity.seed })),
    },
    trialSummaries: input.trials.map((trial) => ({ id: trial.id, pairIndex: trial.pairIndex, order: trial.order,
      condition: trial.condition, telemetryEnabled: trial.telemetryEnabled, status: trial.status,
      invalidReasons: [...trial.invalidReasons], metrics: trial.metrics ? { ...trial.metrics } : undefined,
      percentiles: samplePercentiles(trial) })),
    metrics, relativeComparison,
    ...(comparison.kind === 'telemetry-overhead' ? { instrumentationOverhead: relativeComparison }
      : { optimizationRegression: relativeComparison }),
  };
}

function argumentsOf(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 2) {
    const flag = argv[index];
    invariant(flag === '--input' || flag === '--output', `Unknown argument '${flag}'.`);
    invariant(argv[index + 1], `Missing value for '${flag}'.`);
    result[flag.slice(2)] = argv[index + 1];
  }
  invariant(result.input && result.output,
    'Usage: node scripts/analyzeIntegratedBenchmark.mjs --input <comparison-input.json> --output <comparison-report.json>');
  return result;
}

async function main() {
  const args = argumentsOf(process.argv.slice(2));
  const inputPath = path.resolve(args.input);
  const outputPath = path.resolve(args.output);
  invariant(inputPath !== outputPath, 'Input and output paths must differ.');
  const report = analyzeIntegratedBenchmark(JSON.parse(await readFile(inputPath, 'utf8')));
  await writeFile(outputPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  process.stdout.write(`${report.decision}: ${report.validPairCount} valid pairs; report written to ${outputPath}\n`);
}

const entrypoint = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (entrypoint) main().catch((error) => { process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`); process.exitCode = 1; });
