import { describe, expect, it } from 'vitest';
import { IDLE_SNOWGUN_TOOL, reduceSnowgunTool } from './snowmakingGunControllerModel';

const item = (draftId: string, variantId: 'HKD_ImpulseR5_10s' | 'HKD_ImpulseR5_20t') => ({
  draftId, variantId, point: [0, 0] as [number, number], elevM: null,
});

describe('snowgun tool reducer', () => {
  it('keeps mixed variants on their placed rows and removes individual items', () => {
    let state = reduceSnowgunTool(IDLE_SNOWGUN_TOOL,
      { type: 'arm', variantId: 'HKD_ImpulseR5_10s' });
    state = reduceSnowgunTool(state, { type: 'add', item: item('a', 'HKD_ImpulseR5_10s') });
    state = reduceSnowgunTool(state, { type: 'variant', variantId: 'HKD_ImpulseR5_20t' });
    state = reduceSnowgunTool(state, { type: 'add', item: item('b', 'HKD_ImpulseR5_20t') });
    expect(state.phase === 'placing' && state.items.map((row) => row.variantId)).toEqual([
      'HKD_ImpulseR5_10s', 'HKD_ImpulseR5_20t',
    ]);
    state = reduceSnowgunTool(state, { type: 'remove', draftId: 'a' });
    expect(state.phase === 'placing' && state.items.map((row) => row.draftId)).toEqual(['b']);
  });

  it('keeps the cart through review/back and discards it on cancel', () => {
    let state = reduceSnowgunTool(IDLE_SNOWGUN_TOOL,
      { type: 'arm', variantId: 'HKD_ImpulseR5_10s' });
    state = reduceSnowgunTool(state, { type: 'add', item: item('a', 'HKD_ImpulseR5_10s') });
    state = reduceSnowgunTool(state, { type: 'review', revision: 3 });
    expect(state.phase).toBe('review');
    state = reduceSnowgunTool(state, { type: 'back' });
    expect(state.phase === 'placing' && state.items).toHaveLength(1);
    expect(reduceSnowgunTool(state, { type: 'cancel' })).toBe(IDLE_SNOWGUN_TOOL);
  });
});
