import { describe, expect, it } from 'vitest';
import {
  PHASE_4_INJURY_FORMULAS,
  assertInjuryTraversalResult,
  evaluateTraversalInjury,
  injuryTraversalChecksum,
  isInjuryTraversalResult,
  traversalIncidents,
  type InjuryTraversalInput,
} from './injury.ts';

const baseInput: InjuryTraversalInput = {
  worldSeed: 'phase4-golden-world',
  guestId: 'guest-42',
  runId: 'run-blue-1',
  traversalId: 'guest-42:run-blue-1:0',
  entryTick: 1_000,
  durationSeconds: 300,
  decisionOrdinal: 0,
  ability: 0.8,
  effectiveDifficulty: 0.45,
  traffic: 0,
  coverage: 1,
  grooming: 1,
  exposure: 0,
};

function evaluate(overrides: Partial<InjuryTraversalInput> = {}) {
  return evaluateTraversalInjury({ ...baseInput, ...overrides });
}

describe('Phase 4 traversal injury hazard', () => {
  it('is versioned, explicit about calibration, and deterministic at run entry', () => {
    const first = evaluate({ ability: 1, effectiveDifficulty: 0 });
    const second = evaluate({ ability: 1, effectiveDifficulty: 0 });
    expect(first).toEqual(second);
    expect(first.formulaVersion).toBe(PHASE_4_INJURY_FORMULAS.version);
    expect(first.calibration).toBe('uncalibrated');
    expect(first.randomDrawCount).toBe(1);
    expect(first.hazardScore).toBe(0);
    expect(first.probability).toBe(0);
    expect(first.scheduledIncident).toBeNull();
    expect(first.severity).toBe('none');
    expect(first.checksum).toBe(injuryTraversalChecksum(first));
    expect(Object.isFrozen(first)).toBe(true);
    expect(Object.isFrozen(first.factors)).toBe(true);
    expect(isInjuryTraversalResult(first)).toBe(true);
  });

  it('uses positive directional contributions for every risk input', () => {
    const baseline = evaluate();
    expect(evaluate({ ability: 0.2 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ effectiveDifficulty: 0.9 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ traffic: 2 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ coverage: 0.25 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ grooming: 0.1 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ exposure: 1 }).probability).toBeGreaterThan(baseline.probability);
    expect(evaluate({ effectiveDifficulty: 0.9, ability: 0.2 }).factors.abilityDeficit).toBeGreaterThan(
      evaluate({ effectiveDifficulty: 0.45, ability: 0.8 }).factors.abilityDeficit,
    );
  });

  it('schedules zero or one incident in the half-open traversal interval', () => {
    const safe = evaluate();
    expect(traversalIncidents(safe)).toHaveLength(0);

    const highRisk: Partial<InjuryTraversalInput> = {
      ability: 0,
      effectiveDifficulty: 1,
      traffic: 4,
      coverage: 0,
      grooming: 0,
      exposure: 1,
      durationSeconds: 900,
    };
    let event = null as ReturnType<typeof evaluate> | null;
    for (let ordinal = 0; ordinal < 250; ordinal += 1) {
      const candidate = evaluate({ ...highRisk, decisionOrdinal: ordinal, traversalId: `high-risk:${ordinal}` });
      if (candidate.scheduledIncident !== null) {
        event = candidate;
        break;
      }
    }
    expect(event).not.toBeNull();
    const incident = event!.scheduledIncident!;
    expect(traversalIncidents(event!)).toEqual([incident]);
    expect(traversalIncidents(event!)).toHaveLength(1);
    expect(incident.incidentTick).toBeGreaterThanOrEqual(event!.entryTick);
    expect(incident.incidentTick).toBeLessThan(event!.entryTick + event!.durationSeconds);
    expect(incident.positionFraction).toBeGreaterThanOrEqual(0);
    expect(incident.positionFraction).toBeLessThan(1);
  });

  it('keeps reason vectors typed, weighted, and explainable', () => {
    const result = evaluate({ ability: 0.1, effectiveDifficulty: 0.9, traffic: 1.5, coverage: 0.4, grooming: 0.2, exposure: 0.8 });
    const contributions = Object.values(result.reasonVector);
    expect(contributions.every((value) => value >= 0 && value <= 1)).toBe(true);
    expect(result.hazardScore).toBeCloseTo(contributions.reduce((sum, value) => sum + value, 0), 12);
    expect(result.reasonVector.abilityDeficit).toBeGreaterThan(result.reasonVector.effectiveDifficulty);
    if (result.scheduledIncident !== null) {
      expect(result.scheduledIncident.primaryReasonCode).toBe('abilityDeficit');
      expect(result.scheduledIncident.reasonVector).toBe(result.reasonVector);
    }
  });

  it('rejects malformed inputs and tampered persisted results', () => {
    expect(() => evaluate({ coverage: 1.1 })).toThrow(/coverage/);
    expect(() => evaluate({ grooming: -0.1 })).toThrow(/grooming/);
    expect(() => evaluate({ traffic: -1 })).toThrow(/traffic/);
    expect(() => evaluate({ durationSeconds: 0 })).toThrow(/durationSeconds/);
    const result = evaluate({ ability: 0.2, effectiveDifficulty: 0.9, exposure: 1 });
    const tampered = { ...result, probability: result.probability + 0.01 };
    expect(isInjuryTraversalResult(tampered)).toBe(false);
    expect(() => assertInjuryTraversalResult(tampered)).toThrow();
  });

  it('is monotonic with longer exposure duration until the configured cap', () => {
    const short = evaluate({ exposure: 1, durationSeconds: 30 });
    const reference = evaluate({ exposure: 1, durationSeconds: 300 });
    const long = evaluate({ exposure: 1, durationSeconds: 1_200 });
    expect(reference.probability).toBeGreaterThan(short.probability);
    expect(long.probability).toBeGreaterThan(reference.probability);
    expect(long.probability).toBeLessThanOrEqual(PHASE_4_INJURY_FORMULAS.maximumProbability);
  });
});
