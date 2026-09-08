/** Phase 6 lodging and multi-day stay records. */

import { eventCalendarChecksum } from './eventCalendar.ts';
import { encodeBinarySidecar, decodeBinarySidecar } from './binaryCodec.ts';

export const PHASE6_LODGING_VERSION = 1 as const;
export type Phase6LodgingVersion = typeof PHASE6_LODGING_VERSION;
export const SIMULATED_SECONDS_PER_DAY = 86_400;

export interface LodgingProperty {
  readonly id: string;
  readonly capacityGuests: number;
  /** Offsets within a simulated day, represented as a half-open interval. */
  readonly checkInFromTick: number;
  readonly checkInUntilTick: number;
  readonly checkOutFromTick: number;
  readonly checkOutUntilTick: number;
  readonly nightlyRateCents?: number;
}

export interface LodgingStayRequest {
  readonly id: string;
  readonly visitorKey: string;
  readonly guestIds: readonly string[];
  readonly lodgingId: string;
  readonly arrivalDay: number;
  readonly departureDay: number;
}

export type LodgingStayStatus = 'confirmed' | 'waitlisted' | 'rejected';

export interface LodgingStayRecord extends LodgingStayRequest {
  readonly status: LodgingStayStatus;
  readonly checkInTick: number | null;
  readonly checkOutTick: number | null;
  readonly nights: number;
}

export interface LodgingScheduleSnapshot {
  readonly version: Phase6LodgingVersion;
  readonly properties: readonly LodgingProperty[];
  readonly stays: readonly LodgingStayRecord[];
  readonly checksum: string;
}

function integer(value: number, label: string, minimum = 0): void {
  if (!Number.isSafeInteger(value) || value < minimum) throw new RangeError(`${label} must be a safe integer >= ${minimum}`);
}

function text(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be non-empty`);
}

function validateProperty(property: LodgingProperty): void {
  text(property.id, 'lodging id'); integer(property.capacityGuests, 'lodging capacityGuests', 1);
  integer(property.checkInFromTick, 'checkInFromTick'); integer(property.checkInUntilTick, 'checkInUntilTick');
  integer(property.checkOutFromTick, 'checkOutFromTick'); integer(property.checkOutUntilTick, 'checkOutUntilTick');
  if (property.checkInUntilTick <= property.checkInFromTick || property.checkOutUntilTick <= property.checkOutFromTick
    || property.checkInUntilTick > SIMULATED_SECONDS_PER_DAY || property.checkOutUntilTick > SIMULATED_SECONDS_PER_DAY) throw new RangeError(`invalid check-in/out interval for lodging ${property.id}`);
  if (property.nightlyRateCents !== undefined) integer(property.nightlyRateCents, 'nightlyRateCents');
}

function validateRequest(request: LodgingStayRequest): void {
  text(request.id, 'stay id'); text(request.visitorKey, 'visitorKey'); text(request.lodgingId, 'lodgingId');
  if (!Array.isArray(request.guestIds) || request.guestIds.length === 0) throw new RangeError(`stay ${request.id} must have guests`);
  for (const guestId of request.guestIds) text(guestId, 'guestId');
  integer(request.arrivalDay, 'arrivalDay'); integer(request.departureDay, 'departureDay');
  if (request.departureDay <= request.arrivalDay) throw new RangeError(`stay ${request.id} must span at least one night`);
  if (new Set(request.guestIds).size !== request.guestIds.length) throw new RangeError(`stay ${request.id} repeats a guest`);
}

function checksumProjection(value: unknown): string { return eventCalendarChecksum(value); }

/** Build a stable multi-day lodging schedule. Conflicting requests are waitlisted. */
export function createLodgingSchedule(input: { readonly properties: readonly LodgingProperty[]; readonly stays: readonly LodgingStayRequest[] }): LodgingScheduleSnapshot {
  const properties = input.properties.slice().sort((left, right) => left.id.localeCompare(right.id));
  const propertyById = new Map<string, LodgingProperty>();
  for (const property of properties) { validateProperty(property); if (propertyById.has(property.id)) throw new RangeError(`duplicate lodging ${property.id}`); propertyById.set(property.id, property); }
  const requests = input.stays.slice().sort((left, right) => left.arrivalDay - right.arrivalDay || left.id.localeCompare(right.id));
  const requestIds = new Set<string>(); const occupancyByPropertyDay = new Map<string, number>(); const stays: LodgingStayRecord[] = [];
  for (const request of requests) {
    validateRequest(request); if (requestIds.has(request.id)) throw new RangeError(`duplicate stay ${request.id}`); requestIds.add(request.id);
    const property = propertyById.get(request.lodgingId);
    if (!property) throw new RangeError(`unknown lodging ${request.lodgingId}`);
    const current = Array.from({ length: request.departureDay - request.arrivalDay }, (_, offset) => occupancyByPropertyDay.get(`${property.id}:${request.arrivalDay + offset}`) ?? 0);
    const fits = current.every((occupancy) => occupancy + request.guestIds.length <= property.capacityGuests);
    const status: LodgingStayStatus = fits ? 'confirmed' : 'waitlisted';
    if (fits) for (let day = request.arrivalDay; day < request.departureDay; day += 1) occupancyByPropertyDay.set(`${property.id}:${day}`, (occupancyByPropertyDay.get(`${property.id}:${day}`) ?? 0) + request.guestIds.length);
    const checkInTick = fits ? request.arrivalDay * SIMULATED_SECONDS_PER_DAY + property.checkInFromTick : null;
    const checkOutTick = fits ? request.departureDay * SIMULATED_SECONDS_PER_DAY + property.checkOutFromTick : null;
    stays.push(Object.freeze({ ...request, guestIds: Object.freeze([...request.guestIds]), status, checkInTick, checkOutTick, nights: request.departureDay - request.arrivalDay }));
  }
  const base = { version: PHASE6_LODGING_VERSION, properties: Object.freeze(properties.map((property) => Object.freeze({ ...property }))), stays: Object.freeze(stays.sort((left, right) => left.id.localeCompare(right.id))) };
  return Object.freeze({ ...base, checksum: checksumProjection(base) });
}

export const scheduleLodging = createLodgingSchedule;
export const scheduleLodgingStays = createLodgingSchedule;

export function lodgingScheduleChecksum(schedule: LodgingScheduleSnapshot): string {
  return checksumProjection({ version: schedule.version, properties: schedule.properties, stays: schedule.stays });
}

export function isLodgingScheduleSnapshot(value: unknown): value is LodgingScheduleSnapshot {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<LodgingScheduleSnapshot>;
  return candidate.version === PHASE6_LODGING_VERSION && typeof candidate.checksum === 'string'
    && Array.isArray(candidate.properties) && Array.isArray(candidate.stays)
    && candidate.checksum === checksumProjection({ version: candidate.version, properties: candidate.properties, stays: candidate.stays });
}

export function encodeLodgingSchedule(schedule: LodgingScheduleSnapshot): Uint8Array { if (!isLodgingScheduleSnapshot(schedule)) throw new RangeError('invalid lodging schedule'); return encodeBinarySidecar(schedule); }
export function decodeLodgingSchedule(bytes: Uint8Array): LodgingScheduleSnapshot { const schedule = decodeBinarySidecar<LodgingScheduleSnapshot>(bytes); if (!isLodgingScheduleSnapshot(schedule)) throw new RangeError('invalid lodging schedule'); return schedule; }

/** Return the occupancy of one property on one overnight day. */
export function lodgingOccupancyAt(schedule: LodgingScheduleSnapshot, lodgingId: string, day: number): number {
  integer(day, 'day'); if (!schedule.properties.some((property) => property.id === lodgingId)) throw new RangeError(`unknown lodging ${lodgingId}`);
  return schedule.stays.filter((stay) => stay.status === 'confirmed' && stay.lodgingId === lodgingId && day >= stay.arrivalDay && day < stay.departureDay).reduce((sum, stay) => sum + stay.guestIds.length, 0);
}

/** A small schedule query used by the vehicle/access integration. */
export function lodgingCheckInOpenAt(schedule: LodgingScheduleSnapshot, lodgingId: string, tick: number): boolean {
  integer(tick, 'tick'); const property = schedule.properties.find((candidate) => candidate.id === lodgingId); if (!property) throw new RangeError(`unknown lodging ${lodgingId}`);
  const offset = tick % SIMULATED_SECONDS_PER_DAY;
  return offset >= property.checkInFromTick && offset < property.checkInUntilTick;
}

export function lodgingCheckOutOpenAt(schedule: LodgingScheduleSnapshot, lodgingId: string, tick: number): boolean {
  integer(tick, 'tick'); const property = schedule.properties.find((candidate) => candidate.id === lodgingId); if (!property) throw new RangeError(`unknown lodging ${lodgingId}`);
  const offset = tick % SIMULATED_SECONDS_PER_DAY;
  return offset >= property.checkOutFromTick && offset < property.checkOutUntilTick;
}
