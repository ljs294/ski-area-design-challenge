/** Sidecar-safe repeat-visitor memory for multi-day access and lodging. */

import { eventCalendarChecksum } from './eventCalendar.ts';
import { encodeBinarySidecar, decodeBinarySidecar } from './binaryCodec.ts';

export const PHASE6_REPEAT_MEMORY_VERSION = 1 as const;
export type Phase6RepeatMemoryVersion = typeof PHASE6_REPEAT_MEMORY_VERSION;

export interface RepeatVisitorRecord {
  readonly visitorKey: string;
  readonly visitCount: number;
  readonly firstVisitDay: number;
  readonly lastVisitDay: number;
  readonly preferredPortalId: string | null;
  readonly lastLodgingId: string | null;
  readonly satisfactionTotal: number;
}

export interface RepeatVisitorVisit {
  readonly visitId: string;
  readonly visitorKey: string;
  readonly day: number;
  readonly portalId?: string;
  readonly lodgingId?: string;
  readonly satisfaction?: number;
}

export interface RepeatVisitorMemorySnapshot {
  readonly version: Phase6RepeatMemoryVersion;
  readonly revision: number;
  readonly records: readonly RepeatVisitorRecord[];
  readonly appliedVisitIds: readonly string[];
  readonly checksum: string;
}

function integer(value: number, label: string, minimum = 0): void {
  if (!Number.isSafeInteger(value) || value < minimum) throw new RangeError(`${label} must be a safe integer >= ${minimum}`);
}

function text(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be non-empty`);
}

function finiteScore(value: number, label: string): void {
  if (!Number.isFinite(value) || value < 0 || value > 1) throw new RangeError(`${label} must be in [0,1]`);
}

function checksumProjection(value: unknown): string { return eventCalendarChecksum(value); }

function baseSnapshot(revision: number, records: readonly RepeatVisitorRecord[], appliedVisitIds: readonly string[]): RepeatVisitorMemorySnapshot {
  integer(revision, 'memory revision');
  const ordered = records.slice().sort((left, right) => left.visitorKey.localeCompare(right.visitorKey));
  const base = { version: PHASE6_REPEAT_MEMORY_VERSION, revision, records: Object.freeze(ordered.map((record) => Object.freeze({ ...record }))),
    appliedVisitIds: Object.freeze([...appliedVisitIds].sort()) };
  return Object.freeze({ ...base, checksum: checksumProjection(base) });
}

function validateRecord(record: RepeatVisitorRecord): void {
  text(record.visitorKey, 'visitorKey'); integer(record.visitCount, 'visitCount', 1); integer(record.firstVisitDay, 'firstVisitDay'); integer(record.lastVisitDay, 'lastVisitDay');
  if (record.lastVisitDay < record.firstVisitDay) throw new RangeError('lastVisitDay cannot precede firstVisitDay');
  if (record.preferredPortalId !== null) text(record.preferredPortalId, 'preferredPortalId');
  if (record.lastLodgingId !== null) text(record.lastLodgingId, 'lastLodgingId');
  if (!Number.isFinite(record.satisfactionTotal) || record.satisfactionTotal < 0) throw new RangeError('satisfactionTotal must be finite and non-negative');
}

function validateVisit(visit: RepeatVisitorVisit): void {
  text(visit.visitId, 'visitId'); text(visit.visitorKey, 'visitorKey'); integer(visit.day, 'visit day');
  if (visit.portalId !== undefined) text(visit.portalId, 'portalId'); if (visit.lodgingId !== undefined) text(visit.lodgingId, 'lodgingId');
  if (visit.satisfaction !== undefined) finiteScore(visit.satisfaction, 'satisfaction');
}

export function createRepeatVisitorMemorySnapshot(records: readonly RepeatVisitorRecord[] = [], revision = 0,
  appliedVisitIds: readonly string[] = []): RepeatVisitorMemorySnapshot {
  const seen = new Set<string>(); for (const record of records) { validateRecord(record); if (seen.has(record.visitorKey)) throw new RangeError(`duplicate repeat visitor ${record.visitorKey}`); seen.add(record.visitorKey); }
  return baseSnapshot(revision, records, appliedVisitIds);
}

export const createRepeatVisitorSnapshot = createRepeatVisitorMemorySnapshot;

export function recordRepeatVisitorVisits(snapshot: RepeatVisitorMemorySnapshot, visits: readonly RepeatVisitorVisit[]): RepeatVisitorMemorySnapshot {
  if (!isRepeatVisitorMemorySnapshot(snapshot)) throw new RangeError('invalid repeat visitor memory');
  const ordered = visits.slice().sort((left, right) => left.day - right.day || left.visitId.localeCompare(right.visitId));
  const byKey = new Map(snapshot.records.map((record) => [record.visitorKey, record]));
  const visitIds = new Set(snapshot.appliedVisitIds); let applied = 0;
  for (const visit of ordered) {
    validateVisit(visit); if (visitIds.has(visit.visitId)) continue; visitIds.add(visit.visitId); applied += 1;
    const prior = byKey.get(visit.visitorKey);
    const satisfaction = visit.satisfaction ?? 0;
    byKey.set(visit.visitorKey, prior ? {
      visitorKey: prior.visitorKey, visitCount: prior.visitCount + 1, firstVisitDay: Math.min(prior.firstVisitDay, visit.day), lastVisitDay: Math.max(prior.lastVisitDay, visit.day),
      preferredPortalId: visit.portalId ?? prior.preferredPortalId, lastLodgingId: visit.lodgingId ?? prior.lastLodgingId, satisfactionTotal: prior.satisfactionTotal + satisfaction,
    } : { visitorKey: visit.visitorKey, visitCount: 1, firstVisitDay: visit.day, lastVisitDay: visit.day, preferredPortalId: visit.portalId ?? null, lastLodgingId: visit.lodgingId ?? null, satisfactionTotal: satisfaction });
  }
  if (applied === 0) return snapshot;
  return baseSnapshot(snapshot.revision + 1, [...byKey.values()], [...visitIds]);
}

export const applyRepeatVisitorVisits = recordRepeatVisitorVisits;
export const updateRepeatVisitorMemory = recordRepeatVisitorVisits;

export function repeatVisitorMemoryFor(snapshot: RepeatVisitorMemorySnapshot, visitorKey: string): RepeatVisitorRecord | undefined {
  if (!isRepeatVisitorMemorySnapshot(snapshot)) throw new RangeError('invalid repeat visitor memory'); text(visitorKey, 'visitorKey'); return snapshot.records.find((record) => record.visitorKey === visitorKey);
}

export function repeatVisitorAverageSatisfaction(record: RepeatVisitorRecord): number {
  validateRecord(record); return record.satisfactionTotal / record.visitCount;
}

export function repeatVisitorMemoryChecksum(snapshot: RepeatVisitorMemorySnapshot): string {
  return checksumProjection({ version: snapshot.version, revision: snapshot.revision, records: snapshot.records,
    appliedVisitIds: snapshot.appliedVisitIds });
}

export function isRepeatVisitorMemorySnapshot(value: unknown): value is RepeatVisitorMemorySnapshot {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<RepeatVisitorMemorySnapshot>;
  const revision = candidate.revision;
  if (candidate.version !== PHASE6_REPEAT_MEMORY_VERSION || !Number.isSafeInteger(revision) || (revision as number) < 0
    || !Array.isArray(candidate.records) || !Array.isArray(candidate.appliedVisitIds) || typeof candidate.checksum !== 'string') return false;
  try {
    const keys = candidate.records.map((record) => record.visitorKey);
    if (keys.some((key) => typeof key !== 'string') || keys.some((key, index) => index > 0 && key <= keys[index - 1]!)) return false;
    candidate.records.forEach((record) => validateRecord(record));
  } catch { return false; }
  if (candidate.appliedVisitIds.some((id) => typeof id !== 'string' || id.length === 0)
    || candidate.appliedVisitIds.length !== new Set(candidate.appliedVisitIds).size) return false;
  return candidate.checksum === checksumProjection({ version: candidate.version, revision, records: candidate.records,
    appliedVisitIds: candidate.appliedVisitIds });
}

export function encodeRepeatVisitorMemory(snapshot: RepeatVisitorMemorySnapshot): Uint8Array { if (!isRepeatVisitorMemorySnapshot(snapshot)) throw new RangeError('invalid repeat visitor memory'); return encodeBinarySidecar(snapshot); }
export function decodeRepeatVisitorMemory(bytes: Uint8Array): RepeatVisitorMemorySnapshot { const snapshot = decodeBinarySidecar<RepeatVisitorMemorySnapshot>(bytes); if (!isRepeatVisitorMemorySnapshot(snapshot)) throw new RangeError('invalid repeat visitor memory'); return snapshot; }
