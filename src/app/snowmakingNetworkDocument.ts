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

const SNOWMAKING_NETWORK_PREPARATION = Symbol('snowmaking-network-preparation');

/** An opaque network snapshot transition that can be applied before it is
 * published. This is used by composite building/network commits so React and
 * map observers never see one side of an owned pump without the other. */
export interface PreparedSnowmakingNetworkCommit {
  readonly revision: number;
  readonly changed: boolean;
  readonly [SNOWMAKING_NETWORK_PREPARATION]: {
    readonly owner: SnowmakingNetworkDocument;
    readonly transaction: SnowmakingNetworkTransaction;
    readonly baseRevision: number;
    readonly snapshot: MutableSnowmakingNetworkSnapshot;
    readonly changedCollections: { nodes: boolean; pipes: boolean; guns: boolean; nextNumbers: boolean };
    applied: boolean;
    published: boolean;
    cancelled: boolean;
  };
}

export type SnowmakingNetworkPrepareResult =
  | { ok: true; prepared: PreparedSnowmakingNetworkCommit }
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
  private prepared: PreparedSnowmakingNetworkCommit | null = null;

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
    const preparation = this.prepareCommit();
    if (!preparation.ok) {
      // Preserve the historical one-shot transaction contract: a commit that
      // discovers a stale network cannot be retried against a moving graph.
      this.settled = true;
      return preparation;
    }
    if (!this.document.applyPrepared(preparation.prepared)) {
      return { ok: false, reason: this.settled ? 'settled' : 'stale' };
    }
    this.document.publishPrepared(preparation.prepared);
    return { ok: true, revision: preparation.prepared.revision,
      changed: preparation.prepared.changed };
  }

  /** Validate and own the completed edit without changing the live document. */
  prepareCommit(): SnowmakingNetworkPrepareResult {
    if (this.settled || this.prepared) return { ok: false, reason: 'settled' };
    const preparation = this.document.prepareCommit(this, this.base, this.next);
    if (preparation.ok) this.prepared = preparation.prepared;
    return preparation;
  }

  /** Called by the owning document when a preparation becomes authoritative. */
  settlePrepared(prepared: PreparedSnowmakingNetworkCommit): boolean {
    if (this.settled || this.prepared !== prepared) return false;
    this.settled = true;
    return true;
  }

  applyPrepared(prepared: PreparedSnowmakingNetworkCommit): boolean {
    return this.document.applyPrepared(prepared);
  }

  publishPrepared(prepared: PreparedSnowmakingNetworkCommit): void {
    this.document.publishPrepared(prepared);
  }

  abort(): void {
    if (this.prepared) this.prepared[SNOWMAKING_NETWORK_PREPARATION].cancelled = true;
    this.settled = true;
  }
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

  /** Validate and own the next network snapshot without notifying observers. */
  prepareCommit(
    transaction: SnowmakingNetworkTransaction,
    base: MutableSnowmakingNetworkSnapshot,
    next: SnowmakingNetworkState,
  ): SnowmakingNetworkPrepareResult {
    if (base.revision !== this.current.revision) return { ok: false, reason: 'stale' };
    const changed = changedBetween(base, next);
    const anyChanged = Object.values(changed).some(Boolean);
    const revision = this.current.revision + (anyChanged ? 1 : 0);
    const snapshot = anyChanged ? ownedSnapshot(next, revision) : this.current;
    return { ok: true, prepared: {
      revision,
      changed: anyChanged,
      [SNOWMAKING_NETWORK_PREPARATION]: {
        owner: this,
        transaction,
        baseRevision: base.revision,
        snapshot,
        changedCollections: changed,
        applied: false,
        published: false,
        cancelled: false,
      },
    } };
  }

  /** Apply an owned preparation without invoking observers. */
  applyPrepared(prepared: PreparedSnowmakingNetworkCommit): boolean {
    const state = prepared[SNOWMAKING_NETWORK_PREPARATION];
    if (state.owner !== this || state.applied || state.cancelled ||
        state.baseRevision !== this.current.revision ||
        !state.transaction.settlePrepared(prepared)) return false;
    if (prepared.changed) this.current = state.snapshot;
    state.applied = true;
    return true;
  }

  /** Publish a preparation that has already become authoritative. */
  publishPrepared(prepared: PreparedSnowmakingNetworkCommit): void {
    const state = prepared[SNOWMAKING_NETWORK_PREPARATION];
    if (state.owner !== this || !state.applied || state.published) return;
    state.published = true;
    if (prepared.changed) this.onChange({ snapshot: this.current,
      changed: state.changedCollections });
  }

  commit(base: MutableSnowmakingNetworkSnapshot,
    next: SnowmakingNetworkState): SnowmakingNetworkCommitResult {
    const transaction = new SnowmakingNetworkTransaction(this, base);
    transaction.replace(next);
    return transaction.commit();
  }
}
