import type { Dispatch, SetStateAction } from 'react';
import type { SkiNetwork } from '../network';
import type { ToolCoordinator, DockId } from './toolCoordinator';
import type { useInMapDashboards } from './useInMapDashboards';
import type { GameSimulationController } from './useGameSimulation';
import type { DashboardKind } from './dashboardMode';
import { dockForSection, type WorkspaceRequest } from './workspaceModel';
type Setter<T> = Dispatch<SetStateAction<T>>;
type SelectionTarget = { kind: 'lift' | 'trail' | 'ski-path' | 'snowmaking-node' | 'snowmaking-pipe' | 'snowgun'; id: string };
interface Options {
  toolCoordinator: ToolCoordinator;
  openDock: DockId | null;
  layersAlongsideBuild: boolean;
  dashboards: ReturnType<typeof useInMapDashboards>;
  simulation: GameSimulationController;
  guests: { clearSelectedGuest(): void };
  showStats: boolean;
  network: SkiNetwork;
  selectedLakeId: string | null;
  selectedStreamId: string | null;
  selectedLiftId: string | null;
  selectedTrailId: string | null;
  selectedDamId: string | null;
  selectedPondId: string | null;
  selectedSnowmakingNodeId: string | null;
  selectedSnowmakingPipeId: string | null;
  selectedSnowgunId: string | null;
  selectedBuildingId: string | null;
  setSelectedLakeId: Setter<string | null>;
  setSelectedStreamId: Setter<string | null>;
  setSelectedLiftId: Setter<string | null>;
  setSelectedTrailId: Setter<string | null>;
  setSelectedDamId: Setter<string | null>;
  setSelectedPondId: Setter<string | null>;
  setSelectedSnowmakingNodeId: Setter<string | null>;
  setSelectedSnowmakingPipeId: Setter<string | null>;
  setOpenDock: Setter<DockId | null>;
  setShowStats: Setter<boolean>;
  setLiftEditing: Setter<boolean>;
  setTrailEditing: Setter<boolean>;
  clearSelectionState(): void;
  transitionSelection(target: SelectionTarget): void;
}
/** Navigation coordinates existing owners without duplicating their state. */
export function createWorkspaceNavigation(options: Options) {
  const { toolCoordinator, openDock, layersAlongsideBuild, dashboards, simulation, guests, showStats, network,
    setOpenDock, setShowStats, clearSelectionState, transitionSelection, setLiftEditing, setTrailEditing,
selectedLakeId, selectedStreamId, selectedLiftId, selectedTrailId, selectedDamId, selectedPondId, selectedSnowmakingNodeId, selectedSnowmakingPipeId, selectedSnowgunId, selectedBuildingId, setSelectedLakeId, setSelectedStreamId, setSelectedLiftId, setSelectedTrailId, setSelectedDamId, setSelectedPondId, setSelectedSnowmakingNodeId, setSelectedSnowmakingPipeId } = options;
  /** Close/open a bottom dock, yielding any active draw tool of the others. */
  function toggleDock(which: DockId) {
    const waterDetailOpen = selectedLakeId !== null || selectedStreamId !== null;
    const activeTool = toolCoordinator.snapshot.activeTool;
    const isOpen = !waterDetailOpen && (which === 'layers'
      ? openDock === 'layers' || layersAlongsideBuild
      : which === 'lifts' ? openDock === 'lifts' || activeTool === 'lift' || selectedLiftId !== null
        : which === 'trails' ? openDock === 'trails' || activeTool === 'trail' ||
          activeTool === 'ski-node' || activeTool === 'ski-path' || selectedTrailId !== null
          : which === 'snowmaking' ? openDock === 'snowmaking' || activeTool === 'dam' ||
            activeTool === 'pond' || activeTool === 'snowmaking-pipe' ||
            activeTool === 'snowmaking-node' || activeTool === 'snowmaking-gun' ||
            selectedDamId !== null || selectedPondId !== null || selectedSnowmakingNodeId !== null ||
            selectedSnowmakingPipeId !== null || selectedSnowgunId !== null || selectedBuildingId !== null
            : openDock === 'infrastructure' || activeTool === 'road' || activeTool === 'guest-portal');
    if (toolCoordinator.toggleDock(which, isOpen) === 'layers-alongside') return;

    setSelectedLakeId(null);
    setSelectedStreamId(null);
    if (which !== 'lifts') {
      setSelectedLiftId(null);
      setLiftEditing(false);
    }
    if (which !== 'trails') {
      setSelectedTrailId(null);
      setTrailEditing(false);
    }
    if (which !== 'snowmaking') {
      setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null);
      setSelectedSnowmakingPipeId(null); }
    if (isOpen) {
      if (which === 'lifts') {
        setSelectedLiftId(null);
        setLiftEditing(false);
      }
      if (which === 'trails') {
        setSelectedTrailId(null);
        setTrailEditing(false);
      }
      if (which === 'snowmaking') {
        setSelectedDamId(null); setSelectedPondId(null); setSelectedSnowmakingNodeId(null);
        setSelectedSnowmakingPipeId(null); }
    }
  }

  /** Navigation changes presentation, while each feature still owns cancellation. */
  function closeWorkspace() {
    toolCoordinator.cancelActive();
    toolCoordinator.setLayersAlongsideBuild(false);
    clearSelectionState();
    guests.clearSelectedGuest();
    setOpenDock(null);
    dashboards.close();
    setShowStats(false);
    if (simulation.analysisOpen) simulation.toggleAnalysis();
  }

  function openWorkspaceDashboard(kind: DashboardKind | null) {
    closeWorkspace();
    dashboards.change(kind);
  }

  function navigateWorkspace(request: WorkspaceRequest) {
    if (request.section === 'layers') {
      if (dashboards.active || showStats || simulation.analysisOpen) closeWorkspace();
      toggleDock('layers');
      return;
    }
    if (request.view === 'analysis' || request.view === 'network') {
      openWorkspaceDashboard(request.section === 'snowmaking' ? 'snowmaking' : 'trails');
      if (request.section === 'snowmaking') dashboards.setSnowMode(request.view === 'analysis' ? 'analysis' : 'inspect');
      return;
    }
    const nextDock = dockForSection(request.section);
    const wasOpen = request.section === 'resort' ? showStats
      : request.section === 'weather' ? simulation.analysisOpen
      : request.section === 'guests' ? dashboards.active === 'guests'
      : nextDock === openDock && !dashboards.active && !showStats && !simulation.analysisOpen;
    closeWorkspace();
    if (wasOpen) return;
    if (request.section === 'resort') setShowStats(true);
    else if (request.section === 'weather') simulation.toggleAnalysis();
    else if (request.section === 'guests') dashboards.change('guests');
    else setOpenDock(nextDock);
  }

  function editAnalysisSelection() {
    const edge = dashboards.edgeId ? network.edgeById.get(dashboards.edgeId) : null;
    if (dashboards.liftId) {
      transitionSelection({ kind: 'lift', id: dashboards.liftId }); setLiftEditing(true);
    } else if (edge?.kind === 'trail') {
      transitionSelection({ kind: 'trail', id: edge.trailId }); setTrailEditing(true);
    } else if (edge?.kind === 'path') {
      transitionSelection({ kind: 'ski-path', id: edge.pathId });
    } else if (dashboards.snowSelection) {
      const selected = dashboards.snowSelection;
      transitionSelection({ kind: selected.kind === 'node' ? 'snowmaking-node' : selected.kind === 'pipe' ? 'snowmaking-pipe' : 'snowgun', id: selected.id });
    }
  }

  return { toggleDock, closeWorkspace, openWorkspaceDashboard, navigateWorkspace, editAnalysisSelection };
}
