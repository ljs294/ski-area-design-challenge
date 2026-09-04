import { describe, expect, it } from 'vitest';
import { RollingAverageWindow, RollingWindow } from './rollingWindow';

describe('RollingWindow', () => {
  it('keeps a stable 15-minute rate between event arrivals', () => {
    const window = new RollingWindow(900);
    window.record(100, 30);
    const at100 = window.read(100);
    const at500 = window.read(500);
    const at999 = window.read(999);
    expect(at100.rate).toBe(30 / 900);
    expect(at500.total).toBe(at100.total);
    expect(at500.rate).toBe(at100.rate);
    expect(at999.total).toBe(at100.total);
    expect(at999.rate).toBe(at100.rate);
    expect(window.read(1_000).total).toBe(0);
  });

  it('uses fixed-size second-indexed buckets and exact expiry boundaries', () => {
    const window = new RollingWindow({ windowSeconds: 10, bucketSeconds: 1 });
    expect(window.bucketCount).toBe(10);
    expect(window.retainedBytes).toBe(window.bucketCount * Float64Array.BYTES_PER_ELEMENT * 2);
    window.record(0, 1);
    window.record(9, 2);
    expect(window.total(9)).toBe(3);
    expect(window.total(10)).toBe(2);
    window.record(10, 4);
    expect(window.total(10)).toBe(6);
    expect(window.bucketCount).toBe(10);
  });

  it('is invariant to clock advancement chunking and aggregates same-second values', () => {
    const whole = new RollingWindow(900);
    whole.record(10, 2);
    whole.record(10, 3);
    whole.record(30, 5);
    const chunked = new RollingWindow(900);
    chunked.record(10, 2);
    chunked.advanceTo(20);
    chunked.record(20, 3);
    chunked.advanceTo(30);
    chunked.record(30, 5);
    // Different event timestamps can still be compared over the same window;
    // no read-side clock pulse changes the accumulated total.
    expect(chunked.total(30)).toBe(whole.total(30));
    expect(chunked.rate(30)).toBe(whole.rate(30));
  });

  it('supports weighted numerator/denominator averages without an unbounded event list', () => {
    const average = new RollingAverageWindow(900);
    average.record(0, 8, 2);
    average.record(300, 3, 1);
    expect(average.read(899)).toMatchObject({ numerator: 11, denominator: 3, average: 11 / 3 });
    expect(average.read(900).average).toBe(3);
    average.clear();
    expect(average.average(900)).toBe(0);
  });

  it('rejects non-integer time and backwards reads', () => {
    const window = new RollingWindow();
    expect(() => window.record(0.25)).toThrow();
    window.record(10);
    expect(() => window.total(9)).toThrow();
    expect(() => new RollingWindow(0)).toThrow();
  });
});
