export type IntegratedFixtureKind = 'ci' | 'jackson';
export type IntegratedRunTier = 'diagnostic' | 'qualification';
export type IntegratedScenario = { fixture: IntegratedFixtureKind; checkpointTarget: 0 | 32 | 1000 | 3000;
  requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64; workload: 'empty' | 'detailed' | 'aggregate'; workflow: string;
  profile: 'Standard' | 'High'; cssViewport: { width: number; height: number };
  canvas: { width: number; height: number }; devicePixelRatio: number };
export const integratedBenchmarkScenarios: Readonly<Record<string, IntegratedScenario>>;
export function resolveIntegratedBenchmarkConfiguration(env?: NodeJS.ProcessEnv): Readonly<{ fixture: IntegratedFixtureKind;
  tier: IntegratedRunTier; isQualification: boolean; scenarioName: string; scenario: IntegratedScenario }>;
