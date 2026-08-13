import { describe, expect, it } from 'vitest';
import { createSnowmakingAnalysisState, pumpAnalysisSetting,
  snowmakingAnalysisReducer } from './snowmakingAnalysisModel';

describe('snowmaking analysis state', () => {
  it('uses explicit, transient pump defaults', () => {
    expect(pumpAnalysisSetting(undefined)).toEqual({ on: false, horsepowerHp: null,
      efficiency: 0.85 });
  });

  it('clears checked results after an input changes and reset clears everything', () => {
    const result = { status: 'failed' as const, diagnostics: [], systems: [], sources: [],
      summary: { systemCount: 0, readySystemCount: 0, selectedGunCount: 0,
        analyzedGunCount: 0, readyGunCount: 0, notAnalyzedGunCount: 0,
        requestedDemandGpm: 0, waterUseGalPerHour: 0, minimumGunPressurePsi: null,
        limitingSourceRuntimeHours: null, overallReady: false } };
    let state = snowmakingAnalysisReducer(createSnowmakingAnalysisState(),
      { type: 'analyzed', result });
    expect(state.stale).toBe(false);
    state = snowmakingAnalysisReducer(state, { type: 'wet-bulb', value: '14' });
    expect(state.stale).toBe(false);
    expect(state.result).toBeNull();
    expect(snowmakingAnalysisReducer(state, { type: 'reset' }))
      .toEqual(createSnowmakingAnalysisState());
  });

  it('toggles selections and reconciles removed assets', () => {
    let state = createSnowmakingAnalysisState();
    state = snowmakingAnalysisReducer(state, { type: 'toggle-gun', id: 'gun-1' });
    state = snowmakingAnalysisReducer(state, { type: 'toggle-intake', id: 'intake-1' });
    state = snowmakingAnalysisReducer(state, { type: 'pump-on', id: 'pump-1', on: true });
    expect(state.selectedGunIds).toEqual(['gun-1']);
    expect(state.selectedIntakeNodeIds).toEqual(['intake-1']);
    state = snowmakingAnalysisReducer(state,
      { type: 'reconcile', intakeNodeIds: [], gunIds: [], pumpIds: [] });
    expect(state.selectedGunIds).toEqual([]);
    expect(state.selectedIntakeNodeIds).toEqual([]);
    expect(state.pumpSettings).toEqual({});
  });
});
