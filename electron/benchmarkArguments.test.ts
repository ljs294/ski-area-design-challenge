import { describe, expect, it } from 'vitest';
import { integratedBenchmarkQuery } from './benchmarkArguments';

describe('integratedBenchmarkQuery', () => {
  it('keeps normal and incomplete launches free of benchmark query state', () => {
    expect(integratedBenchmarkQuery([])).toEqual({});
    expect(integratedBenchmarkQuery(['--integrated-benchmark-dev-console',
      '--integrated-benchmark-telemetry=1'])).toEqual({});
  });

  it('maps an explicit scenario and its opt-in presentation flags', () => {
    expect(integratedBenchmarkQuery(['--integrated-benchmark-scenario=encoded',
      '--integrated-benchmark-telemetry=1', '--integrated-benchmark-dev-console'])).toEqual({
      'integrated-benchmark-scenario': 'encoded',
      'integrated-benchmark-telemetry': '1',
      'dev-console': '',
    });
  });
});
