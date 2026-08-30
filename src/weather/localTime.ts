/** IANA calendar helpers kept independent of UI, storage, and game time. */

export interface WeatherLocalDateTime {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  second: number;
}

export interface WeatherCalendarDelta {
  hours?: number;
  days?: number;
  weeks?: number;
  months?: number;
}

const formatterCache = new Map<string, Intl.DateTimeFormat>();

function formatter(timezone: string): Intl.DateTimeFormat {
  const cached = formatterCache.get(timezone);
  if (cached) return cached;
  try {
    const next = new Intl.DateTimeFormat('en-US-u-ca-gregory', {
      timeZone: timezone,
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    });
    // Force eager validation. Intl otherwise delays it in some engines.
    next.format(0);
    formatterCache.set(timezone, next);
    return next;
  } catch {
    throw new Error(`Invalid IANA weather timezone: ${timezone}`);
  }
}

export function isWeatherTimezone(timezone: string): boolean {
  try {
    formatter(timezone);
    return true;
  } catch {
    return false;
  }
}

export function weatherLocalParts(at: string | number | Date, timezone: string): WeatherLocalDateTime {
  const instant = at instanceof Date ? at : new Date(at);
  if (Number.isNaN(instant.getTime())) throw new Error('Weather calendar received an invalid instant.');
  const values: Partial<Record<Intl.DateTimeFormatPartTypes, number>> = {};
  for (const part of formatter(timezone).formatToParts(instant)) {
    if (part.type === 'year' || part.type === 'month' || part.type === 'day' ||
      part.type === 'hour' || part.type === 'minute' || part.type === 'second') values[part.type] = Number(part.value);
  }
  // h23 prevents this in current engines, but normalize the old 24:00 form too.
  const year = values.year;
  const month = values.month;
  const day = values.day;
  const hour = values.hour === 24 ? 0 : values.hour;
  const minute = values.minute;
  const second = values.second;
  if (!Number.isInteger(year) || !Number.isInteger(month) || !Number.isInteger(day) ||
    !Number.isInteger(hour) || !Number.isInteger(minute) || !Number.isInteger(second)) {
    throw new Error(`Unable to read local weather time in ${timezone}.`);
  }
  return {
    year: year as number, month: month as number, day: day as number, hour: hour as number,
    minute: minute as number, second: second as number,
  };
}

function wallEpoch(parts: WeatherLocalDateTime): number {
  return Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second);
}

function compareLocal(left: WeatherLocalDateTime, right: WeatherLocalDateTime): number {
  return wallEpoch(left) - wallEpoch(right);
}

function offsetMinutesAt(instant: number, timezone: string): number {
  const local = weatherLocalParts(instant, timezone);
  return Math.round((wallEpoch(local) - instant) / 60_000);
}

/** The offset differentiates the two 01:00 hours during a fall-back transition. */
export function weatherUtcOffsetMinutes(at: string | number | Date, timezone: string): number {
  const instant = at instanceof Date ? at : new Date(at);
  if (Number.isNaN(instant.getTime())) throw new Error('Weather calendar received an invalid instant.');
  return offsetMinutesAt(instant.getTime(), timezone);
}

function offsetKey(offsetMinutes: number): string {
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const absolute = Math.abs(offsetMinutes);
  return `${sign}${Math.floor(absolute / 60).toString().padStart(2, '0')}${(absolute % 60).toString().padStart(2, '0')}`;
}

function sameLocal(left: WeatherLocalDateTime, right: WeatherLocalDateTime): boolean {
  return left.year === right.year && left.month === right.month && left.day === right.day &&
    left.hour === right.hour && left.minute === right.minute && left.second === right.second;
}

/**
 * Resolve a local wall-clock time using Temporal's "compatible" convention:
 * choose the earlier occurrence during a fall-back repeat, and move forward by
 * the gap during a spring-forward nonexistent local time.
 */
export function weatherInstantForLocal(parts: WeatherLocalDateTime, timezone: string): string {
  const targetWall = wallEpoch(parts);
  const offsets = new Set<number>();
  for (const deltaHours of [-36, -24, -12, 0, 12, 24, 36]) {
    offsets.add(offsetMinutesAt(targetWall + deltaHours * 3_600_000, timezone));
  }
  const candidates = [...offsets]
    .map((offset) => targetWall - offset * 60_000)
    .filter((instant) => sameLocal(weatherLocalParts(instant, timezone), parts))
    .sort((left, right) => left - right);
  if (candidates.length > 0) return new Date(candidates[0]).toISOString();

  // DST gap: select the first representable local time at or after the desired
  // wall time. Checking offset candidates avoids a minute-by-minute search.
  const forward = [...offsets]
    .map((offset) => targetWall - offset * 60_000)
    .map((instant) => ({ instant, local: weatherLocalParts(instant, timezone) }))
    .filter((candidate) => compareLocal(candidate.local, parts) >= 0)
    .sort((left, right) => compareLocal(left.local, right.local) || left.instant - right.instant);
  if (forward.length > 0) return new Date(forward[0].instant).toISOString();
  throw new Error(`Unable to resolve local weather time in ${timezone}.`);
}

function dayCount(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

/** Add calendar units while preserving map-local clock time whenever possible. */
export function addWeatherLocalTime(at: string, timezone: string, delta: WeatherCalendarDelta): string {
  const current = weatherLocalParts(at, timezone);
  if (delta.hours) return new Date(new Date(at).getTime() + delta.hours * 3_600_000).toISOString();
  const calendar = new Date(Date.UTC(current.year, current.month - 1, current.day));
  if (delta.days) calendar.setUTCDate(calendar.getUTCDate() + delta.days);
  if (delta.weeks) calendar.setUTCDate(calendar.getUTCDate() + delta.weeks * 7);
  if (delta.months) {
    const originalDay = current.day;
    calendar.setUTCDate(1);
    calendar.setUTCMonth(calendar.getUTCMonth() + delta.months);
    calendar.setUTCDate(Math.min(originalDay, dayCount(calendar.getUTCFullYear(), calendar.getUTCMonth() + 1)));
  }
  const next: WeatherLocalDateTime = {
    year: calendar.getUTCFullYear(), month: calendar.getUTCMonth() + 1, day: calendar.getUTCDate(),
    hour: current.hour, minute: current.minute, second: current.second,
  };
  return weatherInstantForLocal(next, timezone);
}

export function localWeatherDateKey(at: string, timezone: string): string {
  const local = weatherLocalParts(at, timezone);
  return `${local.year.toString().padStart(4, '0')}-${local.month.toString().padStart(2, '0')}-${local.day.toString().padStart(2, '0')}`;
}

export function localWeatherClockKey(at: string, timezone: string): string {
  const local = weatherLocalParts(at, timezone);
  return `${local.month.toString().padStart(2, '0')}-${local.day.toString().padStart(2, '0')}-${local.hour.toString().padStart(2, '0')}`;
}

/** Local calendar comparison key, including UTC offset so fall-back hours stay distinct. */
export function localWeatherClockOffsetKey(at: string, timezone: string): string {
  const local = weatherLocalParts(at, timezone);
  return `${local.month.toString().padStart(2, '0')}-${local.day.toString().padStart(2, '0')}` +
    `T${local.hour.toString().padStart(2, '0')}:${local.minute.toString().padStart(2, '0')}:${local.second.toString().padStart(2, '0')}` +
    `@${offsetKey(weatherUtcOffsetMinutes(at, timezone))}`;
}

/** Day-local key for analog source-day records; intentionally omits calendar date/year. */
export function localWeatherTimeOffsetKey(at: string, timezone: string): string {
  const local = weatherLocalParts(at, timezone);
  return `${local.hour.toString().padStart(2, '0')}:${local.minute.toString().padStart(2, '0')}:${local.second.toString().padStart(2, '0')}` +
    `@${offsetKey(weatherUtcOffsetMinutes(at, timezone))}`;
}
