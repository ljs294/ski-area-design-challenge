import type { SavedTrail } from '../types';
import type { Units } from './SettingsContext';
import type { SavedNode, SavedPath } from '../types/topology';
import { describeAnchor } from '../skiNodes';
import type { JunctionSummary } from '../topology';
import { fmtDistance } from '../lifts';
import { DIFFICULTY_COLORS, DIFFICULTY_LABELS, DIFFICULTY_SYMBOL, fmtSlope } from '../trails';

// Floating roll-up content for the trails network: runs, free-standing nodes, and the
// footpaths that connect them. Presentational only — MapView owns the state
// and all map interaction (same house rule as LiftControl.tsx); this
// component just renders whatever it's handed and reports clicks upward.
//
// Visual idiom borrowed from LayerPanel.tsx: `.layer-section-title` headers
// over a compact row list. No CSS lives here — see app.css for
// `.trails-panel` / `.trails-tool` and friends.

export type TrailsTool = 'none' | 'trail' | 'node-add' | 'node-remove' | 'path';

export function TrailsPanel({
  trails,
  junctions,
  legacyNodes,
  paths,
  units,
  selectedTrailId,
  selectedNodeId,
  selectedPathId,
  activeTool,
  warnings,
  onPaintRun,
  onAddNode,
  onRemoveNodeTool,
  onDrawPath,
  onSelectTrail,
  onSelectNode,
  onSelectPath,
  onDeleteNode,
  onDeleteLegacyNode,
  onDeletePath,
  onClose,
}: {
  trails: SavedTrail[];
  /** The graph nodes, numbered and pre-checked for removability. */
  junctions: JunctionSummary[];
  /** Free-standing pins from saves made before nodes became graph nodes. */
  legacyNodes: SavedNode[];
  paths: SavedPath[];
  units: Units;
  selectedTrailId: string | null;
  selectedNodeId: string | null;
  selectedPathId: string | null;
  activeTool: TrailsTool;
  warnings: string[];
  onPaintRun: () => void;
  onAddNode: () => void;
  onRemoveNodeTool: () => void;
  onDrawPath: () => void;
  onSelectTrail: (id: string) => void;
  onSelectNode: (id: string) => void;
  onSelectPath: (id: string) => void;
  onDeleteNode: (id: string) => void;
  onDeleteLegacyNode: (id: string) => void;
  onDeletePath: (id: string) => void;
  onClose: () => void;
}) {
  return (
    <div className="trails-panel">
      <div className="dock-head">
        <span className="dock-head-title">Ski runs</span>
        <button className="settings-close-x" aria-label="Close" onClick={onClose}>
          ✕
        </button>
      </div>

      <div className="layer-section-title">Tools</div>
      <div className="trails-tools" role="group" aria-label="Trails tools">
        <button
          type="button"
          className={`trails-tool${activeTool === 'trail' ? ' is-active' : ''}`}
          aria-pressed={activeTool === 'trail'}
          onClick={onPaintRun}
        >
          <span aria-hidden="true">◈</span> Create Trail
        </button>
        <button
          type="button"
          className={`trails-tool${activeTool === 'node-add' ? ' is-active' : ''}`}
          aria-pressed={activeTool === 'node-add'}
          onClick={onAddNode}
        >
          <span aria-hidden="true">●</span> Add node
        </button>
        <button
          type="button"
          className={`trails-tool${activeTool === 'node-remove' ? ' is-active' : ''}`}
          aria-pressed={activeTool === 'node-remove'}
          onClick={onRemoveNodeTool}
        >
          <span aria-hidden="true">○</span> Remove node
        </button>
        <button
          type="button"
          className={`trails-tool${activeTool === 'path' ? ' is-active' : ''}`}
          aria-pressed={activeTool === 'path'}
          onClick={onDrawPath}
        >
          <span aria-hidden="true">⋯</span> Draw path
        </button>
      </div>

      <div className="layer-section-title">Runs ({trails.length})</div>
      {trails.length === 0 ? (
        <div className="trails-empty">No runs yet — paint your first one.</div>
      ) : (
        <div className="trails-list">
          {trails.map((t) => (
            <div className="trails-row" key={t.id}>
              <button
                type="button"
                className={`trails-row-btn${selectedTrailId === t.id ? ' is-selected' : ''}`}
                data-row-id={t.id}
                onClick={() => onSelectTrail(t.id)}
                title={`View ${t.name}`}
              >
                <span className="trails-row-symbol" style={{ color: DIFFICULTY_COLORS[t.difficulty] }}>
                  {DIFFICULTY_SYMBOL[t.difficulty]}
                </span>
                <span className="trails-row-main">
                  <span className="trails-row-name">{t.name}</span>
                  <span className="trails-row-meta">
                    {DIFFICULTY_LABELS[t.difficulty]}
                    {` · ${fmtDistance(t.lengthM, units)}`}
                    {` · ${fmtSlope(t.avgSlopeDeg)} avg`}
                    {t.status === 'planning' ? ' · Planning' : ''}
                  </span>
                </span>
              </button>
            </div>
          ))}
        </div>
      )}

      <div className="layer-section-title">Nodes ({junctions.length})</div>
      {junctions.length === 0 && legacyNodes.length === 0 ? (
        <div className="trails-empty">No nodes yet — paint a run, or split one to add a node.</div>
      ) : (
        <div className="trails-list">
          {junctions.map((j) => (
            <div className="trails-row" key={j.id}>
              <button
                type="button"
                className={`trails-row-btn${selectedNodeId === j.id ? ' is-selected' : ''}`}
                data-row-id={j.id}
                onClick={() => onSelectNode(j.id)}
                title={`Show node ${j.number}`}
              >
                <span className="trails-row-main">
                  <span className="trails-row-name">Node {j.number}</span>
                  <span className="trails-row-meta">
                    {j.label}
                    {j.blocked ? ` · ${j.blocked}` : ' · mid-run'}
                  </span>
                </span>
              </button>
              {/* A load-bearing node keeps its button, disabled and holding the
                  reason — hiding it would read as "this row is different" without
                  ever saying why. */}
              <button
                type="button"
                className="trails-row-delete"
                aria-label={`Remove node ${j.number}`}
                disabled={j.blocked !== null}
                title={j.blocked ?? `Remove node ${j.number}`}
                onClick={() => onDeleteNode(j.id)}
              >
                ✕
              </button>
            </div>
          ))}
          {legacyNodes.map((n) => (
            <div className="trails-row" key={n.id}>
              <button
                type="button"
                className={`trails-row-btn${selectedNodeId === n.id ? ' is-selected' : ''}`}
                data-row-id={n.id}
                onClick={() => onSelectNode(n.id)}
                title={`View ${n.name}`}
              >
                <span className="trails-row-main">
                  <span className="trails-row-name">{n.name}</span>
                  <span className="trails-row-meta">
                    {`Old pin · ${n.anchor ? describeAnchor(n.anchor) : 'Unattached'}`}
                  </span>
                </span>
              </button>
              <button
                type="button"
                className="trails-row-delete"
                aria-label={`Delete ${n.name}`}
                onClick={() => onDeleteLegacyNode(n.id)}
              >
                ✕
              </button>
            </div>
          ))}
        </div>
      )}

      <div className="layer-section-title">Paths ({paths.length})</div>
      {paths.length === 0 ? (
        <div className="trails-empty">No paths yet — connect a lift to a run.</div>
      ) : (
        <div className="trails-list">
          {paths.map((p) => (
            <div className="trails-row" key={p.id}>
              <button
                type="button"
                className={`trails-row-btn${selectedPathId === p.id ? ' is-selected' : ''}`}
                data-row-id={p.id}
                onClick={() => onSelectPath(p.id)}
                title={`View ${p.name}`}
              >
                <span className="trails-row-main">
                  <span className="trails-row-name">{p.name}</span>
                  <span className="trails-row-meta">
                    {fmtDistance(p.lengthM, units)}
                    {` · ${describeAnchor(p.from)} → ${describeAnchor(p.to)}`}
                    {p.status === 'planning' ? ' · Planning' : ''}
                  </span>
                </span>
              </button>
              <button
                type="button"
                className="trails-row-delete"
                aria-label={`Delete ${p.name}`}
                onClick={() => onDeletePath(p.id)}
              >
                ✕
              </button>
            </div>
          ))}
        </div>
      )}

      {warnings.length > 0 && (
        <>
          <div className="layer-section-title">Warnings</div>
          <ul className="trails-warnings">
            {warnings.map((w, i) => (
              <li className="trails-warning" key={i}>
                {w}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
