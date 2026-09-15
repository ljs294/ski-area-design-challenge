import { readFileSync } from 'node:fs';
import path from 'node:path';

export const integratedBenchmarkScenarios = Object.freeze(JSON.parse(
  readFileSync(path.resolve('scripts/integratedBenchmarkScenarios.json'), 'utf8')));

export function resolveIntegratedBenchmarkConfiguration(env = process.env) {
  const fixture = env.INTEGRATED_FIXTURE_KIND ?? (env.INTEGRATED_FIXTURE_ROOT ? 'jackson' : 'ci');
  if (fixture !== 'ci' && fixture !== 'jackson') throw new Error('INTEGRATED_FIXTURE_KIND must be ci or jackson.');
  const tier = env.INTEGRATED_TIER ?? 'diagnostic';
  if (tier !== 'diagnostic' && tier !== 'qualification') throw new Error('INTEGRATED_TIER must be diagnostic or qualification.');
  if (tier === 'qualification' && fixture !== 'jackson') throw new Error('Qualification requires the Jackson fixture.');
  const scenarioName = env.INTEGRATED_SCENARIO ?? (fixture === 'ci' ? 'ci-diagnostic' : 'detailed-3000');
  const scenario = integratedBenchmarkScenarios[scenarioName];
  if (!scenario) throw new Error(`Unknown integrated benchmark scenario: ${scenarioName}`);
  const scenarioFixture = scenario.fixture ?? 'jackson';
  if (scenarioFixture !== fixture) throw new Error(`Scenario '${scenarioName}' requires the ${scenarioFixture} fixture.`);
  return Object.freeze({ fixture, tier, isQualification: tier === 'qualification', scenarioName, scenario });
}
