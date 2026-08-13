import type { SnowmakingAnalysisResult, SnowmakingPumpAnalysisSetting } from '../snowmakingHydraulics';

export interface SnowmakingPumpAnalysisDraft {
  on: boolean;
  horsepowerHp: string;
  efficiencyPercent: string;
}

export interface SnowmakingAnalysisState {
  selectedGunIds: string[];
  selectedIntakeNodeIds: string[];
  suppressedAutoIntakeNodeIds: string[];
  wetBulbF: string;
  pumpSettings: Record<string, SnowmakingPumpAnalysisDraft>;
  result: SnowmakingAnalysisResult | null;
  stale: boolean;
  calculating: boolean;
  error: string | null;
}

export type SnowmakingAnalysisAction =
  | { type: 'toggle-gun'; id: string }
  | { type: 'set-guns'; ids: string[] }
  | { type: 'toggle-intake'; id: string }
  | { type: 'auto-intakes'; ids: string[]; relevantIds: string[] }
  | { type: 'wet-bulb'; value: string }
  | { type: 'pump-on'; id: string; on: boolean }
  | { type: 'pump-hp'; id: string; value: string }
  | { type: 'pump-efficiency'; id: string; value: string }
  | { type: 'calculation-started' }
  | { type: 'analyzed'; result: SnowmakingAnalysisResult }
  | { type: 'analysis-error'; message: string }
  | { type: 'clear-result' }
  | { type: 'reconcile'; gunIds: string[]; intakeNodeIds: string[]; pumpIds: string[] }
  | { type: 'reset' };

export const DEFAULT_PUMP_ANALYSIS_DRAFT: Readonly<SnowmakingPumpAnalysisDraft> = Object.freeze({
  on: false,
  horsepowerHp: '',
  efficiencyPercent: '85',
});

export function createSnowmakingAnalysisState(): SnowmakingAnalysisState {
  return { selectedGunIds: [], selectedIntakeNodeIds: [], suppressedAutoIntakeNodeIds: [],
    wetBulbF: '28', pumpSettings: {}, result: null, stale: false,
    calculating: false, error: null };
}

function changed(state: SnowmakingAnalysisState,
  patch: Partial<SnowmakingAnalysisState>): SnowmakingAnalysisState {
  // Input edits invalidate the last hydraulic snapshot immediately. Keeping
  // the old result around made it too easy to mistake a previous calculation
  // for the current selection.
  return { ...state, ...patch, result: null, stale: false, calculating: false, error: null };
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
    case 'toggle-gun': return changed(state,
      { selectedGunIds: toggle(state.selectedGunIds, action.id) });
    case 'set-guns': return changed(state, { selectedGunIds: [...new Set(action.ids)] });
    case 'toggle-intake': {
      const selected = state.selectedIntakeNodeIds.includes(action.id);
      return changed(state, {
        selectedIntakeNodeIds: toggle(state.selectedIntakeNodeIds, action.id),
        suppressedAutoIntakeNodeIds: selected
          ? [...new Set([...state.suppressedAutoIntakeNodeIds, action.id])]
          : state.suppressedAutoIntakeNodeIds.filter((id) => id !== action.id),
      });
    }
    case 'auto-intakes': {
      const relevant = new Set(action.relevantIds);
      const suppressedAutoIntakeNodeIds = state.suppressedAutoIntakeNodeIds
        .filter((id) => relevant.has(id));
      const suppressed = new Set(suppressedAutoIntakeNodeIds);
      const selectedIntakeNodeIds = [...new Set([
        ...state.selectedIntakeNodeIds.filter((id) => relevant.has(id)),
        ...action.ids.filter((id) => !suppressed.has(id)),
      ])];
      if (selectedIntakeNodeIds.join('\0') === state.selectedIntakeNodeIds.join('\0') &&
        suppressedAutoIntakeNodeIds.join('\0') === state.suppressedAutoIntakeNodeIds.join('\0')) return state;
      return changed(state, { selectedIntakeNodeIds, suppressedAutoIntakeNodeIds });
    }
    case 'wet-bulb': return changed(state, { wetBulbF: action.value });
    case 'pump-on': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), on: action.on } } });
    case 'pump-hp': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), horsepowerHp: action.value } } });
    case 'pump-efficiency': return changed(state, { pumpSettings: { ...state.pumpSettings,
      [action.id]: { ...pumpDraft(state, action.id), efficiencyPercent: action.value } } });
    case 'calculation-started': return { ...state, result: null, stale: false,
      calculating: true, error: null };
    case 'analyzed': return { ...state, result: action.result, stale: false,
      calculating: false, error: null };
    case 'analysis-error': return { ...state, calculating: false, error: action.message };
    case 'clear-result': return { ...state, result: null, stale: false,
      calculating: false, error: null };
    case 'reset': return createSnowmakingAnalysisState();
    case 'reconcile': {
      const guns = new Set(action.gunIds), intakes = new Set(action.intakeNodeIds);
      const pumps = new Set(action.pumpIds);
      const selectedGunIds = state.selectedGunIds.filter((id) => guns.has(id));
      const selectedIntakeNodeIds = state.selectedIntakeNodeIds.filter((id) => intakes.has(id));
      const suppressedAutoIntakeNodeIds = state.suppressedAutoIntakeNodeIds
        .filter((id) => intakes.has(id));
      const pumpSettings = Object.fromEntries(Object.entries(state.pumpSettings)
        .filter(([id]) => pumps.has(id)));
      const didChange = selectedGunIds.length !== state.selectedGunIds.length ||
        selectedIntakeNodeIds.length !== state.selectedIntakeNodeIds.length ||
        suppressedAutoIntakeNodeIds.length !== state.suppressedAutoIntakeNodeIds.length ||
        Object.keys(pumpSettings).length !== Object.keys(state.pumpSettings).length;
      return didChange ? changed(state, { selectedGunIds, selectedIntakeNodeIds,
        suppressedAutoIntakeNodeIds, pumpSettings }) : state;
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
