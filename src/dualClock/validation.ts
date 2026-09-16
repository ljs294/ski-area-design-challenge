import type { DualCheckpoint } from '../types/dualClock';

const nonnegative = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const integer = (value: unknown): value is number => nonnegative(value) && Number.isSafeInteger(value);
const values = (record: unknown): unknown[] => record && typeof record === 'object' && !Array.isArray(record) ? Object.values(record) : [NaN];
const date = (value: unknown) => typeof value === 'string' && Number.isFinite(Date.parse(value));
const localDate = (value: unknown) => typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(`${value}T00:00:00.000Z`));

/** Schema 17 never substitutes fresh simulation state for missing or corrupt authority. */
export function validateDualCheckpoint(value: unknown): asserts value is DualCheckpoint {
  const invalid = () => { throw new Error('The dual-clock checkpoint is invalid or unsupported. Restore another save or keep this resort open for design recovery.'); };
  if (!value || typeof value !== 'object') invalid();
  const state = value as DualCheckpoint, config = state.config, clock = state.clock;
  if (state.version !== 1 || typeof state.seed !== 'string' || !integer(state.nextTicketPriceCents) || !integer(state.resortRevision) || config?.version !== 1 || !clock
    || !date(clock.at) || !date(clock.winterStart) || !date(clock.winterEnd) || Date.parse(clock.winterStart) >= Date.parse(clock.winterEnd)
    || !nonnegative(clock.macroSecond) || !nonnegative(clock.microSecond) || !integer(clock.revision)
    || ![1, 2, 4, 8, 16, 64].includes(clock.speed) || typeof clock.paused !== 'boolean' || !['winter', 'summer'].includes(clock.season)
    || !(config.macroSecondsPerSecond > 0) || !Number.isFinite(config.macroSecondsPerSecond)
    || ![1, 2, 4, 8, 16, 64].every(speed => nonnegative(config.microRates?.[speed as keyof typeof config.microRates]))
    || !integer(config.representativeLimit) || config.representativeLimit > 10000 || !integer(config.winterWeeks) || config.winterWeeks > 52
    || !integer(config.openingHour) || !integer(config.closingHour) || config.openingHour >= config.closingHour || config.closingHour > 24
    || !config.wear || !values(config.wear).every(nonnegative) || !config.wear.referenceWidthM || !config.wear.referenceSlopeDeg
    || config.wear.clearFraction >= config.wear.warningFraction || config.wear.warningFraction > 1 || config.wear.hardPasses < config.wear.packedPasses) invalid();
  const movement = config.guestMovement;
  if (movement && (movement.version !== 1 || !Number.isFinite(movement.trailEntrySpacingMicroSeconds)
    || movement.trailEntrySpacingMicroSeconds <= 0 || typeof movement.speedDomainTag !== 'string'
    || !movement.speedDomainTag || !Number.isFinite(movement.speedNormalLimit) || movement.speedNormalLimit <= 0
    || movement.speedNormalLimit > 3)) invalid();
  try { new Intl.DateTimeFormat('en', { timeZone: clock.timezone }).format(new Date(clock.at)); } catch { invalid(); }
  if (!Array.isArray(state.cohorts) || !Array.isArray(state.guests) || state.guests.length > config.representativeLimit + 1
    || !Array.isArray(state.transitRoutes) || !Array.isArray(state.signals) || !Array.isArray(state.history) || state.history.length > 256
    || !Array.isArray(state.dailyLedgers) || state.dailyLedgers.length > 366 || !state.snowLoss || !values(state.snowLoss).every(nonnegative)
    || !integer(state.nextCohortId) || !integer(state.nextGuestId) || !Number.isSafeInteger(state.lastAdmissionMinute)
    || !Number.isSafeInteger(state.lastWeatherHour) || !nonnegative(state.admissionResidual) || state.admissionResidual >= 1.000001
    || !values(state.serviceCredits).every(nonnegative) || !values(state.amenityCredits).every(nonnegative)
    || !values(state.amenityInventory).every(integer) || !values(state.dailyPrices).every(integer)
    || typeof state.autoTrack !== 'boolean') invalid();
  if (state.trailQueues !== undefined && (!state.trailQueues || Array.isArray(state.trailQueues)
    || !values(state.trailQueues).every(queue => {
      if (!queue || typeof queue !== 'object') return false;
      const value = queue as Record<string, unknown>;
      if (typeof value.trailId !== 'string' || typeof value.entryNode !== 'string'
        || !nonnegative(value.nextReleaseMicroSecond) || (value.lastReleaseMicroSecond !== undefined && !nonnegative(value.lastReleaseMicroSecond))
        || !Array.isArray(value.entries)) return false;
      return value.entries.every(entry => {
        if (!entry || typeof entry !== 'object') return false;
        const item = entry as Record<string, unknown>;
        return typeof item.guestId === 'string' && nonnegative(item.arrivalMicroSecond)
          && integer(item.ordinal) && nonnegative(item.releaseMicroSecond)
          && item.releaseMicroSecond >= item.arrivalMicroSecond;
      });
    }))) invalid();
  const dailyLiftBoardings = state.dailyLiftBoardings;
  if (dailyLiftBoardings && (!localDate(dailyLiftBoardings.date) || !dailyLiftBoardings.counts || !values(dailyLiftBoardings.counts).every(integer)
    || !['complete', 'partial'].includes(dailyLiftBoardings.accuracy)
    || (dailyLiftBoardings.trackingSince !== undefined && !date(dailyLiftBoardings.trackingSince)))) invalid();
  const flow = state.flow;
  if (!flow || ![flow.admitted, flow.active, flow.departed, flow.turnedAway, flow.ticketRevenueCents, flow.amenityRevenueCents, flow.completedRuns].every(integer)
    || flow.active + flow.departed !== flow.admitted || !flow.queues || !flow.trails) invalid();
  if (!values(flow.queues).every(queue => {
    if (!queue || typeof queue !== 'object') return false;
    const value = queue as Record<string, unknown>;
    return [value.guests, value.waitSeconds, value.boarded].every(nonnegative)
      && (value.riders === undefined || nonnegative(value.riders))
      && (value.serviceAvailable === undefined || typeof value.serviceAvailable === 'boolean')
      && (value.dailyBoarded === undefined || integer(value.dailyBoarded))
      && (value.dailyAccuracy === undefined || value.dailyAccuracy === 'complete' || value.dailyAccuracy === 'partial')
      && (value.trackingSince === undefined || date(value.trackingSince));
  })) invalid();
  if (state.cohorts.some(c => !integer(c.id) || typeof c.admissionId !== 'string' || !integer(c.count) || !Number.isFinite(c.due)
    || !Number.isFinite(c.started) || !Number.isFinite(c.leaveAt) || !integer(c.budgetCents) || !['choosing', 'queue', 'travel', 'amenity'].includes(c.status))
    || state.cohorts.reduce((sum, cohort) => sum + cohort.count, 0) !== flow.active) invalid();
  if (state.guests.some(g => typeof g.id !== 'string' || !integer(g.runs) || !integer(g.spendingCents)
    || !integer(g.personalBudgetCents) || !integer(g.needsSecond) || !g.needs || values(g.needs).some(n => !nonnegative(n) || n > 1)
    || !Number.isFinite(g.started) || !Number.isFinite(g.due) || !nonnegative(g.satisfaction) || g.satisfaction > 1
    || !Array.isArray(g.history) || g.history.length > 128 || !date(g.trackingBeganAt)
    || (g.speedZ !== undefined && (!Number.isFinite(g.speedZ) || g.speedZ < -3 || g.speedZ > 3))
    || (g.trailQueueKey !== undefined && typeof g.trailQueueKey !== 'string')
    || (g.trailQueueArrivalMicroSecond !== undefined && !nonnegative(g.trailQueueArrivalMicroSecond))
    || (g.trailQueueReleaseMicroSecond !== undefined && !nonnegative(g.trailQueueReleaseMicroSecond))
    || (g.lastTrailId !== undefined && typeof g.lastTrailId !== 'string')
    || (g.transferTrailContinuationId !== undefined && typeof g.transferTrailContinuationId !== 'string'))) invalid();
  if (state.advance && (!date(state.advance.request?.target) || !date(state.advance.startedAt) || !nonnegative(state.advance.fraction)
    || state.advance.fraction > 1 || !state.advance.baseline || !values(state.advance.baseline).every(integer))) invalid();
  if (state.snow) {
    const snow = state.snow, size = snow.width * snow.height;
    if (!integer(snow.width) || !integer(snow.height) || snow.width < 2 || snow.height < 2 || snow.width > 512 || snow.height > 512
      || !snow.bounds || !values(snow.bounds).every(n => typeof n === 'number' && Number.isFinite(n))
      || snow.bounds.west >= snow.bounds.east || snow.bounds.south >= snow.bounds.north
      || !Array.isArray(snow.depthM) || !Array.isArray(snow.surface) || !Array.isArray(snow.exposure)
      || snow.depthM.length !== size || snow.surface.length !== size || snow.exposure.length !== size
      || !snow.depthM.every(nonnegative) || !snow.exposure.every(nonnegative)
      || snow.surface.some(n => !integer(n) || n > 11)) invalid();
  }
}
