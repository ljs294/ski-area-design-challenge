import { describe, expect, it } from 'vitest';
import { boundedWeatherChunks, matchesWeatherReadinessAck, weatherCoverageContains } from './useDualClockRuntime';

const ack = {
  requestId: 7, generation: 3,
  acceptedFrom: '2026-11-02T08:00:00.000Z', acceptedTo: '2026-11-05T08:00:00.000Z',
};

describe('dual-clock weather readiness', () => {
  it('accepts matching coverage even when a newer generic response id exists', () => {
    expect(matchesWeatherReadinessAck(ack, 7, 3, '2026-11-03T12:00:00.000Z')).toBe(true);
  });

  it.each([
    ['wrong request', 8, 3], ['wrong generation', 7, 4],
  ])('rejects %s acknowledgements', (_name, requestId, generation) => {
    expect(matchesWeatherReadinessAck(ack, requestId, generation, '2026-11-03T12:00:00.000Z')).toBe(false);
  });

  it('does not become ready when a nonempty window misses the current hour', () => {
    expect(weatherCoverageContains(ack, '2026-11-06T08:00:00.000Z')).toBe(false);
    expect(weatherCoverageContains(ack, '2026-11-02T08:00:00.000Z')).toBe(true);
  });

  it('requires a bounded accepted window with valid ordered dates', () => {
    expect(weatherCoverageContains({ ...ack, acceptedFrom: 'bad' }, ack.acceptedFrom)).toBe(false);
    expect(weatherCoverageContains({ ...ack, acceptedFrom: ack.acceptedTo, acceptedTo: ack.acceptedFrom }, ack.acceptedFrom)).toBe(false);
  });

  it('splits bulk weather transport into bounded sequential chunks', () => {
    const hours = Array.from({ length: 257 }, (_, index) => ({ at: `2026-11-${String(2 + Math.floor(index / 24)).padStart(2, '0')}T${String(index % 24).padStart(2, '0')}:00:00.000Z` })) as never;
    expect(boundedWeatherChunks(hours)).toHaveLength(4);
    expect(boundedWeatherChunks(hours).map(chunk => chunk.length)).toEqual([72, 72, 72, 41]);
    expect(Math.max(...boundedWeatherChunks(hours).map(chunk => chunk.length))).toBeLessThanOrEqual(72);
  });
});
