import { describe, expect, it } from 'vitest';
import {
  KeyedRandom,
  keyedRandomFloat,
  keyedRandomInt,
  keyedRandomUint32,
  keyedRandomUint24,
  uniformOpen01,
} from './random';

describe('guest simulation keyed randomness', () => {
  it('keeps stable golden vectors across repeated calls', () => {
    const vectors = [
      ['world-1', 'guest-7', 'route', 0, 3_468_365_962],
      ['world-1', 'guest-7', 'route', 1, 1_707_325_713],
      ['world-1', 'guest-7', 'mood', 0, 3_383_577_881],
      ['world-2', 'guest-7', 'route', 0, 3_618_405_706],
      ['world-1', 'guest-8', 'route', 0, 2_328_331_381],
    ] as const;
    for (const [worldSeed, entityId, domainTag, ordinal, expected] of vectors) {
      expect(keyedRandomUint32(worldSeed, entityId, domainTag, ordinal)).toBe(expected);
      expect(keyedRandomUint32(worldSeed, entityId, domainTag, ordinal)).toBe(expected);
    }
  });

  it('isolates world, entity, domain, and ordinal key dimensions', () => {
    const baseline = keyedRandomUint32('world', 'guest-1', 'route', 3);
    expect(keyedRandomUint32('other-world', 'guest-1', 'route', 3)).not.toBe(baseline);
    expect(keyedRandomUint32('world', 'guest-2', 'route', 3)).not.toBe(baseline);
    expect(keyedRandomUint32('world', 'guest-1', 'mood', 3)).not.toBe(baseline);
    expect(keyedRandomUint32('world', 'guest-1', 'route', 4)).not.toBe(baseline);
  });

  it('keeps an unrelated entity/domain sequence unchanged when another gets draws', () => {
    const expected = [0, 1, 2, 3].map((ordinal) =>
      keyedRandomUint32('world', 'guest-b', 'route', ordinal));
    // guest-a gets an arbitrary number of decisions between guest-b draws.
    const observed = [0, 1, 2, 3].map((ordinal) => {
      for (let draw = 0; draw < ordinal + 2; draw += 1) {
        keyedRandomUint32('world', 'guest-a', 'route', draw);
      }
      return keyedRandomUint32('world', 'guest-b', 'route', ordinal);
    });
    expect(observed).toEqual(expected);
  });

  it('returns deterministic floats in [0, 1)', () => {
    const value = keyedRandomFloat('world-1', 'guest-7', 'route', 0);
    expect(value).toBe((3_468_365_962 + 0.5) / 0x1_0000_0000);
    expect(value).toBeGreaterThanOrEqual(0);
    expect(value).toBeLessThan(1);
  });

  it('keeps the named uniform draw strictly inside both endpoints', () => {
    const value = uniformOpen01('world-1', 'guest-7', 'route', 0);
    expect(value).toBeGreaterThan(0);
    expect(value).toBeLessThan(1);
    expect(value).toBe((3_468_365_962 + 0.5) / 0x1_0000_0000);
  });

  it('extracts stable uint24 arrival-CDF samples from the high bits', () => {
    expect(keyedRandomUint24('world-1', 'guest-7', 'route', 0)).toBe(13_548_304);
    expect(keyedRandomUint24('world-1', 'guest-7', 'route', 1)).toBe(6_669_241);
    expect(keyedRandomUint24('world-1', 'guest-7', 'mood', 0)).toBe(13_217_101);
    expect(keyedRandomUint24('world-1', 'guest-7', 'route', 0)).toBe(
      keyedRandomUint32('world-1', 'guest-7', 'route', 0) >>> 8,
    );
  });

  it('uses rejection sampling for an incomplete bounded range', () => {
    // For this vector ordinals 0, 1, and 2 are in the rejected tail of a
    // 2^31+1-sized range; ordinal 3 is the first accepted candidate.
    expect(keyedRandomInt('w', 'g0', 'bounded', 0, 0, 2_147_483_648)).toBe(1_596_757_706);
    expect(keyedRandomInt('w', 'g0', 'bounded', 0, -10, 2_147_483_638)).toBe(1_596_757_696);
  });

  it('offers the same keyed behavior through the world facade', () => {
    const random = new KeyedRandom('world');
    expect(random.uint32('guest', 'route', 2)).toBe(keyedRandomUint32('world', 'guest', 'route', 2));
    expect(random.float01('guest', 'route', 2)).toBe(keyedRandomFloat('world', 'guest', 'route', 2));
    expect(random.int('guest', 'route', 2, 4, 9)).toBeGreaterThanOrEqual(4);
    expect(random.int('guest', 'route', 2, 4, 9)).toBeLessThanOrEqual(9);
  });
});
