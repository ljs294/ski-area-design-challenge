export type DashboardKind = 'trails' | 'snowmaking';

export type SnowmakingDashboardMode = 'inspect' | 'analysis';

export type SnowgunSelectionPhase = 'idle' | 'armed' | 'review';

export interface DashboardPresentation {
  kind: DashboardKind;
  snowmakingMode?: SnowmakingDashboardMode;
}
