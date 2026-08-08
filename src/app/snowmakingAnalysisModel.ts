import type { SnowmakingAnalysisResult, SnowmakingPumpAnalysisSetting } from '../snowmakingHydraulics';

export interface SnowmakingPumpAnalysisDraft {
  on: boolean;
  horsepowerHp: string;
  efficiencyPercent: string;
}

export interface SnowmakingAnalysisState {
  selectedPipeIds: string[];
  selectedGunIds: string[];
  wetBulbF: string;
  pumpSettings: Record<string, SnowmakingPumpAnalysisDraft>;
  result: SnowmakingAnalysisResult | null;
  stale: boolean;
}

export type SnowmakingAnalysisAction =
  | { type: 'toggle-pipe'; id: string }
  | { type: 'toggle-gun'; id: string }
  | { type: 'wet-bulb'; value: string }
  | { type: 'pump-on'; id: string; on: boolean }
  | { type: 'pump-hp'; id: string; value: string }
  | { type: 'pump-efficiency'; id: string; value: string }
  | { type: 'checked'; result: SnowmakingAnalysisResult }
  | { type: 'reconcile'; pipeIds: string[]; gunIds: string[]; pumpIds: string[] }
  | { type: 'reset' };

export const DEFAULT_PUMP_ANALYSIS_DRAFT: Readonly<SnowmakingPumpAnalysisDraft> = Object.freeze({
  on: false,
  horsepowerHp: '',
  efficiencyPercent: '85',
});

export function createSnowmakingAnalysisState(): SnowmakingAnalysisState {
  return { selectedPipeIds: [], selectedGunIds: [], wetBulbF: '28', pumpSettings: {},
    result: null, stale: false };
}

function changed(state: SnowmakingAnalysisState, patch: Partial<SnowmakingAnalysisState>): SnowmakingAnalysisState {
  return { ...state, ...patch, stale: state.result !== null };
}

function toggle(ids: readonly string[], id: string): string[] {
  return ids.includes(id) ? ids.filter((candidate) => candidate !== id) : [...ids, id];
}

function pumpDraft(state: SnowmakingAnalysisState, id: string): SnowmakingPumpAnalysisDraft {
  return state.pumpSettings[id] ?? { ...DEFAULT_PUMP_ANALYSIS_DRAFT };
}

export function snowmakingAnalysisReducer(state: SnowmakingAnalysisState,
  action: SnowmakingAnalysisAction): SnowmakingAnalysisState {
  switch (action.type) {
    case 'toggle-pipe': return changed(state,
      { selectedPipeIds: toggle(state.selectedPipeIds, action.id) });
    case 'toggle-gun': return changed(state,
      { selectedGunIds: toggle(state.selectedGunIds, action.id) });
    case 'wet-bulb': return changed(state, { wetBulbF: action.value });
    case 'pump-on': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), on: action.on } } });
    case 'pump-hp': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), horsepowerHp: action.value } } });
    case 'pump-efficiency': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), efficiencyPercent: action.value } } });
    case 'checked': return { ...state, result: action.result, stale: false };
    case 'reset': return createSnowmakingAnalysisState();
    case 'reconcile': {
      const pipes = new Set(action.pipeIds), guns = new Set(action.gunIds), pumps = new Set(action.pumpIds);
      const selectedPipeIds = state.selectedPipeIds.filter((id) => pipes.has(id));
      const selectedGunIds = state.selectedGunIds.filter((id) => guns.has(id));
      const pumpSettings = Object.fromEntries(Object.entries(state.pumpSettings)
        .filter(([id]) => pumps.has(id)));
      const didChange = selectedPipeIds.length !== state.selectedPipeIds.length ||
        selectedGunIds.length !== state.selectedGunIds.length ||
        Object.keys(pumpSettings).length !== Object.keys(state.pumpSettings).length;
      return didChange ? changed(state, { selectedPipeIds, selectedGunIds, pumpSettings }) : state;
    }
  }
}

export function pumpAnalysisSetting(draft: SnowmakingPumpAnalysisDraft | undefined):
SnowmakingPumpAnalysisSetting {
  const value = draft ?? DEFAULT_PUMP_ANALYSIS_DRAFT;
  return { on: value.on,
    horsepowerHp: value.horsepowerHp.trim() === '' ? null : Number(value.horsepowerHp),
    efficiency: Number(value.efficiencyPercent) / 100 };
}
