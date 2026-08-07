import type { TerrainRecord } from '../types/terrain';
import {
  ConstructionLock,
  type ConstructionActivity,
  type ConstructionOutcome,
} from './constructionLock';
import type { DeepReadonly } from '../types/readonly';
import type { VectorFeatureSet } from '../types/vectorFeatures';

/**
 * The committed terrain package, and the single path every change to it takes.
 *
 * A terrain edit is not one write: it moves the record, the height/display
 * caches, the tile protocols, the map sources, and the unsaved-terrain flag.
 * Those used to be six statements repeated at every build site, which is how an
 * asynchronous cover edit could rebuild a package from the record it captured
 * before a grade landed and quietly discard the grade. Every change now names
 * the revision it expects, and a change built on a superseded revision is
 * rejected outright rather than published over the newer one.
 */
export type TerrainEditKind = 'elevation' | 'cover';

export type TerrainRecordView = DeepReadonly<TerrainRecord>;

export interface TerrainSnapshot {
  readonly record: TerrainRecordView | null;
  readonly revision: number;
}

export interface TerrainPublication {
  readonly record: TerrainRecord;
  readonly revision: number;
  /** The edit this publication carries; `null` for a clean load or replacement. */
  readonly edit: TerrainEditKind | null;
  /** Context was already persisted independently; retain any unrelated dirty flag. */
  readonly preserveDirty?: boolean;
}

export interface TerrainCommitRequest {
  readonly expectedRevision: number;
  readonly record: TerrainRecord;
  readonly kind: TerrainEditKind;
}

export type TerrainCommitResult =
  | { ok: true; revision: number }
  | { ok: false; reason: 'stale' };

const TERRAIN_PREPARATION = Symbol('terrain-preparation');

/** Opaque two-phase commit owned by its TerrainDocument. Applying changes the
 * authoritative snapshot; publishing fans that already-committed snapshot out
 * to caches, protocols, React, and map sources. */
export interface PreparedTerrainCommit {
  readonly revision: number;
  readonly [TERRAIN_PREPARATION]: {
    readonly owner: TerrainDocument;
    readonly expectedRevision: number;
    readonly publication: TerrainPublication;
    applied: boolean;
    published: boolean;
  };
}

export type TerrainPrepareResult =
  | { ok: true; prepared: PreparedTerrainCommit }
  | { ok: false; reason: 'stale' };

/**
 * Everything one terrain change has to touch, in the order a coherent
 * publication needs it: the caches feed the protocols and the map sources, and
 * the React projection lands carrying the dirty flag the edit implies.
 */
export interface TerrainDocumentPorts {
  cacheDisplayAssets(record: TerrainRecord): void;
  activateProtocols(record: TerrainRecord): void;
  publishState(publication: TerrainPublication): void;
  refreshSources(publication: TerrainPublication): void;
  /** The pending edits reached disk; the document is clean again. */
  publishPersisted(): void;
  publishConstruction(activity: ConstructionActivity | null): void;
}

/**
 * Token issuer for asynchronous preview work — the terrain-grade preview shared
 * by the road and trail tools. Claiming supersedes whatever was outstanding, so
 * a late worker response can always tell that it no longer owns the preview.
 */
export class PreviewOwnership {
  private token = 0;

  claim(): number {
    return ++this.token;
  }

  get current(): number {
    return this.token;
  }

  isCurrent(token: number): boolean {
    return token === this.token;
  }

  invalidate(): void {
    this.token++;
  }
}

export class TerrainDocument {
  private current: { readonly record: TerrainRecord | null; readonly revision: number } =
    Object.freeze({ record: null, revision: 0 });
  /** Bumped on disposal so queued cover work can tell the session ended. */
  private generation = 0;
  private coverQueue: Promise<void> = Promise.resolve();
  private readonly ports: TerrainDocumentPorts;
  private readonly lock: ConstructionLock;
  readonly preview = new PreviewOwnership();

  constructor(ports: TerrainDocumentPorts) {
    this.ports = ports;
    this.lock = new ConstructionLock((activity) => ports.publishConstruction(activity));
  }

  snapshot(): TerrainSnapshot {
    return this.current;
  }

  get record(): TerrainRecordView | null {
    return this.current.record;
  }

  get revision(): number {
    return this.current.revision;
  }

  /**
   * Initial load or package replacement. This is not an edit: the package it
   * publishes is exactly what is already on disk, so the document lands clean
   * rather than carrying a dirty flag the player never earned.
   */
  replace(record: TerrainRecord): TerrainSnapshot {
    this.publish(record, null);
    return this.current;
  }

  /** Publish metadata-only context after its independent storage transaction.
   * The latest authoritative terrain is extended, so a provider request that
   * overlapped construction cannot replace newer elevation or cover data. */
  publishMapContext(vectorFeatures: VectorFeatureSet, updatedAt: string): TerrainSnapshot {
    if (!this.current.record) return this.current;
    this.publish({ ...this.current.record, vectorFeatures, updatedAt }, null, true);
    return this.current;
  }

  /** Publish an edited package, or reject it for having been built on a
   *  revision that has since been superseded. */
  commit(request: TerrainCommitRequest): TerrainCommitResult {
    const preparation = this.prepareCommit(request);
    if (!preparation.ok) return preparation;
    if (!this.applyPrepared(preparation.prepared)) return { ok: false, reason: 'stale' };
    this.publishPrepared(preparation.prepared);
    return { ok: true, revision: preparation.prepared.revision };
  }

  /** Validate and own the next record without changing the live document. */
  prepareCommit(request: TerrainCommitRequest): TerrainPrepareResult {
    if (request.expectedRevision !== this.current.revision) return { ok: false, reason: 'stale' };
    const revision = this.current.revision + 1;
    const record = Object.freeze({ ...request.record });
    const publication = Object.freeze({ record, revision, edit: request.kind });
    return { ok: true, prepared: {
      revision,
      [TERRAIN_PREPARATION]: {
        owner: this,
        expectedRevision: request.expectedRevision,
        publication,
        applied: false,
        published: false,
      },
    } };
  }

  /** Apply a prepared record without invoking observers. Used by the atomic
   * terrain/topology coordinator so both documents move before either emits. */
  applyPrepared(prepared: PreparedTerrainCommit): boolean {
    const state = prepared[TERRAIN_PREPARATION];
    if (state.owner !== this || state.applied ||
        state.expectedRevision !== this.current.revision) return false;
    this.current = Object.freeze({ record: state.publication.record,
      revision: state.publication.revision });
    state.applied = true;
    return true;
  }

  /** Publish a preparation that has already become authoritative. */
  publishPrepared(prepared: PreparedTerrainCommit): void {
    const state = prepared[TERRAIN_PREPARATION];
    if (state.owner !== this || !state.applied || state.published) return;
    state.published = true;
    this.publishToPorts(state.publication);
  }

  /** Clear the dirty flag after a write, unless something was built while the
   *  write was in flight — that edit is not covered by it. */
  markPersisted(expectedRevision: number): boolean {
    if (expectedRevision !== this.current.revision) return false;
    this.ports.publishPersisted();
    return true;
  }

  get constructionActivity(): ConstructionActivity | null {
    return this.lock.active;
  }

  runConstruction<T>(
    activity: ConstructionActivity,
    operation: () => Promise<T>,
  ): Promise<ConstructionOutcome<T>> {
    return this.lock.run(activity, operation);
  }

  /**
   * Serialize cover edits. Each task runs against the snapshot current when it
   * starts, never the one current when it was queued, so a queued edit is
   * always built on top of every terrain change that landed ahead of it.
   */
  async runCoverEdit(task: (snapshot: TerrainSnapshot) => Promise<void>): Promise<void> {
    const generation = this.generation;
    const previous = this.coverQueue;
    let done!: () => void;
    this.coverQueue = new Promise<void>((resolve) => {
      done = resolve;
    });
    try {
      await previous;
      if (generation !== this.generation) return;
      await task(this.current);
    } finally {
      done();
    }
  }

  /** Invalidate everything in flight on teardown. The document stays usable so
   *  a StrictMode remount does not retire it. */
  dispose(): void {
    this.generation++;
    this.preview.invalidate();
    this.lock.dispose();
  }

  private publish(
    record: TerrainRecord,
    edit: TerrainEditKind | null,
    preserveDirty = false,
  ): void {
    const revision = this.current.revision + 1;
    // Copy and freeze the record shell once at publication. Large elevation,
    // cover, contour, and imagery payloads are deliberately not deep-cloned;
    // the public snapshot projects them recursively read-only and every edit
    // creates replacement payloads before it reaches this ownership boundary.
    const owned = Object.freeze({ ...record });
    this.current = Object.freeze({ record: owned, revision });
    const publication: TerrainPublication = Object.freeze({ record: owned, revision, edit,
      ...(preserveDirty ? { preserveDirty: true } : {}) });
    this.publishToPorts(publication);
  }

  private publishToPorts(publication: TerrainPublication): void {
    this.ports.cacheDisplayAssets(publication.record);
    this.ports.activateProtocols(publication.record);
    this.ports.publishState(publication);
    this.ports.refreshSources(publication);
  }
}
