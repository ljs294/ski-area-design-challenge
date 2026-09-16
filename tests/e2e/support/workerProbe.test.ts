import { describe, expect, it } from 'vitest';
import { BoundedProbeRequestLedger, classifyDualClockProbePublications, orderedProbeRing, type WorkerProbePublication } from './workerProbe';

const response = (overrides: Partial<WorkerProbePublication>): WorkerProbePublication => ({
  id: 1, type: 'publication', generation: 4, operationGeneration: 7, committedRevision: 9,
  selectedId: null, ...overrides,
});

describe('dual-clock worker probe classification', () => {
  it('keeps acknowledgements and stale generations from replacing the latest accepted movement publication', () => {
    const movement = response({ macroSecond: 100, microSecond: 80, movementCount: 32, flow: { active: 32 } });
    const aggregate = response({ id: 2, macroSecond: 120, microSecond: 80, movementCount: 0, flow: { active: 32 } });
    const weatherAck = response({ id: 3, type: 'weather-ack' });
    const stale = response({ id: 4, generation: 3, macroSecond: 200, microSecond: 160,
      movementCount: 0, flow: { active: 0 } });
    const classified = classifyDualClockProbePublications([movement, aggregate, weatherAck, stale],
      { generation: 4, operationGeneration: 7 });
    expect(classified.measured).toEqual([movement, aggregate]);
    expect(classified.measured.at(-1)?.movementCount).toBe(0);
    expect(classified.acknowledgements).toEqual([weatherAck]);
    expect(classified.stale).toEqual([stale]);
  });

  it('reads a wrapped fixed ring in chronological order', () => {
    expect(orderedProbeRing(['four', 'five', 'three'], 3, 2)).toEqual(['three', 'four', 'five']);
    expect(orderedProbeRing(['one', 'two'], 2, 0)).toEqual(['one', 'two']);
  });

  it('bounds unresolved requests, retires terminal responses, and clears on cancellation', () => {
    const ledger = new BoundedProbeRequestLedger<string>(3);
    for (let id = 1; id <= 10_000; id++) ledger.set(id, `request-${id}`);
    expect(ledger.size).toBe(3);
    expect(ledger.dropped).toBe(9_997);
    expect(ledger.peek(9_998)).toBe('request-9998');
    expect(ledger.complete(9_999)).toBe('request-9999');
    expect(ledger.size).toBe(2);
    ledger.clear();
    expect(ledger.size).toBe(0);
  });
});
