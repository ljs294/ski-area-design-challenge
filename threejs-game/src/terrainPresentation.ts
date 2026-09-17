import type { TerrainPublication } from '../../src/app/terrainDocument';

export type TerrainPresentationState = 'ready' | 'updating' | 'failed';

export interface TerrainPresentationStatus {
  readonly state: TerrainPresentationState;
  readonly committedRevision: number;
  readonly presentedRevision: number;
  readonly message?: string;
}

export interface TerrainPresentationPreparation<TStage> {
  readonly publication: TerrainPublication;
  /** Undefined means provenance was incomplete and a full refresh is required. */
  readonly changedSampleIndices?: readonly number[];
  readonly startedAt: number;
  readonly stage: TStage;
  readonly preparationMs: number;
}

interface PendingInvalidation {
  full: boolean;
  samples: Set<number>;
}

export interface TerrainPresentationCoordinatorOptions<TStage> {
  prepare(publication: TerrainPublication,
    changedSampleIndices: readonly number[] | undefined): TStage | Promise<TStage>;
  activate(preparation: TerrainPresentationPreparation<TStage>): void;
  schedule(task: () => void): void;
  now?(): number;
  onStatus?(status: TerrainPresentationStatus): void;
  onActivated?(publication: TerrainPublication, preparationMs: number, activationMs: number): void;
}

/** Owns cumulative renderer invalidation independently of the authoritative
 * terrain document. New work may supersede preparation, but it never erases
 * dirty samples from accepted revisions until a coherent frame activation. */
export class TerrainPresentationCoordinator<TStage> {
  private readonly options: TerrainPresentationCoordinatorOptions<TStage>;
  private readonly now: () => number;
  private latest: TerrainPublication;
  private dirty: PendingInvalidation = { full: false, samples: new Set() };
  private preparing = false;
  private scheduled = false;
  private disposed = false;
  private staged: TerrainPresentationPreparation<TStage> | null = null;
  private presentedRevision: number;
  private failedMessage: string | undefined;

  constructor(initial: TerrainPublication, options: TerrainPresentationCoordinatorOptions<TStage>) {
    this.latest = initial;
    this.presentedRevision = initial.revision;
    this.options = options;
    this.now = options.now ?? (() => performance.now());
  }

  get status(): TerrainPresentationStatus {
    return Object.freeze({
      state: this.failedMessage ? 'failed' : this.isCurrent ? 'ready' : 'updating',
      committedRevision: this.latest.revision,
      presentedRevision: this.presentedRevision,
      ...(this.failedMessage ? { message: this.failedMessage } : {}),
    });
  }

  get isCurrent(): boolean {
    return this.presentedRevision === this.latest.revision && !this.preparing && !this.staged;
  }

  enqueue(publication: TerrainPublication): void {
    if (this.disposed || publication.revision <= this.presentedRevision
      || publication.revision < this.latest.revision) return;
    this.latest = publication;
    this.failedMessage = undefined;
    if (publication.edit !== 'elevation' || publication.changedSampleIndices === undefined) {
      this.dirty.full = true;
      this.dirty.samples.clear();
    } else if (!this.dirty.full) {
      for (const index of publication.changedSampleIndices) this.dirty.samples.add(index);
    }
    this.emitStatus();
    this.requestPreparation();
  }

  /** Called by the single world-frame owner immediately before matrices,
   * anchored overlays, picking and rendering use the presentation. */
  activateAtFrameBoundary(): boolean {
    const prepared = this.staged;
    if (!prepared || this.disposed) return false;
    if (prepared.publication.revision !== this.latest.revision) {
      this.staged = null;
      this.requestPreparation();
      return false;
    }
    const started = this.now();
    try {
      this.options.activate(prepared);
    } catch (error) {
      this.staged = null;
      this.failedMessage = error instanceof Error ? error.message : String(error);
      this.emitStatus();
      return false;
    }
    const activationMs = this.now() - started;
    this.presentedRevision = prepared.publication.revision;
    this.staged = null;
    this.dirty = { full: false, samples: new Set() };
    this.failedMessage = undefined;
    this.options.onActivated?.(prepared.publication, prepared.preparationMs, activationMs);
    this.emitStatus();
    return true;
  }

  retry(): void {
    if (this.disposed || this.isCurrent) return;
    this.failedMessage = undefined;
    this.emitStatus();
    this.requestPreparation();
  }

  dispose(): void {
    this.disposed = true;
    this.staged = null;
    this.dirty.samples.clear();
  }

  private requestPreparation(): void {
    if (this.disposed || this.preparing || this.scheduled || this.staged) return;
    this.scheduled = true;
    this.options.schedule(() => {
      this.scheduled = false;
      void this.prepareLatest();
    });
  }

  private async prepareLatest(): Promise<void> {
    if (this.disposed || this.preparing || this.staged) return;
    this.preparing = true;
    const publication = this.latest;
    const changedSampleIndices = this.dirty.full ? undefined : [...this.dirty.samples];
    const startedAt = this.now();
    try {
      const stage = await this.options.prepare(publication, changedSampleIndices);
      if (this.disposed) return;
      if (publication.revision !== this.latest.revision) {
        this.preparing = false;
        this.requestPreparation();
        return;
      }
      this.staged = { publication, changedSampleIndices, startedAt, stage,
        preparationMs: this.now() - startedAt };
    } catch (error) {
      if (!this.disposed && publication.revision === this.latest.revision) {
        this.failedMessage = error instanceof Error ? error.message : String(error);
      }
    } finally {
      this.preparing = false;
      if (!this.disposed && publication.revision !== this.latest.revision) this.requestPreparation();
      this.emitStatus();
    }
  }

  private emitStatus(): void {
    this.options.onStatus?.(this.status);
  }
}
