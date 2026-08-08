import type { DeepReadonly } from '../types/readonly';
import type { SnowmakingNetworkState } from '../snowmakingNetwork';

interface MutableSnowmakingNetworkSnapshot extends SnowmakingNetworkState {
  revision: number;
}

export type SnowmakingNetworkSnapshot = DeepReadonly<MutableSnowmakingNetworkSnapshot>;

export interface SnowmakingNetworkChange {
  snapshot: SnowmakingNetworkSnapshot;
  changed: { nodes: boolean; pipes: boolean; guns: boolean; nextNumbers: boolean };
}

export type SnowmakingNetworkCommitResult =
  | { ok: true; revision: number; changed: boolean }
  | { ok: false; reason: 'stale' | 'settled' };

function freezeDeep(value: unknown): void {
  if (!value || typeof value !== 'object' || Object.isFrozen(value)) return;
  for (const child of Object.values(value)) freezeDeep(child);
  Object.freeze(value);
}

function ownedSnapshot(state: SnowmakingNetworkState, revision: number): MutableSnowmakingNetworkSnapshot {
  const snapshot = structuredClone({ ...state, revision });
  freezeDeep(snapshot);
  return snapshot;
}

export function snowmakingNetworkProjection(
  snapshot: SnowmakingNetworkSnapshot,
): SnowmakingNetworkState {
  return structuredClone({
    nodes: snapshot.nodes,
    pipes: snapshot.pipes,
    guns: snapshot.guns,
    nextNumbers: snapshot.nextNumbers,
  }) as SnowmakingNetworkState;
}

function changedBetween(base: SnowmakingNetworkState, next: SnowmakingNetworkState) {
  return {
    nodes: base.nodes !== next.nodes,
    pipes: base.pipes !== next.pipes,
    guns: base.guns !== next.guns,
    nextNumbers: base.nextNumbers !== next.nextNumbers,
  };
}

export class SnowmakingNetworkTransaction {
  private readonly document: SnowmakingNetworkDocument;
  private readonly base: MutableSnowmakingNetworkSnapshot;
  private next: SnowmakingNetworkState;
  private settled = false;

  constructor(document: SnowmakingNetworkDocument, base: MutableSnowmakingNetworkSnapshot) {
    this.document = document;
    this.base = base;
    this.next = { nodes: base.nodes as never, pipes: base.pipes as never, guns: base.guns as never,
      nextNumbers: base.nextNumbers as never };
  }

  get baseRevision(): number { return this.base.revision; }

  snapshot(): SnowmakingNetworkState {
    return structuredClone(this.next);
  }

  replace(next: SnowmakingNetworkState): void {
    if (this.settled) return;
    this.next = next;
  }

  commit(): SnowmakingNetworkCommitResult {
    if (this.settled) return { ok: false, reason: 'settled' };
    this.settled = true;
    return this.document.commit(this.base, this.next);
  }

  abort(): void { this.settled = true; }
}

export class SnowmakingNetworkDocument {
  private current: MutableSnowmakingNetworkSnapshot;
  private readonly onChange: (change: SnowmakingNetworkChange) => void;

  constructor(initial: SnowmakingNetworkState,
    onChange: (change: SnowmakingNetworkChange) => void = () => {}) {
    this.current = ownedSnapshot(initial, 0);
    this.onChange = onChange;
  }

  snapshot(): SnowmakingNetworkSnapshot { return this.current; }
  get revision(): number { return this.current.revision; }
  begin(): SnowmakingNetworkTransaction { return new SnowmakingNetworkTransaction(this, this.current); }

  commit(base: MutableSnowmakingNetworkSnapshot,
    next: SnowmakingNetworkState): SnowmakingNetworkCommitResult {
    if (base.revision !== this.current.revision) return { ok: false, reason: 'stale' };
    const changed = changedBetween(base, next);
    const anyChanged = Object.values(changed).some(Boolean);
    if (!anyChanged) return { ok: true, revision: this.current.revision, changed: false };
    this.current = ownedSnapshot(next, this.current.revision + 1);
    this.onChange({ snapshot: this.current, changed });
    return { ok: true, revision: this.current.revision, changed: true };
  }
}
