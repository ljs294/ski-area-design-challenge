import type { RepresentativeGuest } from '../types/dualClock';

export interface RepresentativeEvent {
  guest: RepresentativeGuest;
  due: number;
  priority: number;
}

/** Min-heap for deterministic representative deadlines across all guests. */
export class RepresentativeEventHeap {
  private readonly items: RepresentativeEvent[] = [];

  private before(a: RepresentativeEvent, b: RepresentativeEvent): boolean {
    return a.due < b.due || (a.due === b.due && (a.priority < b.priority
      || (a.priority === b.priority && (a.guest.ordinal < b.guest.ordinal
        || (a.guest.ordinal === b.guest.ordinal && a.guest.id < b.guest.id)))));
  }

  get size(): number { return this.items.length; }

  push(item: RepresentativeEvent): void {
    this.items.push(item);
    let index = this.items.length - 1;
    while (index > 0) {
      const parent = (index - 1) >>> 1;
      if (!this.before(item, this.items[parent])) break;
      this.items[index] = this.items[parent]; index = parent;
    }
    this.items[index] = item;
  }

  pop(): RepresentativeEvent | undefined {
    const root = this.items[0];
    const last = this.items.pop();
    if (!root || !last) return root;
    if (!this.items.length) return root;
    let index = 0;
    while (true) {
      const left = index * 2 + 1, right = left + 1;
      if (left >= this.items.length) break;
      let child = left;
      if (right < this.items.length && this.before(this.items[right], this.items[left])) child = right;
      if (!this.before(this.items[child], last)) break;
      this.items[index] = this.items[child]; index = child;
    }
    this.items[index] = last;
    return root;
  }
}

/** Drain all due representative deadlines in chronological order. */
export function drainRepresentativeEvents(
  guests: RepresentativeGuest[],
  to: number,
  visible: (guest: RepresentativeGuest) => boolean,
  advance: (guest: RepresentativeGuest, due: number) => void,
): void {
  const priority = (guest: RepresentativeGuest) => guest.status === 'trail-queue' ? 1 : 0;
  const events = new RepresentativeEventHeap();
  const enqueue = (guest: RepresentativeGuest) => {
    if (guest.status !== 'departed' && visible(guest) && guest.due <= to) {
      events.push({ guest, due: guest.due, priority: priority(guest) });
    }
  };
  for (const guest of guests) enqueue(guest);
  const processedStates = new Set<string>();
  while (events.size) {
    const event = events.pop()!;
    const guest = event.guest;
    if (guest.status === 'departed' || !visible(guest) || guest.due !== event.due || guest.due > to) continue;
    const fingerprint = [guest.id, guest.status, guest.due, guest.edgeId ?? '', guest.nodeId,
      guest.trailQueueKey ?? '', guest.lastTrailId ?? '', guest.transferTrailContinuationId ?? ''].join('|');
    if (processedStates.has(fingerprint)) {
      throw new Error(`Representative guest event made no chronological progress for ${guest.id} at ${event.due}.`);
    }
    processedStates.add(fingerprint);
    advance(guest, event.due);
    enqueue(guest);
  }
}
