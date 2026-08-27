export interface WeatherCalendarHour {
  at: string;
  localDateTime: string;
  year: number;
  month: number;
  day: number;
  hour: number;
  utcOffsetMinutes: number;
  fold: 0 | 1;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatter(timezone: string): Intl.DateTimeFormat {
  const cached = formatters.get(timezone);
  if (cached) return cached;
  const created = new Intl.DateTimeFormat('en-US-u-ca-gregory-nu-latn', {
    timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  });
  formatters.set(timezone, created);
  return created;
}

function localParts(at: number, timezone: string): Omit<WeatherCalendarHour, 'at' | 'localDateTime' | 'utcOffsetMinutes' | 'fold'> & { minute: number; second: number } {
  const values = Object.fromEntries(formatter(timezone).formatToParts(new Date(at))
    .filter((part) => part.type !== 'literal').map((part) => [part.type, Number(part.value)]));
  return { year: values.year, month: values.month, day: values.day, hour: values.hour, minute: values.minute, second: values.second };
}

function offsetMinutes(at: number, timezone: string): number {
  const parts = localParts(at, timezone);
  const representedAsUtc = Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second);
  return Math.round((representedAsUtc - at) / 60_000);
}

export function instantForLocalMidnight(year: number, timezone: string): number {
  try { formatter(timezone).format(new Date()); } catch { throw new Error(`Invalid IANA timezone: ${timezone}`); }
  let candidate = Date.UTC(year, 0, 1);
  for (let iteration = 0; iteration < 4; iteration += 1) {
    const next = Date.UTC(year, 0, 1) - offsetMinutes(candidate, timezone) * 60_000;
    if (next === candidate) break;
    candidate = next;
  }
  const resolved = localParts(candidate, timezone);
  if (resolved.year !== year || resolved.month !== 1 || resolved.day !== 1 || resolved.hour !== 0) {
    throw new Error(`Unable to resolve local January 1 for ${timezone}`);
  }
  return candidate;
}

export function weatherCalendarYear(year: number, timezone: string): readonly WeatherCalendarHour[] {
  if (!Number.isInteger(year) || year < 1900 || year > 2200) throw new Error('validationYear must be between 1900 and 2200');
  const start = instantForLocalMidnight(year, timezone);
  const end = instantForLocalMidnight(year + 1, timezone);
  const seen = new Map<string, number>();
  const hours: WeatherCalendarHour[] = [];
  for (let at = start; at < end; at += 3_600_000) {
    const parts = localParts(at, timezone);
    const localDateTime = `${parts.year.toString().padStart(4, '0')}-${parts.month.toString().padStart(2, '0')}-${parts.day.toString().padStart(2, '0')}T${parts.hour.toString().padStart(2, '0')}:00:00`;
    const count = seen.get(localDateTime) ?? 0;
    seen.set(localDateTime, count + 1);
    hours.push({ at: new Date(at).toISOString(), localDateTime, year: parts.year, month: parts.month,
      day: parts.day, hour: parts.hour, utcOffsetMinutes: offsetMinutes(at, timezone), fold: count > 0 ? 1 : 0 });
  }
  const leap = new Date(Date.UTC(year, 1, 29)).getUTCMonth() === 1;
  const expected = leap ? 8_784 : 8_760;
  if (hours.length !== expected) throw new Error(`Timezone ${timezone} produced ${hours.length} hours for ${year}; expected ${expected}`);
  return hours;
}

export function daysInMonth(year: number, month: number): number {
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

export function monthBlend(calendar: WeatherCalendarHour): { previous: number; current: number; next: number; previousWeight: number; nextWeight: number } {
  const blendRadius = 3.5;
  const count = daysInMonth(calendar.year, calendar.month);
  const dayPosition = calendar.day - 1 + calendar.hour / 24;
  const previous = calendar.month === 1 ? 12 : calendar.month - 1;
  const next = calendar.month === 12 ? 1 : calendar.month + 1;
  const previousWeight = dayPosition < blendRadius ? (blendRadius - dayPosition) / (blendRadius * 2) : 0;
  const fromEnd = count - dayPosition;
  const nextWeight = fromEnd <= blendRadius ? (blendRadius - fromEnd) / (blendRadius * 2) : 0;
  return { previous, current: calendar.month, next, previousWeight, nextWeight };
}
