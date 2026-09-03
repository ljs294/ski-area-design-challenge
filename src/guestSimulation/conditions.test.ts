import { describe, expect, it } from 'vitest';
import {
  ConditionValidationError,
  applyConditionUpdates,
  conditionAwareRouteScoringInputs,
  conditionSnapshotChecksum,
  createConditionDomain,
  createConditionSnapshot,
  effectiveDifficulty,
  isConditionSnapshot,
  scoreConditionAwareRoute,
  sortConditionUpdates,
} from './conditions.ts';

function initialConditions() {
  return createConditionSnapshot({
    tick: 10,
    revision: 4,
    edges: [
      { edgeId: 'trail-z', baseDifficulty: 0.72, grooming: 0.15, snowQuality: 0.4, coverage: 0.8, occupancy: 8, capacity: 10 },
      { edgeId: 'trail-a', baseDifficulty: 0.3, grooming: { quality: 0.9 }, snowQuality: { quality: 0.8 }, coverage: { fraction: 1, depthCm: 45 }, occupancy: { guests: 1, capacity: 10 } },
    ],
  });
}

describe('Phase 2 guest condition domain', () => {
  it('normalizes edge records in stable order and owns every nested value', () => {
    const snapshot = initialConditions();
    expect(snapshot.edges.map((edge) => edge.edgeId)).toEqual(['trail-a', 'trail-z']);
    expect(snapshot.edges[0]?.revision).toBe(4);
    expect(snapshot.edges[0]?.occupancy.crowding.level).toBe('light');
    expect(snapshot.edges[1]?.terrainCharacter).toBe('variable');
    expect(Object.isFrozen(snapshot)).toBe(true);
    expect(Object.isFrozen(snapshot.edges)).toBe(true);
    expect(Object.isFrozen(snapshot.edges[0])).toBe(true);
    expect(Object.isFrozen(snapshot.edges[0]?.grooming)).toBe(true);
    expect(snapshot.checksum).toBe(conditionSnapshotChecksum(snapshot));
    expect(isConditionSnapshot(snapshot)).toBe(true);
  });

  it('makes condition penalties monotonic and derives comfort from the same record', () => {
    const ideal = effectiveDifficulty(0.4, 1, 1, 1, 0);
    const poor = effectiveDifficulty(0.4, 0, 0, 0.25, 1.4);
    expect(poor).toBeGreaterThan(ideal);
    const trail = initialConditions().edges.find((edge) => edge.edgeId === 'trail-a')!;
    const rough = initialConditions().edges.find((edge) => edge.edgeId === 'trail-z')!;
    expect(trail.comfort).toBeGreaterThan(rough.comfort);
    expect(rough.effectiveDifficulty).toBeGreaterThan(rough.baseDifficulty);
  });

  it('sorts a batch deterministically and advances global and per-edge revisions', () => {
    const before = initialConditions();
    const updates = [
      { edgeId: 'trail-z', coverage: 0.55, sequence: 2 },
      { edgeId: 'trail-a', occupancy: 5, capacity: 10, sequence: 1 },
    ] as const;
    const first = applyConditionUpdates(before, updates, { tick: 11, expectedRevision: 4 });
    const second = applyConditionUpdates(before, [...updates].reverse(), { tick: 11, expectedRevision: 4 });
    expect(first).toEqual(second);
    expect(first.revision).toBe(5);
    expect(first.edges.every((edge) => edge.revision === 5)).toBe(true);
    expect(isConditionSnapshot(first)).toBe(true);
    expect(first.edges.find((edge) => edge.edgeId === 'trail-a')?.occupancy.fraction).toBe(0.5);
    expect(sortConditionUpdates([...updates].reverse()).map((update) => update.edgeId)).toEqual(['trail-a', 'trail-z']);
  });

  it('rejects stale updates, unknown edges, malformed summaries, and tampered checksums', () => {
    const before = initialConditions();
    expect(() => applyConditionUpdates(before, [{ edgeId: 'trail-a', grooming: 0.1, expectedRevision: 3 }])).toThrow(ConditionValidationError);
    expect(() => applyConditionUpdates(before, [{ edgeId: 'missing', grooming: 0.1 }])).toThrow(ConditionValidationError);
    expect(() => createConditionSnapshot({ edges: [{ edgeId: 'bad', baseDifficulty: 1.2 }] })).toThrow(ConditionValidationError);
    expect(() => createConditionSnapshot({ edges: [{ edgeId: 'bad', grooming: { quality: 0.2, status: 'groomed' } }] })).toThrow(ConditionValidationError);
    expect(isConditionSnapshot({ ...before, checksum: 'tampered' })).toBe(false);
  });

  it('exposes condition-aware route inputs and a bounded score', () => {
    const snapshot = initialConditions();
    const inputs = conditionAwareRouteScoringInputs(snapshot, ['trail-a', 'trail-z']);
    expect(inputs.segments.map((segment) => segment.edgeId)).toEqual(['trail-a', 'trail-z']);
    expect(inputs.minimumComfort).toBeLessThan(inputs.averageComfort);
    const score = scoreConditionAwareRoute(snapshot, ['trail-a', 'trail-z'], { ability: 1, comfortDemand: 0.8 });
    expect(score.canProceed).toBe(true);
    expect(score.score).toBeGreaterThanOrEqual(0);
    expect(score.score).toBeLessThanOrEqual(1);
    expect(() => conditionAwareRouteScoringInputs(snapshot, ['missing'])).toThrow(ConditionValidationError);
  });

  it('keeps above-ability terrain eligible while applying a strong compatibility penalty', () => {
    const snapshot = createConditionSnapshot({ revision: 1, tick: 10, edges: [{
      edgeId: 'expert-run', baseDifficulty: 0.95, grooming: 0.25, snowQuality: 0.35,
      coverage: 0.8, occupancy: { guests: 20, capacity: 40 },
    }] });
    const score = scoreConditionAwareRoute(snapshot, ['expert-run'], { ability: 0.2, targetDifficulty: 0.3 });
    expect(score.canProceed).toBe(true);
    expect(score.compatibility).toBeLessThan(0.25);
  });

  it('offers an immutable-snapshot stateful facade with no-op identity for empty batches', () => {
    const domain = createConditionDomain(initialConditions());
    const current = domain.snapshot;
    expect(domain.update([])).toBe(current);
    const next = domain.update([{ edgeId: 'trail-a', snowQuality: 0.2 }], 12);
    expect(next).not.toBe(current);
    expect(domain.getSnapshot()).toBe(next);
    expect(next.tick).toBe(12);
  });
});
