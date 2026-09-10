import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { SkiNetwork } from '../network';
import { LiftOperationsRows, liftOperationsFromFlow } from './liftOperations';

const network = { liftEdgeIds: new Map([['summit', 'l:summit']]) } as unknown as SkiNetwork;

describe('lift operations read model', () => {
  it('uses the network lift-edge mapping and retains known counts when service is closed', () => {
    const operations = liftOperationsFromFlow('summit', network, { admitted: 0, active: 0, departed: 0, turnedAway: 0,
      ticketRevenueCents: 0, amenityRevenueCents: 0, completedRuns: 0, trails: {}, queues: {
        'l:summit': { guests: 12, riders: 8, boarded: 14, dailyBoarded: 14, dailyAccuracy: 'complete',
          waitSeconds: 0, serviceAvailable: false, serviceUnavailableReason: 'closed' },
      } });
    expect(operations).toMatchObject({ queued: 12, currentRiders: 8, servedToday: 14,
      wait: { state: 'unavailable', reason: 'Lift is closed.' } });
  });

  it('does not invent telemetry and identifies legacy served counts as partial', () => {
    expect(liftOperationsFromFlow('unknown', network, null)).toBeNull();
    const uninitialized = renderToStaticMarkup(<LiftOperationsRows operations={null} />);
    expect(uninitialized).not.toMatch(/lift-stat-value">0</);
    expect(uninitialized).not.toContain('partial');
    const operations = liftOperationsFromFlow('summit', network, { admitted: 0, active: 0, departed: 0, turnedAway: 0,
      ticketRevenueCents: 0, amenityRevenueCents: 0, completedRuns: 0, trails: {}, queues: {
        'l:summit': { guests: 3, waitSeconds: 90, boarded: 7 },
      } });
    const html = renderToStaticMarkup(<LiftOperationsRows operations={operations} />);
    expect(html).toContain('People in line');
    expect(html).toContain('1:30');
    expect(html).toContain('Currently riding');
    expect(html).toContain('>—<');
    expect(html).toContain('7 (partial)');
  });

  it('keeps observed passenger counts while a viable route is unavailable', () => {
    const operations = liftOperationsFromFlow('summit', network, { admitted: 0, active: 0, departed: 0, turnedAway: 0,
      ticketRevenueCents: 0, amenityRevenueCents: 0, completedRuns: 0, trails: {}, queues: {
        'l:summit': { guests: 4, riders: 2, boarded: 9, waitSeconds: 0, serviceAvailable: false,
          serviceUnavailableReason: 'no-descent', dailyBoarded: 9, dailyAccuracy: 'complete' },
      } });
    const html = renderToStaticMarkup(<LiftOperationsRows operations={operations} />);
    expect(html).toContain('4');
    expect(html).toContain('2');
    expect(html).toContain('9');
    expect(html).toContain('No viable route is open from this lift.');
  });
});
