import { describe, expect, it } from 'vitest';
import {
  PHASE_3_REPUTATION_FORMULAS,
  assertReputationLedger,
  createReputationLedger,
  evaluateReputationSignal,
  isReputationLedger,
  recordExperienceSignal,
  recordSafetyMetricsSignal,
  recordSafetySignal,
  reputationLedgerChecksum,
  reputationScore,
  type ReputationSignal,
} from './reputation.ts';

function experience(overrides: Partial<Extract<ReputationSignal, { kind: 'experience' }>> = {}): Extract<ReputationSignal, { kind: 'experience' }> {
  return { kind: 'experience', eventId: 'experience-1', guestId: 'guest-1', tick: 100,
    satisfaction: 1, reasonCode: 'positive-experience', ...overrides };
}

describe('Phase 3 reputation ledger', () => {
  it('creates a versioned bounded baseline and validates its checksum', () => {
    const ledger = createReputationLedger({ maximumSignals: 3 });
    expect(ledger.scoreBps).toBe(PHASE_3_REPUTATION_FORMULAS.baselineScoreBps);
    expect(ledger.metrics.appliedSignalCount).toBe(0);
    expect(ledger.maximumSignals).toBe(3);
    expect(ledger.checksum).toBe(reputationLedgerChecksum(ledger));
    expect(isReputationLedger(ledger)).toBe(true);
    expect(Object.isFrozen(ledger)).toBe(true);
  });

  it('turns experience channels into a reconciled reason vector', () => {
    const record = evaluateReputationSignal({ kind: 'experience', eventId: 'channels-1', guestId: 'guest-1', tick: 1,
      channels: { terrain: 1, wait: 0.25, comfort: 0.75 }, weight: 0.5 });
    const sum = Object.values(record.reasonVectorBps).reduce((total, value) => total + value, 0);
    expect(sum).toBe(record.deltaBps);
    expect(record.reasonVectorBps.terrain).toBeGreaterThan(0);
    expect(record.reasonVectorBps.wait).toBeLessThan(0);
    expect(record.reasonVectorBps.comfort).toBeGreaterThan(0);
    expect(record.checksum).toBeDefined();
  });

  it('supports scalar/thought experience signals and negative dissatisfaction', () => {
    const positive = evaluateReputationSignal(experience());
    const negative = evaluateReputationSignal(experience({ eventId: 'experience-2', satisfaction: 0, reasonCode: 'long-wait' }));
    const neutral = evaluateReputationSignal(experience({ eventId: 'experience-3', satisfaction: 0.5, sentiment: 'neutral' }));
    expect(positive.deltaBps).toBeGreaterThan(0);
    expect(negative.deltaBps).toBeLessThan(0);
    expect(negative.reasonVectorBps['long-wait']).toBe(negative.deltaBps);
    expect(neutral.deltaBps).toBe(0);
  });

  it('applies safety severity, outcome, and response penalties', () => {
    const minor = evaluateReputationSignal({ kind: 'safety', eventId: 'safety-minor', guestId: 'guest-1', tick: 1,
      severity: 'minor', outcome: 'resolved', responseSeconds: 60 });
    const major = evaluateReputationSignal({ kind: 'safety', eventId: 'safety-major', guestId: 'guest-1', tick: 1,
      severity: 'major', outcome: 'failed', responseSeconds: 1_800 });
    expect(minor.deltaBps).toBeLessThan(0);
    expect(major.deltaBps).toBeLessThan(minor.deltaBps);
    expect(major.reasonVectorBps['safety-incident']).toBeLessThan(0);
    expect(major.reasonVectorBps['safety-response']).toBeLessThan(0);
  });

  it('can consume an aggregate safety observation exactly once', () => {
    const ledger = createReputationLedger();
    const input = { eventId: 'safety-day-1', guestId: 'resort', tick: 86_400, observedTraversals: 100, incidentCount: 2, failedIncidents: 1 };
    const first = recordSafetyMetricsSignal(ledger, input);
    const duplicate = recordSafetyMetricsSignal(first.ledger, input);
    expect(first.applied).toBe(true);
    expect(duplicate.applied).toBe(false);
    expect(duplicate.duplicate).toBe(true);
    expect(duplicate.ledger).toBe(first.ledger);
  });

  it('deduplicates retries and preserves additive order-independent replay', () => {
    const empty = createReputationLedger();
    const first = recordExperienceSignal(empty, experience({ eventId: 'b', tick: 20, satisfaction: 0.2 }));
    const duplicate = recordExperienceSignal(first.ledger, experience({ eventId: 'b', tick: 20, satisfaction: 0.2 }));
    expect(duplicate.ledger).toBe(first.ledger);
    const second = recordSafetySignal(first.ledger, { eventId: 'a', guestId: 'guest-1', tick: 10, severity: 'minor', outcome: 'resolved' });
    const replay = recordSafetySignal(empty, { eventId: 'a', guestId: 'guest-1', tick: 10, severity: 'minor', outcome: 'resolved' });
    const replayBoth = recordExperienceSignal(replay.ledger, experience({ eventId: 'b', tick: 20, satisfaction: 0.2 }));
    expect(second.ledger.scoreBps).toBe(replayBoth.ledger.scoreBps);
    expect(second.ledger.metrics.netDeltaBps).toBe(replayBoth.ledger.metrics.netDeltaBps);
    expect(second.ledger.appliedEventIds).toEqual(['a', 'b']);
  });

  it('rejects reuse of an event id for a different payload', () => {
    const ledger = recordExperienceSignal(createReputationLedger(), experience()).ledger;
    expect(() => recordExperienceSignal(ledger, experience({ satisfaction: 0 }))).toThrow(/reused/i);
  });

  it('keeps the score bounded while preserving the unbounded additive metric', () => {
    let ledger = createReputationLedger({ baselineScoreBps: 5_000, maximumSignals: 4 });
    for (let index = 0; index < 4; index += 1) {
      ledger = recordExperienceSignal(ledger, experience({ eventId: `positive-${index}`, tick: index, satisfaction: 1 })).ledger;
    }
    expect(ledger.scoreBps).toBeLessThanOrEqual(ledger.maximumScoreBps);
    expect(reputationScore(ledger)).toBeLessThanOrEqual(1);
    expect(ledger.metrics.netDeltaBps).toBeGreaterThan(0);
    expect(() => recordExperienceSignal(ledger, experience({ eventId: 'overflow', tick: 9 }))).toThrow(/capacity/i);
  });

  it('rejects tampered ledger and malformed signals', () => {
    const ledger = recordExperienceSignal(createReputationLedger(), experience()).ledger;
    expect(() => assertReputationLedger({ ...ledger, scoreBps: ledger.scoreBps + 1 })).toThrow(/checksum|reconcile/i);
    expect(() => assertReputationLedger({ ...ledger, scoreBps: ledger.maximumScoreBps + 1,
      checksum: reputationLedgerChecksum({ ...ledger, scoreBps: ledger.maximumScoreBps + 1 }) })).toThrow(/outside|reconcile/i);
    expect(() => evaluateReputationSignal(experience({ eventId: '' }))).toThrow(/eventId/i);
    expect(() => evaluateReputationSignal({ ...experience({ eventId: 'bad-channel' }), channels: { terrain: 2 } })).toThrow(/channel terrain/i);
  });
});
