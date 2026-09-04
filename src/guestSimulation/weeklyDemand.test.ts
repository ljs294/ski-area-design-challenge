import { describe, expect, it } from 'vitest';
import { weeklyGuestWeighting, weightedAdditiveOutcome } from './weeklyDemand';

describe('weekly guest weighting', () => {
  it('simulates an average day and weights additive outcomes to the week', () => {
    const result = weeklyGuestWeighting([1_000, 1_000, 1_000, 1_000, 1_000, 2_000, 2_000]);
    expect(result.weeklyDemand).toBe(9_000);
    expect(result.simulatedRoster).toBe(1_286);
    expect(weightedAdditiveOutcome(result.simulatedRoster, result)).toBeCloseTo(9_000);
    expect(result.daySelectionWeights.reduce((sum, value) => sum + value, 0)).toBeCloseTo(1);
  });

  it('caps active individuals while preserving the aggregate total', () => {
    const result = weeklyGuestWeighting(Array(7).fill(20_000));
    expect(result.simulatedRoster).toBe(10_000);
    expect(result.outcomeWeight).toBe(14);
  });

  it('handles a closed week without producing invalid weights', () => {
    const result = weeklyGuestWeighting(Array(7).fill(0));
    expect(result.simulatedRoster).toBe(0);
    expect(result.outcomeWeight).toBe(0);
    expect(result.daySelectionWeights.every((value) => value === 1 / 7)).toBe(true);
  });
});
