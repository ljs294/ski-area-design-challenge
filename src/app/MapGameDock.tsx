import { useMemo } from 'react';
import type { GameSave, SavedDam, SavedJunction, SavedLift, SavedNode, SavedPath,
  SavedPond, SavedRoad, SavedSnowmakingNode, SavedTrail,
  TerrainRecord } from '../types';
import type { SavedWeatherRun } from '../types/gameSave';
import type { SavedSnowgun, SavedSnowmakingPipe, SnowmakingLakeSource } from '../types/snowmaking';
import { analyzeLake } from '../lakeAnalysis';
import { analyzeStream } from '../streamAnalysis';
import { describeAnchor, pathLengthM } from '../skiNodes';
import type { SkiNetwork } from '../network';
import { summarizeJunctions } from '../topology';
import { fmtDistance } from '../lifts';
import type { LayerToggle } from './analysisLayers';
import { GameToolbar } from './GameToolbar';
import { InfrastructureControl } from './InfrastructureControl';
import { RoadDetail } from './RoadDetail';
import { LakeDetail } from './LakeDetail';
import { LayerList } from './LayerPanel';
import { Legend, type OverlayId } from './Legend';
import { LiftControl } from './LiftControl';
import { LiftDetail } from './LiftDetail';
import { LiftOverview } from './LiftOverview';
import { SnowmakingControl } from './SnowmakingControl';
import { StreamDetail } from './StreamDetail';
import { AnchorValue, TrailControl } from './TrailControl';
import { TrailDetail } from './TrailDetail';
import { TrailsPanel, type TrailsTool } from './TrailsPanel';
import { useCursorReadout, type CursorReadoutStore } from './CursorReadout';
import type { Units } from './SettingsContext';
import type { DockId, ToolCoordinatorSnapshot } from './toolCoordinator';
import type { LiftController } from './useLiftController';
import type { RoadController } from './useRoadController';
import type { TrailController } from './useTrailController';
import type { useNodePathController } from './useNodePathController';
import type { useSnowmakingController } from './useSnowmakingController';
import { SnowLayerControl } from './SnowLayerControl';
import type { SnowDisplayMode } from './snowStyle';
import { analyzeBuiltRoad, analyzeImportedRoad, type RoadAnalysis } from '../roadAnalysis';

type NodePathController = ReturnType<typeof useNodePathController>;
type SnowmakingController = ReturnType<typeof useSnowmakingController>;

export interface MapGameDockProps {
  saved: GameSave;
  units: Units;
  readoutStore: CursorReadoutStore;
  building: boolean;
  openDock: DockId | null;
  layersAlongsideBuild: boolean;
  coordinator: ToolCoordinatorSnapshot;
  layers: LayerToggle[];
  activeOverlay: OverlayId | null;
  lifts: SavedLift[];
  trails: SavedTrail[];
  roads: SavedRoad[];
  dams: SavedDam[];
  ponds: SavedPond[];
  snowmakingNodes: SavedSnowmakingNode[];
  snowmakingPipes: SavedSnowmakingPipe[];
  snowguns: SavedSnowgun[];
  snowmakingLakes: SnowmakingLakeSource[];
  skiNodes: SavedNode[];
  skiPaths: SavedPath[];
  junctions: SavedJunction[];
  terrainRecord: TerrainRecord | null;
  weatherRun: SavedWeatherRun | undefined;
  onWeatherRunChange(run: SavedWeatherRun): void;
  network: SkiNetwork;
  selectedLiftId: string | null;
  selectedTrailId: string | null;
  selectedDamId: string | null;
  selectedPondId: string | null;
  selectedSnowmakingNodeId: string | null;
  selectedSnowmakingPipeId: string | null;
  selectedSnowgunId: string | null;
  selectedNodeId: string | null;
  selectedPathId: string | null;
  selectedRoadKey: string | null;
  selectedLakeId: string | null;
  selectedStreamId: string | null;
  liftEditing: boolean;
  trailEditing: boolean;
  lakeDepthOverrides: Record<string, number>;
  lakeNameOverrides: Record<string, string>;
  snowmakingLakeIds: string[];
  streamWidthOverrides: Record<string, number>;
  snowControl: { mode: SnowDisplayMode; change(mode: SnowDisplayMode): void;
    close(): void; escapeEnabled: boolean } | null;
  liftController: LiftController;
  roadController: RoadController;
  trailController: TrailController;
  nodePathController: NodePathController;
  snowmakingController: SnowmakingController;
  openSnowmakingAnalysis(): void;
  toggleDock(dock: DockId): void;
  closeDock(): void;
  closeLayers(): void;
  toggleLayer(id: string): void;
  openStats(): void;
  setLiftEditing(value: boolean): void;
  setTrailEditing(value: boolean): void;
  clearSelectedLift(): void;
  clearSelectedTrail(): void;
  clearSelectedDam(): void;
  clearSelectedPond(): void;
  clearSelectedSnowmakingNode(): void;
  clearSelectedSnowmakingPipe(): void;
  clearSelectedSnowgun(): void;
  clearSelectedNode(): void;
  clearSelectedPath(): void;
  clearSelectedRoad(): void;
  clearSelectedLake(): void;
  clearSelectedStream(): void;
  setLakeName(id: string, name: string | null): void;
  setLakeDepth(id: string, depthM: number | null): void;
  setLakeSnowmaking(id: string, enabled: boolean): void;
  setStreamWidth(id: string, widthM: number | null): void;
}

export function MapGameDock(props: MapGameDockProps) {
  const readout = useCursorReadout(props.readoutStore);
  const { liftController, roadController, trailController, nodePathController,
    snowmakingController } = props;
  const liftTool = liftController.state, trailTool = trailController.state;
  const roadTool = roadController.state, { nodeTool, pathTool } = nodePathController;
  const damTool = snowmakingController.dam.state, pondTool = snowmakingController.pond.state;
  const liftActive = props.coordinator.activeTool === 'lift' || props.selectedLiftId !== null;
  const trailActive = props.coordinator.activeTool === 'trail' ||
    props.coordinator.activeTool === 'ski-node' || props.coordinator.activeTool === 'ski-path' ||
    props.selectedTrailId !== null;
  const snowmakingActive = props.coordinator.activeTool === 'dam' ||
    props.coordinator.activeTool === 'pond' || props.coordinator.activeTool === 'snowmaking-pipe' ||
    props.coordinator.activeTool === 'snowmaking-node' || props.coordinator.activeTool === 'snowmaking-gun' ||
    props.selectedDamId !== null ||
    props.selectedPondId !== null || props.selectedSnowmakingNodeId !== null ||
    props.selectedSnowmakingPipeId !== null || props.selectedSnowgunId !== null;
  const selectedLakeFeature = props.selectedLakeId
    ? props.terrainRecord?.vectorFeatures?.waterPolygons.find(
      (lake) => lake.id === props.selectedLakeId) ?? null : null;
  const selectedLake = useMemo(() => selectedLakeFeature && props.terrainRecord
    ? analyzeLake(selectedLakeFeature, props.terrainRecord,
      props.lakeDepthOverrides[selectedLakeFeature.id], props.lakeNameOverrides[selectedLakeFeature.id])
    : null, [selectedLakeFeature, props.terrainRecord,
      props.lakeDepthOverrides, props.lakeNameOverrides]);
  const selectedStreamFeature = props.selectedStreamId
    ? props.terrainRecord?.vectorFeatures?.waterLines.find(
      (stream) => stream.id === props.selectedStreamId) ?? null : null;
  const selectedStream = useMemo(() => selectedStreamFeature
    ? analyzeStream(selectedStreamFeature, props.streamWidthOverrides[selectedStreamFeature.id]) : null,
  [selectedStreamFeature, props.streamWidthOverrides]);
  const selectedRoad = useMemo<RoadAnalysis | null>(() => {
    if (!props.selectedRoadKey) return null;
    if (props.selectedRoadKey.startsWith('player:')) {
      const road = props.roads.find((entry) => `player:${entry.id}` === props.selectedRoadKey);
      return road ? analyzeBuiltRoad(road) : null;
    }
    const road = props.terrainRecord?.vectorFeatures?.roads.find(
      (entry) => `osm:${entry.id}` === props.selectedRoadKey);
    return road ? analyzeImportedRoad(road) : null;
  }, [props.selectedRoadKey, props.roads, props.terrainRecord]);
  const contextDetailOpen = selectedLake !== null || selectedStream !== null || selectedRoad !== null;
  const liftsOpen = !contextDetailOpen && (props.openDock === 'lifts' || liftActive);
  const trailsOpen = !contextDetailOpen && !liftsOpen && (props.openDock === 'trails' || trailActive);
  const snowmakingOpen = !contextDetailOpen && !liftsOpen && !trailsOpen &&
    (props.openDock === 'snowmaking' || snowmakingActive);
  const infrastructureOpen = selectedRoad !== null || (!contextDetailOpen && !liftsOpen && !trailsOpen &&
    !snowmakingOpen && (props.openDock === 'infrastructure' || props.coordinator.activeTool === 'road'));
  const layersOpen = !contextDetailOpen && !liftsOpen &&
    (props.openDock === 'layers' || props.layersAlongsideBuild);
  const selectedLift = props.selectedLiftId
    ? props.lifts.find((lift) => lift.id === props.selectedLiftId) ?? null : null;
  const selectedTrail = props.selectedTrailId
    ? props.trails.find((trail) => trail.id === props.selectedTrailId) ?? null : null;
  const selectedDam = props.selectedDamId
    ? props.dams.find((dam) => dam.id === props.selectedDamId) ?? null : null;
  const selectedPond = props.selectedPondId
    ? props.ponds.find((pond) => pond.id === props.selectedPondId) ?? null : null;
  const selectedSnowmakingNode = props.selectedSnowmakingNodeId
    ? props.snowmakingNodes.find((node) => node.id === props.selectedSnowmakingNodeId) ?? null : null;
  const selectedSnowmakingPipe = props.selectedSnowmakingPipeId
    ? props.snowmakingPipes.find((pipe) => pipe.id === props.selectedSnowmakingPipeId) ?? null : null;
  const selectedSnowgun = props.selectedSnowgunId
    ? props.snowguns.find((gun) => gun.id === props.selectedSnowgunId) ?? null : null;
  const anchorWorld = useMemo(() => ({ trails: props.trails, lifts: props.lifts,
    junctions: props.junctions, nodes: props.skiNodes, paths: props.skiPaths }),
  [props.trails, props.lifts, props.junctions, props.skiNodes, props.skiPaths]);
  const junctionRows = useMemo(() => summarizeJunctions(anchorWorld), [anchorWorld]);
  const trailPanelBusy = trailTool.phase !== 'idle' || props.trailEditing ||
    nodeTool.phase !== 'idle' || pathTool.phase !== 'idle' || props.selectedTrailId !== null;
  const activeTrailsTool: TrailsTool = trailTool.phase !== 'idle' ? 'trail'
    : nodeTool.phase === 'add' ? 'node-add' : nodeTool.phase === 'remove' ? 'node-remove'
      : pathTool.phase !== 'idle' ? 'path' : 'none';
  const trailNetworkWarnings = useMemo(() => {
    const diagnostics = props.network.diagnostics, warnings: string[] = [];
    const plural = (count: number, one: string, many: string) => count === 1 ? one : many;
    if (diagnostics.orphanTrailIds.length) warnings.push(`${diagnostics.orphanTrailIds.length} ${
      plural(diagnostics.orphanTrailIds.length, 'run is', 'runs are')} not reachable from any lift.`);
    const unresolved = diagnostics.unresolvedAnchorTrailIds.length +
      diagnostics.unresolvedAnchorPathIds.length;
    if (unresolved) warnings.push(`${unresolved} start ${plural(unresolved,
      'connection no longer resolves', 'connections no longer resolve')} — the target was moved or deleted.`);
    if (diagnostics.overreachingAnchorIds.length) warnings.push(
      `${diagnostics.overreachingAnchorIds.length} ${plural(diagnostics.overreachingAnchorIds.length,
        'connection spans', 'connections span')} an unusually long gap.`);
    if (diagnostics.degeneratePathIds.length) warnings.push(
      `${diagnostics.degeneratePathIds.length} ${plural(diagnostics.degeneratePathIds.length,
        'path starts', 'paths start')} and ends at the same junction.`);
    if (diagnostics.componentCount > 1) warnings.push(
      `The mountain is in ${diagnostics.componentCount} disconnected pieces.`);
    return warnings;
  }, [props.network]);

  return <div className="game-dock"><div className="dock-stack">
    {props.snowControl && <SnowLayerControl mode={props.snowControl.mode}
      onModeChange={props.snowControl.change} onClose={props.snowControl.close}
      escapeEnabled={props.snowControl.escapeEnabled} readout={readout} units={props.units} />}
    <div className="dock-rollups">
    {selectedStream && <div className="dock-rollup dock-stream" data-panel="stream">
      <div className="dock-panel"><StreamDetail stream={selectedStream} units={props.units}
        onWidthOverride={(width) => props.setStreamWidth(selectedStream.id, width)}
        onClose={props.clearSelectedStream} /></div></div>}
    {selectedLake && <div className="dock-rollup dock-lake" data-panel="lake">
      <div className="dock-panel"><LakeDetail lake={selectedLake} units={props.units}
        isSnowmaking={props.snowmakingLakeIds.includes(selectedLake.id)}
        onSnowmakingChange={(enabled) => props.setLakeSnowmaking(selectedLake.id, enabled)}
        onNameOverride={(name) => props.setLakeName(selectedLake.id, name)}
        onDepthOverride={(depth) => props.setLakeDepth(selectedLake.id, depth)}
        onClose={props.clearSelectedLake} /></div></div>}
    {layersOpen && <div className="dock-rollup dock-layers">
      {props.activeOverlay && props.activeOverlay !== 'snow' && <div className="dock-legend-popover">
        <Legend overlay={props.activeOverlay} /></div>}
      <div className="dock-panel"><div className="dock-head"><span className="dock-head-title">Layers</span>
        <button className="settings-close-x" aria-label="Close" onClick={props.closeLayers}>✕</button>
      </div><LayerList layers={props.layers} onToggle={props.toggleLayer}
        activeOverlay={props.activeOverlay} inlineLegend={false} /></div></div>}
    {trailsOpen && <div className="dock-rollup dock-trails" data-panel="trails"><div className="dock-panel">
      {trailPanelBusy ? nodeTool.phase !== 'idle' ? <div className="site-control site-control-wide trail-panel">
        <div className="dock-head"><span className="dock-head-title">
          {nodeTool.phase === 'add' ? 'Add node' : 'Remove node'}</span>
          <button className="settings-close-x" aria-label="Close" onClick={nodePathController.cancelNode}>✕</button>
        </div><div className="site-hint">{nodeTool.phase === 'add'
          ? 'Click anywhere along a run to split it there.'
          : 'Click a node on a run. Only one the run passes straight through can go.'}</div>
        {nodeTool.phase === 'add' && nodeTool.candidate && <div className="readout-line">
          <span className="lift-stat-label">Splits</span><span className="lift-stat-value">
            <AnchorValue anchor={nodeTool.candidate} world={anchorWorld} /></span></div>}
        {nodeTool.phase === 'remove' && nodeTool.junctionId && (() => {
          const row = junctionRows.find((entry) => entry.id === nodeTool.junctionId);
          return row ? <div className="readout-line"><span className="lift-stat-label">
            Node {row.number}</span><span className="lift-stat-value">{row.label}</span></div> : null;
        })()}
        {nodeTool.error && <div className="lift-warning">{nodeTool.error}</div>}
        <div className="site-actions"><button className="site-btn"
          onClick={nodePathController.cancelNode}>Done</button>
          {nodeTool.phase === 'add' ? <button className="site-btn site-btn-primary"
            disabled={!nodeTool.candidate} onClick={nodePathController.confirmAddNode}>Add node</button>
            : <button className="site-btn site-btn-primary"
              disabled={!nodeTool.junctionId || nodeTool.error !== null}
              onClick={nodePathController.confirmRemoveNode}>Remove node</button>}</div>
      </div> : pathTool.phase !== 'idle' ? <div className="site-control site-control-wide trail-panel">
        <div className="dock-head"><span className="dock-head-title">Draw path</span>
          <button className="settings-close-x" aria-label="Close" onClick={nodePathController.cancelPath}>✕</button>
        </div>{pathTool.phase === 'review' ? <><input className="name-entry-input lift-name-input"
          value={pathTool.name} onChange={(event) => nodePathController.renamePath(event.target.value)} />
          <div className="readout-line"><span className="lift-stat-label">From</span>
            <span className="lift-stat-value">{describeAnchor(pathTool.from)}</span></div>
          <div className="readout-line"><span className="lift-stat-label">To</span>
            <span className="lift-stat-value">{describeAnchor(pathTool.to)}</span></div>
          <div className="readout-line"><span className="lift-stat-label">Length</span>
            <span className="lift-stat-value">{fmtDistance(pathLengthM(pathTool.points), props.units)}</span></div>
          <div className="site-actions"><button className="site-btn"
            onClick={nodePathController.cancelPath}>Cancel</button>
            <button className="site-btn site-btn-primary"
              onClick={nodePathController.confirmPath}>Build path</button></div></> : <>
          <div className="site-hint">{pathTool.phase === 'armed'
            ? 'Click anywhere along a ski trail to start the connector.'
            : 'Click along the route. Finish on a different ski trail — a node is added where each end meets a run.'}</div>
          <div className="site-actions"><button className="site-btn" onClick={nodePathController.undoPath}
            disabled={pathTool.phase !== 'drawing'}>Undo point</button>
            <button className="site-btn site-btn-primary" onClick={nodePathController.finishPath}
              disabled={pathTool.phase !== 'drawing' || pathTool.points.length < 2}>Finish</button></div>
          <button className="site-btn" onClick={nodePathController.cancelPath}>Cancel</button></>}</div>
      : trailTool.phase === 'idle' && selectedTrail && !props.trailEditing
        ? <TrailDetail trail={selectedTrail} units={props.units}
          onEdit={() => props.setTrailEditing(true)} onRemove={() => trailController.remove(selectedTrail.id)}
          onToggleClosed={(closed) => trailController.patch(selectedTrail.id, { closed })}
          onClose={props.clearSelectedTrail} />
        : <TrailControl tool={trailTool} trails={props.trails} world={anchorWorld}
          selectedId={trailTool.phase === 'idle' ? props.selectedTrailId : null} units={props.units}
          brushWidthM={trailController.brushWidthM} onBrushWidthChange={trailController.changeBrushWidth}
          onCancel={trailController.cancel} onModeChange={trailController.setPaintMode}
          onUndo={trailController.undoPaint} onClear={trailController.clearPaint}
          onFinish={trailController.finishPaint} onDraftChange={trailController.patchDraft}
          onGradingChange={trailController.setGrading} onConfirm={trailController.confirm}
          building={props.building} onEditPatch={trailController.patch}
          onCloseEdit={() => props.setTrailEditing(false)} onDelete={trailController.remove}
          onRetryElevation={trailController.retryElevation} onChangeHead={trailController.changeHead}
          onBackToPaint={trailController.backToPaint} />
      : <TrailsPanel trails={props.trails} junctions={junctionRows} legacyNodes={props.skiNodes}
        paths={props.skiPaths} units={props.units} selectedTrailId={props.selectedTrailId}
        selectedNodeId={props.selectedNodeId} selectedPathId={props.selectedPathId}
        activeTool={activeTrailsTool} warnings={trailNetworkWarnings}
        onPaintRun={trailController.arm} onAddNode={() => nodePathController.armNode('add')}
        onRemoveNodeTool={() => nodePathController.armNode('remove')}
        onDrawPath={nodePathController.armPath} onSelectTrail={trailController.select}
        onSelectNode={nodePathController.selectNode} onSelectPath={(id) => id
          ? nodePathController.selectPath(id) : props.clearSelectedPath()}
        onDeleteNode={nodePathController.removeNode} onDeleteLegacyNode={nodePathController.deleteLegacyNode}
        onDeletePath={nodePathController.removePath} onClose={props.closeDock} />}
    </div></div>}
    {liftsOpen && <div className="dock-rollup dock-lifts"><div className="dock-panel">
      {liftTool.phase === 'idle' && selectedLift && !props.liftEditing
        ? <LiftDetail lift={selectedLift} units={props.units}
          onEdit={() => props.setLiftEditing(true)} onRemove={() => liftController.remove(selectedLift.id)}
          onToggleClosed={(closed) => liftController.patch(selectedLift.id, { closed })}
          onClose={props.clearSelectedLift} />
        : liftTool.phase === 'idle' && !selectedLift
          ? <LiftOverview lifts={props.lifts} units={props.units} onArm={liftController.arm}
            onSelect={liftController.select} onClose={props.closeDock} />
          : <LiftControl tool={liftTool} lifts={props.lifts}
            selectedId={liftTool.phase === 'idle' ? props.selectedLiftId : null} units={props.units}
            onArm={liftController.arm} onStartPlacement={liftController.startPlacement}
            onTypeChange={liftController.setType} onCancel={liftController.cancel}
            onDraftChange={liftController.patchDraft} onConfirm={liftController.confirm}
            building={props.building} onSelect={liftController.select} onEditPatch={liftController.patch}
            onCloseEdit={() => props.setLiftEditing(false)} onDelete={liftController.remove}
            onRetryElevation={liftController.retryElevation} />}
    </div></div>}
    {snowmakingOpen && <div className="dock-rollup dock-snowmaking"><div className="dock-panel">
      <SnowmakingControl damTool={damTool} pondTool={pondTool} dams={props.dams} ponds={props.ponds}
        lakes={props.snowmakingLakes}
        selectedDam={selectedDam} selectedPond={selectedPond} nodes={props.snowmakingNodes}
        pipes={props.snowmakingPipes} guns={props.snowguns} selectedNode={selectedSnowmakingNode}
        selectedPipe={selectedSnowmakingPipe} selectedGun={selectedSnowgun}
        pipeTool={snowmakingController.network.pipeTool}
        nodeTool={snowmakingController.network.nodeTool}
        hydrantRunTool={snowmakingController.network.hydrantRunTool}
        hydrantRunPreview={snowmakingController.network.hydrantRunPreview}
        gunTool={snowmakingController.guns.tool} gunPreview={snowmakingController.guns.preview}
        diameterIn={snowmakingController.network.diameterIn}
        snapping={snowmakingController.network.snapping} units={props.units}
        onArmDam={snowmakingController.dam.arm} onCancelDam={snowmakingController.dam.cancel}
        onDamDraftChange={snowmakingController.dam.patchDraft}
        onConfirmDam={snowmakingController.dam.confirm} onSelectDam={snowmakingController.dam.select}
        onDeleteDam={snowmakingController.dam.remove} onCloseDam={props.clearSelectedDam}
        onArmPond={snowmakingController.pond.arm} onCancelPond={snowmakingController.pond.cancel}
        onUndoPond={snowmakingController.pond.undo} onFinishPond={snowmakingController.pond.finish}
        onPondDraftChange={snowmakingController.pond.patchDraft}
        onPondElevationChange={snowmakingController.pond.changeElevation}
        onPondExcavationChange={snowmakingController.pond.changeExcavation}
        onConfirmPond={snowmakingController.pond.confirm} onSelectPond={snowmakingController.pond.select}
        onDeletePond={snowmakingController.pond.remove}
        onPondSnowmakingChange={snowmakingController.pond.setSnowmaking}
        onClosePond={props.clearSelectedPond}
        onArmPipe={snowmakingController.network.armPipe}
        onCancelPipe={snowmakingController.network.cancelPipe}
        onUndoPipe={snowmakingController.network.undoPipe}
        onFinishPipe={snowmakingController.network.finishPipe}
        onConfirmPipe={snowmakingController.network.confirmPipe}
        onRenameDraftPipe={snowmakingController.network.renameDraftPipe}
        onDiameterChange={snowmakingController.network.setDiameter}
        onSnappingChange={snowmakingController.network.setSnapping}
        onArmNode={snowmakingController.network.armNode}
        onCancelNode={snowmakingController.network.cancelNode}
        onConfirmNode={snowmakingController.network.confirmNode}
        onSetPumpSuctionSide={snowmakingController.network.setPumpSuctionSide}
        onSetPumpPort={snowmakingController.network.setPumpPort}
        onArmHydrantRun={snowmakingController.network.armHydrantRun}
        onCancelHydrantRun={snowmakingController.network.cancelHydrantRun}
        onBackHydrantRun={snowmakingController.network.backHydrantRun}
        onHydrantRunModeChange={snowmakingController.network.setHydrantRunMode}
        onHydrantRunCountChange={snowmakingController.network.setHydrantRunCount}
        onHydrantRunSpacingChange={snowmakingController.network.setHydrantRunSpacing}
        onConfirmHydrantRun={snowmakingController.network.confirmHydrantRun}
        onSelectNode={snowmakingController.network.selectNode}
        onRenameNode={snowmakingController.network.renameNode}
        onDeleteNode={snowmakingController.network.removeNode}
        onCloseNode={props.clearSelectedSnowmakingNode}
        onSelectPipe={snowmakingController.network.selectPipe}
        onPatchPipe={snowmakingController.network.patchPipe}
        onDeletePipe={snowmakingController.network.removePipe}
        onClosePipe={props.clearSelectedSnowmakingPipe}
        onArmGuns={snowmakingController.guns.arm} onCancelGuns={snowmakingController.guns.cancel}
        onSnowgunVariantChange={snowmakingController.guns.setVariant}
        onRemoveDraftGun={snowmakingController.guns.removeDraft}
        onReviewGuns={snowmakingController.guns.review} onBackGuns={snowmakingController.guns.back}
        onConfirmGuns={snowmakingController.guns.confirm} onSelectGun={(id) =>
          snowmakingController.network.selectGun(id)} onMoveGun={snowmakingController.guns.armMove}
        onConfirmMoveGun={snowmakingController.guns.confirmMove}
        onDeleteGun={snowmakingController.guns.remove} onCloseGun={props.clearSelectedSnowgun}
        onAnalyzeSystem={props.openSnowmakingAnalysis}
        building={props.building} onClose={props.closeDock} />
    </div></div>}
    {infrastructureOpen && <div className="dock-rollup dock-infrastructure"><div className="dock-panel">
      {selectedRoad ? <RoadDetail road={selectedRoad} units={props.units}
        onClose={props.clearSelectedRoad} /> : <InfrastructureControl tool={roadTool} roads={props.roads} units={props.units}
        onArm={roadController.arm} onCancel={roadController.cancel} onUndo={roadController.undo}
        onFinish={roadController.finish} onDraftChange={roadController.patchDraft}
        onConfirm={roadController.confirm} building={props.building} onClose={props.closeDock} />}
    </div></div>}
  </div><div className="dock-circles">
    <DockButton id="layers" label="Layers" open={layersOpen} onClick={props.toggleDock} />
    <DockButton id="lifts" label="Ski lifts" open={liftsOpen} onClick={props.toggleDock} />
    <DockButton id="trails" label="Ski runs" open={trailsOpen} onClick={props.toggleDock} />
    <DockButton id="snowmaking" label="Snowmaking" open={snowmakingOpen} onClick={props.toggleDock} />
    <DockButton id="infrastructure" label="Infrastructure" open={infrastructureOpen}
      onClick={props.toggleDock} />
  </div></div><GameToolbar resortName={props.saved.name} onOpenStats={props.openStats}
    readout={readout} units={props.units} terrain={props.terrainRecord}
    weatherRun={props.weatherRun} onWeatherRunChange={props.onWeatherRunChange} /></div>;
}

function DockButton({ id, label, open, onClick }: {
  id: Exclude<DockId, null>; label: string; open: boolean; onClick(id: DockId): void;
}) {
  return <button className={`dock-circle dock-circle-${id}${open ? ' is-active' : ''}`}
    onClick={() => onClick(id)} aria-pressed={open} title={label} aria-label={label}>
    {id === 'layers' ? <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
      <path d="M12 3 2 8l10 5 10-5-10-5Z" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" />
      <path d="M2 12l10 5 10-5M2 16l10 5 10-5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" /></svg>
      : id === 'lifts' ? <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
        <path d="M3 6l18-3" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
        <circle cx="10" cy="5.4" r="1.1" fill="currentColor" /><path d="M10 6.5v2.8m-2.4 0h4.8l-.7 3.4H8.3l-.7-3.4Z" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round" /></svg>
        : id === 'trails' ? <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
          <path d="M3 20 12 4l9 16Z" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinejoin="round" />
          <path d="M8.5 12q2 2.4 3.5 0t3.5 0" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" /></svg>
          : id === 'snowmaking' ? <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
            <path d="M12 3v18M4.2 7.5l15.6 9M19.8 7.5l-15.6 9" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
            <path d="M9.6 4.8 12 7.2l2.4-2.4M9.6 19.2 12 16.8l2.4 2.4" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" /></svg>
            : <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true">
              <path d="M5 22c0-7 4-8 4-13 0-3-1-5-1-7M19 22c0-7-4-8-4-13 0-3 1-5 1-7" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
              <path d="M12 20v-3m0-3v-3m0-3V5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>}
  </button>;
}
