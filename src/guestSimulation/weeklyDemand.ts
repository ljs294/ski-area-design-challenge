export const MAX_ACTIVE_WEEKLY_GUESTS = 10_000;

export interface WeeklyGuestWeighting {
  readonly dailyDemand: readonly number[];
  readonly weeklyDemand: number;
  readonly simulatedRoster: number;
  readonly outcomeWeight: number;
  readonly daySelectionWeights: readonly number[];
}

/** Build a representative individual roster while retaining weekly totals. */
export function weeklyGuestWeighting(
  dailyDemand: readonly number[],
  maxActiveGuests = MAX_ACTIVE_WEEKLY_GUESTS,
): WeeklyGuestWeighting {
  if (dailyDemand.length !== 7 || dailyDemand.some((value) => !Number.isFinite(value) || value < 0)) {
    throw new RangeError('Weekly demand requires seven finite non-negative daily values.');
  }
  if (!Number.isSafeInteger(maxActiveGuests) || maxActiveGuests <= 0) {
    throw new RangeError('Maximum active guests must be a positive safe integer.');
  }
  const normalized = Object.freeze(dailyDemand.map((value) => Math.max(0, value)));
  const weeklyDemand = normalized.reduce((sum, value) => sum + value, 0);
  const simulatedRoster = Math.min(maxActiveGuests, Math.round(weeklyDemand / 7));
  const outcomeWeight = weeklyDemand / Math.max(1, simulatedRoster);
  const daySelectionWeights = Object.freeze(normalized.map((value) => weeklyDemand > 0 ? value / weeklyDemand : 1 / 7));
  return Object.freeze({ dailyDemand: normalized, weeklyDemand, simulatedRoster, outcomeWeight,
    daySelectionWeights });
}

export function weightedAdditiveOutcome(value: number, weighting: WeeklyGuestWeighting): number {
  if (!Number.isFinite(value)) throw new RangeError('Outcome value must be finite.');
  return value * weighting.outcomeWeight;
}
