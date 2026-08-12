export const PUMP_MIN_FORWARD_FLOW_GPM = 1;

const PIPE_FLOW_DERIVATIVE_FLOOR_GPM = 0.01;
const CONTINUITY_TOLERANCE_GPM = 0.01;
const ENERGY_TOLERANCE_FT = 0.01;
const MAX_NEWTON_ITERATIONS = 100;
const MIN_LINE_SEARCH_STEP = 1 / 1024;
const PUMP_POWER_STAGES = [0.1, 0.25, 0.5, 0.75, 1] as const;

export interface HydraulicSolverLink {
  id: string;
  a: string;
  b: string;
  kind: 'pipe' | 'pump';
  resistance: number;
  pumpNodeId?: string;
  pumpPower?: number;
}

export interface HydraulicSolverModel<Link extends HydraulicSolverLink = HydraulicSolverLink> {
  links: Link[];
  nodeKeys: string[];
  elevationFtByKey: Map<string, number>;
  fixedHeadByKey: Map<string, number>;
  demandByKey: Map<string, number>;
}

export interface HydraulicConvergenceInfo {
  newtonIterations: number;
  maximumContinuityResidualGpm: number;
  maximumEnergyResidualFt: number;
}

export interface HydraulicNumericSolution {
  ok: boolean;
  heads: Map<string, number>;
  flows: Map<string, number>;
  convergence: HydraulicConvergenceInfo;
}

export function hydraulicLinkHeadLoss(link: HydraulicSolverLink, flow: number,
  powerScale: number): number {
  if (link.kind === 'pipe') return link.resistance * flow * Math.pow(Math.abs(flow), 0.852);
  return -(link.pumpPower ?? 0) * powerScale / Math.max(flow, PUMP_MIN_FORWARD_FLOW_GPM);
}

function linkDerivative(link: HydraulicSolverLink, flow: number, powerScale: number): number {
  if (link.kind === 'pipe') return 1.852 * link.resistance *
    Math.pow(Math.max(Math.abs(flow), PIPE_FLOW_DERIVATIVE_FLOOR_GPM), 0.852);
  return (link.pumpPower ?? 0) * powerScale /
    Math.pow(Math.max(flow, PUMP_MIN_FORWARD_FLOW_GPM), 2);
}

function initialFlows(model: HydraulicSolverModel): Map<string, number> {
  const flow = new Map(model.links.map((link) => [link.id, 0]));
  const adjacency = new Map<string, HydraulicSolverLink[]>();
  for (const link of model.links) {
    adjacency.set(link.a, [...(adjacency.get(link.a) ?? []), link]);
    adjacency.set(link.b, [...(adjacency.get(link.b) ?? []), link]);
  }
  const fixed = new Set(model.fixedHeadByKey.keys());
  for (const [target, demand] of [...model.demandByKey].sort(([a], [b]) => a.localeCompare(b))) {
    if (demand <= 0) continue;
    const distance = new Map<string, number>([[target, 0]]);
    const parent = new Map<string, { previous: string; link: HydraulicSolverLink }>();
    const queue = new Set(model.nodeKeys);
    while (queue.size) {
      let key: string | null = null, best = Number.POSITIVE_INFINITY;
      for (const candidate of queue) {
        const value = distance.get(candidate) ?? Number.POSITIVE_INFINITY;
        if (value < best || (value === best && candidate < (key ?? candidate))) { key = candidate; best = value; }
      }
      if (key == null || !Number.isFinite(best)) break;
      queue.delete(key);
      if (fixed.has(key)) {
        let current = key;
        while (current !== target) {
          const step = parent.get(current);
          if (!step) break;
          const direction = step.link.a === current && step.link.b === step.previous ? 1 : -1;
          flow.set(step.link.id, (flow.get(step.link.id) ?? 0) + direction * demand);
          current = step.previous;
        }
        break;
      }
      for (const link of adjacency.get(key) ?? []) {
        const other = link.a === key ? link.b : link.a;
        if (!queue.has(other)) continue;
        const weight = link.kind === 'pipe' ? Math.max(link.resistance, 1e-9) : 1e-6;
        const next = best + weight;
        if (next < (distance.get(other) ?? Number.POSITIVE_INFINITY)) {
          distance.set(other, next); parent.set(other, { previous: key, link });
        }
      }
    }
  }
  for (const link of model.links) if (link.kind === 'pump') {
    flow.set(link.id, Math.max(PUMP_MIN_FORWARD_FLOW_GPM, flow.get(link.id) ?? 0));
  }
  return flow;
}

function residuals(model: HydraulicSolverModel, heads: Map<string, number>,
  flows: Map<string, number>, powerScale: number, unknownIndex: Map<string, number>) {
  const continuity = new Float64Array(unknownIndex.size);
  for (const [key, demand] of model.demandByKey) {
    const index = unknownIndex.get(key); if (index != null) continuity[index] += demand;
  }
  const energy = new Float64Array(model.links.length);
  model.links.forEach((link, linkIndex) => {
    const q = flows.get(link.id) ?? 0;
    const aIndex = unknownIndex.get(link.a), bIndex = unknownIndex.get(link.b);
    if (aIndex != null) continuity[aIndex] += q;
    if (bIndex != null) continuity[bIndex] -= q;
    energy[linkIndex] = (heads.get(link.a) ?? 0) - (heads.get(link.b) ?? 0) -
      hydraulicLinkHeadLoss(link, q, powerScale);
  });
  return { continuity, energy };
}

function maxAbs(values: Float64Array): number {
  let maximum = 0;
  for (const value of values) maximum = Math.max(maximum, Math.abs(value));
  return maximum;
}

function dot(a: Float64Array, b: Float64Array): number {
  let value = 0; for (let index = 0; index < a.length; index += 1) value += a[index] * b[index];
  return value;
}

function solvePcg(size: number, multiply: (value: Float64Array) => Float64Array,
  diagonal: Float64Array, rhs: Float64Array): Float64Array | null {
  if (size === 0) return new Float64Array();
  const x = new Float64Array(size), r = rhs.slice();
  const z = new Float64Array(size), p = new Float64Array(size);
  for (let index = 0; index < size; index += 1) z[index] = r[index] /
    Math.max(diagonal[index], 1e-12);
  p.set(z);
  let rz = dot(r, z);
  const rhsNorm = Math.max(Math.sqrt(dot(rhs, rhs)), 1e-15);
  if (Math.sqrt(dot(r, r)) / rhsNorm <= 1e-9) return x;
  const maxIterations = Math.min(5000, Math.max(100, 10 * size));
  for (let iteration = 0; iteration < maxIterations; iteration += 1) {
    const ap = multiply(p), denominator = dot(p, ap);
    if (!Number.isFinite(denominator) || denominator <= 0) return null;
    const alpha = rz / denominator;
    for (let index = 0; index < size; index += 1) { x[index] += alpha * p[index]; r[index] -= alpha * ap[index]; }
    if (Math.sqrt(dot(r, r)) / rhsNorm <= 1e-9) return x;
    for (let index = 0; index < size; index += 1) z[index] = r[index] /
      Math.max(diagonal[index], 1e-12);
    const nextRz = dot(r, z);
    if (!Number.isFinite(nextRz) || Math.abs(rz) < 1e-30) return null;
    const beta = nextRz / rz;
    for (let index = 0; index < size; index += 1) p[index] = z[index] + beta * p[index];
    rz = nextRz;
  }
  return null;
}

export function solveHydraulicModel(model: HydraulicSolverModel): HydraulicNumericSolution {
  const unknownKeys = model.nodeKeys.filter((key) => !model.fixedHeadByKey.has(key));
  const unknownIndex = new Map(unknownKeys.map((key, index) => [key, index]));
  const heads = new Map<string, number>();
  const averageFixedHead = model.fixedHeadByKey.size
    ? [...model.fixedHeadByKey.values()].reduce((sum, value) => sum + value, 0) /
      model.fixedHeadByKey.size : 0;
  for (const key of model.nodeKeys) heads.set(key,
    model.fixedHeadByKey.get(key) ?? averageFixedHead);
  const flows = initialFlows(model);
  let totalIterations = 0, maximumContinuityResidualGpm = Number.POSITIVE_INFINITY;
  let maximumEnergyResidualFt = Number.POSITIVE_INFINITY;

  for (const powerScale of PUMP_POWER_STAGES) {
    let converged = false;
    for (let iteration = 0; iteration < MAX_NEWTON_ITERATIONS; iteration += 1) {
      totalIterations++;
      const current = residuals(model, heads, flows, powerScale, unknownIndex);
      maximumContinuityResidualGpm = maxAbs(current.continuity);
      maximumEnergyResidualFt = maxAbs(current.energy);
      if (maximumContinuityResidualGpm <= CONTINUITY_TOLERANCE_GPM &&
        maximumEnergyResidualFt <= ENERGY_TOLERANCE_FT) { converged = true; break; }
      const invDerivative = new Float64Array(model.links.length);
      const diagonal = new Float64Array(unknownKeys.length);
      model.links.forEach((link, index) => {
        const derivative = linkDerivative(link, flows.get(link.id) ?? 0, powerScale);
        if (!Number.isFinite(derivative) || derivative <= 0) return { ok: false, heads, flows,
          convergence: { newtonIterations: totalIterations, maximumContinuityResidualGpm,
            maximumEnergyResidualFt } };
        invDerivative[index] = 1 / derivative;
        const a = unknownIndex.get(link.a), b = unknownIndex.get(link.b);
        if (a != null) diagonal[a] += invDerivative[index];
        if (b != null) diagonal[b] += invDerivative[index];
      });
      const rhs = new Float64Array(unknownKeys.length);
      for (let index = 0; index < rhs.length; index += 1) rhs[index] = -current.continuity[index];
      model.links.forEach((link, index) => {
        const correction = invDerivative[index] * current.energy[index];
        const a = unknownIndex.get(link.a), b = unknownIndex.get(link.b);
        if (a != null) rhs[a] -= correction;
        if (b != null) rhs[b] += correction;
      });
      const multiply = (value: Float64Array) => {
        const out = new Float64Array(value.length);
        model.links.forEach((link, index) => {
          const a = unknownIndex.get(link.a), b = unknownIndex.get(link.b);
          const difference = (a == null ? 0 : value[a]) - (b == null ? 0 : value[b]);
          const contribution = invDerivative[index] * difference;
          if (a != null) out[a] += contribution;
          if (b != null) out[b] -= contribution;
        });
        return out;
      };
      const deltaHead = solvePcg(unknownKeys.length, multiply, diagonal, rhs);
      if (!deltaHead) return { ok: false, heads, flows,
        convergence: { newtonIterations: totalIterations, maximumContinuityResidualGpm,
          maximumEnergyResidualFt } };
      const deltaFlow = new Float64Array(model.links.length);
      model.links.forEach((link, index) => {
        const a = unknownIndex.get(link.a), b = unknownIndex.get(link.b);
        deltaFlow[index] = invDerivative[index] *
          ((a == null ? 0 : deltaHead[a]) - (b == null ? 0 : deltaHead[b]) + current.energy[index]);
      });
      const flowScale = Math.max(1, [...model.demandByKey.values()].reduce((sum, value) => sum + value, 0));
      const elevations = [...model.elevationFtByKey.values()];
      const headScale = Math.max(100, Math.max(...elevations, 0) - Math.min(...elevations, 0));
      const currentNorm = Math.max(maximumContinuityResidualGpm / flowScale,
        maximumEnergyResidualFt / headScale);
      let step = 1, accepted = false;
      while (step >= MIN_LINE_SEARCH_STEP) {
        const nextHeads = new Map(heads), nextFlows = new Map(flows);
        unknownKeys.forEach((key, index) => nextHeads.set(key, (heads.get(key) ?? 0) + step * deltaHead[index]));
        let valid = true;
        model.links.forEach((link, index) => {
          const value = (flows.get(link.id) ?? 0) + step * deltaFlow[index];
          if (!Number.isFinite(value) || (link.kind === 'pump' && value < PUMP_MIN_FORWARD_FLOW_GPM)) valid = false;
          nextFlows.set(link.id, value);
        });
        const next = valid ? residuals(model, nextHeads, nextFlows, powerScale, unknownIndex) : null;
        const nextNorm = next ? Math.max(maxAbs(next.continuity) / flowScale,
          maxAbs(next.energy) / headScale) : Number.POSITIVE_INFINITY;
        if (Number.isFinite(nextNorm) && nextNorm <= currentNorm * (1 - 1e-4 * step)) {
          heads.clear(); nextHeads.forEach((value, key) => heads.set(key, value));
          flows.clear(); nextFlows.forEach((value, key) => flows.set(key, value));
          accepted = true; break;
        }
        step /= 2;
      }
      if (!accepted) return { ok: false, heads, flows,
        convergence: { newtonIterations: totalIterations, maximumContinuityResidualGpm,
          maximumEnergyResidualFt } };
    }
    if (!converged) return { ok: false, heads, flows,
      convergence: { newtonIterations: totalIterations, maximumContinuityResidualGpm,
        maximumEnergyResidualFt } };
  }
  return { ok: true, heads, flows,
    convergence: { newtonIterations: totalIterations, maximumContinuityResidualGpm,
      maximumEnergyResidualFt } };
}
