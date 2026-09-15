import assert from 'node:assert/strict';
import test from 'node:test';
import { resolveIntegratedBenchmarkConfiguration } from './integratedBenchmarkConfiguration.mjs';

test('separates Jackson fixture selection from diagnostic and qualification tiers', () => {
  const diagnostic = resolveIntegratedBenchmarkConfiguration({ INTEGRATED_FIXTURE_KIND: 'jackson',
    INTEGRATED_TIER: 'diagnostic', INTEGRATED_SCENARIO: 'empty' });
  const qualification = resolveIntegratedBenchmarkConfiguration({ INTEGRATED_FIXTURE_KIND: 'jackson',
    INTEGRATED_TIER: 'qualification', INTEGRATED_SCENARIO: 'empty' });
  assert.equal(diagnostic.fixture, 'jackson'); assert.equal(diagnostic.isQualification, false);
  assert.equal(qualification.fixture, 'jackson'); assert.equal(qualification.isQualification, true);
  assert.equal(diagnostic.scenario.checkpointTarget, 0);
});

test('uses the explicit CI diagnostic scenario and rejects CI qualification', () => {
  const configuration = resolveIntegratedBenchmarkConfiguration({});
  assert.equal(configuration.fixture, 'ci'); assert.equal(configuration.scenarioName, 'ci-diagnostic');
  assert.equal(configuration.scenario.checkpointTarget, 32);
  assert.throws(() => resolveIntegratedBenchmarkConfiguration({ INTEGRATED_FIXTURE_KIND: 'ci',
    INTEGRATED_TIER: 'qualification', INTEGRATED_SCENARIO: 'ci-diagnostic' }), /requires the Jackson fixture/);
});
