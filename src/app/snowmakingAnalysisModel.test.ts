import { describe, expect, it } from 'vitest';
import { createSnowmakingAnalysisState, pumpAnalysisSetting,
  snowmakingAnalysisReducer } from './snowmakingAnalysisModel';

describe('snowmaking analysis state', () => {
  it('uses explicit, transient pump defaults', () => {
    expect(pumpAnalysisSetting(undefined)).toEqual({ on: false, horsepowerHp: null,
      efficiency: 0.85 });
  });

  it('marks checked results stale after an input changes and reset clears everything', () => {
    const result = { ok: false as const, diagnostics: [{ code: 'no-pipes' as const,
      message: 'Select pipes.' }] };
    let state = snowmakingAnalysisReducer(createSnowmakingAnalysisState(),
      { type: 'checked', result });
    expect(state.stale).toBe(false);
    state = snowmakingAnalysisReducer(state, { type: 'wet-bulb', value: '14' });
    expect(state.stale).toBe(true);
    expect(snowmakingAnalysisReducer(state, { type: 'reset' }))
      .toEqual(createSnowmakingAnalysisState());
  });

  it('toggles selections and reconciles removed assets', () => {
    let state = createSnowmakingAnalysisState();
    state = snowmakingAnalysisReducer(state, { type: 'toggle-pipe', id: 'pipe-1' });
    state = snowmakingAnalysisReducer(state, { type: 'toggle-gun', id: 'gun-1' });
    state = snowmakingAnalysisReducer(state, { type: 'pump-on', id: 'pump-1', on: true });
    expect(state.selectedPipeIds).toEqual(['pipe-1']);
    expect(state.selectedGunIds).toEqual(['gun-1']);
    state = snowmakingAnalysisReducer(state,
      { type: 'reconcile', pipeIds: [], gunIds: [], pumpIds: [] });
    expect(state.selectedPipeIds).toEqual([]);
    expect(state.selectedGunIds).toEqual([]);
    expect(state.pumpSettings).toEqual({});
  });
});
