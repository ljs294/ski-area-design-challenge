import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import { formatLakeVolume } from '../lakeAnalysis';
import { fmtDistance } from '../lifts';
import { makeFrame, simplifyRing, toMeters, type MetersFrame, type XY } from '../network';
import { SNOWMAKING_NODE_LABELS } from '../snowmakingNodes';
import type { SavedSnowmakingNode, SnowmakingNodeKind } from '../types/snowmaking';
import type { SavedDam, SavedLift, SavedPond, SavedTrail, TerrainRecord } from '../types';
import type { SnowmakingLakeSource } from '../types/snowmaking';
import { FILL_BY_CODE } from './coverVectorize';
import { localContourGeoJSON } from './localContours';
import type { Units } from './SettingsContext';

/**
 * The snowmaking dashboard: a to-scale, geographic plan view of the pipe
 * network's water sources and placed nodes, drawn in SVG the same way
 * NetworkMap draws the trail/lift topology — but this view keeps real
 * geography (not a schematic graph), because "where is this relative to
 * that pond" is the whole point. Trail and lift geometry is not drawn — only
 * ground cover, contour lines, water bodies, and nodes — so cleared terrain
 * isn't confused for a planned run; trails/lifts still contribute to the
 * frame's fallback bounds when there's no water/node geometry yet.
 *
 * Sibling of NetworkMap, not a variant of it: separate pan/zoom state, own
 * coordinate frame (scoped to water + node geometry, not the whole mountain),
 * own draw order. Visually consistent by reusing NetworkMap's CSS classes
 * (.network-map/.network-canvas/.network-svg/.network-grid-line/
 * .network-chrome-tl/.network-chrome-bl/.network-empty) rather than
 * reinventing the dialog chrome.
 */

const PAD_FRAC = 0.06;
const MIN_SPAN_M = 120;
// Nominal reference width in "pixels" that NetworkMap itself normalizes radii
// against (see its `active.w / 900` scale factor) — reused here so the cover
// backdrop's simplify tolerance and area-drop threshold are pinned to the
// same nominal scale as the rest of the dashboard chrome.
const NOMINAL_PX = 900;
// A screen pixel worth of area at the nominal scale, in tolerance-metres^2.
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

/** Shoelace area of a projected ring (closed or open — the wraparound term
 *  contributes zero when the last point already duplicates the first). */
function ringAreaM2(points: XY[]): number {
  let sum = 0;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    sum += points[j].x * points[i].y - points[i].x * points[j].y;
  }
  return Math.abs(sum) / 2;
}

function ringPathD(points: XY[]): string {
  if (points.length < 2) return '';
  return 'M' + points.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join('L') + 'Z';
}

/**
 * Name and capacity of the dam/pond a node was auto-seeded from, or null when
 * the node has no `source` (a future hand-placed node). Mirrors
 * SnowmakingControl.tsx's `snowmakingSourceName` — an orphaned reference
 * (source no longer resolves — should be pruned by reconcileSnowmakingNodes,
 * but stay defensive rather than throwing on stale data) degrades to
 * 'Unknown' / 0 rather than crashing.
 */
function snowmakingSourceInfo(
  node: SavedSnowmakingNode,
  dams: SavedDam[],
  ponds: SavedPond[],
  lakes: SnowmakingLakeSource[] = [],
): { name: string; capacityM3: number } | null {
  const source = node.source;
  if (!source) return null;
  if (source.kind === 'dam') {
    const dam = dams.find((d) => d.id === source.damId);
    return dam ? { name: dam.name, capacityM3: dam.capacityM3 } : { name: 'Unknown', capacityM3: 0 };
  }
  if (source.kind === 'lake') {
    const lake = lakes.find((item) => item.id === source.lakeId);
    return lake ? { name: lake.name, capacityM3: lake.capacityM3 ?? 0 }
      : { name: 'Unknown', capacityM3: 0 };
  }
  const pond = ponds.find((p) => p.id === source.pondId);
  return pond ? { name: pond.name, capacityM3: pond.capacityM3 } : { name: 'Unknown', capacityM3: 0 };
}

export function SnowmakingDashboard({
  dams,
  ponds,
  lakes = [],
  trails,
  lifts,
  nodes,
  coverDisplay,
  terrainRecord,
  units,
  selectedNodeId,
  onSelectNode,
  onClose,
}: {
  dams: SavedDam[];
  ponds: SavedPond[];
  lakes?: SnowmakingLakeSource[];
  trails: SavedTrail[];
  lifts: SavedLift[];
  nodes: SavedSnowmakingNode[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
  units: Units;
  selectedNodeId: string | null;
  onSelectNode: (id: string | null) => void;
  onClose: () => void;
}) {
  const [view, setView] = useState<View | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const dragRef = useRef<{ x: number; y: number; view: View; moved: boolean } | null>(null);

  const empty = dams.length === 0 && ponds.length === 0 && lakes.length === 0 && nodes.length === 0;
  const selectedNode = nodes.find((n) => n.id === selectedNodeId) ?? null;

  // Frame is scoped to water + node geometry — the features this dashboard
  // exists to show — not the whole trail/lift network. Trails/lifts still
  // render as context, reached by panning out.
  const frame = useMemo<MetersFrame>(() => {
    const samples: [number, number][] = [];
    for (const pond of ponds) for (const p of pond.boundary) samples.push(p);
    for (const lake of lakes) for (const p of lake.boundary) samples.push(p);
    for (const dam of dams) for (const ring of dam.pondRings) for (const p of ring) samples.push(p);
    for (const node of nodes) samples.push(node.point);
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
  }, [dams, ponds, lakes, nodes, trails, lifts]);

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
    if (!Number.isFinite(minX)) return { x: -200, y: -200, w: 400, h: 400 };
    const w = Math.max(MIN_SPAN_M, maxX - minX);
    const h = Math.max(MIN_SPAN_M, maxY - minY);
    const pad = Math.max(w, h) * PAD_FRAC;
    return { x: minX - pad, y: minY - pad, w: w + pad * 2, h: h + pad * 2 };
  }, [dams, ponds, lakes, nodes, place]);

  // A new fit whenever the drawn water/node set changes shape.
  const fitKey = `${dams.length}:${ponds.length}:${lakes.length}:${nodes.length}`;
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

  return (
    <div className="network-map" role="dialog" aria-modal="true" aria-label="Snowmaking network map">
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
            onSelectNode(null);
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

          {/* 4. Nodes, colored by kind, selection highlighted. */}
          <g className="snowmaking-dashboard-nodes">
            {nodes.map((node) => {
              const p = place(node.point);
              const selected = node.id === selectedNodeId;
              return (
                <g
                  key={node.id}
                  className={`snowmaking-dashboard-node snowmaking-dashboard-node--${node.kind}${selected ? ' is-selected' : ''}`}
                  data-node-id={node.id}
                  onClick={(e) => {
                    e.stopPropagation();
                    if (dragRef.current?.moved) return;
                    onSelectNode(node.id);
                  }}
                >
                  <circle
                    cx={p.x}
                    cy={p.y}
                    r={(selected ? 7 : 5) * (active.w / NOMINAL_PX)}
                    fill={NODE_COLORS[node.kind]}
                    className="snowmaking-dashboard-node-dot"
                    vectorEffect="non-scaling-stroke"
                  />
                  <text
                    x={p.x}
                    y={p.y - active.w / 70}
                    textAnchor="middle"
                    className="snowmaking-dashboard-node-label"
                    style={{ fontSize: active.w / 70 }}
                  >
                    {node.name}
                  </text>
                </g>
              );
            })}
          </g>
        </svg>

        <div className="network-chrome-tl">
          <button className="site-btn network-close" onClick={onClose}>
            ✕ Close snowmaking map
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
            <span>No dams, ponds, or nodes yet — build a dam or pond in the Snowmaking dock to see them here.</span>
          </div>
        )}
      </div>

      <SnowmakingInspector
        selectedNode={selectedNode}
        dams={dams}
        ponds={ponds} lakes={lakes}
        nodes={nodes}
        units={units}
        onSelectNode={onSelectNode}
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

/**
 * The dashboard's right-hand inspector, mirroring NetworkMap's
 * NetworkInspector: a detail view for the selected node (name, kind, source,
 * capacity, elevation) when one is selected, or a summary-plus-directory
 * otherwise. This exists because .network-map is a fully opaque full-screen
 * overlay (see app.css) that hides the dock behind it — while this dashboard
 * is open, clicking a node in the SVG has nowhere else to show its detail.
 * Read-only: renaming stays exclusive to SnowmakingControl's dock panel, and
 * there's no "condition" concept for these nodes the way there is for
 * lifts/trails, so unlike NetworkInspector there is no toggle here.
 */
function SnowmakingInspector({
  selectedNode,
  dams,
  ponds,
  lakes = [],
  nodes,
  units,
  onSelectNode,
}: {
  selectedNode: SavedSnowmakingNode | null;
  dams: SavedDam[];
  ponds: SavedPond[];
  lakes?: SnowmakingLakeSource[];
  nodes: SavedSnowmakingNode[];
  units: Units;
  onSelectNode: (id: string | null) => void;
}) {
  if (selectedNode) {
    const sourceInfo = snowmakingSourceInfo(selectedNode, dams, ponds, lakes);
    return (
      <aside className="network-inspector" data-inspector="node">
        <div className="dock-head">
          <span className="dock-head-title">{selectedNode.name}</span>
        </div>
        <div className="network-sub">{SNOWMAKING_NODE_LABELS[selectedNode.kind]}</div>
        <div className="network-stats">
          {sourceInfo && <Stat label="Source" value={sourceInfo.name} />}
          {sourceInfo && <Stat label="Capacity" value={formatLakeVolume(sourceInfo.capacityM3, units)} />}
          <Stat
            label="Elevation"
            value={selectedNode.elevM != null ? fmtDistance(selectedNode.elevM, units) : '—'}
          />
        </div>
      </aside>
    );
  }

  return (
    <aside className="network-inspector" data-inspector="summary">
      <div className="dock-head">
        <span className="dock-head-title">Snowmaking network</span>
      </div>
      <div className="network-sub">Click a node to see its detail.</div>
      <div className="network-stats">
        <Stat label="Dams" value={`${dams.length}`} />
        <Stat label="Ponds" value={`${ponds.length + lakes.length}`} />
        <Stat label="Nodes" value={`${nodes.length}`} />
      </div>
      {nodes.length > 0 && (
        <>
          <div className="network-section-title">Nodes</div>
          <ul className="network-run-list">
            {nodes.map((node) => {
              const info = snowmakingSourceInfo(node, dams, ponds, lakes);
              return (
                <li key={node.id}>
                  <button className="network-run" onClick={() => onSelectNode(node.id)}>
                    <span className="network-run-name">{node.name}</span>
                    <span className="network-run-meta">
                      {SNOWMAKING_NODE_LABELS[node.kind]}
                      {info ? ` · ${info.name}` : ''}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </aside>
  );
}
