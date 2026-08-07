import { describe, expect, it } from 'vitest';
import { IDLE_NODE_TOOL, IDLE_PATH_TOOL, pathFromReview,
  reduceNodeTool, reducePathTool } from './nodePathControllerModel';

const anchor = { kind: 'trail' as const, trailId: 'trail-1', point: [0, 0] as [number, number] };
const destination = { kind: 'trail' as const, trailId: 'trail-2', point: [2, 2] as [number, number] };

describe('node/path controller model', () => {
  it('retains node tool mode while clearing a committed candidate', () => {
    let state = reduceNodeTool(IDLE_NODE_TOOL, { type: 'arm', phase: 'add' });
    state = reduceNodeTool(state, { type: 'add-candidate', candidate: anchor, error: null });
    expect(reduceNodeTool(state, { type: 'committed' })).toEqual({
      phase: 'add', candidate: null, error: null,
    });
  });

  it('undoes a one-point path back to armed and snaps the review endpoint', () => {
    let state = reducePathTool(IDLE_PATH_TOOL, { type: 'arm' });
    state = reducePathTool(state, { type: 'start', anchor });
    expect(reducePathTool(state, { type: 'undo' })).toEqual({ phase: 'armed' });
    state = reducePathTool(state, { type: 'start', anchor });
    state = reducePathTool(state, { type: 'add-point', point: [1, 1] });
    state = reducePathTool(state, { type: 'review', to: destination, name: 'Path 1' });
    expect(state).toMatchObject({ phase: 'review', points: [[0, 0], [2, 2]],
      from: anchor, to: destination });
  });

  it('constructs a saved connector without changing its topology discriminators', () => {
    let state = reducePathTool(IDLE_PATH_TOOL, { type: 'arm' });
    state = reducePathTool(state, { type: 'start', anchor });
    state = reducePathTool(state, { type: 'add-point', point: [1, 1] });
    state = reducePathTool(state, { type: 'review', to: destination, name: ' ' });
    if (state.phase !== 'review') throw new Error('Expected review');
    expect(pathFromReview(state, [], 'path-1', 'now')).toMatchObject({
      id: 'path-1', name: 'Path 1', from: anchor, to: destination,
      widthM: 6, status: 'complete', createdAt: 'now',
    });
  });
});
