import type { TerrainGradeRequest, TerrainGradeResponse } from './terrainGradeProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const POST_FAILED = 'Terrain grading could not start. Try again.';
const INVALID_RESPONSE = 'Terrain grading returned an invalid response. Try again.';

export type TerrainGradeSuccess = Extract<TerrainGradeResponse, { ok: true }>;

/** What a finished grade must still describe to be worth applying. */
export interface TerrainGradeIdentity {
  baseElevationChecksum: string;
  trailGeometryKey: string;
}

export interface TerrainGradeHandlers {
  /** True while `id` still owns the shared grade preview. */
  isCurrent(id: number): boolean;
  /** The footprint and package on screen now, read when the response lands. */
  live(): TerrainGradeIdentity;
  onResult(response: TerrainGradeSuccess): void;
  /** The grade describes a footprint or a package that has since changed. */
  onSuperseded(): void;
  /** The grading engine refused the request. */
  onError(message: string): void;
  onCrash(): void;
}

function terrainGradeWorker(): WorkerLike<TerrainGradeRequest, TerrainGradeResponse> {
  return new Worker(new URL('./terrainGrade.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * The grade preview shared by the road and trail tools. Only one preview exists
 * on the map, so only one grade may be in flight: starting a second abandons
 * the first outright rather than leaving it to grind out contours for a
 * footprint that has already been redrawn.
 *
 * Three separate things can make a finished grade inapplicable, and all three
 * are checked here. The preview token says whether this request is still the
 * one being awaited; the elevation checksum says whether it was computed
 * against the package that is committed now; the geometry key says whether it
 * describes the footprint currently in review. A grade that fails any of them
 * is reported as superseded, never quietly applied.
 */
export class TerrainGradeAdapter {
  private readonly session: WorkerSession<TerrainGradeRequest, TerrainGradeResponse>;
  private pending = false;

  constructor(factory: WorkerFactory<TerrainGradeRequest, TerrainGradeResponse> = terrainGradeWorker) {
    this.session = new WorkerSession(factory);
  }

  run(request: TerrainGradeRequest, handlers: TerrainGradeHandlers): void {
    // An unfinished grade is for a footprint nobody is looking at any more. An
    // idle worker is kept: toggling the preview off and on is the common case.
    if (this.pending) this.session.stop();
    this.pending = true;
    this.session.connect({
      onResponse: (response) => {
        if (!isTerrainGradeResponse(response)) {
          this.pending = false;
          this.session.stop();
          if (handlers.isCurrent(request.id)) handlers.onError(INVALID_RESPONSE);
          return;
        }
        if (response.id !== request.id) return;
        this.pending = false;
        if (!handlers.isCurrent(response.id)) return;
        if (!response.ok) {
          handlers.onError(response.error);
          return;
        }
        const live = handlers.live();
        if (response.baseElevationChecksum !== live.baseElevationChecksum ||
            response.trailGeometryKey !== live.trailGeometryKey) {
          handlers.onSuperseded();
          return;
        }
        handlers.onResult(response);
      },
      onCrash: () => {
        this.pending = false;
        if (!handlers.isCurrent(request.id)) return;
        handlers.onCrash();
      },
    });
    if (!this.session.post(request, [request.heights.buffer])) {
      this.pending = false;
      if (handlers.isCurrent(request.id)) handlers.onError(POST_FAILED);
    }
  }

  /** Abandon the grade in flight; the caller has already dropped its preview. */
  stop(): void {
    this.pending = false;
    this.session.stop();
  }

  /**
   * Teardown is the same abandonment as cancelling the tool. The adapter stays
   * usable afterwards so a StrictMode remount does not retire it.
   */
  dispose(): void {
    this.stop();
  }
}

function isTerrainGradeResponse(value: unknown): value is TerrainGradeResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  return typeof response.baseElevationChecksum === 'string' &&
    typeof response.trailGeometryKey === 'string';
}
