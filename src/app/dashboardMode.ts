export type DashboardKind = 'trails' | 'snowmaking';

export type SnowmakingDashboardMode = 'inspect' | 'analysis';

export interface DashboardPresentation {
  kind: DashboardKind;
  snowmakingMode?: SnowmakingDashboardMode;
}
