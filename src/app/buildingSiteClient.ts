import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';
import type {
  BuildingSiteIdentity, BuildingSiteRequest, BuildingSiteResponse,
} from './buildingSiteProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const POST_FAILED = 'Building-site analysis could not start. Try again.';
const INVALID_RESPONSE = 'Building-site analysis returned an invalid response. Try again.';
const CRASHED = 'The building-site analysis worker failed. Try again.';

export interface BuildingSiteHandlers {
  /** Optional caller-side token guard. A false token never reaches review. */
  isCurrent?(id: number): boolean;
  /** Identity currently displayed by the review controller. */
  live?(): BuildingSiteIdentity;
  onResult(result: BuildingSiteAnalysisResult, identity: BuildingSiteIdentity): void;
  onSuperseded?(): void;
  onError(message: string): void;
  onCrash?(): void;
}

function buildingSiteWorker(): WorkerLike<BuildingSiteRequest, BuildingSiteResponse> {
  return new Worker(new URL('./buildingSite.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * One building-site request at a time. Starting a request invalidates the old
 * token and terminates its worker; identity checks provide a second guard when
 * the terrain document changes while a response is in flight.
 */
export class BuildingSiteAdapter {
  private readonly session: WorkerSession<BuildingSiteRequest, BuildingSiteResponse>;
  private requestId = 0;
  private pending = false;

  constructor(factory: WorkerFactory<BuildingSiteRequest, BuildingSiteResponse> = buildingSiteWorker) {
    this.session = new WorkerSession(factory);
  }

  get isPending(): boolean { return this.pending; }

  run(request: Omit<BuildingSiteRequest, 'id'>, handlers: BuildingSiteHandlers): number {
    this.cancel();
    // cancel() advances the monotonic token; re-use that newly allocated token
    // for this request so the first run starts at one and every supersession
    // advances exactly once.
    const id = this.requestId;
    const heights = Float32Array.from(request.heights);
    const identity: BuildingSiteIdentity = {
      terrainRevision: request.terrainRevision,
      elevationChecksum: request.elevationChecksum ?? request.baseElevationChecksum ?? '',
      geometryKey: request.geometryKey,
    };
    const posted: BuildingSiteRequest = { ...request, id, heights,
      type: 'analyze-building-site', elevationChecksum: identity.elevationChecksum };
    this.pending = true;
    const current = () => id === this.requestId && (handlers.isCurrent?.(id) ?? true);
    this.session.connect({
      onResponse: (response) => {
        if (!isBuildingSiteResponse(response)) {
          this.pending = false;
          this.session.stop();
          if (current()) handlers.onError(INVALID_RESPONSE);
          return;
        }
        if (response.id !== id) return;
        this.pending = false;
        this.session.stop();
        if (!current()) return;
        if (!response.ok) {
          handlers.onError(response.error);
          return;
        }
        const live = handlers.live?.();
        if (live && !sameIdentity(response, live)) {
          handlers.onSuperseded?.();
          return;
        }
        handlers.onResult(response.result, {
          terrainRevision: response.terrainRevision,
          elevationChecksum: checksumOf(response),
          geometryKey: response.geometryKey,
        });
      },
      onCrash: () => {
        this.pending = false;
        if (current()) handlers.onCrash?.() ?? handlers.onError(CRASHED);
      },
    });
    if (!this.session.post(posted, [heights.buffer])) {
      this.pending = false;
      if (current()) handlers.onError(POST_FAILED);
    }
    return id;
  }

  /** Abandon work and invalidate every late response. */
  cancel(): void {
    this.requestId++;
    this.pending = false;
    this.session.stop();
  }

  stop(): void { this.cancel(); }
  invalidate(): void { this.cancel(); }
  dispose(): void { this.cancel(); }
}

export class BuildingSiteAnalysisAdapter extends BuildingSiteAdapter {}

function sameIdentity(a: BuildingSiteIdentity, b: BuildingSiteIdentity): boolean {
  return a.geometryKey === b.geometryKey && a.terrainRevision === b.terrainRevision &&
    checksumOf(a) === checksumOf(b);
}

function checksumOf(value: Pick<BuildingSiteIdentity, 'elevationChecksum' | 'baseElevationChecksum'>): string {
  return value.elevationChecksum ?? value.baseElevationChecksum ?? '';
}

function isBuildingSiteResponse(value: unknown): value is BuildingSiteResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  return typeof response.geometryKey === 'string' &&
    (typeof response.terrainRevision === 'string' || typeof response.terrainRevision === 'number') &&
    (typeof response.elevationChecksum === 'string' || typeof response.baseElevationChecksum === 'string') && !!response.result &&
    typeof response.result === 'object';
}
