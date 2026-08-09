import { describe, expect, it } from 'vitest';
import { buildSnowmakingPipe, EMPTY_SNOWMAKING_NODE_NEXT_NUMBERS } from '../snowmakingNetwork';
import type { SavedSnowmakingNode } from '../types/snowmaking';
import { applySnowmakingNodeCandidate, inlinePumpCandidate,
  resolveSnowmakingPipeDraft } from './snowmakingNodePlacement';

const pipe = buildSnowmakingPipe({ id: 'pipe-1', name: 'Main', diameterIn: 8,
  points: [[0, 0], [0, 0.001]], nodeIds: [null, null], createdAt: '2026-01-01' }, () => 100);
const base = { nodes: [] as SavedSnowmakingNode[], pipes: [pipe], guns: [],
  nextNumbers: { ...EMPTY_SNOWMAKING_NODE_NEXT_NUMBERS } };
const snap = (point: [number, number]) => ({ kind: 'pipe' as const, pipeId: pipe.id, point });

describe('inline snowmaking pump placement', () => {
  it('rejects segment endpoints and requires an interior pipe location', () => {
    expect(inlinePumpCandidate({ pipes: [pipe], nodes: [], snap: snap([0, 0]), revision: 2,
      sampleElevation: () => 100 })).toBeTypeOf('string');
    expect(inlinePumpCandidate({ pipes: [], nodes: [], snap: snap([0, 0.0005]), revision: 2,
      sampleElevation: () => 100 })).toBeTypeOf('string');
  });

  it('rejects placing a pump directly on a water-source intake', () => {
    const source: SavedSnowmakingNode = { id: 'source', kind: 'intake', name: 'Pond Intake',
      point: [0, 0], elevM: 100, source: { kind: 'pond', pondId: 'pond' }, createdAt: 'now' };
    const sourcePipe = buildSnowmakingPipe({ id: 'source-pipe', name: 'Source main', diameterIn: 8,
      points: [[0, 0], [0, 0.001]], nodeIds: [source.id, null], createdAt: 'now' }, () => 100);
    expect(inlinePumpCandidate({ pipes: [sourcePipe], nodes: [source],
      snap: { kind: 'pipe', pipeId: sourcePipe.id, point: source.point }, revision: 2,
      sampleElevation: () => 100 })).toContain('cannot occupy the water source');
  });

  it('commits the selected direction with the inline node in one state change', () => {
    const candidate = inlinePumpCandidate({ pipes: [pipe], nodes: [], snap: snap([0, 0.0005]), revision: 2,
      sampleElevation: () => 100 });
    expect(typeof candidate).not.toBe('string');
    if (typeof candidate === 'string') return;
    candidate.pumpSuctionSide = 'route-end';
    let id = 0;
    const result = applySnowmakingNodeCandidate(base,
      { phase: 'placing', kind: 'pump', candidate, error: null }, () => `id-${++id}`, () => '2026-01-01');
    expect(typeof result).not.toBe('string');
    if (typeof result === 'string') return;
    expect(result.nodes).toHaveLength(1);
    expect(result.pipes[0].segments?.map((segment) => [segment.endPumpPort,
      segment.startPumpPort])).toEqual([['discharge', null], [null, 'suction']]);
  });

  it('prevents a new pipe endpoint from attaching to an existing pump', () => {
    const pump: SavedSnowmakingNode = { id: 'pump-1', kind: 'pump', name: 'Pump 1',
      labelNumber: 1, point: [0, 0], elevM: 100, createdAt: '2026-01-01' };
    const result = resolveSnowmakingPipeDraft({ ...base, nodes: [pump] }, { phase: 'review',
      name: 'Branch', error: null, points: [{ point: pump.point,
        snap: { kind: 'node', nodeId: pump.id, point: pump.point } },
      { point: [0.001, 0], snap: null }] }, () => 'new-id', () => '2026-01-01');
    expect(result).toBe('Connect new pipes at a junction, not directly to a pump.');
  });
});
