import { expect, it } from 'vitest';
import { decodeDualMovement, DualMovementPublisher } from './dualMovementPublication';

it('transfers precise coordinates, bounded motion columns and safely reuses returned buffers', () => {
  const publisher = new DualMovementPublisher();
  const first = publisher.frame([{ id: 'selected', lng: -120.123456789, lat: 45.987654321, status: 'skiing',
    motion: { routeId: 'trail@revision', lane: 8, progress: 0.123456789, duration: 300 } }])!;
  const decoded = decodeDualMovement(first);
  expect(decoded[0]).toMatchObject({ id: 'selected', lng: -120.123456789, lat: 45.987654321, status: 'skiing' });
  expect(decoded[0].motion?.progress).toBeCloseTo(0.123456789, 7);
  publisher.recycle(first.buffer);
  const second = publisher.frame([{ id: 'next', lng: 1, lat: 2, status: 'resting' }])!;
  expect(second.buffer).toBe(first.buffer);
  expect(decodeDualMovement(second)[0].motion).toBeUndefined();
  expect(decoded[0].lng).toBe(-120.123456789);
  expect(publisher.frame([])).toBeUndefined();
});

it('appends trail queue status after the existing transport codes', () => {
  const publisher = new DualMovementPublisher();
  const frame = publisher.frame([
    { id: 'lift', lng: 0, lat: 0, status: 'lift-queue' },
    { id: 'trail', lng: 1, lat: 1, status: 'trail-queue' },
  ])!;
  const decoded = decodeDualMovement(frame);
  expect(decoded.map(point => point.status)).toEqual(['lift-queue', 'trail-queue']);
});
