import { liftJunction, removeJunction, splitTrailAt } from '../topology';
import type { SavedLift } from '../types/lifts';
import type { SavedJunction, SavedNode, SavedPath } from '../types/topology';
import type { SavedTrail } from '../types/trails';

/**
 * The ski topology as one document: runs, the junctions that stitch them
 * together, connector paths, and the legacy free-standing nodes.
 *
 * These four collections describe one graph, but they used to be four
 * independent React setters. Splitting a run publishes a trail whose segments
 * name a junction that does not exist yet unless both land together, and
 * confirming a run materializes up to two junctions across an await — long
 * enough for the collections it read to have moved on. A transaction takes a
 * working copy, applies pure topology operations to it, and lands every
 * collection it touched in one publication against the revision it started
 * from.
 */
export interface TopologyState {
  trails: SavedTrail[];
  nodes: SavedNode[];
  paths: SavedPath[];
  junctions: SavedJunction[];
}

export interface TopologySnapshot extends TopologyState {
  readonly revision: number;
}

export interface TopologyChanged {
  trails: boolean;
  nodes: boolean;
  paths: boolean;
  junctions: boolean;
}

export interface TopologyChange {
  readonly snapshot: TopologySnapshot;
  readonly changed: TopologyChanged;
}

export type TopologyCommitResult =
  | { ok: true; revision: number; changed: boolean }
  | { ok: false; reason: 'stale' | 'settled' };

function stateOf(state: TopologyState): TopologyState {
  return {
    trails: state.trails,
    nodes: state.nodes,
    paths: state.paths,
    junctions: state.junctions,
  };
}

/** Every operation replaces the collection it touches, so identity is the
 *  exact test for "this edit moved that collection". */
function changedBetween(base: TopologyState, next: TopologyState): TopologyChanged {
  return {
    trails: next.trails !== base.trails,
    nodes: next.nodes !== base.nodes,
    paths: next.paths !== base.paths,
    junctions: next.junctions !== base.junctions,
  };
}

/**
 * One atomic edit in progress. Every operation replaces the collection it
 * touches rather than mutating it, so the document a consumer is holding is
 * never disturbed by an edit that is later abandoned or rejected.
 */
export class TopologyTransaction {
  private readonly document: TopologyDocument;
  private readonly base: TopologySnapshot;
  private next: TopologyState;
  private settled = false;

  constructor(document: TopologyDocument, base: TopologySnapshot) {
    this.document = document;
    this.base = base;
    this.next = stateOf(base);
  }

  /** The revision this edit was built against. */
  get baseRevision(): number {
    return this.base.revision;
  }

  /** Which collections this edit has moved so far. An operation that resolved
   *  to something already there leaves every one of them false. */
  get changed(): TopologyChanged {
    return changedBetween(this.base, this.next);
  }

  /** Split a run at `point`, materializing its junction — or reusing the one
   *  already there, in which case no collection moves. */
  splitTrail(
    trailId: string,
    point: [number, number],
    idFactory: () => string,
  ): SavedJunction | null {
    const edit = splitTrailAt(this.next.trails, this.next.junctions, trailId, point, idFactory);
    if (!edit) return null;
    this.next.trails = edit.trails;
    this.next.junctions = edit.junctions;
    return edit.junction;
  }

  /** Materialize (or reuse) the junction standing at a lift terminal. */
  liftTerminalJunction(
    lifts: SavedLift[],
    liftId: string,
    end: 'top' | 'base',
    point: [number, number],
    idFactory: () => string,
  ): SavedJunction {
    const edit = liftJunction(lifts, this.next.junctions, liftId, end, point, idFactory);
    this.next.junctions = edit.junctions;
    return edit.junction;
  }

  /** Dissolve a junction, merging the two segments it separated. */
  removeJunction(junctionId: string): boolean {
    const edit = removeJunction(this.next.trails, this.next.junctions, this.next.paths, junctionId);
    if (!edit) return false;
    this.next.trails = edit.trails;
    this.next.junctions = edit.junctions;
    return true;
  }

  addTrail(trail: SavedTrail): void {
    this.next.trails = [...this.next.trails, trail];
  }

  patchTrail(id: string, patch: Partial<SavedTrail>): void {
    this.next.trails = this.next.trails.map((trail) =>
      trail.id === id ? { ...trail, ...patch } : trail);
  }

  /** Rewrite every run — the backfill path, which resolves elevations that a
   *  save made offline could not. */
  mapTrails(project: (trail: SavedTrail) => SavedTrail): void {
    this.next.trails = this.next.trails.map(project);
  }

  /**
   * Remove a run along with every junction nothing references any more. Lift
   * terminals survive: they belong to the lift, not to the run that reached it.
   */
  removeTrail(id: string): boolean {
    const remaining = this.next.trails.filter((trail) => trail.id !== id);
    if (remaining.length === this.next.trails.length) return false;
    const referenced = new Set(remaining.flatMap((trail) => trail.parts.flatMap((part) =>
      (part.segments ?? []).flatMap((segment) => [segment.fromJunctionId, segment.toJunctionId]))));
    for (const path of this.next.paths) {
      if (path.fromJunctionId) referenced.add(path.fromJunctionId);
      if (path.toJunctionId) referenced.add(path.toJunctionId);
    }
    this.next.junctions = this.next.junctions.filter((junction) =>
      junction.liftTerminal || referenced.has(junction.id));
    this.next.trails = remaining;
    return true;
  }

  removeNode(id: string): void {
    this.next.nodes = this.next.nodes.filter((node) => node.id !== id);
  }

  addPath(path: SavedPath): void {
    this.next.paths = [...this.next.paths, path];
  }

  patchPath(id: string, patch: Partial<SavedPath>): void {
    this.next.paths = this.next.paths.map((path) => (path.id === id ? { ...path, ...patch } : path));
  }

  removePath(id: string): void {
    this.next.paths = this.next.paths.filter((path) => path.id !== id);
  }

  /** Publish every collection this edit touched, or reject the whole edit
   *  because the document moved underneath it. */
  commit(): TopologyCommitResult {
    if (this.settled) return { ok: false, reason: 'settled' };
    this.settled = true;
    return this.document.publish(this.base, this.next);
  }

  /** Abandon the edit. Nothing was published, so there is nothing to undo. */
  abort(): void {
    this.settled = true;
  }
}

export class TopologyDocument {
  private current: TopologySnapshot;
  private readonly onChange: (change: TopologyChange) => void;

  /**
   * The clean load: hydrated, sanitized collections seeded at revision zero.
   * There is no runtime replacement command because opening another resort
   * remounts the session rather than swapping a document underneath it.
   */
  constructor(initial: TopologyState, onChange: (change: TopologyChange) => void = () => {}) {
    this.current = Object.freeze({ ...stateOf(initial), revision: 0 });
    this.onChange = onChange;
  }

  snapshot(): TopologySnapshot {
    return this.current;
  }

  get revision(): number {
    return this.current.revision;
  }

  /** Begin an atomic edit against the current revision. */
  begin(): TopologyTransaction {
    return new TopologyTransaction(this, this.current);
  }

  /**
   * Land a transaction. Called by `TopologyTransaction.commit`; nothing else
   * should reach past a transaction to publish.
   */
  publish(base: TopologySnapshot, next: TopologyState): TopologyCommitResult {
    if (base.revision !== this.current.revision) return { ok: false, reason: 'stale' };
    const changed = changedBetween(base, next);
    if (!changed.trails && !changed.nodes && !changed.paths && !changed.junctions) {
      return { ok: true, revision: this.current.revision, changed: false };
    }
    this.current = Object.freeze({ ...next, revision: this.current.revision + 1 });
    this.onChange({ snapshot: this.current, changed });
    return { ok: true, revision: this.current.revision, changed: true };
  }
}
