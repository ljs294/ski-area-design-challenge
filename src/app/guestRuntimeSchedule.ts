import type { SimulationClock } from '../types/simulation';
import type { GuestOperatingWindow } from './guestNetworkAdapter';

export const GUEST_OPERATING_START_MINUTE = 8 * 60;
export const GUEST_OPERATING_DAY_SECONDS = 12 * 60 * 60;
export const GUEST_COMPOSITE_WEEK_SECONDS = GUEST_OPERATING_DAY_SECONDS;

/**
 * Resolve the current composite winter week onto the authoritative elapsed
 * simulation clock. Every winter week is one continuous 43,200-second day;
 * there is deliberately no local-midnight or wall-clock subtraction here.
 */
export function guestOperatingWindow(clock: SimulationClock): GuestOperatingWindow {
  if (Number.isFinite(clock.elapsedSimSecond)) {
    const weekIndex = Number.isSafeInteger(clock.winterWeek) && (clock.winterWeek ?? 0) > 0
      ? (clock.winterWeek ?? 1) - 1
      : Math.floor(clock.elapsedSimSecond / GUEST_COMPOSITE_WEEK_SECONDS);
    return guestOperatingWindowForWeek(weekIndex);
  }
  // Legacy compatibility for partially hydrated callers. New clocks always
  // take the elapsed-second path above.
  return guestOperatingWindowForLocalDay(clock.absoluteGameMinute - clock.minuteOfDay);
}

export function guestOperatingWindowForWeek(weekIndex: number): GuestOperatingWindow {
  if (!Number.isSafeInteger(weekIndex) || weekIndex < 0) {
    throw new RangeError('composite winter week index must be a non-negative safe integer');
  }
  const startTick = weekIndex * GUEST_COMPOSITE_WEEK_SECONDS;
  return Object.freeze({ startTick, endTick: startTick + GUEST_COMPOSITE_WEEK_SECONDS });
}

export function guestOperatingWindowForLocalDay(localMidnightAbsoluteMinute: number): GuestOperatingWindow {
  if (!Number.isSafeInteger(localMidnightAbsoluteMinute) || localMidnightAbsoluteMinute < 0) {
    throw new RangeError('local midnight must be a non-negative absolute game minute');
  }
  const startTick = (localMidnightAbsoluteMinute + GUEST_OPERATING_START_MINUTE) * 60;
  return Object.freeze({ startTick, endTick: startTick + GUEST_OPERATING_DAY_SECONDS });
}

/** After a developer teleport, start a fresh roster at the next demand bucket. */
export function guestSimulationWindowAfterDiscontinuity(
  operatingWindow: GuestOperatingWindow,
  localMidnightAbsoluteMinute: number,
  discontinuity: { readonly absoluteGameMinute: number; readonly localMidnightAbsoluteMinute: number } | null | undefined,
  bucketSeconds: number,
): GuestOperatingWindow {
  if (!Number.isSafeInteger(bucketSeconds) || bucketSeconds <= 0) {
    throw new RangeError('guest demand bucket must be a positive safe integer');
  }
  const resetTick = discontinuity?.localMidnightAbsoluteMinute === localMidnightAbsoluteMinute
    ? discontinuity.absoluteGameMinute * 60 : null;
  const startTick = resetTick === null || resetTick <= operatingWindow.startTick ? operatingWindow.startTick
    : Math.ceil(resetTick / bucketSeconds) * bucketSeconds;
  return Object.freeze({ startTick, endTick: operatingWindow.endTick });
}

/** Start a fresh weekly roster at the first bucket at or after a teleport. */
export function guestSimulationWindowAfterClockDiscontinuity(
  operatingWindow: GuestOperatingWindow,
  currentSecond: number,
  discontinuity: { readonly revision: number } | null | undefined,
  bucketSeconds: number,
): GuestOperatingWindow {
  if (!Number.isFinite(currentSecond) || currentSecond < operatingWindow.startTick) {
    throw new RangeError('current composite second is outside the operating window');
  }
  if (!Number.isSafeInteger(bucketSeconds) || bucketSeconds <= 0) {
    throw new RangeError('guest demand bucket must be a positive safe integer');
  }
  if (!discontinuity) return operatingWindow;
  const startTick = Math.min(operatingWindow.endTick,
    Math.max(operatingWindow.startTick, Math.ceil(currentSecond / bucketSeconds) * bucketSeconds));
  return Object.freeze({ startTick, endTick: operatingWindow.endTick });
}

/** A topology match alone is insufficient: checkpoints contain a single day's roster. */
export function guestCheckpointMatchesOperatingWindow(
  snapshot: { readonly demandPlan: { readonly startTick: number; readonly endTick: number };
    readonly topologyRevision: number },
  window: GuestOperatingWindow,
  topologyRevision: number,
): boolean {
  return snapshot.topologyRevision === topologyRevision
    && snapshot.demandPlan.startTick === window.startTick
    && snapshot.demandPlan.endTick === window.endTick;
}
