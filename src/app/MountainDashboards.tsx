import { NetworkMap } from './NetworkMap';
import { SnowmakingDashboard } from './SnowmakingDashboard';
import { GuestVibeCheck } from './GuestVibeCheck';
import type { DashboardKind, SnowmakingDashboardMode } from './dashboardMode';
import type { SnowmakingMapPresentation } from './dashboardMapLayers';

// Compose existing analysis views inside the owning gameplay workspace.

export type { DashboardKind } from './dashboardMode';

type NetworkMapProps = Omit<Parameters<typeof NetworkMap>[0], 'onClose'>;
type SnowmakingDashboardProps = Omit<Parameters<typeof SnowmakingDashboard>[0], 'onClose'>;
type GuestVibeCheckProps = Parameters<typeof GuestVibeCheck>[0];

export function MountainDashboards({
  dashboard,
  snowmakingMode = 'inspect',
  networkProps,
  snowmakingProps,
  guestProps,
  onClose,
  onFit,
  onSnowmakingPresentationChange,
}: {
  dashboard: DashboardKind;
  snowmakingMode?: SnowmakingDashboardMode;
  networkProps: NetworkMapProps;
  snowmakingProps: SnowmakingDashboardProps;
  guestProps?: GuestVibeCheckProps;
  onClose: () => void;
  onFit: () => void;
  onSnowmakingPresentationChange?: (presentation: SnowmakingMapPresentation) => void;
}) {
  if (dashboard === 'trails') return <NetworkMap {...networkProps} panelOnly onFit={onFit} onClose={onClose} />;
  if (dashboard === 'snowmaking') return <SnowmakingDashboard {...snowmakingProps} panelOnly onFit={onFit}
    mode={snowmakingMode} onPresentationChange={onSnowmakingPresentationChange} onClose={onClose} />;
  if (!guestProps) return null;
  return <aside className="dashboard-sidebar" aria-label="Guest dashboard">
    <button className="settings-close-x dashboard-sidebar-close" aria-label="Close guest dashboard"
      onClick={onClose}>✕</button>
    <GuestVibeCheck {...guestProps} />
  </aside>;
}
