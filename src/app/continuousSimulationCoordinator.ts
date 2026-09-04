import { SIMULATION_SPEED_RATES, type SimulationSpeed } from '../types/simulation';

export { SIMULATION_SECONDS_PER_WEEK, SIMULATION_SPEED_RATES } from '../types/simulation';
export const MAX_SIMULATION_SLICE_SECONDS = 60;
export const SIMULATION_DISPATCH_INTERVAL_MS = 50;
export const SIMULATION_SLICE_BUDGET_MS = 8;

const SPEEDS: readonly SimulationSpeed[] = ['slow', 'normal', 'fast', 'ultrafast'];
const THROTTLE_BACKLOG_MS = 250;
const RECOVERY_BACKLOG_MS = 100;
const RECOVERY_WINDOW_MS = 2_000;

export interface SimulationCoordinatorStatus {
  readonly selectedSpeed: SimulationSpeed;
  readonly effectiveSpeed: SimulationSpeed;
  readonly effectiveRate: number;
  readonly targetSecond: number;
  readonly committedSecond: number;
  readonly backlogSeconds: number;
  readonly throttled: boolean;
}

/**
 * Wall-clock accumulator for the simulation worker. It never mutates domain
 * state: only acknowledged worker seconds may be published as game time.
 */
export class ContinuousSimulationCoordinator {
  private selectedSpeed: SimulationSpeed;
  private effectiveSpeed: SimulationSpeed;
  private targetSecond: number;
  private committedSecond: number;
  private lastWallMs: number | null = null;
  private consecutiveOverBudget = 0;
  private recoveryStartedAt: number | null = null;

  constructor(committedSecond: number, speed: SimulationSpeed = 'normal') {
    if (!Number.isFinite(committedSecond) || committedSecond < 0) {
      throw new RangeError('Committed simulation time must be a non-negative finite second.');
    }
    this.committedSecond = committedSecond;
    this.targetSecond = committedSecond;
    this.selectedSpeed = speed;
    this.effectiveSpeed = speed;
  }

  setSpeed(speed: SimulationSpeed): void {
    this.selectedSpeed = speed;
    this.effectiveSpeed = speed;
    this.consecutiveOverBudget = 0;
    this.recoveryStartedAt = null;
  }

  reset(committedSecond: number, wallMs?: number): void {
    if (!Number.isFinite(committedSecond) || committedSecond < 0) {
      throw new RangeError('Committed simulation time must be a non-negative finite second.');
    }
    this.committedSecond = committedSecond;
    this.targetSecond = committedSecond;
    this.lastWallMs = wallMs ?? null;
    this.consecutiveOverBudget = 0;
    this.recoveryStartedAt = null;
  }

  advanceWall(wallMs: number, hidden = false): void {
    if (!Number.isFinite(wallMs)) return;
    if (this.lastWallMs === null || hidden) {
      this.lastWallMs = wallMs;
      return;
    }
    // Visibility transitions explicitly reset lastWallMs. While visible, keep
    // the complete elapsed interval so a busy main thread slows presentation
    // without silently deleting simulation time.
    const deltaMs = Math.max(0, wallMs - this.lastWallMs);
    this.lastWallMs = wallMs;
    this.targetSecond += deltaMs / 1_000 * SIMULATION_SPEED_RATES[this.effectiveSpeed];
  }

  nextTarget(): number | null {
    const target = Math.min(Math.floor(this.targetSecond),
      this.committedSecond + MAX_SIMULATION_SLICE_SECONDS);
    return target > this.committedSecond ? target : null;
  }

  acknowledge(committedSecond: number, cpuMs: number, wallMs: number): SimulationCoordinatorStatus {
    if (!Number.isFinite(committedSecond) || committedSecond < this.committedSecond
      || committedSecond > Math.floor(this.targetSecond)) {
      throw new RangeError('Worker acknowledgement is outside the pending simulation interval.');
    }
    this.committedSecond = committedSecond;
    const backlogMs = this.backlogMilliseconds();
    this.consecutiveOverBudget = cpuMs > SIMULATION_SLICE_BUDGET_MS
      ? this.consecutiveOverBudget + 1 : 0;
    if (this.consecutiveOverBudget >= 3 || backlogMs > THROTTLE_BACKLOG_MS) {
      this.lowerEffectiveSpeed();
      this.recoveryStartedAt = null;
    } else if (backlogMs < RECOVERY_BACKLOG_MS && cpuMs < SIMULATION_SLICE_BUDGET_MS / 2) {
      this.recoveryStartedAt ??= wallMs;
      if (wallMs - this.recoveryStartedAt >= RECOVERY_WINDOW_MS) {
        this.raiseEffectiveSpeed();
        this.recoveryStartedAt = wallMs;
      }
    } else {
      this.recoveryStartedAt = null;
    }
    return this.status();
  }

  status(): SimulationCoordinatorStatus {
    return Object.freeze({
      selectedSpeed: this.selectedSpeed,
      effectiveSpeed: this.effectiveSpeed,
      effectiveRate: SIMULATION_SPEED_RATES[this.effectiveSpeed],
      targetSecond: this.targetSecond,
      committedSecond: this.committedSecond,
      backlogSeconds: Math.max(0, this.targetSecond - this.committedSecond),
      throttled: this.effectiveSpeed !== this.selectedSpeed,
    });
  }

  private backlogMilliseconds(): number {
    return Math.max(0, this.targetSecond - this.committedSecond)
      / SIMULATION_SPEED_RATES[this.effectiveSpeed] * 1_000;
  }

  private lowerEffectiveSpeed(): void {
    const selectedIndex = SPEEDS.indexOf(this.effectiveSpeed);
    if (selectedIndex > 0) this.effectiveSpeed = SPEEDS[selectedIndex - 1];
    this.consecutiveOverBudget = 0;
  }

  private raiseEffectiveSpeed(): void {
    const effectiveIndex = SPEEDS.indexOf(this.effectiveSpeed);
    const selectedIndex = SPEEDS.indexOf(this.selectedSpeed);
    if (effectiveIndex < selectedIndex) this.effectiveSpeed = SPEEDS[effectiveIndex + 1];
  }
}
