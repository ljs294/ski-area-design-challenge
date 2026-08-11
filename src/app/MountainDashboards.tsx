import { NetworkMap } from './NetworkMap';
import { SnowmakingDashboard } from './SnowmakingDashboard';
import type { DashboardKind, SnowmakingDashboardMode } from './dashboardMode';
import type { SnowmakingMapPresentation } from './dashboardMapLayers';

// Thin picker + delegator over the two full-screen dashboards. Neither
// NetworkMap nor SnowmakingDashboard is edited by this file — both already
// own their own dialog chrome (role="dialog", a close affordance, Escape
// handling). This wrapper only adds the segmented picker that switches
// between them, positioned as its own fixed overlay so it survives which
// dashboard is mounted underneath.

export type { DashboardKind } from './dashboardMode';

type NetworkMapProps = Omit<Parameters<typeof NetworkMap>[0], 'onClose'>;
type SnowmakingDashboardProps = Omit<Parameters<typeof SnowmakingDashboard>[0], 'onClose'>;

export function MountainDashboards({
  dashboard,
  snowmakingMode = 'inspect',
  networkProps,
  snowmakingProps,
  onClose,
  onFit,
  onSnowmakingPresentationChange,
}: {
  dashboard: DashboardKind;
  snowmakingMode?: SnowmakingDashboardMode;
  networkProps: NetworkMapProps;
  snowmakingProps: SnowmakingDashboardProps;
  onClose: () => void;
  onFit: () => void;
  onSnowmakingPresentationChange?: (presentation: SnowmakingMapPresentation) => void;
}) {
  return dashboard === 'trails'
    ? <NetworkMap {...networkProps} panelOnly onFit={onFit} onClose={onClose} />
    : <SnowmakingDashboard {...snowmakingProps} panelOnly onFit={onFit}
      mode={snowmakingMode} onPresentationChange={onSnowmakingPresentationChange}
      onClose={onClose} />;
}
