import type { DeepReadonly } from './types/readonly';
import type { SavedBuilding } from './types/buildings';

export interface BuildingState {
  buildings: SavedBuilding[];
}

interface MutableBuildingSnapshot extends BuildingState {
  readonly revision: number;
}

export type BuildingSnapshot = DeepReadonly<MutableBuildingSnapshot>;

export function buildingProjection(snapshot: BuildingSnapshot): BuildingState {
  return structuredClone({ buildings: snapshot.buildings }) as BuildingState;
}

export interface BuildingChange {
  readonly snapshot: BuildingSnapshot;
}

export type BuildingCommitResult =
  | { ok: true; revision: number; changed: boolean }
  | { ok: false; reason: 'stale' | 'settled' };

const BUILDING_PREPARATION = Symbol('building-preparation');

export interface PreparedBuildingCommit {
  readonly revision: number;
  readonly changed: boolean;
  readonly [BUILDING_PREPARATION]: {
    readonly owner: BuildingDocument;
    readonly transaction: BuildingTransaction;
    readonly baseRevision: number;
    readonly snapshot: MutableBuildingSnapshot;
    applied: boolean;
    published: boolean;
    cancelled: boolean;
  };
}

export type BuildingPrepareResult =
  | { ok: true; prepared: PreparedBuildingCommit }
  | { ok: false; reason: 'stale' | 'settled' };

function ownSnapshot(state: BuildingState, revision: number): MutableBuildingSnapshot {
  const owned = structuredClone({ buildings: state.buildings, revision }) as MutableBuildingSnapshot;
  const freeze = (value: unknown): void => {
    if (!value || typeof value !== 'object' || Object.isFrozen(value)) return;
    for (const child of Object.values(value)) freeze(child);
    Object.freeze(value);
  };
  freeze(owned);
  return owned;
}

function sameState(a: BuildingState, b: BuildingState): boolean {
  return a.buildings === b.buildings;
}

function stateOf(snapshot: BuildingSnapshot): BuildingState {
  return { buildings: snapshot.buildings as SavedBuilding[] };
}

/** One revisioned edit in progress against a BuildingDocument snapshot. */
export class BuildingTransaction {
  private readonly document: BuildingDocument;
  private readonly base: MutableBuildingSnapshot;
  private next: BuildingState;
  private settled = false;
  private prepared: PreparedBuildingCommit | null = null;

  constructor(document: BuildingDocument, base: MutableBuildingSnapshot) {
    this.document = document;
    this.base = base;
    this.next = stateOf(base);
  }

  get baseRevision(): number {
    return this.base.revision;
  }

  get buildings(): readonly SavedBuilding[] {
    return this.next.buildings;
  }

  addBuilding(building: SavedBuilding): boolean {
    if (this.next.buildings.some((candidate) => candidate.id === building.id)) return false;
    this.next.buildings = [...this.next.buildings, structuredClone(building)];
    return true;
  }

  renameBuilding(id: string, name: string): boolean {
    const trimmed = name.trim();
    if (!trimmed) return false;
    const index = this.next.buildings.findIndex((building) => building.id === id);
    if (index < 0 || this.next.buildings[index]?.name === trimmed) return false;
    this.next.buildings = this.next.buildings.map((building, position) =>
      position === index ? { ...building, name: trimmed } : building);
    return true;
  }

  patchBuilding(id: string, patch: Pick<Partial<SavedBuilding>, 'name'>): boolean {
    return patch.name == null ? false : this.renameBuilding(id, patch.name);
  }

  removeBuilding(id: string): boolean {
    const remaining = this.next.buildings.filter((building) => building.id !== id);
    if (remaining.length === this.next.buildings.length) return false;
    this.next.buildings = remaining;
    return true;
  }

  commit(): BuildingCommitResult {
    const preparation = this.prepareCommit();
    if (!preparation.ok) return preparation;
    if (!this.document.applyPrepared(preparation.prepared)) {
      return { ok: false, reason: this.settled ? 'settled' : 'stale' };
    }
    this.document.publishPrepared(preparation.prepared);
    return {
      ok: true,
      revision: preparation.prepared.revision,
      changed: preparation.prepared.changed,
    };
  }

  prepareCommit(): BuildingPrepareResult {
    if (this.settled || this.prepared) return { ok: false, reason: 'settled' };
    const preparation = this.document.prepareCommit(this, this.base, this.next);
    if (preparation.ok) this.prepared = preparation.prepared;
    return preparation;
  }

  settlePrepared(prepared: PreparedBuildingCommit): boolean {
    if (this.settled || this.prepared !== prepared) return false;
    this.settled = true;
    return true;
  }

  applyPrepared(prepared: PreparedBuildingCommit): boolean {
    return this.document.applyPrepared(prepared);
  }

  publishPrepared(prepared: PreparedBuildingCommit): void {
    this.document.publishPrepared(prepared);
  }

  abort(): void {
    if (this.prepared) this.prepared[BUILDING_PREPARATION].cancelled = true;
    this.settled = true;
  }
}

/** Authoritative collection of player buildings with two-phase publication. */
export class BuildingDocument {
  private current: MutableBuildingSnapshot;
  private readonly onChange: (change: BuildingChange) => void;

  constructor(initial: BuildingState | SavedBuilding[] = { buildings: [] },
    onChange: (change: BuildingChange) => void = () => {}) {
    const state: BuildingState = Array.isArray(initial) ? { buildings: initial } : initial;
    this.current = ownSnapshot(state, 0);
    this.onChange = onChange;
  }

  snapshot(): BuildingSnapshot {
    return this.current;
  }

  get revision(): number {
    return this.current.revision;
  }

  begin(): BuildingTransaction {
    return new BuildingTransaction(this, this.current);
  }

  prepareCommit(
    transaction: BuildingTransaction,
    base: MutableBuildingSnapshot,
    next: BuildingState,
  ): BuildingPrepareResult {
    if (base.revision !== this.current.revision) return { ok: false, reason: 'stale' };
    const changed = !sameState(base, next);
    const revision = this.current.revision + (changed ? 1 : 0);
    const snapshot = changed ? ownSnapshot(next, revision) : this.current;
    return { ok: true, prepared: {
      revision,
      changed,
      [BUILDING_PREPARATION]: {
        owner: this,
        transaction,
        baseRevision: base.revision,
        snapshot,
        applied: false,
        published: false,
        cancelled: false,
      },
    } };
  }

  applyPrepared(prepared: PreparedBuildingCommit): boolean {
    const state = prepared[BUILDING_PREPARATION];
    if (state.owner !== this || state.applied || state.cancelled ||
        state.baseRevision !== this.current.revision ||
        !state.transaction.settlePrepared(prepared)) return false;
    if (prepared.changed) this.current = state.snapshot;
    state.applied = true;
    return true;
  }

  publishPrepared(prepared: PreparedBuildingCommit): void {
    const state = prepared[BUILDING_PREPARATION];
    if (state.owner !== this || !state.applied || state.published) return;
    state.published = true;
    if (prepared.changed) this.onChange({ snapshot: state.snapshot });
  }
}
