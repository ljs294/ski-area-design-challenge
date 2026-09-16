import type { SnowGrid } from '../types/snow';
import type { NetworkEdge } from '../network';
import { DEFAULT_DUAL_CONFIG, type SimulationSpeedProfile, type OperationalSignal } from './model';
import { snowCellArea, type TrailFootprint } from './geometry';

export interface WearResult { trafficLossM3: number; cutoffLossM3: number; changedCells: number }
/** Mutates worker-owned staged arrays only. Call once for each authoritative exposure interval. */
export function applyTrafficWear(grid: SnowGrid, exposure: Float64Array, footprint: TrailFootprint,
  skierMeters: number, config: SimulationSpeedProfile = DEFAULT_DUAL_CONFIG): WearResult {
  const result = { trafficLossM3: 0, cutoffLossM3: 0, changedCells: 0 };
  if (!Number.isFinite(skierMeters) || skierMeters < 0) throw new RangeError('Invalid traffic exposure');
  if (!footprint.areaM2 || !skierMeters) return result;
  const cellArea = snowCellArea(grid), c = config.wear;
  const slope = Math.min(2, Math.max(0.5, footprint.slopeDeg / c.referenceSlopeDeg));
  for (let j = 0; j < footprint.indices.length; j++) {
    const i = footprint.indices[j], before = grid.depthM[i];
    if (before <= 0) continue;
    const passes = c.referenceWidthM * skierMeters * footprint.areas[j] / footprint.areaM2 / cellArea;
    exposure[i] += passes;
    const loss = Math.min(before, passes * c.lossM * slope);
    let depth = before - loss;
    result.trafficLossM3 += loss * cellArea;
    if (depth < 0.02) { result.cutoffLossM3 += depth * cellArea; depth = 0; grid.surface[i] = 0; }
    else if (grid.surface[i] === 1 || grid.surface[i] === 2 || grid.surface[i] === 3 || grid.surface[i] === 4) {
      if (exposure[i] >= c.hardPasses) grid.surface[i] = 4;
      else if (grid.surface[i] === 1 && exposure[i] >= c.packedPasses) grid.surface[i] = 2;
    }
    grid.depthM[i] = depth;
    result.changedCells++;
  }
  return result;
}
export function thinTrailSignal(grid: SnowGrid, footprint: TrailFootprint, at: string,
  previous?: OperationalSignal, config = DEFAULT_DUAL_CONFIG): OperationalSignal | null {
  let thin = 0;
  for (let j = 0; j < footprint.indices.length; j++) if (grid.depthM[footprint.indices[j]] < config.wear.warningDepthM) thin += footprint.areas[j];
  const fraction = footprint.areaM2 ? thin / footprint.areaM2 : 0;
  const active = previous && !previous.resolved ? fraction >= config.wear.clearFraction : fraction >= config.wear.warningFraction;
  if (!active) return previous ? { ...previous, resolved: true } : null;
  return { id: `thin:${footprint.trailId}`, entityId: footprint.trailId, entityKind: 'trail', title: 'Thin trail coverage',
    message: `${Math.round(fraction * 100)}% of this trail has less than ${Math.round(config.wear.warningDepthM * 100)} cm of snow.`,
    severity: 'advisory', at: previous && !previous.resolved ? previous.at : at,
    acknowledged: previous && !previous.resolved ? previous.acknowledged : false, resolved: false };
}

export function refreshTrailSignals(grid: SnowGrid | null, coverage: Iterable<TrailFootprint>, edges: readonly NetworkEdge[],
  signals: readonly OperationalSignal[], at: string, config: SimulationSpeedProfile,
  publish: (signal: OperationalSignal) => void): void {
  if (!grid) return;
  for (const footprint of coverage) {
    const previous = signals.find(signal => signal.id === `thin:${footprint.trailId}`);
    const open = edges.some(edge => edge.kind === 'trail' && edge.trailId === footprint.trailId && edge.open);
    const signal = open ? thinTrailSignal(grid, footprint, at, previous, config)
      : previous ? { ...previous, resolved: true } : null;
    if (signal) publish(signal);
  }
}
