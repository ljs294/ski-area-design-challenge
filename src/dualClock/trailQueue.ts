import type { NetworkEdge } from '../network';
import type { RepresentativeGuest, TrailQueueEntry, TrailQueueState } from '../types/dualClock';

export function trailEntrySpacing(config: { guestMovement?: { trailEntrySpacingMicroSeconds: number } }): number {
  return config.guestMovement?.trailEntrySpacingMicroSeconds ?? 2;
}

export function hasTrailEntrance(
  edges: ReadonlyMap<string, NetworkEdge>, queue: Pick<TrailQueueState, 'trailId' | 'entryNode'>,
  isEligible: (edge: NetworkEdge) => boolean,
): boolean {
  for (const edge of edges.values()) {
    if (edge.kind === 'trail' && edge.trailId === queue.trailId && edge.from === queue.entryNode && isEligible(edge)) return true;
  }
  return false;
}

export function trailQueueKey(trailId: string, entryNode: string): string {
  return `${trailId}|${entryNode}`;
}

function sortEntries(entries: TrailQueueEntry[]): void {
  entries.sort((a, b) => a.arrivalMicroSecond - b.arrivalMicroSecond || a.ordinal - b.ordinal || a.guestId.localeCompare(b.guestId));
}

function recalculateTrailQueue(queue: TrailQueueState, spacing: number, notBefore?: number): void {
  const lastActual = queue.lastReleaseMicroSecond === undefined ? -Infinity : queue.lastReleaseMicroSecond + spacing;
  if (notBefore !== undefined) {
    sortEntries(queue.entries);
    let cursor = Math.max(lastActual, notBefore);
    for (const entry of queue.entries) {
      entry.releaseMicroSecond = Math.max(entry.arrivalMicroSecond, cursor);
      cursor = entry.releaseMicroSecond + spacing;
    }
  }
  const lastReservation = queue.entries.reduce((latest, entry) => Math.max(latest, entry.releaseMicroSecond + spacing), -Infinity);
  queue.nextReleaseMicroSecond = Math.max(lastActual, lastReservation, 0);
}

export interface TrailQueueReconcileCallbacks {
  hasEntrance: (queue: Pick<TrailQueueState, 'trailId' | 'entryNode'>) => boolean;
  isEligible: (guest: RepresentativeGuest, queue: Pick<TrailQueueState, 'trailId' | 'entryNode'>) => boolean;
}

/** Remove departed, retired, and topology-invalid waiters from every queue. */
export function reconcileTrailQueues(
  queues: Record<string, TrailQueueState>, guests: RepresentativeGuest[], now: number,
  spacing: number, callbacks: TrailQueueReconcileCallbacks, expireCooldown = true,
): void {
  const guestsById = new Map(guests.map(guest => [guest.id, guest]));
  for (const [key, queue] of Object.entries(queues)) {
    const entrance = callbacks.hasEntrance(queue);
    const retained: TrailQueueEntry[] = [];
    const originalCount = queue.entries.length;
    for (const entry of queue.entries) {
      const guest = guestsById.get(entry.guestId);
      if (guest && guest.status === 'trail-queue' && guest.trailQueueKey === key && callbacks.isEligible(guest, queue)) {
        retained.push(entry);
        continue;
      }
      if (guest && guest.status === 'departed' && guest.trailQueueKey === key) {
        guest.trailQueueKey = undefined;
        guest.trailQueueArrivalMicroSecond = undefined;
        guest.trailQueueReleaseMicroSecond = undefined;
        guest.edgeId = null;
      } else if (guest && guest.status !== 'departed' && guest.trailQueueKey === key) {
        guest.trailQueueKey = undefined;
        guest.trailQueueArrivalMicroSecond = undefined;
        guest.trailQueueReleaseMicroSecond = undefined;
        guest.edgeId = null;
        guest.nodeId = queue.entryNode;
        guest.status = 'walking';
        guest.started = now;
        guest.due = now;
        guest.nextPlan = 'Wait for a suitable route';
      }
    }
    if (!retained.length) {
      queue.entries = [];
      recalculateTrailQueue(queue, spacing);
      if (!entrance || queue.lastReleaseMicroSecond === undefined || (expireCooldown && queue.nextReleaseMicroSecond <= now)) delete queues[key];
      continue;
    }
    sortEntries(retained);
    queue.entries = retained;
    recalculateTrailQueue(queue, spacing, retained.length < originalCount ? now : undefined);
    for (const entry of queue.entries) {
      const guest = guestsById.get(entry.guestId);
      if (guest) {
        guest.trailQueueReleaseMicroSecond = entry.releaseMicroSecond;
        guest.due = entry.releaseMicroSecond;
      }
    }
  }
}

export function removeTrailQueueEntry(
  queues: Record<string, TrailQueueState> | undefined,
  guest: RepresentativeGuest, at: number, spacing: number, actualRelease: boolean,
): void {
  const key = guest.trailQueueKey;
  if (key) {
    const queue = queues?.[key];
    if (queue) {
      const removed = queue.entries.find(entry => entry.guestId === guest.id);
      queue.entries = queue.entries.filter(entry => entry.guestId !== guest.id);
      if (actualRelease && removed) queue.lastReleaseMicroSecond = Math.max(queue.lastReleaseMicroSecond ?? -Infinity, at);
      recalculateTrailQueue(queue, spacing);
      if (!queue.entries.length && (queue.lastReleaseMicroSecond === undefined || queue.nextReleaseMicroSecond <= at)) delete queues![key];
    }
  }
  guest.trailQueueKey = undefined;
  guest.trailQueueArrivalMicroSecond = undefined;
  guest.trailQueueReleaseMicroSecond = undefined;
}

export function releaseTrailGuest(
  guest: RepresentativeGuest, queues: Record<string, TrailQueueState> | undefined,
  edges: ReadonlyMap<string, NetworkEdge>, at: number, spacing: number,
  isEligible: (edge: NetworkEdge, queue: TrailQueueState) => boolean,
  duration: (guest: RepresentativeGuest, edge: NetworkEdge) => number,
): void {
  const queue = guest.trailQueueKey ? queues?.[guest.trailQueueKey] : undefined;
  const edge = queue && guest.edgeId ? edges.get(guest.edgeId) : undefined;
  const eligible = !!queue && !!edge && edge.kind === 'trail' && edge.trailId === queue.trailId
    && edge.from === queue.entryNode && isEligible(edge, queue);
  removeTrailQueueEntry(queues, guest, at, spacing, eligible);
  if (!eligible || !edge || edge.kind !== 'trail') {
    guest.edgeId = null;
    guest.nodeId = queue?.entryNode ?? guest.nodeId;
    guest.status = 'walking';
    guest.started = at;
    guest.due = at;
    guest.nextPlan = 'Wait for a suitable route';
    return;
  }
  guest.edgeId = edge.id;
  guest.lastTrailId = edge.trailId;
  guest.started = at;
  guest.due = at + duration(guest, edge);
  guest.status = 'skiing';
  guest.nextPlan = 'Reach the next junction';
}

/** Reserve an entrance slot and leave the activity message to the engine. */
export function enqueueTrailGuest(
  queues: Record<string, TrailQueueState>, guest: RepresentativeGuest, edge: NetworkEdge, arrival: number, spacing: number,
): string | undefined {
  if (edge.kind !== 'trail') return undefined;
  const key = trailQueueKey(edge.trailId, edge.from);
  const queue = queues[key] ??= { trailId: edge.trailId, entryNode: edge.from, nextReleaseMicroSecond: arrival, entries: [] };
  if (!queue.entries.length) {
    const cooldown = queue.lastReleaseMicroSecond === undefined ? arrival : queue.lastReleaseMicroSecond + spacing;
    queue.nextReleaseMicroSecond = Math.max(arrival, cooldown);
  }
  const release = Math.max(arrival, queue.nextReleaseMicroSecond);
  queue.entries.push({ guestId: guest.id, arrivalMicroSecond: arrival, ordinal: guest.ordinal, releaseMicroSecond: release });
  sortEntries(queue.entries);
  queue.nextReleaseMicroSecond = release + spacing;
  guest.edgeId = edge.id;
  guest.trailQueueKey = key;
  guest.trailQueueArrivalMicroSecond = arrival;
  guest.trailQueueReleaseMicroSecond = release;
  guest.started = arrival;
  guest.due = release;
  guest.status = 'trail-queue';
  guest.nextPlan = `Enter ${edge.trailName}`;
  return edge.trailName;
}
