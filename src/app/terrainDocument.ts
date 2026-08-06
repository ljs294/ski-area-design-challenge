import type { TerrainRecord } from '../types/terrain';
import {
  ConstructionLock,
  type ConstructionActivity,
  type ConstructionOutcome,
} from './constructionLock';
import type { DeepReadonly } from '../types/readonly';

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
}

export interface TerrainCommitRequest {
  readonly expectedRevision: number;
  readonly record: TerrainRecord;
  readonly kind: TerrainEditKind;
}

export type TerrainCommitResult =
  | { ok: true; revision: number }
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

  /** Publish an edited package, or reject it for having been built on a
   *  revision that has since been superseded. */
  commit(request: TerrainCommitRequest): TerrainCommitResult {
    if (request.expectedRevision !== this.current.revision) return { ok: false, reason: 'stale' };
    this.publish(request.record, request.kind);
    return { ok: true, revision: this.current.revision };
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

  private publish(record: TerrainRecord, edit: TerrainEditKind | null): void {
    const revision = this.current.revision + 1;
    // Copy and freeze the record shell once at publication. Large elevation,
    // cover, contour, and imagery payloads are deliberately not deep-cloned;
    // the public snapshot projects them recursively read-only and every edit
    // creates replacement payloads before it reaches this ownership boundary.
    const owned = Object.freeze({ ...record });
    this.current = Object.freeze({ record: owned, revision });
    const publication: TerrainPublication = Object.freeze({ record: owned, revision, edit });
    this.ports.cacheDisplayAssets(owned);
    this.ports.activateProtocols(owned);
    this.ports.publishState(publication);
    this.ports.refreshSources(publication);
  }
}
