import { describe, expect, it } from 'vitest';
import {
  DEFAULT_GUEST_SIMULATION_CONFIG,
  DEFAULT_GUEST_PROBABILITY_CONFIG,
  ARRIVAL_BIN_SECONDS,
  ARRIVAL_HORIZON_SECONDS,
  ARRIVAL_SHIFT_SECONDS,
  ARRIVAL_WEEKDAY_BACKGROUND_MIX,
  ARRIVAL_WEEKDAY_MEDIAN_SECONDS,
  ARRIVAL_WEEKDAY_SIGMA,
  ARRIVAL_WEEKEND_BACKGROUND_MIX,
  ARRIVAL_WEEKEND_MEDIAN_SECONDS,
  ARRIVAL_WEEKEND_SIGMA,
  GUEST_SIMULATION_CONFIG_VERSION,
  createGuestProbabilityConfig,
  createGuestSimulationConfig,
  isGuestProbabilityConfig,
  isGuestSimulationConfig,
  validateGuestSimulationConfig,
} from './config';

describe('guest simulation config', () => {
  it('uses an immutable v1 config with one-second ticks', () => {
    expect(DEFAULT_GUEST_SIMULATION_CONFIG.version).toBe(1);
    expect(DEFAULT_GUEST_SIMULATION_CONFIG.configVersion).toBe(GUEST_SIMULATION_CONFIG_VERSION);
    expect(DEFAULT_GUEST_SIMULATION_CONFIG.tickSeconds).toBe(1);
    expect(DEFAULT_GUEST_SIMULATION_CONFIG.simulatedSecondsPerTick).toBe(1);
    expect(DEFAULT_GUEST_SIMULATION_CONFIG.probability).toBe(DEFAULT_GUEST_PROBABILITY_CONFIG);
    expect(DEFAULT_GUEST_PROBABILITY_CONFIG.demandElasticity).toBeLessThan(0);
    expect(DEFAULT_GUEST_PROBABILITY_CONFIG.softmaxTemperature).toBe(0.10);
    expect(DEFAULT_GUEST_PROBABILITY_CONFIG.arrivalCurve).toMatchObject({ horizonSeconds: ARRIVAL_HORIZON_SECONDS,
      binSeconds: ARRIVAL_BIN_SECONDS, shiftSeconds: ARRIVAL_SHIFT_SECONDS,
      weekday: { medianSeconds: ARRIVAL_WEEKDAY_MEDIAN_SECONDS, sigma: ARRIVAL_WEEKDAY_SIGMA, backgroundMix: ARRIVAL_WEEKDAY_BACKGROUND_MIX },
      weekend: { medianSeconds: ARRIVAL_WEEKEND_MEDIAN_SECONDS, sigma: ARRIVAL_WEEKEND_SIGMA, backgroundMix: ARRIVAL_WEEKEND_BACKGROUND_MIX } });
    expect(Object.isFrozen(DEFAULT_GUEST_SIMULATION_CONFIG)).toBe(true);
    expect(isGuestSimulationConfig(DEFAULT_GUEST_SIMULATION_CONFIG)).toBe(true);
  });

  it('normalizes overrides while preserving matching tick aliases and version', () => {
    const config = createGuestSimulationConfig({ maxGuests: 25_000, tickSeconds: 2 });
    expect(config.maxGuests).toBe(25_000);
    expect(config.tickSeconds).toBe(2);
    expect(config.simulatedSecondsPerTick).toBe(2);
    expect(config.version).toBe(1);
    expect(config.configVersion).toBe(1);
    expect(validateGuestSimulationConfig(config)).toBe(config);
  });

  it('rejects fractional, mismatched, and out-of-bounds config values', () => {
    expect(() => createGuestSimulationConfig({ tickSeconds: 1.5 })).toThrow(RangeError);
    expect(() => createGuestSimulationConfig({ tickSeconds: 2, simulatedSecondsPerTick: 1 })).toThrow(RangeError);
    expect(() => createGuestSimulationConfig({ maxGuests: 50_001 })).toThrow(RangeError);
    expect(isGuestSimulationConfig({ ...DEFAULT_GUEST_SIMULATION_CONFIG, version: 2 })).toBe(false);
  });

  it('keeps probability tuning versioned and rejects unsafe curve bounds', () => {
    const probability = createGuestProbabilityConfig({ demandElasticity: -0.75, softmaxTemperature: 0.2,
      abilityGap: { muRiskSlope: 0.7 } });
    expect(probability.version).toBe(1);
    expect(probability.demandElasticity).toBe(-0.75);
    expect(probability.abilityGap.muRiskSlope).toBe(0.7);
    expect(isGuestProbabilityConfig(probability)).toBe(true);
    expect(() => createGuestProbabilityConfig({ demandElasticity: 0 })).toThrow(RangeError);
    expect(() => createGuestProbabilityConfig({ arrivalCurve: { weekend: { sigma: 0 } } })).toThrow(RangeError);
    expect(() => createGuestProbabilityConfig({ arrivalCurve: { horizonSeconds: 0 } })).toThrow(RangeError);
    const weekend = createGuestProbabilityConfig({ arrivalCurve: { weekend: { medianSeconds: 3_000, sigma: 0.4, backgroundMix: 0.1 } } });
    expect(weekend.arrivalCurve.weekend.medianSeconds).toBe(3_000);
  });
});
