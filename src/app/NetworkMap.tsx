import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ConditionToggle } from './ConditionToggle';
import { CHAIR_LABELS, fmtDistance } from '../lifts';
import {
  edgesForTrail,
  toMeters,
  trailsFromLift,
  type LiftEdge,
  type NetworkEdge,
  type SkiNetwork,
  type TrailEdge,
} from '../network';
import {
  DIFFICULTY_COLORS,
  DIFFICULTY_LABELS,
  DIFFICULTY_SYMBOL,
  fmtArea,
  fmtSlope,
  fmtVertical,
} from '../trails';
import type { Units } from './SettingsContext';

/**
 * The node map: a deliberately plain, to-scale plan view of the mountain's
 * topology, drawn in SVG rather than on MapLibre so it can strip away terrain
 * entirely and still answer "what connects to what, and what does this lift
 * serve?".
 *
 * Presentational only, following the house rule from LiftControl: MapView owns
 * the network and the selection; this component renders them. The pan/zoom
 * viewBox is pure view state and stays local.
 *
 * Scale is preserved by drawing in the network's own local-meters frame with a
 * uniform fit (preserveAspectRatio), so distances and angles read true.
 */

const PAD_FRAC = 0.06;
const MIN_SPAN_M = 120;

interface View {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface PlacedEdge {
  edge: NetworkEdge;
  points: { x: number; y: number }[];
  /** Midpoint plus local tangent, for the one-way arrowhead. */
  mid: { x: number; y: number; angle: number };
  lengthSvg: number;
}

function niceDistance(target: number): number {
  if (!Number.isFinite(target) || target <= 0) return 100;
  const pow = Math.pow(10, Math.floor(Math.log10(target)));
  const norm = target / pow;
  const step = norm >= 5 ? 5 : norm >= 2 ? 2 : 1;
  return step * pow;
}

function fmtDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—';
  const total = Math.round(seconds);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

export function NetworkMap({
  network,
  units,
  selectedLiftId,
  selectedEdgeId,
  onSelectLift,
  onSelectEdge,
  onToggleTrailClosed,
  onToggleLiftClosed,
  onClose,
}: {
  network: SkiNetwork;
  units: Units;
  selectedLiftId: string | null;
  selectedEdgeId: string | null;
  onSelectLift: (liftId: string | null) => void;
  onSelectEdge: (edgeId: string | null) => void;
  onToggleTrailClosed: (trailId: string, closed: boolean) => void;
  onToggleLiftClosed: (liftId: string, closed: boolean) => void;
  onClose: () => void;
}) {
  const [view, setView] = useState<View | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const dragRef = useRef<{ x: number; y: number; view: View; moved: boolean } | null>(null);

  // North is up: SVG y grows downward, the meters frame's y grows north.
  const place = useCallback(
    (p: [number, number]) => {
      const m = toMeters(network.frame, p);
      return { x: m.x, y: -m.y };
    },
    [network.frame]
  );

  const placed = useMemo<PlacedEdge[]>(() => {
    const out: PlacedEdge[] = [];
    for (const edge of network.edges) {
      if (edge.id.endsWith(':r')) continue; // the twin draws on top of its forward edge
      const points = edge.path.map((p) => place(p as [number, number]));
      if (points.length < 2) continue;
      let total = 0;
      const cum = [0];
      for (let i = 1; i < points.length; i++) {
        total += Math.hypot(points[i].x - points[i - 1].x, points[i].y - points[i - 1].y);
        cum.push(total);
      }
      const half = total / 2;
      let k = 1;
      while (k < cum.length - 1 && cum[k] < half) k++;
      const span = cum[k] - cum[k - 1] || 1;
      const t = (half - cum[k - 1]) / span;
      const a = points[k - 1];
      const b = points[k];
      out.push({
        edge,
        points,
        mid: {
          x: a.x + (b.x - a.x) * t,
          y: a.y + (b.y - a.y) * t,
          angle: (Math.atan2(b.y - a.y, b.x - a.x) * 180) / Math.PI,
        },
        lengthSvg: total,
      });
    }
    return out;
  }, [network.edges, place]);

  const nodePos = useMemo(() => {
    const map = new Map<string, { x: number; y: number }>();
    for (const n of network.nodes) map.set(n.id, place(n.lngLat));
    return map;
  }, [network.nodes, place]);

  const fitted = useMemo<View>(() => {
    let minX = Infinity;
    let minY = Infinity;
    let maxX = -Infinity;
    let maxY = -Infinity;
    const consider = (p: { x: number; y: number }) => {
      if (p.x < minX) minX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.x > maxX) maxX = p.x;
      if (p.y > maxY) maxY = p.y;
    };
    for (const pe of placed) for (const p of pe.points) consider(p);
    for (const p of nodePos.values()) consider(p);
    if (!Number.isFinite(minX)) return { x: -200, y: -200, w: 400, h: 400 };
    const w = Math.max(MIN_SPAN_M, maxX - minX);
    const h = Math.max(MIN_SPAN_M, maxY - minY);
    const pad = Math.max(w, h) * PAD_FRAC;
    return { x: minX - pad, y: minY - pad, w: w + pad * 2, h: h + pad * 2 };
  }, [placed, nodePos]);

  // A new fit whenever the drawn network changes shape.
  const fitKey = `${network.nodes.length}:${network.edges.length}`;
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
    // preserveAspectRatio="xMidYMid meet": the smaller scale wins and the
    // leftover space is split evenly, so undo exactly that to hit-test.
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

  // Pan is driven from window listeners rather than pointer capture on purpose:
  // capturing on the <svg> retargets the following `click` to the svg itself,
  // which would make every edge and node unclickable.
  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    if (e.button !== 0) return;
    const start = { x: e.clientX, y: e.clientY, view: active, moved: false };
    dragRef.current = start;
    const rect = svgRef.current?.getBoundingClientRect();
    const scale = rect ? Math.min(rect.width / start.view.w, rect.height / start.view.h) || 1 : 1;

    const move = (ev: PointerEvent) => {
      const dx = ev.clientX - start.x;
      const dy = ev.clientY - start.y;
      // A few pixels of slop, so a click with a shaky hand still selects.
      if (!start.moved && Math.hypot(dx, dy) < 4) return;
      start.moved = true;
      setView({ ...start.view, x: start.view.x - dx / scale, y: start.view.y - dy / scale });
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      // Cleared after the click event has been dispatched.
      setTimeout(() => {
        dragRef.current = null;
      }, 0);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  // -- selection ------------------------------------------------------------

  const served = useMemo(() => {
    if (!selectedLiftId) return null;
    const result = trailsFromLift(network, selectedLiftId);
    if (!result) return null;
    const liftEdgeId = network.liftEdgeIds.get(selectedLiftId);
    const edgeIds = new Set(result.reachableEdgeIds);
    if (liftEdgeId) edgeIds.add(liftEdgeId);
    const directEdges = new Set<string>();
    for (const e of network.edges) {
      if (e.kind === 'trail' && result.direct.includes(e.trailId) && e.segmentIndex === 0) {
        directEdges.add(e.id);
      }
    }
    return { result, edgeIds, directEdges };
  }, [network, selectedLiftId]);

  const selectedEdge = selectedEdgeId ? network.edgeById.get(selectedEdgeId) : undefined;

  const scaleM = niceDistance(active.w / 5);
  const scalePct = Math.min(60, (scaleM / active.w) * 100);
  const labelCutoff = active.w * 0.09;

  const empty = network.edges.length === 0;

  return (
    <div className="network-map" role="dialog" aria-modal="true" aria-label="Mountain node map">
      <div className="network-canvas">
        <svg
          ref={svgRef}
          className="network-svg"
          viewBox={`${active.x} ${active.y} ${active.w} ${active.h}`}
          preserveAspectRatio="xMidYMid meet"
          onWheel={onWheel}
          onPointerDown={onPointerDown}
          onClick={() => {
            // A drag that panned the view is not a click on the background.
            if (dragRef.current?.moved) return;
            onSelectLift(null);
            onSelectEdge(null);
          }}
        >
          <defs>
            <pattern
              id="net-grid"
              width={200}
              height={200}
              patternUnits="userSpaceOnUse"
            >
              <path
                d="M 200 0 L 0 0 0 200"
                fill="none"
                className="network-grid-line"
                vectorEffect="non-scaling-stroke"
              />
            </pattern>
          </defs>

          <rect
            x={active.x}
            y={active.y}
            width={active.w}
            height={active.h}
            fill="url(#net-grid)"
          />

          {/* Tie lines bridge the gap between a run's true geometry and the
              junction centroid it snapped to — honest about the offset rather
              than moving the painted line. */}
          <g className="network-ties">
            {placed.map((pe) => {
              const ends: [{ x: number; y: number }, { x: number; y: number }][] = [];
              const from = nodePos.get(pe.edge.from);
              const to = nodePos.get(pe.edge.to);
              if (from) ends.push([pe.points[0], from]);
              if (to) ends.push([pe.points[pe.points.length - 1], to]);
              return ends
                .filter(([a, b]) => Math.hypot(a.x - b.x, a.y - b.y) > 2)
                .map(([a, b], i) => (
                  <line
                    key={`${pe.edge.id}-tie-${i}`}
                    x1={a.x}
                    y1={a.y}
                    x2={b.x}
                    y2={b.y}
                    className="network-tie"
                    vectorEffect="non-scaling-stroke"
                  />
                ));
            })}
          </g>

          <g className="network-edges">
            {placed.map((pe) => {
              const { edge } = pe;
              const isTrail = edge.kind === 'trail';
              const dimmed = served ? !served.edgeIds.has(edge.id) : false;
              const direct = served?.directEdges.has(edge.id) ?? false;
              const selected = edge.id === selectedEdgeId;
              const closed = edge.condition === 'closed';
              const cls = [
                isTrail ? 'network-trail' : 'network-lift',
                closed ? 'is-closed' : '',
                edge.planned ? 'is-planned' : '',
                dimmed ? 'is-dimmed' : '',
                direct ? 'is-direct' : '',
                selected ? 'is-selected' : '',
              ]
                .filter(Boolean)
                .join(' ');
              const pts = pe.points.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ');
              const color = isTrail ? DIFFICULTY_COLORS[(edge as TrailEdge).difficulty] : undefined;
              return (
                <g
                  key={edge.id}
                  className={cls}
                  data-edge-id={edge.id}
                  onClick={(e) => {
                    e.stopPropagation();
                    if (edge.kind === 'lift') {
                      onSelectLift(edge.liftId);
                      onSelectEdge(edge.id);
                    } else {
                      onSelectLift(null);
                      onSelectEdge(edge.id);
                    }
                  }}
                >
                  <polyline className="network-hit" points={pts} vectorEffect="non-scaling-stroke" />
                  <polyline
                    className="network-stroke"
                    points={pts}
                    stroke={closed ? undefined : color}
                    vectorEffect="non-scaling-stroke"
                  />
                  {/* One-way marker: every edge is a vector. */}
                  <polygon
                    className="network-arrow"
                    points="-4,-3 4,0 -4,3"
                    fill={closed ? undefined : color}
                    transform={`translate(${pe.mid.x.toFixed(1)} ${pe.mid.y.toFixed(1)}) rotate(${pe.mid.angle.toFixed(1)}) scale(${(active.w / 900).toFixed(3)})`}
                  />
                  {pe.lengthSvg > labelCutoff && (
                    <text
                      className="network-label"
                      x={pe.mid.x}
                      y={pe.mid.y - active.w / 90}
                      textAnchor="middle"
                      style={{ fontSize: active.w / 60 }}
                    >
                      {isTrail ? (edge as TrailEdge).trailName : (edge as LiftEdge).liftName}
                    </text>
                  )}
                </g>
              );
            })}
          </g>

          <g className="network-nodes">
            {network.nodes.map((node) => {
              const p = nodePos.get(node.id);
              if (!p) return null;
              const terminal = node.liftBases.length > 0 || node.liftTops.length > 0;
              return (
                <circle
                  key={node.id}
                  data-node-id={node.id}
                  className={`network-node network-node--${node.kind}${terminal ? ' is-terminal' : ''}`}
                  cx={p.x}
                  cy={p.y}
                  r={(terminal ? 5 : 3) * (active.w / 900)}
                />
              );
            })}
          </g>
        </svg>

        <div className="network-chrome-tl">
          <button className="site-btn network-close" onClick={onClose}>
            ✕ Close node map
          </button>
          <button className="site-btn" onClick={() => setView(null)}>
            Reset view
          </button>
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
            <span>Paint a run or place a lift, and the node map will wire it up.</span>
          </div>
        )}
      </div>

      <NetworkInspector
        network={network}
        units={units}
        selectedLiftId={selectedLiftId}
        selectedEdge={selectedEdge}
        servedDirect={served?.result.direct ?? []}
        servedReachable={served?.result.reachable ?? []}
        onSelectEdge={onSelectEdge}
        onToggleTrailClosed={onToggleTrailClosed}
        onToggleLiftClosed={onToggleLiftClosed}
      />
    </div>
  );
}

// ---------------------------------------------------------------------------

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="network-stat">
      <span className="network-stat-label">{label}</span>
      <span className="network-stat-value">{value}</span>
    </div>
  );
}

function NetworkInspector({
  network,
  units,
  selectedLiftId,
  selectedEdge,
  servedDirect,
  servedReachable,
  onSelectEdge,
  onToggleTrailClosed,
  onToggleLiftClosed,
}: {
  network: SkiNetwork;
  units: Units;
  selectedLiftId: string | null;
  selectedEdge: NetworkEdge | undefined;
  servedDirect: string[];
  servedReachable: string[];
  onSelectEdge: (edgeId: string | null) => void;
  onToggleTrailClosed: (trailId: string, closed: boolean) => void;
  onToggleLiftClosed: (liftId: string, closed: boolean) => void;
}) {
  const liftEdgeId = selectedLiftId ? network.liftEdgeIds.get(selectedLiftId) : undefined;
  const liftEdge = liftEdgeId ? (network.edgeById.get(liftEdgeId) as LiftEdge | undefined) : undefined;

  if (liftEdge) {
    const onward = servedReachable.filter((id) => !servedDirect.includes(id)).length;
    return (
      <aside className="network-inspector" data-inspector="lift">
        <div className="dock-head">
          <span className="dock-head-title">{liftEdge.liftName}</span>
        </div>
        <div className="network-sub">
          {CHAIR_LABELS[liftEdge.chairSize]} · {liftEdge.planned ? 'Planning' : 'Complete'}
        </div>
        <div className="network-stats">
          <Stat label="Vertical" value={fmtVertical(liftEdge.verticalM, units)} />
          <Stat label="Length" value={fmtDistance(liftEdge.lengthM, units)} />
          <Stat label="Ride time" value={fmtDuration(liftEdge.rideTimeS)} />
          <Stat label="Capacity" value={`${liftEdge.capacityPph.toLocaleString()} p/h`} />
          {/* Placeholder until a simulation drives them — see withLiftQueues. */}
          <Stat label="People waiting" value={`${liftEdge.peopleWaiting}`} />
          <Stat label="Wait time" value={fmtDuration(liftEdge.waitTimeS)} />
        </div>
        <div className="network-note">Queue figures are placeholders until the simulation runs.</div>

        <ConditionToggle
          closed={liftEdge.condition === 'closed'}
          onChange={(closed) => onToggleLiftClosed(liftEdge.liftId, closed)}
        />

        <div className="network-section-title">
          Serves directly ({servedDirect.length})
        </div>
        {servedDirect.length === 0 ? (
          <div className="network-note">No runs start at this lift's top terminal.</div>
        ) : (
          <ul className="network-run-list">
            {servedDirect.map((trailId) => {
              const segs = edgesForTrail(network, trailId);
              if (segs.length === 0) return null;
              const first = segs[0];
              const lengthM = segs.reduce((sum, s) => sum + s.lengthM, 0);
              const avg = segs.reduce((sum, s) => sum + s.avgSlopeDeg * s.lengthM, 0) / (lengthM || 1);
              return (
                <li key={trailId}>
                  <button
                    className="network-run"
                    onClick={() => onSelectEdge(first.id)}
                  >
                    <span
                      className="network-run-symbol"
                      style={{ color: DIFFICULTY_COLORS[first.difficulty] }}
                    >
                      {DIFFICULTY_SYMBOL[first.difficulty]}
                    </span>
                    <span className="network-run-name">{first.trailName}</span>
                    <span className="network-run-meta">
                      {fmtDistance(lengthM, units)} · {fmtSlope(avg)}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
        {onward > 0 && (
          <div className="network-note">{onward} more run{onward === 1 ? '' : 's'} reachable further down.</div>
        )}
      </aside>
    );
  }

  if (selectedEdge && selectedEdge.kind === 'trail') {
    const edge = selectedEdge;
    return (
      <aside className="network-inspector" data-inspector="trail">
        <div className="dock-head">
          <span className="dock-head-title">{edge.trailName}</span>
        </div>
        <div className="network-sub">
          <span style={{ color: DIFFICULTY_COLORS[edge.difficulty] }}>
            {DIFFICULTY_SYMBOL[edge.difficulty]} {DIFFICULTY_LABELS[edge.difficulty]}
          </span>{' '}
          · Segment {edge.segmentIndex + 1} of {edgesForTrail(network, edge.trailId).length}
        </div>
        <div className="network-stats">
          <Stat label="Rating" value={DIFFICULTY_LABELS[edge.difficulty]} />
          <Stat label="Length" value={fmtDistance(edge.lengthM, units)} />
          <Stat label="Vertical" value={fmtVertical(edge.verticalM, units)} />
          <Stat label="Acreage" value={fmtArea(edge.areaM2, units)} />
          <Stat label="Avg pitch" value={fmtSlope(edge.avgSlopeDeg)} />
          <Stat label="Max pitch" value={fmtSlope(edge.maxSlopeDeg)} />
        </div>
        {edge.traverse && (
          <div className="network-note">
            Traverse: this segment is flat or climbs, so it is skated rather than skied.
          </div>
        )}
        <ConditionToggle
          closed={edge.condition === 'closed'}
          onChange={(closed) => onToggleTrailClosed(edge.trailId, closed)}
        />
      </aside>
    );
  }

  const { diagnostics } = network;
  const trailCount = network.trailEdgeIds.size;
  const liftCount = network.liftEdgeIds.size;
  return (
    <aside className="network-inspector" data-inspector="summary">
      <div className="dock-head">
        <span className="dock-head-title">Mountain network</span>
      </div>
      <div className="network-sub">Click a lift to see what it serves.</div>
      <div className="network-stats">
        <Stat label="Runs" value={`${trailCount}`} />
        <Stat label="Lifts" value={`${liftCount}`} />
        <Stat label="Segments" value={`${network.edges.filter((e) => e.kind === 'trail').length}`} />
        <Stat label="Junctions" value={`${network.nodes.filter((n) => n.kind === 'junction' || n.kind === 'crossing').length}`} />
      </div>
      {diagnostics.componentCount > 1 && (
        <div className="network-warn">
          The mountain is in {diagnostics.componentCount} disconnected pieces.
        </div>
      )}
      {diagnostics.orphanTrailIds.length > 0 && (
        <div className="network-warn">
          {diagnostics.orphanTrailIds.length} run
          {diagnostics.orphanTrailIds.length === 1 ? ' is' : 's are'} not reachable from any lift.
        </div>
      )}
      {diagnostics.isolatedLiftIds.length > 0 && (
        <div className="network-warn">
          {diagnostics.isolatedLiftIds.length} lift
          {diagnostics.isolatedLiftIds.length === 1 ? '' : 's'} serve no runs.
        </div>
      )}
    </aside>
  );
}
