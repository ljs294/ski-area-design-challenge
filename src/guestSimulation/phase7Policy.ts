import type { GuestPublicationRow } from './phase7Publication.ts';

/**
 * Renderer degradation is deliberately ordered.  Every overloaded frame:
 *
 * 1. reduces publication frequency;
 * 2. culls rows outside the viewport (with padding);
 * 3. samples visible rows at a deterministic stride;
 * 4. coalesces queued work to the newest publication;
 * 5. pauses publication while a hard backlog remains.
 *
 * Authoritative simulation state is never dropped by these presentation
 * choices.  A later frame can request a full publication when the backlog is
 * clear or the viewport changes.
 */
export const PHASE7_DEGRADATION_ORDER = [
  'cadence',
  'viewport-cull',
  'stable-sample',
  'coalesce-latest',
  'pause-backlog',
] as const;

export type Phase7DegradationStage = typeof PHASE7_DEGRADATION_ORDER[number] | 'none' | 'backlog-cadence';

export interface GuestViewport {
  readonly minX: number;
  readonly minY: number;
  readonly maxX: number;
  readonly maxY: number;
}

export interface GuestPublicationPolicy {
  readonly activeCadenceTicks: number;
  readonly movingCadenceTicks: number;
  readonly idleCadenceTicks: number;
  readonly backlogCadenceTicks: number;
  /** Pending publications at or above this value pause new work. */
  readonly hardBacklog: number;
  /** Visible rows above this count are sampled. */
  readonly maxPublishedGuests: number;
  readonly viewportPadding: number;
}

export const DEFAULT_GUEST_PUBLICATION_POLICY: GuestPublicationPolicy = Object.freeze({
  activeCadenceTicks: 1,
  movingCadenceTicks: 2,
  idleCadenceTicks: 4,
  backlogCadenceTicks: 8,
  hardBacklog: 4,
  maxPublishedGuests: 15_000,
  viewportPadding: 0.05,
});

export interface PublicationDecisionInput {
  readonly tick: number;
  readonly lastPublishedTick: number | null;
  readonly pendingPublications: number;
  readonly runActive: boolean;
  readonly cameraMoving: boolean;
  readonly hasChanges: boolean;
  readonly force?: boolean;
}

export interface PublicationDecision {
  readonly shouldPublish: boolean;
  readonly cadenceTicks: number;
  readonly stage: Phase7DegradationStage;
  readonly reason: 'published' | 'unchanged' | 'cadence' | 'backlog-paused';
}

function integerAtLeast(value: number, name: string, minimum = 0): number {
  if (!Number.isSafeInteger(value) || value < minimum) throw new RangeError(`${name} must be an integer >= ${minimum}`);
  return value;
}

function finite(value: number, name: string): number {
  if (!Number.isFinite(value)) throw new RangeError(`${name} must be finite`);
  return value;
}

function validatePolicy(policy: GuestPublicationPolicy): void {
  integerAtLeast(policy.activeCadenceTicks, 'activeCadenceTicks', 1);
  integerAtLeast(policy.movingCadenceTicks, 'movingCadenceTicks', 1);
  integerAtLeast(policy.idleCadenceTicks, 'idleCadenceTicks', 1);
  integerAtLeast(policy.backlogCadenceTicks, 'backlogCadenceTicks', 1);
  integerAtLeast(policy.hardBacklog, 'hardBacklog', 1);
  integerAtLeast(policy.maxPublishedGuests, 'maxPublishedGuests', 1);
  finite(policy.viewportPadding, 'viewportPadding');
  if (policy.viewportPadding < 0) throw new RangeError('viewportPadding must be non-negative');
}

/** Pure cadence/backlog decision; no renderer or simulation state is mutated. */
export function decideGuestPublication(
  input: PublicationDecisionInput,
  policy: GuestPublicationPolicy = DEFAULT_GUEST_PUBLICATION_POLICY,
): PublicationDecision {
  validatePolicy(policy);
  integerAtLeast(input.tick, 'tick');
  integerAtLeast(input.pendingPublications, 'pendingPublications');
  if (input.lastPublishedTick !== null) {
    integerAtLeast(input.lastPublishedTick, 'lastPublishedTick');
    if (input.lastPublishedTick > input.tick) throw new RangeError('lastPublishedTick cannot be after tick');
  }
  const baseCadence = input.cameraMoving
    ? policy.movingCadenceTicks
    : input.runActive ? policy.activeCadenceTicks : policy.idleCadenceTicks;
  const backlogThrottled = input.pendingPublications > 0;
  const cadenceTicks = backlogThrottled ? Math.max(baseCadence, policy.backlogCadenceTicks) : baseCadence;
  if (input.pendingPublications >= policy.hardBacklog) {
    return { shouldPublish: false, cadenceTicks, stage: 'pause-backlog', reason: 'backlog-paused' };
  }
  if (!input.hasChanges && !input.force) {
    return { shouldPublish: false, cadenceTicks, stage: 'none', reason: 'unchanged' };
  }
  if (input.lastPublishedTick !== null && input.tick - input.lastPublishedTick < cadenceTicks && !input.force) {
    return { shouldPublish: false, cadenceTicks, stage: 'cadence', reason: 'cadence' };
  }
  return {
    shouldPublish: true,
    cadenceTicks,
    stage: backlogThrottled ? 'backlog-cadence' : 'none',
    reason: 'published',
  };
}

export interface PublicationSelection {
  readonly selectedIndices: Uint32Array;
  readonly inputCount: number;
  readonly visibleCount: number;
  readonly culledCount: number;
  readonly sampledCount: number;
  readonly sampleStride: number;
  readonly stage: 'none' | 'viewport-cull' | 'stable-sample';
}

function validateViewport(viewport: GuestViewport): void {
  finite(viewport.minX, 'viewport.minX');
  finite(viewport.minY, 'viewport.minY');
  finite(viewport.maxX, 'viewport.maxX');
  finite(viewport.maxY, 'viewport.maxY');
  if (viewport.maxX < viewport.minX || viewport.maxY < viewport.minY) {
    throw new RangeError('viewport max bounds must be >= min bounds');
  }
}

/**
 * Select presentation rows without allocating row objects.  The returned
 * indexes are unique, deterministic, and retain all rows when under budget.
 */
export function selectGuestPublicationRows(
  rows: readonly Pick<GuestPublicationRow, 'x' | 'y'>[],
  viewport: GuestViewport | null,
  maxPublishedGuests: number,
  tick: number,
  viewportPadding = DEFAULT_GUEST_PUBLICATION_POLICY.viewportPadding,
): PublicationSelection {
  integerAtLeast(maxPublishedGuests, 'maxPublishedGuests', 1);
  integerAtLeast(tick, 'tick');
  finite(viewportPadding, 'viewportPadding');
  if (viewportPadding < 0) throw new RangeError('viewportPadding must be non-negative');
  if (viewport !== null) validateViewport(viewport);

  const visible = new Uint32Array(rows.length);
  const padding = viewportPadding;
  let visibleCount = 0;
  for (let index = 0; index < rows.length; index += 1) {
    const row = rows[index];
    finite(row.x, 'row.x');
    finite(row.y, 'row.y');
    if (viewport === null || (row.x >= viewport.minX - padding && row.x <= viewport.maxX + padding
      && row.y >= viewport.minY - padding && row.y <= viewport.maxY + padding)) {
      visible[visibleCount] = index;
      visibleCount += 1;
    }
  }
  const culledCount = rows.length - visibleCount;
  if (visibleCount <= maxPublishedGuests) {
    return {
      selectedIndices: visible.slice(0, visibleCount),
      inputCount: rows.length,
      visibleCount,
      culledCount,
      sampledCount: 0,
      sampleStride: 1,
      stage: culledCount > 0 ? 'viewport-cull' : 'none',
    };
  }

  // Evenly spaced selection gives bounded output and avoids an expensive sort.
  // Rotating by tick makes successive frames share work fairly while the
  // selected index set remains exactly maxPublishedGuests in every frame.
  const selected = new Uint32Array(maxPublishedGuests);
  const rotation = tick % visibleCount;
  for (let index = 0; index < maxPublishedGuests; index += 1) {
    const evenlySpaced = Math.floor((index * visibleCount) / maxPublishedGuests);
    selected[index] = visible[(evenlySpaced + rotation) % visibleCount];
  }
  return {
    selectedIndices: selected,
    inputCount: rows.length,
    visibleCount,
    culledCount,
    sampledCount: visibleCount - maxPublishedGuests,
    sampleStride: Math.ceil(visibleCount / maxPublishedGuests),
    stage: 'stable-sample',
  };
}

/** A small bounded backlog counter implementing the coalesce/pause stages. */
export class GuestPublicationBacklog {
  public readonly hardBacklog: number;
  private pendingCount = 0;
  private coalescedCount = 0;
  private pausedCount = 0;

  public constructor(hardBacklog = DEFAULT_GUEST_PUBLICATION_POLICY.hardBacklog) {
    integerAtLeast(hardBacklog, 'hardBacklog', 1);
    this.hardBacklog = hardBacklog;
  }

  public get pending(): number {
    return this.pendingCount;
  }

  public get coalesced(): number {
    return this.coalescedCount;
  }

  public get paused(): number {
    return this.pausedCount;
  }

  /**
   * Offer newest work.  The counter tracks outstanding acknowledgements for
   * pressure/backlog telemetry; the caller retains only the newest payload.
   */
  public offer(): 'accepted' | 'coalesced' | 'paused' {
    if (this.pendingCount >= this.hardBacklog) {
      this.pausedCount += 1;
      return 'paused';
    }
    if (this.pendingCount > 0) {
      this.coalescedCount += 1;
      return 'coalesced';
    }
    this.pendingCount = 1;
    return 'accepted';
  }

  public complete(): boolean {
    if (this.pendingCount === 0) return false;
    this.pendingCount -= 1;
    return true;
  }
}
