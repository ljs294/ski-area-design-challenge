/**
 * Ownership of the one in-flight construction.
 *
 * Confirmation used to be gated on React state (`buildingActivity !== null`),
 * which is only ever as fresh as the last commit: two confirmations dispatched
 * in the same tick both read "idle", so both ran. Ownership is taken here
 * synchronously — before the operation's first await — so the second
 * confirmation is rejected instead of building on top of the first one's work.
 */
export type ConstructionActivity = 'lift' | 'trail' | 'road' | 'pond' | 'dam';

export interface ConstructionHandle {
  readonly activity: ConstructionActivity;
  /** Idempotent, and inert once a newer operation owns the lock. */
  release(): void;
}

export type ConstructionOutcome<T> =
  | { ok: true; value: T }
  | { ok: false; reason: 'busy' };

/** Exclusive, synchronously-acquired ownership for confirmed construction. */
export class ConstructionLock {
  private activity: ConstructionActivity | null = null;
  private ticket = 0;
  private readonly publish: (activity: ConstructionActivity | null) => void;

  constructor(publish: (activity: ConstructionActivity | null) => void = () => {}) {
    this.publish = publish;
  }

  get active(): ConstructionActivity | null {
    return this.activity;
  }

  /** `null` means another construction already owns the lock. */
  acquire(activity: ConstructionActivity): ConstructionHandle | null {
    if (this.activity !== null) return null;
    const ticket = ++this.ticket;
    this.activity = activity;
    this.publish(activity);
    return { activity, release: () => this.release(ticket) };
  }

  /**
   * Own the lock for the length of one operation. Ownership is taken before
   * `operation` is invoked and released in `finally` whether the operation
   * returns, throws, or is cancelled. A rejection still propagates: the callers
   * that show a failure in their review panel catch it themselves.
   */
  async run<T>(
    activity: ConstructionActivity,
    operation: () => Promise<T>,
  ): Promise<ConstructionOutcome<T>> {
    const handle = this.acquire(activity);
    if (!handle) return { ok: false, reason: 'busy' };
    try {
      return { ok: true, value: await operation() };
    } finally {
      handle.release();
    }
  }

  /** Drop ownership on teardown. The lock stays usable, so a StrictMode
   *  remount does not retire it. */
  dispose(): void {
    if (this.activity === null) return;
    this.ticket++;
    this.activity = null;
    this.publish(null);
  }

  /** Release is accepted only from the operation that holds the current ticket,
   *  so an older operation can never hand ownership away from a newer one. */
  private release(ticket: number): void {
    if (ticket !== this.ticket || this.activity === null) return;
    this.activity = null;
    this.publish(null);
  }
}
