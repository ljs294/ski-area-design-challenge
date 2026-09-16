import assert from 'node:assert/strict';
import test from 'node:test';
import { emittedTrialErrors } from './runIntegratedBenchmark.mjs';

const expected = { id: 'diagnostic-browser-ci-p1-b', pairIndex: 1, order: 'AB', condition: 'B', telemetryEnabled: true };
const options = { tier: 'diagnostic', runtime: 'browser', fixtureKind: 'ci', scenario: 'ci-diagnostic', measureMs: 30_000 };

function successfulDiagnostic() {
  return { ...expected, status: 'valid', identity: { runtime: { kind: 'browser', version: 'Chromium 140',
    glVendor: 'Google Inc. (NVIDIA)', glRenderer: 'ANGLE (NVIDIA GeForce RTX)', glBackend: 'D3D11' },
  cssViewport: { width: 1920, height: 1080 }, canvas: { width: 1920, height: 1080 }, devicePixelRatio: 1,
  profile: 'Standard', refreshHz: 60 },
  measurement: { windowDurationMs: 30_032, livenessPassed: true },
  samples: { frameIntervalMs: Array(30).fill(16.7), mapRenderIntervalMs: Array(30).fill(16.7) },
  metrics: { pauseResponseMs: 20, workerCancellationMs: 30, sourceRevisionResponseMs: 88, gradingConfirmationMs: 90 },
  evidence: { sourceState: { terrain: true, terrainLoaded: true, snow: true, snowLoaded: true, mapLoaded: true },
    simulation: { publications: [{ active: 32, movementCount: 32 }, { active: 32, movementCount: 32 }] } } };
}

test('accepts a functionally complete CI diagnostic without qualification identity', () => {
  assert.deepEqual(emittedTrialErrors(successfulDiagnostic(), expected, options), []);
});

test('rejects functional diagnostic runtime, framebuffer, readiness, liveness, and population failures', () => {
  const result = successfulDiagnostic();
  result.identity.runtime.glRenderer = 'SwiftShader software';
  result.identity.canvas.width = 1280;
  result.measurement.livenessPassed = false;
  result.evidence.sourceState.snowLoaded = false;
  result.evidence.simulation.publications = [{ movementCount: 0 }, { movementCount: 0 }];
  const errors = emittedTrialErrors(result, expected, options);
  assert.match(errors.join('\n'), /hardware GL/);
  assert.match(errors.join('\n'), /physical canvas/);
  assert.match(errors.join('\n'), /liveness/);
  assert.match(errors.join('\n'), /readiness/);
  assert.match(errors.join('\n'), /population/);
});

test('rejects a diagnostic whose planned trial identity differs', () => {
  const result = successfulDiagnostic();
  result.condition = 'A';
  assert.match(emittedTrialErrors(result, expected, options).join('\n'), /condition/);
});

test('accepts an empty diagnostic with zero population', () => {
  const result = successfulDiagnostic();
  result.evidence.simulation.publications = [{ active: 0, movementCount: 0 }, { active: 0, movementCount: 0 }];
  assert.deepEqual(emittedTrialErrors(result, expected, { ...options, scenario: 'empty', fixtureKind: 'jackson' },
    null).filter(error => !/imagery/.test(error)), []);
});

test('rejects inadequate population, blank GL, wrong profile and refresh, and missing Jackson imagery', () => {
  const result = successfulDiagnostic();
  result.identity.runtime.glVendor = '';
  result.identity.profile = 'High'; result.identity.refreshHz = 30;
  result.evidence.simulation.publications = [{ active: 30 }, { active: 30 }];
  const errors = emittedTrialErrors(result, expected, { ...options, fixtureKind: 'jackson' });
  assert.match(errors.join('\n'), /hardware GL/);
  assert.match(errors.join('\n'), /Standard profile/);
  assert.match(errors.join('\n'), /60 Hz/);
  assert.match(errors.join('\n'), /imagery/);
  assert.match(errors.join('\n'), /population/);
});

test('rejects a mixed full and 94-percent detailed population window', () => {
  const result = successfulDiagnostic();
  result.evidence.simulation.publications = [{ active: 32, movementCount: 32 }, { active: 30, movementCount: 30 }];
  assert.match(emittedTrialErrors(result, expected, options).join('\n'), /population/);
});

test('accepts aggregate active population with no detailed movement and rejects aggregate movement', () => {
  const aggregateOptions = { ...options, scenario: 'aggregate-8', fixtureKind: 'jackson' };
  const result = successfulDiagnostic();
  result.evidence.sourceState.imagery = true; result.evidence.sourceState.imageryLoaded = true;
  result.evidence.simulation.publications = [{ active: 3000, movementCount: 0 }, { active: 2900, movementCount: 0 }];
  assert.deepEqual(emittedTrialErrors(result, expected, aggregateOptions), []);
  result.evidence.simulation.publications[1].movementCount = 1;
  assert.match(emittedTrialErrors(result, expected, aggregateOptions).join('\n'), /population/);
});
