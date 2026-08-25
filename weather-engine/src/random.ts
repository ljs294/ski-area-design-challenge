export interface RandomState {
  s0: number;
  s1: number;
  s2: number;
  s3: number;
}

function rotl(value: number, shift: number): number {
  return ((value << shift) | (value >>> (32 - shift))) >>> 0;
}

/** Stable 128-bit string hash used to seed independent xoshiro streams. */
export function hashSeed(value: string): RandomState {
  let h0 = 0x9e3779b9;
  let h1 = 0x243f6a88;
  let h2 = 0xb7e15162;
  let h3 = 0xdeadbeef;
  for (let i = 0; i < value.length; i += 1) {
    const c = value.charCodeAt(i);
    h0 = Math.imul(h0 ^ c, 0x85ebca6b);
    h1 = Math.imul(h1 ^ c, 0xc2b2ae35);
    h2 = Math.imul(h2 ^ c, 0x27d4eb2f);
    h3 = Math.imul(h3 ^ c, 0x165667b1);
    h0 ^= h1 >>> 13;
    h2 ^= h3 >>> 16;
  }
  const state = { s0: h0 >>> 0, s1: h1 >>> 0, s2: h2 >>> 0, s3: h3 >>> 0 };
  if ((state.s0 | state.s1 | state.s2 | state.s3) === 0) state.s0 = 1;
  return state;
}

export class Xoshiro128 {
  private state: RandomState;
  private spareNormal: number | null = null;

  constructor(seed: string | RandomState) {
    this.state = typeof seed === 'string' ? hashSeed(seed) : { ...seed };
  }

  public snapshot(): RandomState {
    return { ...this.state };
  }

  public nextUint32(): number {
    const result = Math.imul(rotl(Math.imul(this.state.s1, 5) >>> 0, 7), 9) >>> 0;
    const t = (this.state.s1 << 9) >>> 0;
    this.state.s2 = (this.state.s2 ^ this.state.s0) >>> 0;
    this.state.s3 = (this.state.s3 ^ this.state.s1) >>> 0;
    this.state.s1 = (this.state.s1 ^ this.state.s2) >>> 0;
    this.state.s0 = (this.state.s0 ^ this.state.s3) >>> 0;
    this.state.s2 = (this.state.s2 ^ t) >>> 0;
    this.state.s3 = rotl(this.state.s3, 11);
    return result;
  }

  public next(): number {
    return this.nextUint32() / 0x1_0000_0000;
  }

  public int(minInclusive: number, maxInclusive: number): number {
    return minInclusive + Math.floor(this.next() * (maxInclusive - minInclusive + 1));
  }

  public normal(mean = 0, stdDev = 1): number {
    if (this.spareNormal != null) {
      const value = this.spareNormal;
      this.spareNormal = null;
      return mean + value * stdDev;
    }
    const u = Math.max(Number.EPSILON, this.next());
    const v = this.next();
    const magnitude = Math.sqrt(-2 * Math.log(u));
    const a = magnitude * Math.cos(2 * Math.PI * v);
    this.spareNormal = magnitude * Math.sin(2 * Math.PI * v);
    return mean + a * stdDev;
  }

  /** Marsaglia-Tsang gamma sampler, parameterized by shape and scale. */
  public gamma(shape: number, scale: number): number {
    if (shape <= 0 || scale <= 0) return 0;
    if (shape < 1) {
      return this.gamma(shape + 1, scale) * Math.pow(this.next(), 1 / shape);
    }
    const d = shape - 1 / 3;
    const c = 1 / Math.sqrt(9 * d);
    for (;;) {
      const x = this.normal();
      const vBase = 1 + c * x;
      if (vBase <= 0) continue;
      const v = vBase * vBase * vBase;
      const u = this.next();
      if (u < 1 - 0.0331 * x ** 4) return d * v * scale;
      if (Math.log(u) < 0.5 * x * x + d * (1 - v + Math.log(v))) return d * v * scale;
    }
  }
}

export interface WeatherRandomStreams {
  truth: RandomState;
  hourlyShape: RandomState;
  forecastError: RandomState;
}

export function createWeatherRandomStreams(seed: string): WeatherRandomStreams {
  return {
    truth: hashSeed(`${seed}:truth`),
    hourlyShape: hashSeed(`${seed}:hourly-shape`),
    forecastError: hashSeed(`${seed}:forecast-error`),
  };
}
