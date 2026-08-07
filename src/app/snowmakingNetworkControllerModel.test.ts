import { describe, expect, it } from 'vitest';
import { reduceSnowmakingNodeTool, reduceSnowmakingPipeTool,
  IDLE_SNOWMAKING_NODE_TOOL, IDLE_SNOWMAKING_PIPE_TOOL,
  snowmakingPipePreview } from './snowmakingNetworkControllerModel';

describe('snowmaking pipe tool reducer', () => {
  it('moves through armed, drawing, review, rename, and cancel', () => {
    const armed = reduceSnowmakingPipeTool(IDLE_SNOWMAKING_PIPE_TOOL, { type: 'arm' });
    const first = reduceSnowmakingPipeTool(armed, { type: 'add', point: {
      point: [0, 0], snap: null } });
    const second = reduceSnowmakingPipeTool(first, { type: 'add', point: {
      point: [0, 0.001], snap: { kind: 'node', nodeId: 'intake', point: [0, 0.001] } } });
    const review = reduceSnowmakingPipeTool(second, { type: 'review', name: 'Pipe 1' });
    const renamed = reduceSnowmakingPipeTool(review, { type: 'rename', name: 'Main line' });
    expect(renamed).toMatchObject({ phase: 'review', name: 'Main line' });
    expect(reduceSnowmakingPipeTool(renamed, { type: 'cancel' })).toBe(IDLE_SNOWMAKING_PIPE_TOOL);
  });

  it('undoes the last point and returns to armed at the first point', () => {
    const drawing = reduceSnowmakingPipeTool({ phase: 'armed' }, { type: 'add',
      point: { point: [0, 0], snap: null } });
    expect(reduceSnowmakingPipeTool(drawing, { type: 'undo' })).toEqual({ phase: 'armed' });
  });

  it('keeps the completed route visible during review without a cursor segment', () => {
    const points = [
      { point: [0, 0] as [number, number], snap: null },
      { point: [0, 0.001] as [number, number], snap: null },
    ];
    const review = reduceSnowmakingPipeTool({ phase: 'drawing', points,
      cursor: [0, 0.002], cursorSnap: null }, { type: 'review', name: 'Pipe 1' });

    expect(snowmakingPipePreview(review)).toEqual({
      points: [[0, 0], [0, 0.001]],
      cursor: null,
    });
  });
});

describe('snowmaking node tool reducer', () => {
  it('retains placement mode after a committed device', () => {
    const placing = reduceSnowmakingNodeTool(IDLE_SNOWMAKING_NODE_TOOL,
      { type: 'arm', kind: 'hydrant' });
    const candidate = reduceSnowmakingNodeTool(placing, { type: 'candidate', error: null,
      candidate: { point: [0, 0], snap: null, elevM: 100 } });
    const committed = reduceSnowmakingNodeTool(candidate, { type: 'committed' });
    expect(committed).toEqual({ phase: 'placing', kind: 'hydrant', candidate: null, error: null });
  });
});
