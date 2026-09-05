import { useEffect, useRef, useState } from 'react';
import type { MapViewChromeProps } from './MapViewChrome';
import { MapGameDock } from './MapGameDock';
import { GameToolbar, GameWeatherOverlay } from './GameToolbar';
import { GameMenu } from './GameMenu';
import { MountainDashboards } from './MountainDashboards';
import { ResortStatsPanel } from './ResortStatsPanel';
import { useCursorReadout } from './CursorReadout';
import { Icon } from './ui';
import { WORKSPACE_LABELS, sectionForTool, constructionStage, type WorkspaceSection } from './workspaceModel';

type Props = Pick<MapViewChromeProps, 'dock' | 'dashboard' | 'stats' | 'menu' | 'workspace' | 'bottomRightToolOptions'>;
const SECTIONS = ['resort', 'lifts', 'trails', 'snowmaking', 'infrastructure', 'guests'] as const;

/** Presentation is derived from domain owners; no parallel tool or selection state. */
export function GameplayWorkspace(props: Props & { dock: NonNullable<Props['dock']> }) {
  const { dock, workspace } = props;
  const [expanded, setExpanded] = useState(false);
  const [networkSection, setNetworkSection] = useState<'lifts' | 'trails'>('trails');
  const readout = useCursorReadout(dock.readoutStore);
  const tool = dock.coordinator.activeTool;
  const assetCount = dock.lifts.length + dock.trails.length + dock.roads.length + dock.dams.length + dock.ponds.length
    + dock.snowmakingNodes.length + dock.snowmakingPipes.length + dock.snowguns.length + (dock.buildings?.length ?? 0) + dock.skiPaths.length;
  const previousCount = useRef(assetCount);
  const [feedback, setFeedback] = useState(false);
  useEffect(() => {
    if (assetCount > previousCount.current) setFeedback(true);
    previousCount.current = assetCount;
  }, [assetCount]);
  useEffect(() => { if (!feedback) return; const timer = window.setTimeout(() => setFeedback(false), 4000);
    return () => window.clearTimeout(timer); }, [feedback, assetCount]);
  const selectedSnowNode = dock.snowmakingNodes.find((node) => node.id === dock.selectedSnowmakingNodeId);
  const buildAnother = tool ? null : dock.selectedLiftId ? dock.liftController.arm
    : dock.selectedTrailId ? dock.trailController.arm : dock.selectedPathId ? dock.nodePathController.armPath
    : dock.selectedNodeId ? () => dock.nodePathController.armNode('add')
    : dock.selectedRoadKey?.startsWith('player:') ? () => dock.roadController.arm('two-lane')
    : dock.selectedDamId ? dock.snowmakingController.dam.arm : dock.selectedPondId ? dock.snowmakingController.pond.arm
    : dock.selectedBuildingId ? dock.buildingController?.arm
    : dock.selectedSnowmakingPipeId ? dock.snowmakingController.network.armPipe
    : dock.selectedSnowgunId ? dock.snowmakingController.guns.arm
    : selectedSnowNode?.kind === 'pump' || selectedSnowNode?.kind === 'hydrant'
      ? () => dock.snowmakingController.network.armNode(selectedSnowNode.kind === 'pump' ? 'pump' : 'hydrant') : null;
  let section: WorkspaceSection | null = dock.openDock;
  if (dock.selectedLiftId) section = 'lifts';
  if (dock.selectedTrailId || dock.selectedPathId || dock.selectedNodeId) section = 'trails';
  if (dock.selectedDamId || dock.selectedPondId || dock.selectedBuildingId || dock.selectedLakeId || dock.selectedStreamId ||
    dock.selectedSnowmakingNodeId || dock.selectedSnowmakingPipeId || dock.selectedSnowgunId) section = 'snowmaking';
  if (dock.selectedRoadKey) section = 'infrastructure';
  if (tool) section = sectionForTool(tool);
  if (props.stats) section = 'resort';
  if (dock.simulation.analysisOpen) section = 'weather';
  if (props.dashboard) section = props.dashboard.dashboard === 'trails' ? networkSection : props.dashboard.dashboard;
  const analysis = !!props.dashboard;
  if (!section && dock.snowControl) section = 'layers';
  const panelOpen = section !== null && (section !== 'layers' || !!dock.snowControl);
  const layersOpen = dock.openDock === 'layers' || dock.layersAlongsideBuild;
  const snowNode = dock.snowmakingController.network.nodeTool;
  const skiNode = dock.nodePathController.nodeTool;
  const hydrantRun = dock.snowmakingController.network.hydrantRunTool;
  const phases: Record<string, string> = {
    lift: dock.liftController.state.phase, trail: dock.trailController.state.phase,
    road: dock.roadController.state.phase, dam: dock.snowmakingController.dam.state.phase,
    pond: dock.snowmakingController.pond.state.phase,
    'ski-path': dock.nodePathController.pathTool.phase,
    'ski-node': (skiNode.phase === 'add' && skiNode.candidate) || (skiNode.phase === 'remove' && skiNode.junctionId) ? 'review' : 'placing',
    'snowmaking-gun': dock.snowmakingController.guns.tool.phase,
    'snowmaking-node': hydrantRun.phase !== 'idle' ? hydrantRun.phase : snowNode.phase === 'placing' && snowNode.candidate ? 'review' : 'placing',
    'snowmaking-pipe': dock.snowmakingController.network.pipeTool.phase,
    building: dock.buildingController?.state.phase ?? 'placing',
  };
  const stage = dock.building ? 3 : constructionStage(phases[tool ?? ''] ?? 'placing');
  const navigate = (next: WorkspaceSection) => { if (next === 'lifts' || next === 'trails') setNetworkSection(next); workspace?.navigate({ section: next }); };
  return <div className={`workspace-shell${expanded ? ' is-expanded' : ''}${panelOpen ? ' has-panel' : ''}`}>
    <nav className="workspace-rail" aria-label="Mountain workspace">
      <div className="workspace-mark" title="Mountain Planner"><Icon name="resort" /></div>
      {SECTIONS.map((id) => <button key={id} className={`workspace-nav${section === id ? ' is-active' : ''}`}
        aria-label={id === 'lifts' ? 'Ski lifts' : id === 'trails' ? 'Ski runs' : WORKSPACE_LABELS[id]}
        aria-pressed={section === id} onClick={() => navigate(id)}>
        <Icon name={id} /><span>{WORKSPACE_LABELS[id]}</span>
      </button>)}
      <div className="workspace-rail-bottom"><button className={`workspace-nav${layersOpen ? ' is-active' : ''}`}
        aria-pressed={layersOpen} onClick={() => navigate('layers')}><Icon name="layers" /><span>Layers</span></button>
        <GameMenu {...props.menu} />
      </div>
    </nav>
    {panelOpen && <section className="workspace-panel" aria-label={`${section ? WORKSPACE_LABELS[section] : ''} workspace`}>
      <header className="workspace-header"><div><span className="ui-eyebrow">{analysis ? 'Mountain analysis' : tool ? 'Construction' : 'Mountain workspace'}</span>
        <h2>{WORKSPACE_LABELS[section!]}</h2></div>
        <div className="ui-actions"><button className="ui-icon-button" aria-label={expanded ? 'Collapse workspace' : 'Expand workspace'}
          aria-pressed={expanded} onClick={() => setExpanded(!expanded)}><Icon name="expand" /></button>
          <button className="ui-icon-button" aria-label="Close workspace" onClick={() => { workspace?.close(); if (section === 'layers') dock.snowControl?.close(); }}><Icon name="close" /></button></div>
      </header>
      {tool && <ol className="construction-stages" aria-label="Construction progress">
        {['Configure', 'Draw / place', 'Review', 'Build'].map((label, index) => <li key={label}
          aria-current={stage === index ? 'step' : undefined} className={stage === index ? 'is-current' : ''}>{label}</li>)}
      </ol>}
      {!tool && section && ['lifts', 'trails', 'snowmaking'].includes(section) && <div className="workspace-view-switch" role="group" aria-label="Workspace view">
        <button className="ui-button" aria-pressed={!analysis} onClick={() => { if (analysis) navigate(section!); }}>Build & inspect</button>
        {section === 'snowmaking' && <button className="ui-button" aria-pressed={analysis && props.dashboard?.snowmakingMode === 'inspect'}
          onClick={() => workspace?.navigate({ section: 'snowmaking', view: 'network' })}>Network</button>}
        <button className="ui-button" aria-pressed={analysis && (section !== 'snowmaking' || props.dashboard?.snowmakingMode === 'analysis')} onClick={() => { if (section === 'lifts' || section === 'trails') setNetworkSection(section); workspace?.navigate({ section: section!, view: 'analysis' }); }}>Analysis</button>
        {analysis && workspace?.canEditAnalysis && <button className="ui-button ui-button-primary" onClick={workspace.editAnalysis}>Edit selected</button>}
      </div>}
      {feedback && <div className="workspace-feedback" role="status">Construction complete</div>}
      {buildAnother && <div className="workspace-view-switch"><button className="ui-button ui-button-primary" disabled={dock.building}
        onClick={buildAnother}>Build another</button></div>}
      <div className="workspace-body">
        {props.dashboard ? <MountainDashboards {...props.dashboard} />
          : dock.simulation.analysisOpen ? <GameWeatherOverlay terrain={dock.terrainRecord} weather={dock.simulation} units={dock.units} />
          : props.stats ? <ResortStatsPanel {...props.stats} embedded />
          : <MapGameDock {...dock} />}
        {tool && <div className="workspace-tool-options">{props.bottomRightToolOptions}</div>}
      </div>
    </section>}
    {!panelOpen && layersOpen && <div className="workspace-layers-only"><MapGameDock {...dock} /></div>}
    <GameToolbar resortName={dock.saved.name} onOpenStats={() => navigate('resort')}
      onOpenWeather={() => navigate('weather')} showWeatherOverlay={false}
      saveStatus={props.menu.saving ? 'Saving…' : props.menu.unsaved ? 'Unsaved changes' : 'Saved'}
      readout={readout} units={dock.units} terrain={dock.terrainRecord} simulation={dock.simulation} />
  </div>;
}
