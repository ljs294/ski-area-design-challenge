import type { WeatherRandomStateV1 } from '../contracts.ts';

function rotateLeft(value: number, count: number): number {
  return (value << count) | (value >>> (32 - count));
}

export class WeatherRandom {
  private state: [number, number, number, number];
  private draws: number;
  private spare: number | null;

  constructor(snapshot: WeatherRandomStateV1) {
    this.state = [...snapshot.state];
    if (this.state.every((value) => value === 0)) this.state[0] = 1;
    this.draws = snapshot.draws;
    this.spare = snapshot.normalSpare;
  }

  static fromDigest(digest: Uint8Array): WeatherRandom {
    if (digest.byteLength < 16) throw new Error('Weather RNG digest must contain at least 16 bytes');
    const view = new DataView(digest.buffer, digest.byteOffset, digest.byteLength);
    return new WeatherRandom({ state: [view.getUint32(0), view.getUint32(4), view.getUint32(8), view.getUint32(12)], draws: 0, normalSpare: null });
  }

  next(): number {
    const result = Math.imul(rotateLeft(Math.imul(this.state[1], 5), 7), 9) >>> 0;
    const temporary = this.state[1] << 9;
    this.state[2] ^= this.state[0]; this.state[3] ^= this.state[1];
    this.state[1] ^= this.state[2]; this.state[0] ^= this.state[3];
    this.state[2] ^= temporary; this.state[3] = rotateLeft(this.state[3], 11);
    this.draws += 1;
    return result / 0x1_0000_0000;
  }

  int(minimum: number, maximumInclusive: number): number {
    return minimum + Math.floor(this.next() * (maximumInclusive - minimum + 1));
  }

  normal(mean = 0, standardDeviation = 1): number {
    if (this.spare != null) {
      const value = this.spare;
      this.spare = null;
      return mean + value * standardDeviation;
    }
    const first = Math.max(Number.EPSILON, this.next());
    const second = this.next();
    const magnitude = Math.sqrt(-2 * Math.log(first));
    const angle = 2 * Math.PI * second;
    this.spare = magnitude * Math.sin(angle);
    return mean + magnitude * Math.cos(angle) * standardDeviation;
  }

  gamma(shape: number, scale: number): number {
    if (shape <= 0 || scale <= 0) return 0;
    if (shape < 1) return this.gamma(shape + 1, scale) * this.next() ** (1 / shape);
    const d = shape - 1 / 3;
    const c = 1 / Math.sqrt(9 * d);
    for (;;) {
      const sample = this.normal();
      const candidate = (1 + c * sample) ** 3;
      if (candidate <= 0) continue;
      const uniform = this.next();
      if (uniform < 1 - 0.0331 * sample ** 4 || Math.log(uniform) < 0.5 * sample ** 2 + d * (1 - candidate + Math.log(candidate))) {
        return d * candidate * scale;
      }
    }
  }

  weighted(weights: readonly number[]): number {
    const total = weights.reduce((sum, value) => sum + Math.max(0, value), 0);
    if (total <= 0) throw new Error('Weather transition row has no positive probability');
    let cursor = this.next() * total;
    for (let index = 0; index < weights.length; index += 1) {
      cursor -= Math.max(0, weights[index]);
      if (cursor <= 0) return index;
    }
    return weights.length - 1;
  }

  snapshot(): WeatherRandomStateV1 {
    return { state: [...this.state], draws: this.draws, normalSpare: this.spare };
  }
}
