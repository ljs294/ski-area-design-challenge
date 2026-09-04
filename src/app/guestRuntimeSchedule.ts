import type { SimulationClock } from '../types/simulation';
import type { GuestOperatingWindow } from './guestNetworkAdapter';

export const GUEST_OPERATING_START_MINUTE = 8 * 60;
export const GUEST_OPERATING_DAY_SECONDS = 12 * 60 * 60;

/** Resolve today's local 08:00-20:00 guest window onto the absolute game clock. */
export function guestOperatingWindow(clock: SimulationClock): GuestOperatingWindow {
  return guestOperatingWindowForLocalDay(clock.absoluteGameMinute - clock.minuteOfDay);
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
