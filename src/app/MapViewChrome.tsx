import type { CSSProperties, ReactNode } from 'react';
import type { ConstructionActivity } from './constructionLock';
import { ConstructionStatusBug } from './ConstructionStatusBug';
import { CreditsPanel } from './CreditsPanel';
import { CursorReadoutSubscriber, type CursorReadoutStore } from './CursorReadout';
import { GameMenu } from './GameMenu';
import { MapGameDock, type MapGameDockProps } from './MapGameDock';
import { MountainDashboards } from './MountainDashboards';
import { ResortLoadingScreen } from './ResortLoadingScreen';
import { ResortStatsPanel } from './ResortStatsPanel';
import { SearchBox, type GeocodeResult } from './SearchBox';
import { SiteControl } from './SiteControl';
import type { Units } from './SettingsContext';
import { UnsavedChangesModal } from './UnsavedChangesModal';
import { View3DControl } from './View3DControl';
import { DashboardMenu } from './DashboardMenu';
import { SnowmakingPipeTooltip } from './SnowmakingPipeHover';
import type { DashboardKind } from './dashboardMode';
import type { BootProgress } from './resortBoot';
import type { TerrainPackageProgress } from '../types/terrain';

export { useMapContextRecovery } from './useMapContextRecovery';
export { SnowmakingToolOptions } from './SnowmakingToolOptions';
export { snowmakingDashboardProps } from './SnowmakingDashboard';

const PREP_STEPS = [
  ['elevation', 'Elevation data'],
  ['ground-cover', 'Recovery ground cover'],
  ['imagery', 'NAIP imagery & map context'],
  ['decoding', 'Four terrain classes'],
  ['vectorizing-cover', 'Detailed vector cover'],
  ['deriving', 'Local contours'],
  ['saving', 'Saving package'],
  ['verifying', 'Verifying'],
  ['finalizing', 'Final validation'],
] as const;

interface PackageGateProps {
  state: 'preparing' | 'error';
  progress: TerrainPackageProgress | null;
  error: string | null;
  mapContextError: string | null;
  cancel(): void;
  back(): void;
  prepare(): void;
  decideMapContext(decision: 'retry' | 'continue' | 'cancel'): void;
}

interface NameEntryProps {
  value: string;
  saving: boolean;
  change(value: string): void;
  redraw(): void;
  submit(): void;
}

export interface MapViewChromeProps {
  checkpointError: string | null;
  dismissCheckpointError(): void;
  unsaved: Parameters<typeof UnsavedChangesModal>[0] | null;
  packageGate: PackageGateProps | null;
  localBoot: {
    progress: BootProgress;
    title: string;
    imageryUrl: string | null;
    back(): void;
    reveal(): void;
  } | null;
  menu: Parameters<typeof GameMenu>[0];
  searchResult: ((result: GeocodeResult) => void) | null;
  siteControl: Parameters<typeof SiteControl>[0] | null;
  view3D: Parameters<typeof View3DControl>[0] | null;
  buildingActivity: ConstructionActivity | null;
  dashboardToggle: { active: DashboardKind | null; change(kind: DashboardKind | null): void } | null;
  dashboardPipeHover?: Parameters<typeof SnowmakingPipeTooltip>[0] | null;
  dashboard: Parameters<typeof MountainDashboards>[0] | null;
  readout: { store: CursorReadoutStore; units: Units } | null;
  dock: MapGameDockProps | null;
  nameEntry: NameEntryProps | null;
  stats: Parameters<typeof ResortStatsPanel>[0] | null;
  closeCredits: (() => void) | null;
  bottomRightToolOptions?: ReactNode | null;
}

export function MapViewChrome(props: MapViewChromeProps) {
  const gate = props.packageGate;
  return (
    <>
      {props.checkpointError && (
        <div className="checkpoint-error" role="alert">
          <span>{props.checkpointError}</span>
          <button type="button" onClick={props.dismissCheckpointError}>Dismiss</button>
        </div>
      )}

      {props.unsaved && <UnsavedChangesModal {...props.unsaved} />}

      {gate && (
        <div className="package-gate" role="dialog" aria-modal="true" aria-live="polite">
          <div className={`package-card${gate.state === 'error' ? ' is-error' : ''}`}>
            {gate.state !== 'error' && (
              <svg className="topo-motif" viewBox="0 0 120 120" aria-hidden="true">
                <defs><path id="topoRing" d="M60 42 C73 42 80 50 80 60 C80 72 71 80 60 80 C49 80 40 71 40 60 C40 49 47 42 60 42 Z" /></defs>
                <g fill="none" stroke="currentColor" strokeWidth="1.5">
                  {[0.5, 1, 1.55, 2.1].map((scale, index) => (
                    <use key={scale} href="#topoRing" className="topo-ring"
                      style={{ '--i': index } as CSSProperties}
                      transform={`translate(60 60) scale(${scale}) translate(-60 -60)`} />
                  ))}
                </g>
                <circle cx="60" cy="60" r="3.4" className="topo-peak" fill="currentColor" />
              </svg>
            )}
            <div className="package-kicker">LOCAL RESORT DATA</div>
            <h2>{gate.mapContextError ? 'Map context unavailable'
              : gate.state === 'preparing' ? 'Preparing resort data' : 'Preparation failed'}</h2>
            <p>{gate.mapContextError
              ? `Roads and water could not be downloaded. ${gate.mapContextError}`
              : gate.state === 'preparing'
              ? 'Fetching terrain, ground cover, and contours for your build site.'
              : gate.error ?? 'Elevation, contours, and ground cover must be saved locally before designing.'}</p>
            {gate.mapContextError && <div className="package-actions">
              <button className="site-btn"
                onClick={() => gate.decideMapContext('cancel')}>Cancel</button>
              <button className="site-btn"
                onClick={() => gate.decideMapContext('continue')}>Continue Without Map Context</button>
              <button className="site-btn site-btn-primary"
                onClick={() => gate.decideMapContext('retry')}>Retry Map Context</button>
            </div>}
            {!gate.mapContextError && gate.state === 'preparing' && gate.progress && (() => {
              const { completed, total } = gate.progress;
              const pct = Math.round((completed / total) * 100);
              return <>
                <ul className="package-steps">
                  {PREP_STEPS.map(([key, label], index) => {
                    const state = completed > index ? 'done' : completed === index ? 'active' : 'pending';
                    return <li key={key} className={`package-step is-${state}`}>
                      <span className="package-step-dot" aria-hidden="true" />
                      <span className="package-step-label">{label}
                        {state === 'active' && <span className="package-step-detail">{gate.progress?.message}</span>}
                      </span>
                    </li>;
                  })}
                </ul>
                <div className="package-progress"><span style={{ width: `${pct}%` }} /></div>
                <div className="package-progress-label">{pct}% · Step {Math.min(total, completed + 1)} of {total}</div>
                <div className="package-actions"><button className="site-btn" onClick={gate.cancel}>Cancel</button></div>
              </>;
            })()}
            {!gate.mapContextError && gate.state === 'error' && <div className="package-actions">
              <button className="site-btn" onClick={gate.back}>Back to menu</button>
              <button className="site-btn site-btn-primary" onClick={gate.prepare}>Prepare Resort Data</button>
            </div>}
          </div>
        </div>
      )}

      {props.localBoot && !gate && <ResortLoadingScreen
        title={props.localBoot.title} progress={props.localBoot.progress}
        imageryUrl={props.localBoot.imageryUrl} state="loading"
        onBack={props.localBoot.back} onEnterAnyway={props.localBoot.reveal} />}

      <GameMenu {...props.menu} />
      {props.searchResult && <SearchBox onResult={props.searchResult} />}
      <div className="top-right-stack">
        {props.siteControl && <SiteControl {...props.siteControl} />}
        {props.view3D && <View3DControl {...props.view3D} />}
      </div>
      {props.buildingActivity && <ConstructionStatusBug activity={props.buildingActivity} />}

      {props.dashboardToggle && <div className="top-left-stack">
        <DashboardMenu active={props.dashboardToggle.active}
          onChange={props.dashboardToggle.change} />
      </div>}
      {props.dashboardPipeHover && <SnowmakingPipeTooltip {...props.dashboardPipeHover} />}
      {props.dashboard && <MountainDashboards {...props.dashboard} />}
      {props.readout && <CursorReadoutSubscriber store={props.readout.store} units={props.readout.units} />}
      {props.dock && <MapGameDock {...props.dock} />}
      {props.bottomRightToolOptions && <div className="bottom-right-tool-stack">
        {props.bottomRightToolOptions}
      </div>}

      {props.nameEntry && <div className="name-entry">
        <div className="name-entry-title">Name your resort</div>
        <input className="name-entry-input" type="text" placeholder="e.g. Crystal Peak Resort"
          value={props.nameEntry.value} autoFocus
          onChange={(event) => props.nameEntry?.change(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter') props.nameEntry?.submit(); }} />
        <div className="name-entry-actions">
          <button className="site-btn" onClick={props.nameEntry.redraw}>Redraw</button>
          <button className="site-btn site-btn-primary" onClick={props.nameEntry.submit}
            disabled={props.nameEntry.saving}>
            {props.nameEntry.saving ? 'Creating…' : 'Start Designing'}
          </button>
        </div>
      </div>}

      {props.stats && <ResortStatsPanel {...props.stats} />}
      {props.closeCredits && <CreditsPanel onClose={props.closeCredits} />}
    </>
  );
}
