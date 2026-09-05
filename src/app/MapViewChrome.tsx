import type { ReactNode } from 'react';
import type { ConstructionActivity } from './constructionLock';
import { ConstructionStatusBug } from './ConstructionStatusBug';
import { CreditsPanel } from './CreditsPanel';
import { CursorReadoutSubscriber, type CursorReadoutStore } from './CursorReadout';
import { GameMenu } from './GameMenu';
import type { MapGameDockProps } from './MapGameDock';
import type { MountainDashboards } from './MountainDashboards';
import { ResortLoadingScreen } from './ResortLoadingScreen';
import type { ResortStatsPanel } from './ResortStatsPanel';
import type { GeocodeResult } from './SearchBox';
import type { SiteControl } from './SiteControl';
import { SetupWorkspace } from './SetupWorkspace';
import type { Units } from './SettingsContext';
import { UnsavedChangesModal } from './UnsavedChangesModal';
import { View3DControl } from './View3DControl';
import { GameplayWorkspace } from './GameplayWorkspace';
import type { WorkspaceRequest } from './workspaceModel';
import { SnowmakingPipeTooltip } from './SnowmakingPipeHover';
import type { BootProgress } from './resortBoot';
import type { TerrainPackageProgress } from '../types/terrain';
import { DeveloperConsole } from './DeveloperConsole';

export { useMapContextRecovery } from './useMapContextRecovery';
export { SnowmakingToolOptions } from './SnowmakingToolOptions';
export { snowmakingDashboardProps } from './SnowmakingDashboard';

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
  setup?: { prepared: boolean; prepare(): void; back(): void };
  workspace?: { navigate(request: WorkspaceRequest): void; close(): void; canEditAnalysis: boolean; editAnalysis(): void };
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
  dashboardPipeHover?: Parameters<typeof SnowmakingPipeTooltip>[0] | null;
  dashboard: Parameters<typeof MountainDashboards>[0] | null;
  readout: { store: CursorReadoutStore; units: Units } | null;
  dock: MapGameDockProps | null;
  nameEntry: NameEntryProps | null;
  stats: Parameters<typeof ResortStatsPanel>[0] | null;
  closeCredits: (() => void) | null;
  bottomRightToolOptions?: ReactNode | null;
  developerConsole: Parameters<typeof DeveloperConsole>[0] | null;
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

      {(props.setup || gate) && <SetupWorkspace {...props} />}

      {props.localBoot && !gate && <ResortLoadingScreen
        title={props.localBoot.title} progress={props.localBoot.progress}
        imageryUrl={props.localBoot.imageryUrl} state="loading"
        onBack={props.localBoot.back} onEnterAnyway={props.localBoot.reveal} />}

      {!props.dock && <GameMenu {...props.menu} />}
      <div className="top-right-stack">
        {props.view3D && <View3DControl {...props.view3D} />}
      </div>
      {props.buildingActivity && <ConstructionStatusBug activity={props.buildingActivity} />}

      {props.dashboardPipeHover && <SnowmakingPipeTooltip {...props.dashboardPipeHover} />}
      {props.readout && <CursorReadoutSubscriber store={props.readout.store} units={props.readout.units} />}
      {props.dock && <GameplayWorkspace {...props} dock={props.dock} />}

      {props.closeCredits && <CreditsPanel onClose={props.closeCredits} />}
      {props.developerConsole && <DeveloperConsole {...props.developerConsole} />}
    </>
  );
}
