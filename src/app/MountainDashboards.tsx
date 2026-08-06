import { NetworkMap } from './NetworkMap';
import { SnowmakingDashboard } from './SnowmakingDashboard';

// Thin picker + delegator over the two full-screen dashboards. Neither
// NetworkMap nor SnowmakingDashboard is edited by this file — both already
// own their own dialog chrome (role="dialog", a close affordance, Escape
// handling). This wrapper only adds the segmented picker that switches
// between them, positioned as its own fixed overlay so it survives which
// dashboard is mounted underneath.

export type DashboardKind = 'trails' | 'snowmaking';

type NetworkMapProps = Omit<Parameters<typeof NetworkMap>[0], 'onClose'>;
type SnowmakingDashboardProps = Omit<Parameters<typeof SnowmakingDashboard>[0], 'onClose'>;

export function MountainDashboards({
  dashboard,
  onDashboardChange,
  networkProps,
  snowmakingProps,
  onClose,
}: {
  dashboard: DashboardKind;
  onDashboardChange: (dashboard: DashboardKind) => void;
  networkProps: NetworkMapProps;
  snowmakingProps: SnowmakingDashboardProps;
  onClose: () => void;
}) {
  return (
    <div className="mountain-dashboards">
      <div className="dashboard-picker" role="group" aria-label="Mountain dashboards">
        <button
          className="site-btn"
          aria-pressed={dashboard === 'trails'}
          onClick={() => onDashboardChange('trails')}
        >
          Trails &amp; Lifts
        </button>
        <button
          className="site-btn"
          aria-pressed={dashboard === 'snowmaking'}
          onClick={() => onDashboardChange('snowmaking')}
        >
          Snowmaking
        </button>
      </div>
      {dashboard === 'trails' ? (
        <NetworkMap {...networkProps} onClose={onClose} />
      ) : (
        <SnowmakingDashboard {...snowmakingProps} onClose={onClose} />
      )}
    </div>
  );
}
