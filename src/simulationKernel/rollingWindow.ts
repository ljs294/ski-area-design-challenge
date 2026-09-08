/**
 * Fixed-memory, second-indexed rolling metrics.
 *
 * Buckets are tagged with their absolute second rather than relying on an
 * ever-growing map. A read at any later second drops only values outside the
 * window; values remain stable between event arrivals, so a UI rate does not
 * pulse to zero merely because no new event was recorded this second.
 */

export const DEFAULT_ROLLING_WINDOW_SECONDS = 15 * 60;

export interface RollingWindowOptions {
  readonly windowSeconds?: number;
  readonly bucketSeconds?: number;
}

export interface RollingWindowReadout {
  readonly asOfSecond: number;
  readonly windowSeconds: number;
  readonly total: number;
  readonly rate: number;
}

function assertPositiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${label} must be a positive safe integer`);
}

function assertSecond(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${label} must be a non-negative safe integer simulated second`);
  }
}

function assertFiniteValue(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new RangeError(`${label} must be finite`);
}

/** A numeric rolling sum/rate backed by a fixed-size typed-array ring. */
export class RollingWindow {
  readonly windowSeconds: number;
  readonly bucketSeconds: number;
  readonly bucketCount: number;
  private readonly bucketStarts: Float64Array;
  private readonly bucketValues: Float64Array;
  private latest: number | null = null;

  constructor(windowSecondsOrOptions: number | RollingWindowOptions = DEFAULT_ROLLING_WINDOW_SECONDS,
    bucketSeconds = 1) {
    const options = typeof windowSecondsOrOptions === 'number'
      ? { windowSeconds: windowSecondsOrOptions, bucketSeconds }
      : { windowSeconds: windowSecondsOrOptions.windowSeconds ?? DEFAULT_ROLLING_WINDOW_SECONDS,
        bucketSeconds: windowSecondsOrOptions.bucketSeconds ?? 1 };
    assertPositiveInteger(options.windowSeconds, 'windowSeconds');
    assertPositiveInteger(options.bucketSeconds, 'bucketSeconds');
    if (options.bucketSeconds > options.windowSeconds) {
      throw new RangeError('bucketSeconds cannot exceed windowSeconds');
    }
    this.windowSeconds = options.windowSeconds;
    this.bucketSeconds = options.bucketSeconds;
    this.bucketCount = Math.max(1, Math.ceil(this.windowSeconds / this.bucketSeconds));
    this.bucketStarts = new Float64Array(this.bucketCount);
    this.bucketValues = new Float64Array(this.bucketCount);
    this.bucketStarts.fill(-1);
  }

  get currentSecond(): number | null { return this.latest; }

  /** Number of bytes retained is fixed at eight bytes per bucket plus tags. */
  get retainedBytes(): number { return this.bucketStarts.byteLength + this.bucketValues.byteLength; }

  /** Add an event value at an integer second. Multiple values share a bucket. */
  record(second: number, value = 1): void {
    assertSecond(second, 'second');
    assertFiniteValue(value, 'value');
    this.advanceTo(second);
    const bucketStart = this.bucketStartFor(second);
    const index = this.bucketIndexFor(bucketStart);
    if (this.bucketStarts[index] !== bucketStart) {
      this.bucketStarts[index] = bucketStart;
      this.bucketValues[index] = 0;
    }
    this.bucketValues[index] += value;
  }

  add(second: number, value = 1): void { this.record(second, value); }

  /** Move the read cursor without inserting an event. */
  advanceTo(second: number): void {
    assertSecond(second, 'second');
    if (this.latest !== null && second < this.latest) throw new RangeError('rolling window cannot move backwards');
    this.latest = second;
  }

  advance(second: number): void { this.advanceTo(second); }

  total(asOfSecond = this.latest ?? 0): number {
    this.advanceTo(asOfSecond);
    let total = 0;
    for (let index = 0; index < this.bucketCount; index += 1) {
      const bucketStart = this.bucketStarts[index]!;
      if (bucketStart >= 0 && asOfSecond >= bucketStart && asOfSecond - bucketStart < this.windowSeconds) {
        total += this.bucketValues[index]!;
      }
    }
    return total;
  }

  rate(asOfSecond = this.latest ?? 0): number {
    return this.total(asOfSecond) / this.windowSeconds;
  }

  read(asOfSecond = this.latest ?? 0): RollingWindowReadout {
    const total = this.total(asOfSecond);
    return Object.freeze({ asOfSecond, windowSeconds: this.windowSeconds, total, rate: total / this.windowSeconds });
  }

  snapshot(asOfSecond = this.latest ?? 0): RollingWindowReadout { return this.read(asOfSecond); }

  clear(): void {
    this.bucketStarts.fill(-1);
    this.bucketValues.fill(0);
    this.latest = null;
  }

  private bucketStartFor(second: number): number { return Math.floor(second / this.bucketSeconds) * this.bucketSeconds; }
  private bucketIndexFor(bucketStart: number): number {
    return Math.floor(bucketStart / this.bucketSeconds) % this.bucketCount;
  }
}

/** Weighted numerator/denominator window for stable service-quality averages. */
export class RollingAverageWindow {
  readonly numerator: RollingWindow;
  readonly denominator: RollingWindow;

  constructor(windowSecondsOrOptions: number | RollingWindowOptions = DEFAULT_ROLLING_WINDOW_SECONDS,
    bucketSeconds = 1) {
    this.numerator = new RollingWindow(windowSecondsOrOptions, bucketSeconds);
    this.denominator = new RollingWindow(windowSecondsOrOptions, bucketSeconds);
  }

  get windowSeconds(): number { return this.numerator.windowSeconds; }
  get bucketSeconds(): number { return this.numerator.bucketSeconds; }
  get bucketCount(): number { return this.numerator.bucketCount; }
  get retainedBytes(): number { return this.numerator.retainedBytes + this.denominator.retainedBytes; }

  record(second: number, weightedValue: number, weight = 1): void {
    assertFiniteValue(weightedValue, 'weightedValue');
    assertFiniteValue(weight, 'weight');
    this.numerator.record(second, weightedValue);
    this.denominator.record(second, weight);
  }

  add(second: number, weightedValue: number, weight = 1): void { this.record(second, weightedValue, weight); }

  average(asOfSecond = this.numerator.currentSecond ?? 0): number {
    const denominator = this.denominator.total(asOfSecond);
    return denominator === 0 ? 0 : this.numerator.total(asOfSecond) / denominator;
  }

  read(asOfSecond = this.numerator.currentSecond ?? 0): { readonly asOfSecond: number; readonly average: number; readonly numerator: number; readonly denominator: number } {
    const numerator = this.numerator.total(asOfSecond);
    const denominator = this.denominator.total(asOfSecond);
    return Object.freeze({ asOfSecond, average: denominator === 0 ? 0 : numerator / denominator, numerator, denominator });
  }

  clear(): void { this.numerator.clear(); this.denominator.clear(); }
}

export { RollingWindow as RollingMetricWindow, RollingWindow as RollingRateWindow };
