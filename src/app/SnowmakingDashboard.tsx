import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { fmtDistance } from '../lifts';
import { makeFrame, simplifyRing, toMeters, type MetersFrame, type XY } from '../network';
import { SNOWMAKING_NODE_LABELS } from '../snowmakingNodes';
import { snowmakingNodeLabel, snowmakingPipeSegments } from '../snowmakingNetwork';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe, SnowmakingNodeKind,
  SnowmakingPumpPort } from '../types/snowmaking';
import type { SavedDam, SavedLift, SavedPond, SavedTrail, TerrainRecord } from '../types';
import type { SnowmakingLakeSource } from '../types/snowmaking';
import { FILL_BY_CODE } from './coverVectorize';
import { localContourGeoJSON } from './localContours';
import type { Units } from './SettingsContext';
import type { SnowmakingNetworkController } from './useSnowmakingNetworkController';
import type { SnowgunController } from './useSnowgunController';
import { SnowgunDashboardConnections, SnowgunDashboardMarkers } from './SnowgunDashboard';
import { SnowmakingAnalysisPanel } from './SnowmakingAnalysisPanel';
import { snowmakingSegmentAnnotationGeometry } from './snowmakingDashboardGeometry';
import { ringAreaM2, ringPathD } from './snowmakingDashboardModel';
import { snowmakingPressureColor, snowmakingPressureRange } from './snowmakingPressureHeatmap';
import { useSnowmakingAnalysis } from './useSnowmakingAnalysis';
import type { SnowmakingMapPresentation } from './dashboardMapLayers';
import { SnowmakingDashboardInspector } from './SnowmakingDashboardInspector';
import { SnowmakingPipeHoverDetails, type SnowmakingPipeHoverState } from './SnowmakingPipeHover';

type SnowmakingDashboardProps = Parameters<typeof SnowmakingDashboard>[0];

/** Bind the map-owned network state to the dashboard's presentation contract. */
export function snowmakingDashboardProps(input: {
  dams: SavedDam[]; ponds: SavedPond[]; lakes: SnowmakingLakeSource[];
  trails: SavedTrail[]; lifts: SavedLift[]; nodes: SavedSnowmakingNode[];
  pipes: SavedSnowmakingPipe[]; coverDisplay: CoverDisplayGeoJSON | null;
  guns: SavedSnowgun[];
  terrainRecord: TerrainRecord | null; units: Units;
  selectedNodeId: string | null; selectedPipeId: string | null;
  selectedPipeSegmentId?: string | null; selectedGunId: string | null;
  clearNode(): void; clearPipe(): void; clearGun(): void; controller: SnowmakingNetworkController;
  gunController: SnowgunController;
}): Omit<SnowmakingDashboardProps, 'onClose'> {
  const { controller, gunController, clearNode, clearPipe, clearGun, ...dashboard } = input;
  return {
    ...dashboard,
    onSelectNode: (id) => id ? controller.selectNode(id) : clearNode(),
    onSelectPipe: (id) => id ? controller.selectPipe(id) : clearPipe(),
    onSelectGun: (id) => id ? controller.selectGun(id) : clearGun(),
    onRenameNode: controller.renameNode,
    onDeleteNode: controller.removeNode,
    onPatchPipe: controller.patchPipe,
    onSetPumpPort: controller.setPumpPort,
    onDeletePipe: controller.removePipe,
    onMoveGun: gunController.armMove,
    onDeleteGun: gunController.remove,
  };
}

const PAD_FRAC = 0.06;
const MIN_SPAN_M = 120;
const NOMINAL_PX = 900;
const MIN_RING_AREA_PX2 = 4;

// Matches src/app/snowmakingLayers.ts's per-kind circle-color palette —
// duplicated rather than imported because that file only exports MapLibre
// paint expressions, not a plain color record.
const NODE_COLORS: Record<SnowmakingNodeKind, string> = {
  intake: '#397f9f',
  pump: '#f0b44d',
  junction: '#4b5563',
  hydrant: '#22c55e',
};
const SYSTEM_COLORS = ['#e08b24', '#8b5cf6', '#0f9f8f', '#d9468f', '#2563eb', '#65a30d'];
const FLOW_NUMBER = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });

interface View {
  x: number;
  y: number;
  w: number;
  h: number;
}
function niceDistance(target: number): number {
  if (!Number.isFinite(target) || target <= 0) return 100;
  const pow = Math.pow(10, Math.floor(Math.log10(target)));
  const norm = target / pow;
  const step = norm >= 5 ? 5 : norm >= 2 ? 2 : 1;
  return step * pow;
}

export function SnowmakingDashboard({
  dams,
  ponds,
  lakes = [],
  trails,
  lifts,
  nodes,
  pipes = [],
  guns = [],
  coverDisplay,
  terrainRecord,
  units,
  selectedNodeId,
  selectedPipeId = null,
  selectedPipeSegmentId = null,
  selectedGunId = null,
  onSelectNode,
  onSelectPipe = () => {},
  onSelectGun = () => {},
  onRenameNode = () => {},
  onDeleteNode = () => {},
  onPatchPipe = () => {},
  onSetPumpPort = () => {},
  onDeletePipe = () => {},
  onMoveGun = () => {},
  onDeleteGun = () => {},
  mode = 'inspect',
  onClose,
  panelOnly = false,
  onFit,
  onPresentationChange,
  mapHoveredPipe = null,
}: {
  dams: SavedDam[];
  ponds: SavedPond[];
  lakes?: SnowmakingLakeSource[];
  trails: SavedTrail[];
  lifts: SavedLift[];
  nodes: SavedSnowmakingNode[];
  pipes?: SavedSnowmakingPipe[];
  guns?: SavedSnowgun[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
  units: Units;
  selectedNodeId: string | null;
  selectedPipeId?: string | null;
  selectedPipeSegmentId?: string | null;
  selectedGunId?: string | null;
  onSelectNode: (id: string | null) => void;
  onSelectPipe?: (id: string | null) => void;
  onSelectGun?: (id: string | null) => void;
  onRenameNode?: (id: string, name: string) => void;
  onDeleteNode?: (id: string) => void;
  onPatchPipe?: (id: string, patch: Pick<Partial<SavedSnowmakingPipe>, 'name' | 'diameterIn'>) => void;
  onSetPumpPort?: (pipeId: string, segmentId: string, end: 'start' | 'end',
    port: SnowmakingPumpPort | null) => void;
  onDeletePipe?: (id: string) => void;
  onMoveGun?: (id: string) => void;
  onDeleteGun?: (id: string) => void;
  mode?: 'inspect' | 'analysis';
  onClose: () => void;
  panelOnly?: boolean;
  onFit?: () => void;
  onPresentationChange?: (presentation: SnowmakingMapPresentation) => void;
  mapHoveredPipe?: SnowmakingPipeHoverState | null;
}) {
  const [view, setView] = useState<View | null>(null);
  const [showGunTypes, setShowGunTypes] = useState(false);
  const [hoveredGunId, setHoveredGunId] = useState<string | null>(null);
  const [hoveredSegmentId, setHoveredSegmentId] = useState<string | null>(null);
  const [pendingHydrantDeleteId, setPendingHydrantDeleteId] = useState<string | null>(null);
  const { state: analysis, dispatch: analysisDispatch, groups: analysisGroups,
    relevantGroups: analysisRelevantGroups, routing: analysisRouting,
    gunStatuses: analysisStatuses,
    sourceResourcesByIntakeId } = useSnowmakingAnalysis({ nodes, pipes, guns, dams, ponds, lakes });
  const solvedSegments = useMemo(() => (analysis.stale ? [] : analysis.result?.systems ?? [])
    .flatMap((system) => system.segments), [analysis.result, analysis.stale]);
  const analysisSegments = useMemo(() => new Map(solvedSegments
    .map((segment) => [segment.id, segment])),
  [solvedSegments]);
  const pressureRange = useMemo(() => snowmakingPressureRange(solvedSegments),
    [solvedSegments]);
  const invalidPumpIds = useMemo(() => new Set((analysis.result?.diagnostics ?? [])
    .filter((entry) => entry.code === 'pump-direction-blocks-route')
    .flatMap((entry) => entry.entityId ? [entry.entityId] : [])), [analysis.result]);
  const relevantSegmentColors = useMemo(() => {
    const colorByComponent = new Map(analysisRelevantGroups.map((group, index) =>
      [group.componentId, SYSTEM_COLORS[index % SYSTEM_COLORS.length]] as const));
    return new Map(analysisRouting.trees.flatMap((tree) => tree.segmentIds.map((id) =>
      [id, colorByComponent.get(tree.componentId) ?? SYSTEM_COLORS[0]] as const)));
  }, [analysisRelevantGroups, analysisRouting]);

  useEffect(() => onPresentationChange?.({
    mode,
    segments: solvedSegments,
    relevantSegmentColors,
    selectedGunIds: new Set(analysis.selectedGunIds),
    gunStatuses: analysisStatuses ?? {},
    invalidPumpIds,
    pressureRange,
    showGunTypes,
    toggleGun: (id) => analysisDispatch({ type: 'toggle-gun', id }),
    setHoveredSegment: setHoveredSegmentId,
  }), [mode, solvedSegments, relevantSegmentColors, analysis.selectedGunIds,
    analysisStatuses, invalidPumpIds, pressureRange, showGunTypes, onPresentationChange,
    analysisDispatch]);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const dragRef = useRef<{ x: number; y: number; view: View; moved: boolean } | null>(null);

  const empty = dams.length === 0 && ponds.length === 0 && lakes.length === 0 &&
    nodes.length === 0 && pipes.length === 0 && guns.length === 0;
  const selectedNode = mode === 'inspect' ? nodes.find((n) => n.id === selectedNodeId) ?? null : null;
  const selectedPipe = mode === 'inspect'
    ? pipes.find((pipe) => pipe.id === selectedPipeId) ?? null : null;
  const selectedGun = mode === 'inspect' ? guns.find((gun) => gun.id === selectedGunId) ?? null : null;
  // Frame is scoped to water + node geometry — the features this dashboard
  // exists to show — not the whole trail/lift network. Trails/lifts still
  // render as context, reached by panning out.
  const frame = useMemo<MetersFrame>(() => {
    const samples: [number, number][] = [];
    for (const pond of ponds) for (const p of pond.boundary) samples.push(p);
    for (const lake of lakes) for (const p of lake.boundary) samples.push(p);
    for (const dam of dams) for (const ring of dam.pondRings) for (const p of ring) samples.push(p);
    for (const node of nodes) samples.push(node.point);
    for (const pipe of pipes) for (const vertex of pipe.vertices) samples.push(vertex.point);
    for (const gun of guns) samples.push(gun.point);
    if (samples.length > 0) return makeFrame(samples);
    // No water/node geometry yet. Fall back to trail/lift geometry purely so
    // the frame isn't degenerate (makeFrame([]) centers on [0,0], the null
    // island) — the empty-state overlay covers the UX in this case regardless.
    for (const trail of trails) {
      for (const part of trail.parts) {
        const outer = part.polygon[0];
        if (outer && outer.length > 0) return makeFrame(outer);
      }
    }
    for (const lift of lifts) {
      if (lift.points.length > 0) return makeFrame(lift.points);
    }
    return makeFrame([]);
  }, [dams, ponds, lakes, nodes, pipes, guns, trails, lifts]);

  // North is up: SVG y grows downward, the meters frame's y grows north.
  const place = useCallback((p: [number, number]): XY => {
    const m = toMeters(frame, p);
    return { x: m.x, y: -m.y };
  }, [frame]);

  // Initial fit is bounded by water + node geometry only, matching the
  // frame's scope — trails/lifts may extend beyond it, reachable by panning.
  const fitted = useMemo<View>(() => {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    const consider = (p: XY) => {
      if (p.x < minX) minX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.x > maxX) maxX = p.x;
      if (p.y > maxY) maxY = p.y;
    };
    for (const pond of ponds) for (const p of pond.boundary) consider(place(p));
    for (const lake of lakes) for (const p of lake.boundary) consider(place(p));
    for (const dam of dams) for (const ring of dam.pondRings) for (const p of ring) consider(place(p));
    for (const node of nodes) consider(place(node.point));
    for (const pipe of pipes) for (const vertex of pipe.vertices) consider(place(vertex.point));
    for (const gun of guns) consider(place(gun.point));
    if (!Number.isFinite(minX)) return { x: -200, y: -200, w: 400, h: 400 };
    const w = Math.max(MIN_SPAN_M, maxX - minX);
    const h = Math.max(MIN_SPAN_M, maxY - minY);
    const pad = Math.max(w, h) * PAD_FRAC;
    return { x: minX - pad, y: minY - pad, w: w + pad * 2, h: h + pad * 2 };
  }, [dams, ponds, lakes, nodes, pipes, guns, place]);

  // A new fit whenever the drawn water/node set changes shape.
  const fitKey = `${dams.length}:${ponds.length}:${lakes.length}:${nodes.length}:${pipes.length}:${guns.length}`;
  const lastFitKey = useRef(fitKey);
  if (lastFitKey.current !== fitKey) {
    lastFitKey.current = fitKey;
    if (view) setView(null);
  }
  const active = view ?? fitted;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const toSvg = (clientX: number, clientY: number, v: View) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0 || rect.height === 0) return { x: v.x + v.w / 2, y: v.y + v.h / 2 };
    const scale = Math.min(rect.width / v.w, rect.height / v.h);
    const offX = (rect.width - v.w * scale) / 2;
    const offY = (rect.height - v.h * scale) / 2;
    return {
      x: v.x + (clientX - rect.left - offX) / scale,
      y: v.y + (clientY - rect.top - offY) / scale,
    };
  };

  const onWheel = (e: React.WheelEvent<SVGSVGElement>) => {
    const v = active;
    const anchor = toSvg(e.clientX, e.clientY, v);
    const factor = Math.exp(e.deltaY * 0.0015);
    const w = Math.min(fitted.w * 8, Math.max(MIN_SPAN_M / 4, v.w * factor));
    const h = w * (v.h / v.w);
    setView({
      x: anchor.x - ((anchor.x - v.x) * w) / v.w,
      y: anchor.y - ((anchor.y - v.y) * h) / v.h,
      w,
      h,
    });
  };

  // Pan is driven from window listeners rather than pointer capture on
  // purpose: capturing on the <svg> retargets the following `click` to the
  // svg itself, which would make every node unclickable.
  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    if (e.button !== 0) return;
    const start = { x: e.clientX, y: e.clientY, view: active, moved: false };
    dragRef.current = start;
    const rect = svgRef.current?.getBoundingClientRect();
    const scale = rect ? Math.min(rect.width / start.view.w, rect.height / start.view.h) || 1 : 1;

    const move = (ev: PointerEvent) => {
      const dx = ev.clientX - start.x;
      const dy = ev.clientY - start.y;
      if (!start.moved && Math.hypot(dx, dy) < 4) return;
      start.moved = true;
      setView({ ...start.view, x: start.view.x - dx / scale, y: start.view.y - dy / scale });
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      setTimeout(() => {
        dragRef.current = null;
      }, 0);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  // -- ground cover backdrop -------------------------------------------------
  //
  // One <path> per cover CLASS, not per polygon: DOM element count stays
  // fixed regardless of terrain complexity. Tolerance/area-drop thresholds
  // are pinned to the INITIAL FIT's width, not the live pan/zoom `view`, so
  // this memo never re-runs while panning/zooming — the SVG viewBox rescales
  // the already-built paths for free.
  const fitWidthM = fitted.w;
  const coverPaths = useMemo(() => {
    if (!coverDisplay) return [] as { code: number; d: string }[];
    const toleranceM = fitWidthM / NOMINAL_PX;
    const minAreaM2 = MIN_RING_AREA_PX2 * toleranceM * toleranceM;
    const dByCode = new Map<number, string[]>();
    for (const feature of coverDisplay.features) {
      const code = feature.properties.code;
      if (code === 4 || code === 80) continue; // water is drawn separately
      for (const ring of feature.geometry.coordinates) {
        const projected = ring.map((p) => place([p[0], p[1]]));
        const simplified = simplifyRing(
          projected.map((p): [number, number] => [p.x, p.y]),
          toleranceM
        );
        if (simplified.length < 4) continue;
        const area = ringAreaM2(simplified.map(([x, y]) => ({ x, y })));
        if (area < minAreaM2) continue;
        const d = 'M' + simplified.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join('L') + 'Z';
        const list = dByCode.get(code);
        if (list) list.push(d);
        else dByCode.set(code, [d]);
      }
    }
    return [...dByCode.entries()].map(([code, ds]) => ({ code, d: ds.join(' ') }));
  // eslint-disable-next-line react-hooks/exhaustive-deps -- place is entirely derived from frame, which is already a dependency.
  }, [coverDisplay, frame, fitWidthM]);

  // -- elevation contours -----------------------------------------------------
  //
  // Two merged <path>s (major/minor), not one per elevation level: DOM element
  // count stays fixed regardless of how many contour intervals the terrain
  // spans. Each contour "line" from contourSegments is already just a raw
  // 2-point marching-squares segment, so there's nothing to simplify here the
  // way coverPaths simplifies polygon rings — just project and concatenate.
  const contourPaths = useMemo(() => {
    if (!terrainRecord?.bounds || !terrainRecord.contourSegments?.length) {
      return { major: '', minor: '' };
    }
    const fc = localContourGeoJSON(terrainRecord, units === 'imperial');
    const major: string[] = [];
    const minor: string[] = [];
    for (const feature of fc.features) {
      const bucket = feature.properties?.level === 1 ? major : minor;
      for (const line of feature.geometry.type === 'MultiLineString' ? feature.geometry.coordinates : []) {
        if (line.length < 2) continue;
        const pts = line.map((c) => place([c[0], c[1]]));
        bucket.push('M' + pts.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join('L'));
      }
    }
    return { major: major.join(' '), minor: minor.join(' ') };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- place is entirely derived from frame, which is already a dependency.
  }, [terrainRecord, units, frame]);

  const scaleM = niceDistance(active.w / 5);
  const scalePct = Math.min(60, (scaleM / active.w) * 100);

  if (panelOnly) return <aside className="dashboard-sidebar" aria-label="Snowmaking dashboard">
    <div className="dashboard-sidebar-actions">
      <button className="site-btn" type="button" onClick={onFit}>Fit dashboard</button>
      <label className="snowmaking-dashboard-gun-toggle">
        <input type="checkbox" checked={showGunTypes}
          onChange={(event) => setShowGunTypes(event.target.checked)} />
        Show snowgun types
      </label>
      <button className="settings-close-x" type="button" aria-label="Close Snowmaking dashboard"
        onClick={onClose}>✕</button>
    </div>
    {mode === 'analysis' && pressureRange && <div className="snowmaking-pressure-legend"
      aria-label={`Pipe pressure heat map from ${FLOW_NUMBER.format(pressureRange.minPsi)} to ${FLOW_NUMBER.format(pressureRange.maxPsi)} PSI`}>
      <div className="snowmaking-pressure-legend-title">Operating pressure</div>
      <div className="snowmaking-pressure-legend-ramp" aria-hidden="true" />
      <div className="snowmaking-pressure-legend-values"><span>{FLOW_NUMBER.format(pressureRange.minPsi)} PSI</span>
        <span>{FLOW_NUMBER.format((pressureRange.minPsi + pressureRange.maxPsi) / 2)} PSI</span>
        <span>{FLOW_NUMBER.format(pressureRange.maxPsi)} PSI</span></div>
    </div>}
    {empty && <div className="dashboard-sidebar-empty"><strong>Nothing to map yet</strong>
      <span>Build a pond or snowmaking network to populate this dashboard.</span></div>}
    {mode === 'inspect' && mapHoveredPipe && <SnowmakingPipeHoverDetails
      hover={mapHoveredPipe} units={units} />}
    {mode === 'analysis' ? <SnowmakingAnalysisPanel state={analysis} nodes={nodes} pipes={pipes}
      guns={guns} groups={analysisGroups} relevantGroups={analysisRelevantGroups}
      sourceResourcesByIntakeId={sourceResourcesByIntakeId} result={analysis.result}
      toggleGun={(id) => analysisDispatch({ type: 'toggle-gun', id })}
      setGuns={(ids) => analysisDispatch({ type: 'set-guns', ids })}
      toggleIntake={(id) => analysisDispatch({ type: 'toggle-intake', id })}
      setWetBulb={(value) => analysisDispatch({ type: 'wet-bulb', value })}
      setPumpOn={(id, on) => analysisDispatch({ type: 'pump-on', id, on })}
      setPumpHp={(id, value) => analysisDispatch({ type: 'pump-hp', id, value })}
      setPumpEfficiency={(id, value) => analysisDispatch({ type: 'pump-efficiency', id, value })}
      onSetPumpPort={onSetPumpPort} setHoveredGun={setHoveredGunId}
      hoveredSegmentId={hoveredSegmentId} reset={() => analysisDispatch({ type: 'reset' })} />
      : <SnowmakingDashboardInspector selectedNode={selectedNode} selectedPipe={selectedPipe}
        selectedPipeSegmentId={selectedPipeSegmentId}
        selectedGun={selectedGun} dams={dams} ponds={ponds} lakes={lakes} nodes={nodes}
        pipes={pipes} guns={guns} units={units} onSelectNode={onSelectNode}
        onSelectPipe={onSelectPipe} onSelectGun={onSelectGun} onRenameNode={onRenameNode}
        onDeleteNode={onDeleteNode} onPatchPipe={onPatchPipe} onSetPumpPort={onSetPumpPort}
        onDeletePipe={onDeletePipe} onMoveGun={onMoveGun} onDeleteGun={onDeleteGun}
        pendingHydrantDeleteId={pendingHydrantDeleteId}
        onSetPendingHydrantDeleteId={setPendingHydrantDeleteId} />}
  </aside>;

  return (
    <div className={`network-map snowmaking-dashboard--${mode}`} role="dialog" aria-modal="true"
      aria-label="Snowmaking network map">
      <div className="network-canvas">
        <svg
          ref={svgRef}
          className="network-svg"
          viewBox={`${active.x} ${active.y} ${active.w} ${active.h}`}
          preserveAspectRatio="xMidYMid meet"
          onWheel={onWheel}
          onPointerDown={onPointerDown}
          onClick={() => {
            if (dragRef.current?.moved) return;
            if (mode === 'analysis') return;
            onSelectNode(null);
            onSelectPipe(null);
            onSelectGun(null);
          }}
        >
          <defs>
            <pattern id="snow-grid" width={200} height={200} patternUnits="userSpaceOnUse">
              <path
                d="M 200 0 L 0 0 0 200"
                fill="none"
                className="network-grid-line"
                vectorEffect="non-scaling-stroke"
              />
            </pattern>
            {pressureRange && pipes.flatMap((pipe) => snowmakingPipeSegments(pipe).map((segment) => {
              const flow = analysisSegments.get(segment.id);
              if (!flow) return null;
              const rawPoints = segment.vertices.map((vertex) => place(vertex.point));
              const points = flow.flowGpm < 0 ? [...rawPoints].reverse() : rawPoints;
              const first = points[0], last = points[points.length - 1];
              if (!first || !last) return null;
              return <linearGradient key={segment.id} id={`snow-pressure-${segment.id.replace(/[^a-zA-Z0-9_-]/g, '-')}`}
                gradientUnits="userSpaceOnUse" x1={first.x} y1={first.y} x2={last.x} y2={last.y}>
                <stop offset="0%" stopColor={snowmakingPressureColor(flow.upstreamPressurePsi, pressureRange)} />
                <stop offset="100%" stopColor={snowmakingPressureColor(flow.downstreamPressurePsi, pressureRange)} />
              </linearGradient>;
            }))}
          </defs>

          <rect x={active.x} y={active.y} width={active.w} height={active.h} fill="url(#snow-grid)" />

          {/* 1. Ground cover backdrop, one path per class present. */}
          <g className="snowmaking-dashboard-cover">
            {coverPaths.map(({ code, d }) => (
              <path
                key={code}
                d={d}
                fill={FILL_BY_CODE[code] ?? '#888888'}
                fillRule="evenodd"
                className="snowmaking-dashboard-cover-class"
                data-cover-code={code}
              />
            ))}
          </g>

          {/* 2. Elevation contours, major/minor, drawn under water and nodes. */}
          <g className="snowmaking-dashboard-contours">
            {contourPaths.minor && (
              <path
                d={contourPaths.minor}
                className="snowmaking-dashboard-contour-minor"
                vectorEffect="non-scaling-stroke"
              />
            )}
            {contourPaths.major && (
              <path
                d={contourPaths.major}
                className="snowmaking-dashboard-contour-major"
                vectorEffect="non-scaling-stroke"
              />
            )}
          </g>

          {/* 3. Water bodies — dams and standalone ponds. */}
          <g className="snowmaking-dashboard-water">
            {ponds.map((pond) => (
              <path
                key={pond.id}
                d={ringPathD(pond.boundary.map(place))}
                className="snowmaking-dashboard-water-shape"
                fillRule="evenodd"
                data-pond-id={pond.id}
              />
            ))}
            {lakes.map((lake) => (
              <path key={`lake-${lake.id}`} d={ringPathD(lake.boundary.map(place))}
                className="snowmaking-dashboard-water-shape" fillRule="evenodd"
                data-lake-id={lake.id} />
            ))}
            {dams.map((dam) => {
              const d = dam.pondRings.map((ring) => ringPathD(ring.map(place))).filter(Boolean).join(' ');
              if (!d) return null;
              return (
                <path
                  key={dam.id}
                  d={d}
                  className="snowmaking-dashboard-water-shape"
                  fillRule="evenodd"
                  data-dam-id={dam.id}
                />
              );
            })}
          </g>

          {/* 4. Node-bounded pipe segments beneath their connection nodes. */}
          <g className="snowmaking-dashboard-pipes">
            {pipes.flatMap((pipe) => snowmakingPipeSegments(pipe).map((segment) => {
              const flow = analysisSegments.get(segment.id);
              const rawPoints = segment.vertices.map((vertex) => place(vertex.point));
              const points = flow && flow.flowGpm < 0 ? [...rawPoints].reverse() : rawPoints;
              const d = points.length >= 2 ? 'M' + points.map((point) =>
                `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join('L') : '';
              const selected = mode === 'inspect' && pipe.id === selectedPipeId;
              const relevantColor = relevantSegmentColors.get(segment.id);
              const annotation = flow ? snowmakingSegmentAnnotationGeometry(points,
                active.w / NOMINAL_PX) : null;
              const className = `snowmaking-dashboard-pipe${selected ? ' is-selected' : ''}` +
                `${mode === 'analysis' && relevantColor ? ' is-analysis-relevant' : ''}` +
                `${flow?.active ? ' is-analysis-active' : flow ? ' is-analysis-inactive' : ''}` +
                `${hoveredSegmentId === segment.id ? ' is-analysis-hovered' : ''}`;
              if (!d) return null;
              const path = <path key={segment.id} d={d} className={className}
                data-pipe-id={pipe.id} data-segment-id={segment.id}
                {...(mode === 'inspect' ? { role: 'button', tabIndex: 0 } : {})}
                aria-label={`${pipe.name}, segment ${segment.segmentIndex + 1}, ${pipe.diameterIn} inch pipe`}
                vectorEffect="non-scaling-stroke" style={flow && pressureRange
                  ? { stroke: `url(#snow-pressure-${segment.id.replace(/[^a-zA-Z0-9_-]/g, '-')})` }
                  : mode === 'analysis' && relevantColor ? { stroke: relevantColor } : undefined}
                onClick={(event) => { if (mode === 'inspect') {
                  event.stopPropagation(); onSelectPipe(pipe.id);
                } }}
                onKeyDown={(event) => { if (mode === 'inspect' &&
                  (event.key === 'Enter' || event.key === ' ')) {
                  event.preventDefault(); onSelectPipe(pipe.id);
                } }} />;
              if (!flow || !annotation) return path;
              const fontSize = active.w / 92;
              return <g key={segment.id} data-analysis-segment-id={segment.id}>{path}
                <path d={d} className="snowmaking-dashboard-pipe-hit" data-segment-hover-id={segment.id}
                  role="button" tabIndex={0} aria-label={`${pipe.name}, segment ${segment.segmentIndex + 1}. ${FLOW_NUMBER.format(Math.abs(flow.flowGpm))} GPM. ${FLOW_NUMBER.format(flow.upstreamPressurePsi)} to ${FLOW_NUMBER.format(flow.downstreamPressurePsi)} PSI. ${FLOW_NUMBER.format(flow.frictionHeadFt)} feet friction head.`}
                  vectorEffect="non-scaling-stroke"
                  onMouseEnter={() => setHoveredSegmentId(segment.id)}
                  onMouseLeave={() => setHoveredSegmentId(null)}
                  onFocus={() => setHoveredSegmentId(segment.id)}
                  onBlur={() => setHoveredSegmentId(null)} />
                <g className="snowmaking-dashboard-segment-annotation" aria-label={
                  `${FLOW_NUMBER.format(Math.abs(flow.flowGpm))} GPM, ${FLOW_NUMBER.format(flow.upstreamPressurePsi)} to ${FLOW_NUMBER.format(flow.downstreamPressurePsi)} PSI`}>
                  {flow.active && annotation.arrows.map((arrow, index) => <path
                    key={`${segment.id}:arrow:${index}`} className="snowmaking-dashboard-flow-arrow"
                    data-flow-arrow="true" d="M-5,-3 L5,0 L-5,3 Z"
                    transform={`translate(${arrow.x} ${arrow.y}) rotate(${Math.atan2(
                      arrow.tangentY, arrow.tangentX) * 180 / Math.PI}) scale(${active.w / NOMINAL_PX})`} />)}
                  <text x={annotation.flowLabel.x} y={annotation.flowLabel.y}
                    transform={`rotate(${annotation.labelAngleDeg} ${annotation.flowLabel.x} ${annotation.flowLabel.y})`}
                    className="snowmaking-dashboard-flow-label" textAnchor="middle"
                    dominantBaseline="central" style={{ fontSize }}>
                    {FLOW_NUMBER.format(Math.abs(flow.flowGpm))} GPM</text>
                  <text x={annotation.pressureLabel.x} y={annotation.pressureLabel.y}
                    transform={`rotate(${annotation.labelAngleDeg} ${annotation.pressureLabel.x} ${annotation.pressureLabel.y})`}
                    className="snowmaking-dashboard-pressure-label" textAnchor="middle"
                    dominantBaseline="central" style={{ fontSize }}>
                    {FLOW_NUMBER.format(flow.upstreamPressurePsi)} → {FLOW_NUMBER.format(flow.downstreamPressurePsi)} PSI</text>
                </g>
              </g>;
            }))}
          </g>

          <SnowgunDashboardConnections guns={guns} nodes={nodes} place={place} />

          {/* 5. Nodes, colored by kind, selection highlighted. */}
          <g className="snowmaking-dashboard-nodes">
            {nodes.map((node) => {
              const p = place(node.point);
              const selected = node.id === selectedNodeId;
              return (
                <g
                  key={node.id}
                  className={`snowmaking-dashboard-node snowmaking-dashboard-node--${node.kind}${selected ? ' is-selected' : ''}`}
                  data-node-id={node.id}
                  {...(mode === 'inspect' ? { role: 'button', tabIndex: 0 } : {})}
                  aria-label={`${SNOWMAKING_NODE_LABELS[node.kind]} ${snowmakingNodeLabel(node)}, ${node.name}`}
                  onClick={(e) => {
                    e.stopPropagation();
                    if (mode === 'analysis') return;
                    if (dragRef.current?.moved) return;
                    onSelectNode(node.id);
                  }}
                  onKeyDown={(event) => { if (event.key === 'Enter' || event.key === ' ') {
                    if (mode === 'analysis') return;
                    event.preventDefault(); event.stopPropagation(); onSelectNode(node.id); } }}
                >
                  {node.kind === 'hydrant' ? <text x={p.x} y={p.y} textAnchor="middle"
                    dominantBaseline="central" className="snowmaking-dashboard-hydrant-symbol"
                    style={{ fontSize: active.w / 65 }} aria-hidden="true">×</text> : <circle
                    cx={p.x} cy={p.y}
                    r={(selected ? 6 : 4.5) * (active.w / NOMINAL_PX)}
                    fill={NODE_COLORS[node.kind]}
                    className="snowmaking-dashboard-node-dot"
                    vectorEffect="non-scaling-stroke"
                  />}
                  <text
                    x={p.x}
                    y={p.y - active.w / 70}
                    textAnchor="middle"
                    className="snowmaking-dashboard-node-label"
                    style={{ fontSize: active.w / 70 }}
                  >
                    {snowmakingNodeLabel(node)}
                  </text>
                </g>
              );
            })}
          </g>

          {/* 6. Snowguns sit above the pipe graph and remain visible regardless of label preference. */}
          <SnowgunDashboardMarkers guns={guns} nodes={nodes}
            selectedId={mode === 'inspect' ? selectedGunId : null} hoveredId={hoveredGunId}
            analysisSelectedIds={mode === 'analysis' ? analysis.selectedGunIds : undefined}
            analysisStatuses={analysisStatuses} width={active.w} showTypes={showGunTypes} place={place}
            select={(id) => {
              if (mode === 'analysis') {
                if (guns.find((gun) => gun.id === id)?.hydrantId) {
                  analysisDispatch({ type: 'toggle-gun', id });
                }
                return;
              }
              onSelectGun(id);
            }} />
        </svg>

        {mode === 'analysis' && pressureRange && <div className="snowmaking-pressure-legend"
          aria-label={`Pipe pressure heat map from ${FLOW_NUMBER.format(pressureRange.minPsi)} to ${FLOW_NUMBER.format(pressureRange.maxPsi)} PSI`}>
          <div className="snowmaking-pressure-legend-title">Operating pressure</div>
          <div className="snowmaking-pressure-legend-ramp" aria-hidden="true" />
          <div className="snowmaking-pressure-legend-values"><span>{FLOW_NUMBER.format(pressureRange.minPsi)} PSI</span>
            <span>{FLOW_NUMBER.format((pressureRange.minPsi + pressureRange.maxPsi) / 2)} PSI</span>
            <span>{FLOW_NUMBER.format(pressureRange.maxPsi)} PSI</span></div>
        </div>}

        <div className="network-chrome-tl">
          <button className="site-btn network-close" onClick={onClose}>
            ✕ Close {mode === 'analysis' ? 'system analyzer' : 'snowmaking map'}
          </button>
          <button className="site-btn" onClick={() => setView(null)}>
            Reset view
          </button>
          <label className="snowmaking-dashboard-gun-toggle">
            <input type="checkbox" checked={showGunTypes}
              onChange={(event) => setShowGunTypes(event.target.checked)} />
            Show snowgun types
          </label>
        </div>

        <div className="network-chrome-bl">
          <div className="network-north" aria-hidden="true">
            ▲<span>N</span>
          </div>
          <div className="network-scale">
            <div className="network-scale-bar" style={{ width: `${scalePct}%` }} />
            <span>{fmtDistance(scaleM, units)}</span>
          </div>
        </div>

        {empty && (
          <div className="network-empty">
            <strong>Nothing to map yet</strong>
            <span>No dams, ponds, or nodes yet — build a dam or pond in the Snowmaking dock to see them here.</span>
          </div>
        )}
      </div>

      {mode === 'analysis' ? <SnowmakingAnalysisPanel state={analysis} nodes={nodes} pipes={pipes}
        guns={guns} groups={analysisGroups} relevantGroups={analysisRelevantGroups}
        sourceResourcesByIntakeId={sourceResourcesByIntakeId} result={analysis.result}
        toggleGun={(id) => analysisDispatch({ type: 'toggle-gun', id })}
        setGuns={(ids) => analysisDispatch({ type: 'set-guns', ids })}
        toggleIntake={(id) => analysisDispatch({ type: 'toggle-intake', id })}
        setWetBulb={(value) => analysisDispatch({ type: 'wet-bulb', value })}
        setPumpOn={(id, on) => analysisDispatch({ type: 'pump-on', id, on })}
        setPumpHp={(id, value) => analysisDispatch({ type: 'pump-hp', id, value })}
        setPumpEfficiency={(id, value) => analysisDispatch({ type: 'pump-efficiency', id, value })}
        onSetPumpPort={onSetPumpPort}
        setHoveredGun={setHoveredGunId}
        hoveredSegmentId={hoveredSegmentId}
        reset={() => analysisDispatch({ type: 'reset' })} /> : <SnowmakingDashboardInspector
        selectedNode={selectedNode}
        selectedPipe={selectedPipe}
        selectedPipeSegmentId={selectedPipeSegmentId}
        selectedGun={selectedGun}
        dams={dams}
        ponds={ponds} lakes={lakes}
        nodes={nodes}
        pipes={pipes}
        guns={guns}
        units={units}
        onSelectNode={onSelectNode}
        onSelectPipe={onSelectPipe}
        onSelectGun={onSelectGun}
        onRenameNode={onRenameNode}
        onDeleteNode={onDeleteNode}
        onPatchPipe={onPatchPipe}
        onSetPumpPort={onSetPumpPort}
        onDeletePipe={onDeletePipe}
        onMoveGun={onMoveGun}
        onDeleteGun={onDeleteGun}
        pendingHydrantDeleteId={pendingHydrantDeleteId}
        onSetPendingHydrantDeleteId={setPendingHydrantDeleteId}
      />}
    </div>
  );
}
