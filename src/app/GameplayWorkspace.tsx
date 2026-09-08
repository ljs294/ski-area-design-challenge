import { useEffect, useMemo, useRef, useState } from 'react';
import { analyzeLake } from '../lakeAnalysis';
import { LakeDetail } from './LakeDetail';
import type { MapViewChromeProps } from './MapViewChrome';
import { MapGameDock } from './MapGameDock';
import { GameToolbar, GameWeatherOverlay } from './GameToolbar';
import { GameMenu } from './GameMenu';
import { MountainDashboards } from './MountainDashboards';
import { ResortStatsPanel } from './ResortStatsPanel';
import { GameWindow, GameWindows } from './GameWindow';
import { GameTabs } from './GameTabs';
import { TrailDetail } from './TrailDetail';
import { LiftDetail } from './LiftDetail';
import { TrailProfile } from './TrailProfile';
import { PondDetail } from './PondDetail';
import { isStandalonePond } from './pondSelection';
import { Icon } from './ui';
import { MapAppearanceMenu } from './MapAppearanceMenu';
import { Settings } from './Settings';
import type { ResortSettingsCapability } from './Settings';
import { sectionForTool, type WorkspaceSection } from './workspaceModel';
import './gameWindows.css';

type Props = Pick<MapViewChromeProps, 'dock' | 'dashboard' | 'stats' | 'menu' | 'workspace' | 'bottomRightToolOptions' | 'resortSettings'>;
type Entity = { kind: 'trail'; id: string } | { kind: 'lift'; id: string } | { kind: 'pond'; id: string };
type ToolTab = 'lifts' | 'trails' | 'snowmaking' | 'infrastructure';
const TOOLS = [{ value: 'lifts', label: 'Lifts' }, { value: 'trails', label: 'Trails' },
  { value: 'snowmaking', label: 'Snowmaking' }, { value: 'infrastructure', label: 'Infrastructure' }] as const;
const DASHBOARDS = [{ value: 'trails', label: 'Trails Map' }, { value: 'snowmaking', label: 'Snowmaking' },
  { value: 'guests', label: 'Guests' }, { value: 'weather', label: 'Weather' }] as const;
const entityKey = (entity: Entity) => `${entity.kind}:${entity.id}`;

export function GameplayWorkspace(props: Props & { dock: NonNullable<Props['dock']> }) {
  return <GameWindows><Workspace {...props} /></GameWindows>;
}
function Workspace(props: Props & { dock: NonNullable<Props['dock']> }) {
  const { dock, workspace } = props;
  const [lastToolTab, setLastToolTab] = useState<ToolTab>('lifts');
  const [toolboxPinned, setToolboxPinned] = useState(false);
  const [pinned, setPinned] = useState<Entity[]>([]);
  const layersButtonRef = useRef<HTMLButtonElement>(null);
  const tool = dock.coordinator.activeTool;
  const selectedLake = useMemo(() => {
    const feature = dock.terrainRecord?.vectorFeatures?.waterPolygons.find((lake) => lake.id === dock.selectedLakeId);
    return feature && dock.terrainRecord ? analyzeLake(feature, dock.terrainRecord,
      dock.lakeDepthOverrides[feature.id], dock.lakeNameOverrides[feature.id]) : null;
  }, [dock.selectedLakeId, dock.terrainRecord, dock.lakeDepthOverrides, dock.lakeNameOverrides]);
  const weatherOpen = dock.simulation.analysisOpen;
  const dashboardTab = weatherOpen ? 'weather' : props.dashboard?.dashboard ?? 'trails';
  const dashboardOpen = !!props.dashboard || weatherOpen;
  const edge = props.dashboard?.networkProps.selectedEdgeId
    ? dock.network.edgeById.get(props.dashboard.networkProps.selectedEdgeId) : null;
  const selectedPond = dock.selectedPondId
    ? dock.ponds.find((pond) => pond.id === dock.selectedPondId) ?? null : null;
  const selected: Entity | null = tool || dock.liftEditing || dock.trailEditing ? null
    : props.dashboard?.dashboard === 'trails' ? props.dashboard.networkProps.selectedLiftId
      ? { kind: 'lift', id: props.dashboard.networkProps.selectedLiftId }
      : edge?.kind === 'trail' ? { kind: 'trail', id: edge.trailId } : null
    : dock.selectedLiftId ? { kind: 'lift', id: dock.selectedLiftId }
    : dock.selectedTrailId ? { kind: 'trail', id: dock.selectedTrailId }
    : selectedPond && isStandalonePond(dock.ponds, selectedPond.id) ? { kind: 'pond', id: selectedPond.id } : null;
  const entityExists = (entity: Entity) => entity.kind === 'trail'
    ? dock.trails.some((trail) => trail.id === entity.id)
    : entity.kind === 'lift' ? dock.lifts.some((lift) => lift.id === entity.id)
    : isStandalonePond(dock.ponds, entity.id);
  const visiblePinned = pinned.filter(entityExists);
  const inspectors = [...visiblePinned, ...(selected && !visiblePinned.some((entry) => entityKey(entry) === entityKey(selected)) ? [selected] : [])];
  const section = tool ? sectionForTool(tool) : dock.openDock;
  const toolTab: ToolTab = section && TOOLS.some((entry) => entry.value === section) ? section as ToolTab : lastToolTab;
  const toolboxOpen = toolboxPinned || (!dashboardOpen && !props.stats && section !== 'layers' && section !== null);
  const layersOpen = dock.openDock === 'layers' || dock.layersAlongsideBuild;
  const navigate = (next: WorkspaceSection) => workspace?.navigate({ section: next });
  const chooseTool = (next: ToolTab) => {
    setLastToolTab(next);
    if (section !== next || dashboardOpen || selected) navigate(next);
  };
  const chooseDashboard = (next: typeof DASHBOARDS[number]['value']) => {
    if (next === 'weather') { if (!weatherOpen) navigate('weather'); }
    else if (next === 'guests') { if (props.dashboard?.dashboard !== 'guests') navigate('guests'); }
    else if (next !== props.dashboard?.dashboard || weatherOpen) workspace?.navigate({ section: next, view: 'network' });
  };
  const clearInspector = (entity: Entity) => {
    setPinned((current) => current.filter((entry) => entityKey(entry) !== entityKey(entity)));
    if (selected && entityKey(selected) === entityKey(entity)) {
      if (props.dashboard?.dashboard === 'trails') {
        props.dashboard.networkProps.onSelectLift(null); props.dashboard.networkProps.onSelectEdge(null);
      } else if (entity.kind === 'trail') dock.clearSelectedTrail();
      else if (entity.kind === 'lift') dock.clearSelectedLift(); else dock.clearSelectedPond();
    }
  };
  const edit = (entity: Entity) => {
    workspace?.close();
    if (entity.kind === 'trail') { dock.trailController.select(entity.id); dock.setTrailEditing(true); }
    else if (entity.kind === 'lift') { dock.liftController.select(entity.id); dock.setLiftEditing(true); }
  };
  // Catalog presentation does not acquire any additional controller ownership.
  const catalog = { ...dock, openDock: toolTab, layersAlongsideBuild: false, snowControl: null,
    selectedLakeId: null,
    selectedLiftId: selected?.kind === 'lift' && !dock.liftEditing ? null : dock.selectedLiftId,
    selectedTrailId: selected?.kind === 'trail' && !dock.trailEditing ? null : dock.selectedTrailId };
  return <div className="workspace-shell game-window-shell">
    <div className="game-utilities">
      <TopRightSettings resortSettings={props.resortSettings} />
      <button ref={layersButtonRef} className="ui-icon-button" aria-label="Layers" aria-pressed={layersOpen} onClick={() => navigate('layers')}><Icon name="layers" /></button>
      <MapAppearanceMenu />
      <button className="ui-icon-button" aria-label="Settings" onClick={props.menu.onSettings}><span aria-hidden="true">⚙</span></button>
      <GameMenu {...props.menu} />
    </div>
    {dashboardOpen && <GameWindow id="dashboards" title="Dashboards" onClose={() => workspace?.close()}
      tabs={<GameTabs label="Dashboard views" value={dashboardTab} options={DASHBOARDS} onChange={chooseDashboard} panelId="dashboard-content" />}>
      <div id="dashboard-content" role="tabpanel" aria-label={DASHBOARDS.find((entry) => entry.value === dashboardTab)?.label}>
        {props.dashboard?.dashboard === 'snowmaking' && <GameTabs label="Snowmaking views" value={props.dashboard.snowmakingMode ?? 'inspect'}
          options={[{ value: 'inspect', label: 'Network' }, { value: 'analysis', label: 'Pressure & flow' }]}
          onChange={(next) => workspace?.navigate({ section: 'snowmaking', view: next === 'analysis' ? 'analysis' : 'network' })}
          panelId="snowmaking-dashboard-content" />}
        <div id="snowmaking-dashboard-content">
          {props.dashboard ? <MountainDashboards {...props.dashboard} />
            : <GameWeatherOverlay terrain={dock.terrainRecord} weather={dock.simulation} units={dock.units} />}
        </div>
      </div>
    </GameWindow>}
    {toolboxOpen && <GameWindow id="toolbox" title="Toolbox" pinned={toolboxPinned} onPin={() => setToolboxPinned(!toolboxPinned)}
      onClose={() => { setToolboxPinned(false); if (!dashboardOpen) workspace?.close(); }}
      tabs={<GameTabs label="Construction systems" value={toolTab} options={TOOLS} onChange={chooseTool} panelId="toolbox-content" />}>
      <div id="toolbox-content" role="tabpanel" aria-label={TOOLS.find((entry) => entry.value === toolTab)?.label}>
        <MapGameDock {...catalog} />
        {tool && <div className="workspace-tool-options">{props.bottomRightToolOptions}</div>}
      </div>
    </GameWindow>}
    {selectedLake && !tool && <GameWindow key={selectedLake.id} id={`inspector-lake:${selectedLake.id}`}
      title={selectedLake.name} onClose={dock.clearSelectedLake}>
      <div className="entity-inspector" data-panel="lake"><LakeDetail lake={selectedLake} units={dock.units}
        isSnowmaking={dock.snowmakingLakeIds.includes(selectedLake.id)}
        onSnowmakingChange={(enabled) => dock.setLakeSnowmaking(selectedLake.id, enabled)}
        onNameOverride={(name) => dock.setLakeName(selectedLake.id, name)}
        onDepthOverride={(depth) => dock.setLakeDepth(selectedLake.id, depth)}
        onClose={dock.clearSelectedLake} /></div>
    </GameWindow>}
    {inspectors.map((entity) => entity.kind === 'pond'
      ? <PondInspector key={entityKey(entity)} entity={entity} dock={dock}
        pinned={visiblePinned.some((entry) => entityKey(entry) === entityKey(entity))}
        onPin={() => setPinned((current) => current.some((entry) => entityKey(entry) === entityKey(entity))
          ? current.filter((entry) => entityKey(entry) !== entityKey(entity)) : [...current, entity])}
        onClose={() => clearInspector(entity)} onBuildAnother={() => { workspace?.close(); dock.snowmakingController.pond.arm(); }} />
      : <RunOrLiftInspector key={entityKey(entity)} entity={entity} dock={dock}
      pinned={visiblePinned.some((entry) => entityKey(entry) === entityKey(entity))}
      onPin={() => setPinned((current) => current.some((entry) => entityKey(entry) === entityKey(entity))
        ? current.filter((entry) => entityKey(entry) !== entityKey(entity)) : [...current, entity])}
      onClose={() => clearInspector(entity)} onEdit={() => edit(entity)}
      onBuildAnother={() => { workspace?.close(); if (entity.kind === 'trail') dock.trailController.arm(); else dock.liftController.arm(); }} />)}
    {(layersOpen || dock.snowControl) && <GameWindow id="layers" title="Layers" variant="menu" placement="below-anchor" anchorRef={layersButtonRef}
      onClose={() => { dock.closeLayers(); dock.snowControl?.close(); }}>
      <MapGameDock {...dock} openDock="layers" layersAlongsideBuild={false} selectedLiftId={null} selectedTrailId={null}
        selectedRoadKey={null} selectedLakeId={null} selectedStreamId={null} selectedDamId={null} selectedPondId={null}
        selectedSnowmakingNodeId={null} selectedSnowmakingPipeId={null} selectedSnowgunId={null} selectedBuildingId={null}
        selectedNodeId={null} selectedPathId={null} coordinator={{ ...dock.coordinator, activeTool: null }} />
    </GameWindow>}
    {props.stats && <GameWindow id="resort" title="Resort" onClose={() => workspace?.close()}><ResortStatsPanel {...props.stats} embedded /></GameWindow>}
    <GameToolbar resortName={dock.saved.name} onOpenStats={() => navigate('resort')} onOpenWeather={() => chooseDashboard('weather')}
      showWeatherOverlay={false} unsaved={props.menu.unsaved}
      units={dock.units} terrain={dock.terrainRecord} simulation={dock.simulation}
      navigation={<nav className="game-mode-tabs" aria-label="Game windows">
        <button aria-pressed={dashboardOpen} onClick={() => dashboardOpen ? workspace?.close() : chooseDashboard('trails')}><Icon name="trails" />Dashboards</button>
        <button aria-pressed={toolboxOpen && !dashboardOpen} onClick={() => toolboxOpen && !dashboardOpen ? workspace?.close() : navigate(lastToolTab)}><Icon name="infrastructure" />Toolbox</button>
      </nav>} />
  </div>;
}
function TopRightSettings({ resortSettings }: { resortSettings?: ResortSettingsCapability }) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setOpen(false);
      buttonRef.current?.focus();
    };
    window.addEventListener('mousedown', onPointerDown);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  return <div className="top-right-settings" ref={rootRef}>
    <button ref={buttonRef} type="button" className="ui-icon-button top-right-settings-toggle" aria-label="Settings"
      aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
      <span aria-hidden="true">Settings</span>
    </button>
    {open && <Settings presentation="popover" onClose={() => setOpen(false)} resortSettings={resortSettings} />}
  </div>;
}
function RunOrLiftInspector({ entity, dock, pinned, onPin, onClose, onEdit, onBuildAnother }: {
  entity: Extract<Entity, { kind: 'trail' | 'lift' }>; dock: NonNullable<Props['dock']>; pinned: boolean; onPin(): void; onClose(): void; onEdit(): void; onBuildAnother(): void;
}) {
  const [tab, setTab] = useState<'overview' | 'conditions' | 'profile'>('overview');
  const trail = entity.kind === 'trail' ? dock.trails.find((entry) => entry.id === entity.id) : null;
  const lift = entity.kind === 'lift' ? dock.lifts.find((entry) => entry.id === entity.id) : null;
  if (!trail && !lift) return null;
  const title = trail?.name ?? lift!.name;
  const panelId = `inspector-${entityKey(entity)}`;
  return <GameWindow id={panelId} title={title} pinned={pinned} onPin={onPin} onClose={onClose}
    tabs={<GameTabs<'overview' | 'conditions' | 'profile'> label={`${title} details`} value={tab}
      options={trail ? [{ value: 'overview', label: 'Overview' }, { value: 'conditions', label: 'Conditions' }, { value: 'profile', label: 'Profile' }]
        : [{ value: 'overview', label: 'Overview' }, { value: 'conditions', label: 'Operations' }]}
      onChange={setTab} panelId={panelId} />}>
    <div id={panelId} role="tabpanel" aria-label={`${title} ${tab}`} className="entity-inspector">
      {tab === 'overview' ? trail ? <TrailDetail trail={trail} units={dock.units} onEdit={onEdit} onClose={onClose}
        onRemove={() => dock.trailController.remove(trail.id)} onToggleClosed={(closed) => dock.trailController.patch(trail.id, { closed })} />
        : <LiftDetail lift={lift!} units={dock.units} onEdit={onEdit} onClose={onClose}
          onRemove={() => dock.liftController.remove(lift!.id)} onToggleClosed={(closed) => dock.liftController.patch(lift!.id, { closed })} />
        : tab === 'profile' && trail ? <TrailProfile parts={trail.parts} units={dock.units} difficulty={trail.difficulty} />
        : <dl className="inspector-metrics"><div><dt>State</dt><dd>{(trail ?? lift)?.closed ? 'Closed' : 'Open'}</dd></div>
          <div><dt>{trail ? 'Traffic history' : 'Throughput history'}</dt><dd>Not recorded</dd></div>
          {trail && <div><dt>Last groomed</dt><dd>Not recorded</dd></div>}</dl>}
      <button className="site-btn inspector-build-another" disabled={dock.building} onClick={onBuildAnother}>Build another</button>
    </div>
  </GameWindow>;
}
function PondInspector({ entity, dock, pinned, onPin, onClose, onBuildAnother }: {
  entity: Extract<Entity, { kind: 'pond' }>; dock: NonNullable<Props['dock']>; pinned: boolean; onPin(): void; onClose(): void; onBuildAnother(): void;
}) {
  const pond = isStandalonePond(dock.ponds, entity.id)
    ? dock.ponds.find((entry) => entry.id === entity.id) ?? null : null;
  if (!pond) return null;
  return <GameWindow id={`inspector-${entityKey(entity)}`} title={pond.name} pinned={pinned} onPin={onPin} onClose={onClose}>
    <div className="entity-inspector"><PondDetail pond={pond} units={dock.units}
      onRemove={() => dock.snowmakingController.pond.remove(pond.id)} />
      <button className="site-btn inspector-build-another" disabled={dock.building} onClick={onBuildAnother}>Build another</button>
    </div>
  </GameWindow>;
}
