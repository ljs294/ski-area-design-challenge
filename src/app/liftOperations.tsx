import type { AggregateFlowSnapshot, DualPublication, LiftFlowSnapshot } from '../types/dualClock';
import type { SkiNetwork } from '../network';

export type LiftWait =
  | { readonly state: 'available'; readonly seconds: number }
  | { readonly state: 'unavailable'; readonly reason: string }
  | { readonly state: 'unknown' };

/** One committed, lift-id keyed projection for every lift detail entrypoint. */
export interface LiftOperationsReadModel {
  readonly queued: number | null;
  readonly currentRiders: number | null;
  readonly servedToday: number | null;
  readonly wait: LiftWait;
  readonly servedTodayAccuracy: 'complete' | 'partial' | null;
  readonly trackingSince?: string;
}

const UNAVAILABLE_REASON: Readonly<Record<NonNullable<LiftFlowSnapshot['serviceUnavailableReason']>, string>> = {
  missing: 'Lift telemetry is unavailable.',
  'no-descent': 'No viable route is open from this lift.',
  closed: 'Lift is closed.',
  capacity: 'Lift has no operating capacity.',
};

function finiteCount(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;
}

function waitFor(queue: LiftFlowSnapshot): LiftWait {
  if (queue.serviceAvailable === false) {
    return { state: 'unavailable', reason: UNAVAILABLE_REASON[queue.serviceUnavailableReason ?? 'missing'] };
  }
  if (typeof queue.waitSeconds === 'number' && Number.isFinite(queue.waitSeconds) && queue.waitSeconds >= 0) {
    return { state: 'available', seconds: queue.waitSeconds };
  }
  return { state: 'unknown' };
}

/** Uses network lift-edge identities rather than lift display names or array order. */
export function liftOperationsFor(liftId: string, network: SkiNetwork,
  publication: Pick<DualPublication, 'flow'> | null | undefined): LiftOperationsReadModel | null {
  const edgeId = network.liftEdgeIds.get(liftId);
  const queue = edgeId ? publication?.flow.queues[edgeId] : undefined;
  return liftOperationsFromQueue(queue);
}

/** Converts a known edge snapshot only; absent telemetry stays absent. */
export function liftOperationsFromQueue(queue: LiftFlowSnapshot | undefined): LiftOperationsReadModel | null {
  if (!queue) return null;
  const daily = finiteCount(queue.dailyBoarded);
  const legacy = finiteCount(queue.boarded);
  return {
    queued: finiteCount(queue.guests),
    currentRiders: finiteCount(queue.riders),
    servedToday: daily ?? legacy,
    wait: waitFor(queue),
    // Pre-daily-ledger schema-17 checkpoints retain a session boarding total,
    // but cannot honestly call it a complete local-day value.
    servedTodayAccuracy: queue.dailyAccuracy ?? (legacy === null ? null : 'partial'),
    trackingSince: queue.trackingSince,
  };
}

function count(value: number | null): string { return value === null ? '—' : value.toLocaleString(); }
function duration(seconds: number): string {
  const rounded = Math.round(seconds);
  return `${Math.floor(rounded / 60)}:${String(rounded % 60).padStart(2, '0')}`;
}

/** Four fixed rows inside an existing lift detail, never a persistent gameplay surface. */
export function LiftOperationsRows({ operations }: { operations: LiftOperationsReadModel | null | undefined }) {
  const wait = operations?.wait ?? { state: 'unknown' as const };
  const waitValue = wait.state === 'available' ? duration(wait.seconds)
    : wait.state === 'unavailable' ? `Unavailable — ${wait.reason}` : '—';
  const served = operations?.servedToday;
  const accuracy = operations?.servedTodayAccuracy;
  return <div className="lift-stats" aria-label="Lift operations">
    <div className="readout-line"><span className="lift-stat-label">People in line</span><span className="lift-stat-value">{count(operations?.queued ?? null)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Estimated wait</span><span className="lift-stat-value">{waitValue}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Currently riding</span><span className="lift-stat-value">{count(operations?.currentRiders ?? null)}</span></div>
    <div className="readout-line"><span className="lift-stat-label">Served today</span><span className="lift-stat-value">{count(served ?? null)}{accuracy === 'partial' ? ' (partial)' : ''}</span></div>
    {accuracy === 'partial' && <div className="site-hint">Boardings are tracked {operations?.trackingSince ? `since ${operations.trackingSince}` : 'from this session'}.</div>}
  </div>;
}

export function liftOperationsFromFlow(liftId: string, network: SkiNetwork,
  flow: AggregateFlowSnapshot | null | undefined): LiftOperationsReadModel | null {
  return liftOperationsFor(liftId, network, flow ? { flow } : null);
}
