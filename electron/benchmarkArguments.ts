export const BENCHMARK_SCENARIO_ARGUMENT = '--integrated-benchmark-scenario=';
export const BENCHMARK_TELEMETRY_ARGUMENT = '--integrated-benchmark-telemetry=';
export const BENCHMARK_DEV_CONSOLE_ARGUMENT = '--integrated-benchmark-dev-console';

/** Translate explicit runner arguments into renderer bootstrap query fields. */
export function integratedBenchmarkQuery(args: readonly string[]): Record<string, string> {
  const scenario = args.find(argument => argument.startsWith(BENCHMARK_SCENARIO_ARGUMENT))
    ?.slice(BENCHMARK_SCENARIO_ARGUMENT.length);
  if (!scenario) return {};
  const telemetry = args.find(argument => argument.startsWith(BENCHMARK_TELEMETRY_ARGUMENT))
    ?.slice(BENCHMARK_TELEMETRY_ARGUMENT.length);
  return { 'integrated-benchmark-scenario': scenario,
    ...(telemetry === '1' ? { 'integrated-benchmark-telemetry': '1' } : {}),
    ...(args.includes(BENCHMARK_DEV_CONSOLE_ARGUMENT) ? { 'dev-console': '' } : {}) };
}
