import { haversineMeters } from './geo';
import { snowmakingPipeSegments, type SnowmakingPipeSegment } from './snowmakingNetwork';
import { HKD_IMPULSE_R5, type SnowgunPerformanceStage } from './snowmakingGuns';
import { hydraulicLinkHeadLoss, PUMP_MIN_FORWARD_FLOW_GPM, solveHydraulicModel,
  type HydraulicNumericSolution, type HydraulicSolverLink,
  type HydraulicSolverModel } from './snowmakingHydraulicSolver';
import { deriveSnowmakingRoutingForest, type SnowmakingRoutingDiagnostic,
  type SnowmakingRoutingTree } from './snowmakingRouting';
import type {
  SavedSnowgun,
  SavedSnowmakingNode,
  SavedSnowmakingPipe,
  SavedSnowmakingPipeVertex,
  SnowmakingSourceRef,
} from './types/snowmaking';

export const FEET_PER_METER = 3.280839895013123;
export const GALLONS_PER_CUBIC_METER = 264.1720523581484;
export const FEET_HEAD_PER_PSI = 2.31;
export const SNOWGUN_MINIMUM_PRESSURE_PSI = HKD_IMPULSE_R5.minimumWaterPressurePsi;
export const HAZEN_WILLIAMS_C = 120;
export const ACTIVE_SNOWMAKING_FLOW_GPM = 0.1;

export interface SnowmakingPumpAnalysisSetting {
  on: boolean;
  horsepowerHp: number | null;
  efficiency: number;
}

export interface SnowmakingSourceResource {
  sourceKey: string;
  name: string;
  capacityM3: number | null;
}

export interface SnowmakingAnalysisInput {
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  selectedGunIds: readonly string[];
  selectedIntakeNodeIds: readonly string[];
  wetBulbF: number;
  pumpSettings: Readonly<Record<string, SnowmakingPumpAnalysisSetting | undefined>>;
  sourceResourcesByIntakeId?: Readonly<Record<string, SnowmakingSourceResource | undefined>>;
}

export type SnowmakingAnalysisDiagnosticCode =
  | 'invalid-wet-bulb'
  | 'no-guns'
  | 'unknown-gun'
  | 'disconnected-gun'
  | 'missing-source'
  | 'missing-elevation'
  | 'unconfigured-pump-ports'
  | 'invalid-pump'
  | 'pump-direction-blocks-route'
  | 'unroutable-gun'
  | 'pump-no-forward-flow'
  | 'solver-nonconvergence';

export interface SnowmakingAnalysisDiagnostic {
  code: SnowmakingAnalysisDiagnosticCode;
  message: string;
  entityId?: string;
  componentId?: string;
  severity?: 'error' | 'warning';
}

export type SnowmakingGunAnalysisStatus =
  | 'ready'
  | 'too-warm'
  | 'disconnected'
  | 'not-analyzed'
  | 'insufficient-pressure';

export interface SnowmakingGunAnalysisResult {
  gunId: string;
  hydrantId: string | null;
  stage: SnowgunPerformanceStage | null;
  demandGpm: number;
  pressurePsi: number | null;
  status: SnowmakingGunAnalysisStatus;
}

export type SnowmakingPumpOperatingStatus =
  | 'off-passive'
  | 'boosting'
  | 'stalled-passive'
  | 'reverse-passive';

export interface SnowmakingPumpAnalysisResult {
  nodeId: string;
  requestedOn: boolean;
  status: SnowmakingPumpOperatingStatus;
  flowGpm: number;
  horsepowerHp: number | null;
  efficiency: number;
  suctionPressurePsi: number | null;
  dischargePressurePsi: number | null;
  headAddedFt: number;
  pressureAddedPsi: number;
}

export interface SnowmakingSegmentAnalysisResult {
  id: string;
  pipeId: string;
  segmentIndex: number;
  fromNodeKey: string;
  toNodeKey: string;
  flowGpm: number;
  active: boolean;
  lengthFt: number;
  staticHeadFt: number;
  frictionHeadFt: number;
  fromPressurePsi: number;
  toPressurePsi: number;
  upstreamPressurePsi: number;
  downstreamPressurePsi: number;
}

export interface SnowmakingIntakeAnalysisResult {
  intakeNodeId: string;
  sourceKey: string;
  withdrawalGpm: number;
  status: 'supplying' | 'idle' | 'receiving';
}

export interface SnowmakingSourceAnalysisResult extends SnowmakingSourceResource {
  intakeNodeIds: string[];
  netWithdrawalGpm: number;
  status: 'supplying' | 'idle' | 'receiving';
  capacityGallons: number | null;
  runtimeHours: number | null;
}

export interface SnowmakingSystemSummary {
  selectedGunCount: number;
  analyzedGunCount: number;
  readyGunCount: number;
  requestedDemandGpm: number;
  waterUseGalPerHour: number;
  minimumGunPressurePsi: number | null;
  overallReady: boolean;
}

export interface SnowmakingConvergenceInfo {
  newtonIterations: number;
  maximumContinuityResidualGpm: number;
  maximumEnergyResidualFt: number;
}

export interface SnowmakingSystemAnalysisResult {
  systemId: string;
  componentId: string;
  status: 'ready' | 'not-ready' | 'failed';
  diagnostics: SnowmakingAnalysisDiagnostic[];
  summary: SnowmakingSystemSummary;
  convergence: SnowmakingConvergenceInfo | null;
  intakeNodeIds: string[];
  sources: SnowmakingIntakeAnalysisResult[];
  pumps: SnowmakingPumpAnalysisResult[];
  segments: SnowmakingSegmentAnalysisResult[];
  guns: SnowmakingGunAnalysisResult[];
}

export interface SnowmakingAnalysisSummary {
  systemCount: number;
  readySystemCount: number;
  selectedGunCount: number;
  analyzedGunCount: number;
  readyGunCount: number;
  notAnalyzedGunCount: number;
  requestedDemandGpm: number;
  waterUseGalPerHour: number;
  minimumGunPressurePsi: number | null;
  limitingSourceRuntimeHours: number | null;
  overallReady: boolean;
}

export interface SnowmakingAnalysisResult {
  status: 'complete' | 'partial' | 'failed';
  diagnostics: SnowmakingAnalysisDiagnostic[];
  summary: SnowmakingAnalysisSummary;
  systems: SnowmakingSystemAnalysisResult[];
  sources: SnowmakingSourceAnalysisResult[];
}

interface PipeEdge {
  segment: SnowmakingPipeSegment;
  pipe: SavedSnowmakingPipe;
  a: string;
  b: string;
  startElevationM: number | null;
  endElevationM: number | null;
  lengthFt: number | null;
}

interface PhysicalGraph {
  edges: PipeEdge[];
  adjacency: Map<string, PipeEdge[]>;
  nodesByKey: Map<string, SavedSnowmakingNode | null>;
}

interface Component {
  id: string;
  keys: Set<string>;
  edges: PipeEdge[];
}

export interface SnowmakingAnalysisGroup {
  componentId: string;
  gunIds: string[];
  intakeNodeIds: string[];
  pumpNodeIds: string[];
  segmentIds: string[];
}

interface HydraulicLink extends HydraulicSolverLink {
  edge?: PipeEdge;
}

interface HydraulicModel extends HydraulicSolverModel<HydraulicLink> {
  pumpNodeIds: string[];
  pipeEndpointKey: Map<string, { a: string; b: string }>;
}

function addDiagnostic(list: SnowmakingAnalysisDiagnostic[],
  code: SnowmakingAnalysisDiagnosticCode, message: string, entityId?: string,
  componentId?: string, severity: 'error' | 'warning' = 'error'): void {
  if (list.some((entry) => entry.code === code && entry.entityId === entityId &&
    entry.componentId === componentId)) return;
  list.push({ code, message, ...(entityId ? { entityId } : {}),
    ...(componentId ? { componentId } : {}), ...(severity === 'warning' ? { severity } : {}) });
}

export function snowgunStageForWetBulb(wetBulbF: number): SnowgunPerformanceStage | null {
  if (!Number.isFinite(wetBulbF) || wetBulbF > HKD_IMPULSE_R5.maxWetBulbF) return null;
  for (let index = HKD_IMPULSE_R5.stages.length - 1; index >= 0; index -= 1) {
    const stage = HKD_IMPULSE_R5.stages[index];
    if (wetBulbF <= stage.wetBulbF) return stage;
  }
  return HKD_IMPULSE_R5.stages.at(-1) ?? null;
}

export function hazenWilliamsHeadLossFt(lengthFt: number, flowGpm: number,
  diameterIn: number): number {
  if (lengthFt <= 0 || flowGpm <= 0) return 0;
  return 10.67 * lengthFt * Math.pow(flowGpm, 1.852) /
    (Math.pow(HAZEN_WILLIAMS_C, 1.852) * Math.pow(diameterIn, 4.87));
}

export function pumpHeadFt(horsepowerHp: number, efficiency: number, flowGpm: number): number {
  if (horsepowerHp <= 0 || efficiency <= 0 || flowGpm <= 0) return 0;
  return 3960 * horsepowerHp * efficiency / flowGpm;
}

export function snowmakingSourceKey(source: SnowmakingSourceRef | undefined,
  fallback: string): string {
  if (!source) return `intake:${fallback}`;
  return source.kind === 'dam' ? `dam:${source.damId}`
    : source.kind === 'pond' ? `pond:${source.pondId}` : `lake:${source.lakeId}`;
}

function endpointKey(edge: SnowmakingPipeSegment, side: 'a' | 'b'): string {
  const nodeId = side === 'a' ? edge.fromNodeId : edge.toNodeId;
  return nodeId ?? `pipe:${edge.pipeId}:segment:${edge.id}:${side}`;
}

function segmentLengthFt(vertices: readonly SavedSnowmakingPipeVertex[]): number | null {
  if (vertices.some((vertex) => vertex.elevM == null || !Number.isFinite(vertex.elevM))) return null;
  let lengthM = 0;
  for (let index = 1; index < vertices.length; index += 1) {
    lengthM += Math.hypot(haversineMeters(vertices[index - 1].point, vertices[index].point),
      (vertices[index].elevM as number) - (vertices[index - 1].elevM as number));
  }
  return lengthM * FEET_PER_METER;
}

function buildPhysicalGraph(input: Pick<SnowmakingAnalysisInput, 'nodes' | 'pipes'>): PhysicalGraph {
  const nodeById = new Map(input.nodes.map((node) => [node.id, node]));
  const edges: PipeEdge[] = [];
  const adjacency = new Map<string, PipeEdge[]>();
  const nodesByKey = new Map<string, SavedSnowmakingNode | null>();
  for (const pipe of input.pipes) for (const segment of snowmakingPipeSegments(pipe)) {
    const a = endpointKey(segment, 'a'), b = endpointKey(segment, 'b');
    const first = segment.vertices[0], last = segment.vertices.at(-1)!;
    const aNode = segment.fromNodeId ? nodeById.get(segment.fromNodeId) ?? null : null;
    const bNode = segment.toNodeId ? nodeById.get(segment.toNodeId) ?? null : null;
    const edge: PipeEdge = {
      segment, pipe, a, b,
      startElevationM: aNode?.elevM ?? first.elevM,
      endElevationM: bNode?.elevM ?? last.elevM,
      lengthFt: segmentLengthFt(segment.vertices),
    };
    edges.push(edge);
    adjacency.set(a, [...(adjacency.get(a) ?? []), edge]);
    adjacency.set(b, [...(adjacency.get(b) ?? []), edge]);
    nodesByKey.set(a, aNode); nodesByKey.set(b, bNode);
  }
  return { edges, adjacency, nodesByKey };
}

export function deriveSnowmakingAnalysisGroups(input: {
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
}): SnowmakingAnalysisGroup[] {
  const graph = buildPhysicalGraph(input), components = physicalComponents(graph);
  const gunIdsByHydrant = new Map<string, string[]>();
  for (const gun of input.guns) if (gun.hydrantId) {
    gunIdsByHydrant.set(gun.hydrantId, [...(gunIdsByHydrant.get(gun.hydrantId) ?? []), gun.id]);
  }
  return components.map((component) => ({
    componentId: component.id,
    gunIds: [...component.keys].flatMap((key) => gunIdsByHydrant.get(key) ?? []).sort(),
    intakeNodeIds: [...component.keys].flatMap((key) => {
      const node = graph.nodesByKey.get(key); return node?.kind === 'intake' ? [node.id] : [];
    }).sort(),
    pumpNodeIds: [...component.keys].flatMap((key) => {
      const node = graph.nodesByKey.get(key); return node?.kind === 'pump' ? [node.id] : [];
    }).sort(),
    segmentIds: component.edges.map((edge) => edge.segment.id).sort(),
  })).filter((group) => group.gunIds.length > 0);
}

function physicalComponents(graph: PhysicalGraph): Component[] {
  const unseen = new Set(graph.adjacency.keys());
  const components: Component[] = [];
  while (unseen.size) {
    const start = [...unseen].sort()[0];
    const keys = new Set<string>(), edges = new Map<string, PipeEdge>();
    const stack = [start];
    while (stack.length) {
      const key = stack.pop()!;
      if (keys.has(key)) continue;
      keys.add(key); unseen.delete(key);
      for (const edge of graph.adjacency.get(key) ?? []) {
        edges.set(edge.segment.id, edge);
        stack.push(edge.a === key ? edge.b : edge.a);
      }
    }
    const componentEdges = [...edges.values()].sort((left, right) =>
      left.segment.id.localeCompare(right.segment.id));
    components.push({ id: [...keys].sort()[0], keys, edges: componentEdges });
  }
  return components.sort((left, right) => left.id.localeCompare(right.id));
}

function endpointHydraulicKey(edge: PipeEdge, side: 'a' | 'b',
  node: SavedSnowmakingNode | null, passivePumps: ReadonlySet<string>): string {
  if (node?.kind !== 'pump') return side === 'a' ? edge.a : edge.b;
  if (passivePumps.has(node.id)) return `pump:${node.id}:passive`;
  const port = side === 'a' ? edge.segment.startPumpPort : edge.segment.endPumpPort;
  return `pump:${node.id}:${port === 'suction' ? 'suction' : 'discharge'}`;
}

function buildHydraulicModel(input: SnowmakingAnalysisInput, graph: PhysicalGraph,
  edges: readonly PipeEdge[], selectedGuns: readonly SavedSnowgun[], stage: SnowgunPerformanceStage | null,
  selectedIntakes: readonly SavedSnowmakingNode[], passivePumps: ReadonlySet<string>,
  diagnostics: SnowmakingAnalysisDiagnostic[], componentId: string): HydraulicModel | null {
  const retainedKeys = new Set(edges.flatMap((edge) => [edge.a, edge.b]));
  const nodeById = new Map(input.nodes.map((node) => [node.id, node]));
  const incidentPorts = new Map<string, Set<string>>();
  for (const edge of edges) for (const side of ['a', 'b'] as const) {
    const physicalKey = side === 'a' ? edge.a : edge.b;
    const node = graph.nodesByKey.get(physicalKey) ?? null;
    if (node?.kind !== 'pump') continue;
    const port = side === 'a' ? edge.segment.startPumpPort : edge.segment.endPumpPort;
    if (!port) addDiagnostic(diagnostics, 'unconfigured-pump-ports',
      `${node.name} has a pipe with no water direction. Choose where water enters the pump and where the pump pushes it.`,
      node.id, componentId);
    else incidentPorts.set(node.id, new Set([...(incidentPorts.get(node.id) ?? []), port]));
  }
  for (const [pumpId, ports] of incidentPorts) if (!ports.has('suction') || !ports.has('discharge')) {
    const pump = nodeById.get(pumpId)!;
    addDiagnostic(diagnostics, 'unconfigured-pump-ports',
      `${pump.name} needs at least one inlet where water enters and one outlet where the pump pushes water.`, pumpId, componentId);
  }
  if (diagnostics.some((entry) => entry.componentId === componentId && entry.severity !== 'warning')) return null;

  const elevationFtByKey = new Map<string, number>();
  const pipeEndpointKey = new Map<string, { a: string; b: string }>();
  const links: HydraulicLink[] = [];
  const pumpNodeIds = [...incidentPorts.keys()].sort();
  const rememberElevation = (key: string, elevationM: number | null, entityId: string) => {
    if (elevationM == null || !Number.isFinite(elevationM)) {
      addDiagnostic(diagnostics, 'missing-elevation',
        'The selected network has unresolved elevation data.', entityId, componentId);
    } else elevationFtByKey.set(key, elevationM * FEET_PER_METER);
  };
  for (const edge of edges) {
    if (edge.lengthFt == null) addDiagnostic(diagnostics, 'missing-elevation',
      `${edge.pipe.name} has unresolved elevation data.`, edge.pipe.id, componentId);
    const aNode = graph.nodesByKey.get(edge.a) ?? null;
    const bNode = graph.nodesByKey.get(edge.b) ?? null;
    const a = endpointHydraulicKey(edge, 'a', aNode, passivePumps);
    const b = endpointHydraulicKey(edge, 'b', bNode, passivePumps);
    rememberElevation(a, edge.startElevationM, edge.pipe.id);
    rememberElevation(b, edge.endElevationM, edge.pipe.id);
    pipeEndpointKey.set(edge.segment.id, { a, b });
    const lengthFt = edge.lengthFt ?? 0;
    links.push({ id: edge.segment.id, a, b, kind: 'pipe', edge,
      resistance: lengthFt <= 0 ? 1e-12 : hazenWilliamsHeadLossFt(lengthFt, 1,
        edge.pipe.diameterIn) });
  }

  for (const pumpId of pumpNodeIds) {
    const pump = nodeById.get(pumpId)!;
    const setting = input.pumpSettings[pumpId];
    if (passivePumps.has(pumpId) || !setting?.on) continue;
    if (setting.horsepowerHp == null || !Number.isFinite(setting.horsepowerHp) ||
      setting.horsepowerHp <= 0 || !Number.isFinite(setting.efficiency) ||
      setting.efficiency <= 0 || setting.efficiency > 1) {
      addDiagnostic(diagnostics, 'invalid-pump',
        `${pump.name} needs positive horsepower and efficiency between 1% and 100%.`,
        pumpId, componentId);
      continue;
    }
    const suction = `pump:${pumpId}:suction`, discharge = `pump:${pumpId}:discharge`;
    rememberElevation(suction, pump.elevM, pumpId); rememberElevation(discharge, pump.elevM, pumpId);
    links.push({ id: `pump:${pumpId}`, a: suction, b: discharge, kind: 'pump',
      resistance: 0, pumpNodeId: pumpId,
      pumpPower: 3960 * setting.horsepowerHp * setting.efficiency });
  }
  if (diagnostics.some((entry) => entry.componentId === componentId && entry.severity !== 'warning')) return null;

  const demandByKey = new Map<string, number>();
  for (const gun of selectedGuns) if (gun.hydrantId && retainedKeys.has(gun.hydrantId) && stage) {
    demandByKey.set(gun.hydrantId, (demandByKey.get(gun.hydrantId) ?? 0) + stage.waterFlowGpm);
  }
  const fixedHeadByKey = new Map<string, number>();
  for (const intake of selectedIntakes) {
    const head = elevationFtByKey.get(intake.id) ?? (intake.elevM == null ? null : intake.elevM * FEET_PER_METER);
    if (head == null || !Number.isFinite(head)) addDiagnostic(diagnostics, 'missing-elevation',
      `${intake.name} has unresolved water-surface elevation.`, intake.id, componentId);
    else fixedHeadByKey.set(intake.id, head);
  }
  if (diagnostics.some((entry) => entry.componentId === componentId && entry.severity !== 'warning')) return null;
  const nodeKeys = [...new Set(links.flatMap((link) => [link.a, link.b]))].sort();
  return { links: links.sort((left, right) => left.id.localeCompare(right.id)), nodeKeys,
    elevationFtByKey, fixedHeadByKey, demandByKey, pumpNodeIds, pipeEndpointKey };
}

function passivePumpFlow(pumpId: string, edges: readonly PipeEdge[], model: HydraulicModel,
  flows: ReadonlyMap<string, number>): number {
  let outgoingFromSuction = 0;
  for (const edge of edges) {
    const endpoints = model.pipeEndpointKey.get(edge.segment.id);
    if (!endpoints) continue;
    const startNodeId = edge.segment.fromNodeId, endNodeId = edge.segment.toNodeId;
    if (startNodeId === pumpId && edge.segment.startPumpPort === 'suction') {
      outgoingFromSuction += flows.get(edge.segment.id) ?? 0;
    }
    if (endNodeId === pumpId && edge.segment.endPumpPort === 'suction') {
      outgoingFromSuction -= flows.get(edge.segment.id) ?? 0;
    }
  }
  return -outgoingFromSuction;
}

function pressurePsi(heads: ReadonlyMap<string, number>, elevations: ReadonlyMap<string, number>,
  key: string): number {
  return ((heads.get(key) ?? 0) - (elevations.get(key) ?? 0)) / FEET_HEAD_PER_PSI;
}

function failedSystem(systemId: string, componentId: string,
  selectedGuns: readonly SavedSnowgun[],
  stage: SnowgunPerformanceStage | null, diagnostics: SnowmakingAnalysisDiagnostic[],
  selectedIntakeIds: readonly string[]): SnowmakingSystemAnalysisResult {
  const guns = selectedGuns.map((gun): SnowmakingGunAnalysisResult => ({ gunId: gun.id,
    hydrantId: gun.hydrantId, stage, demandGpm: stage?.waterFlowGpm ?? 0,
    pressurePsi: null, status: gun.hydrantId ? 'not-analyzed' : 'disconnected' }));
  const demand = guns.reduce((sum, gun) => sum + gun.demandGpm, 0);
  return { systemId, componentId, status: 'failed', diagnostics,
    summary: { selectedGunCount: guns.length, analyzedGunCount: 0, readyGunCount: 0,
      requestedDemandGpm: demand, waterUseGalPerHour: demand * 60,
      minimumGunPressurePsi: null, overallReady: false }, convergence: null,
    intakeNodeIds: [...selectedIntakeIds], sources: [], pumps: [], segments: [], guns };
}

function analyzeRoutedTree(input: SnowmakingAnalysisInput, graph: PhysicalGraph,
  tree: SnowmakingRoutingTree, selectedGuns: SavedSnowgun[],
  stage: SnowgunPerformanceStage | null): SnowmakingSystemAnalysisResult {
  const diagnostics: SnowmakingAnalysisDiagnostic[] = [];
  const selectedIntakes = input.nodes.filter((node) => node.kind === 'intake' &&
    node.id === tree.intakeNodeId);
  const edgeById = new Map(graph.edges.map((edge) => [edge.segment.id, edge]));
  const edges = tree.segmentIds.flatMap((id) => {
    const edge = edgeById.get(id); return edge ? [edge] : [];
  });
  if (selectedIntakes.length !== 1 || edges.length !== tree.segmentIds.length) {
    addDiagnostic(diagnostics, 'unroutable-gun',
      'The required radial route changed before it could be analyzed.', undefined, tree.componentId);
    return failedSystem(tree.systemId, tree.componentId, selectedGuns, stage,
      diagnostics, selectedIntakes.map((intake) => intake.id));
  }

  const passivePumps = new Set(input.nodes.filter((node) => node.kind === 'pump' &&
    !input.pumpSettings[node.id]?.on).map((node) => node.id));
  let model: HydraulicModel | null = null, solution: HydraulicNumericSolution | null = null;
  const fallbackPumps = new Set<string>();
  while (true) {
    const modelDiagnostics: SnowmakingAnalysisDiagnostic[] = [];
    model = buildHydraulicModel(input, graph, edges, selectedGuns, stage,
      selectedIntakes, new Set([...passivePumps, ...fallbackPumps]), modelDiagnostics, tree.componentId);
    diagnostics.push(...modelDiagnostics);
    if (!model) return failedSystem(tree.systemId, tree.componentId, selectedGuns, stage, diagnostics,
      selectedIntakes.map((intake) => intake.id));
    solution = solveHydraulicModel(model);
    if (solution.ok) break;
    const blocked = model.links.filter((link) => link.kind === 'pump' &&
      !fallbackPumps.has(link.pumpNodeId!) &&
      (solution!.flows.get(link.id) ?? PUMP_MIN_FORWARD_FLOW_GPM) <= PUMP_MIN_FORWARD_FLOW_GPM + 1e-6)
      .map((link) => link.pumpNodeId!).sort()[0];
    if (!blocked) {
      addDiagnostic(diagnostics, 'solver-nonconvergence',
        'The hydraulic equations did not converge for this system.', undefined, tree.componentId);
      return failedSystem(tree.systemId, tree.componentId, selectedGuns, stage, diagnostics,
        selectedIntakes.map((intake) => intake.id));
    }
    fallbackPumps.add(blocked);
  }

  for (const pumpId of fallbackPumps) {
    const flow = passivePumpFlow(pumpId, edges, model, solution.flows);
    if (flow > PUMP_MIN_FORWARD_FLOW_GPM) {
      addDiagnostic(diagnostics, 'pump-no-forward-flow',
        'A requested pump could not reach a valid forward-flow operating point.', pumpId, tree.componentId);
      return failedSystem(tree.systemId, tree.componentId, selectedGuns, stage, diagnostics,
        selectedIntakes.map((intake) => intake.id));
    }
    addDiagnostic(diagnostics, 'pump-no-forward-flow', flow < -ACTIVE_SNOWMAKING_FLOW_GPM
      ? 'The requested pump is experiencing reverse flow and is acting as a passive passage.'
      : 'The requested pump is below its modeled operating flow and is acting as a passive passage.',
    pumpId, tree.componentId, 'warning');
  }

  const segmentResults: SnowmakingSegmentAnalysisResult[] = edges.map((edge) => {
    const endpoints = model!.pipeEndpointKey.get(edge.segment.id)!;
    const q = solution!.flows.get(edge.segment.id) ?? 0;
    const fromPressure = pressurePsi(solution!.heads, model!.elevationFtByKey, endpoints.a);
    const toPressure = pressurePsi(solution!.heads, model!.elevationFtByKey, endpoints.b);
    const friction = Math.abs(hydraulicLinkHeadLoss(
      model!.links.find((link) => link.id === edge.segment.id)!, q, 1));
    return { id: edge.segment.id, pipeId: edge.pipe.id, segmentIndex: edge.segment.segmentIndex,
      fromNodeKey: endpoints.a, toNodeKey: endpoints.b, flowGpm: q,
      active: Math.abs(q) >= ACTIVE_SNOWMAKING_FLOW_GPM, lengthFt: edge.lengthFt!,
      staticHeadFt: ((edge.endElevationM ?? 0) - (edge.startElevationM ?? 0)) * FEET_PER_METER,
      frictionHeadFt: friction, fromPressurePsi: fromPressure, toPressurePsi: toPressure,
      upstreamPressurePsi: q >= 0 ? fromPressure : toPressure,
      downstreamPressurePsi: q >= 0 ? toPressure : fromPressure };
  });

  const pumpResults: SnowmakingPumpAnalysisResult[] = model.pumpNodeIds.map((pumpId) => {
    const setting = input.pumpSettings[pumpId];
    const requested = !!setting?.on;
    const fallback = fallbackPumps.has(pumpId);
    const link = model!.links.find((candidate) => candidate.pumpNodeId === pumpId);
    const flow = fallback || !requested ? passivePumpFlow(pumpId, edges, model!, solution!.flows)
      : solution!.flows.get(link!.id) ?? 0;
    const passiveKey = `pump:${pumpId}:passive`, suctionKey = `pump:${pumpId}:suction`;
    const dischargeKey = `pump:${pumpId}:discharge`;
    const suctionPressure = pressurePsi(solution!.heads, model!.elevationFtByKey,
      fallback || !requested ? passiveKey : suctionKey);
    const dischargePressure = pressurePsi(solution!.heads, model!.elevationFtByKey,
      fallback || !requested ? passiveKey : dischargeKey);
    const headAdded = link ? pumpHeadFt(setting!.horsepowerHp!, setting!.efficiency, flow) : 0;
    const status: SnowmakingPumpOperatingStatus = !requested ? 'off-passive'
      : fallback ? flow < -ACTIVE_SNOWMAKING_FLOW_GPM ? 'reverse-passive' : 'stalled-passive'
        : 'boosting';
    return { nodeId: pumpId, requestedOn: requested, status, flowGpm: flow,
      horsepowerHp: setting?.horsepowerHp ?? null, efficiency: setting?.efficiency ?? 0.85,
      suctionPressurePsi: suctionPressure, dischargePressurePsi: dischargePressure,
      headAddedFt: status === 'boosting' ? headAdded : 0,
      pressureAddedPsi: status === 'boosting' ? headAdded / FEET_HEAD_PER_PSI : 0 };
  });

  const gunResults = selectedGuns.map((gun): SnowmakingGunAnalysisResult => {
    const key = gun.hydrantId!;
    const pressure = pressurePsi(solution!.heads, model!.elevationFtByKey, key);
    const status: SnowmakingGunAnalysisStatus = !stage ? 'too-warm'
      : pressure + 1e-9 < SNOWGUN_MINIMUM_PRESSURE_PSI ? 'insufficient-pressure' : 'ready';
    return { gunId: gun.id, hydrantId: gun.hydrantId, stage,
      demandGpm: stage?.waterFlowGpm ?? 0, pressurePsi: pressure, status };
  });

  const sourceResults = selectedIntakes.map((intake): SnowmakingIntakeAnalysisResult => {
    let withdrawal = 0;
    model!.links.forEach((link) => {
      const q = solution!.flows.get(link.id) ?? 0;
      if (link.a === intake.id) withdrawal += q;
      if (link.b === intake.id) withdrawal -= q;
    });
    return { intakeNodeId: intake.id,
      sourceKey: input.sourceResourcesByIntakeId?.[intake.id]?.sourceKey ??
        snowmakingSourceKey(intake.source, intake.id), withdrawalGpm: withdrawal,
      status: withdrawal > ACTIVE_SNOWMAKING_FLOW_GPM ? 'supplying'
        : withdrawal < -ACTIVE_SNOWMAKING_FLOW_GPM ? 'receiving' : 'idle' };
  });
  const demand = gunResults.reduce((sum, gun) => sum + gun.demandGpm, 0);
  const pressures = gunResults.flatMap((gun) => gun.pressurePsi == null ? [] : [gun.pressurePsi]);
  const readyGunCount = gunResults.filter((gun) => gun.status === 'ready').length;
  const overallReady = gunResults.length > 0 && readyGunCount === gunResults.length;
  return { systemId: tree.systemId, componentId: tree.componentId,
    status: overallReady ? 'ready' : 'not-ready', diagnostics,
    summary: { selectedGunCount: gunResults.length, analyzedGunCount: gunResults.length,
      readyGunCount, requestedDemandGpm: demand, waterUseGalPerHour: demand * 60,
      minimumGunPressurePsi: pressures.length ? Math.min(...pressures) : null, overallReady },
    convergence: solution.convergence, intakeNodeIds: selectedIntakes.map((intake) => intake.id),
    sources: sourceResults, pumps: pumpResults, segments: segmentResults, guns: gunResults };
}

function aggregateSources(input: SnowmakingAnalysisInput,
  systems: readonly SnowmakingSystemAnalysisResult[]): SnowmakingSourceAnalysisResult[] {
  const groups = new Map<string, { resource: SnowmakingSourceResource; intakeNodeIds: Set<string>;
    netWithdrawalGpm: number }>();
  for (const system of systems) for (const source of system.sources) {
    const resource = input.sourceResourcesByIntakeId?.[source.intakeNodeId] ?? {
      sourceKey: source.sourceKey,
      name: input.nodes.find((node) => node.id === source.intakeNodeId)?.name ?? source.sourceKey,
      capacityM3: null,
    };
    const group = groups.get(source.sourceKey) ?? { resource, intakeNodeIds: new Set(), netWithdrawalGpm: 0 };
    group.intakeNodeIds.add(source.intakeNodeId); group.netWithdrawalGpm += source.withdrawalGpm;
    groups.set(source.sourceKey, group);
  }
  return [...groups.values()].map(({ resource, intakeNodeIds, netWithdrawalGpm }):
  SnowmakingSourceAnalysisResult => {
    const capacityGallons = resource.capacityM3 != null && Number.isFinite(resource.capacityM3)
      ? Math.max(0, resource.capacityM3) * GALLONS_PER_CUBIC_METER : null;
    const supplying = netWithdrawalGpm > ACTIVE_SNOWMAKING_FLOW_GPM;
    return { ...resource, intakeNodeIds: [...intakeNodeIds].sort(), netWithdrawalGpm,
      status: supplying ? 'supplying' : netWithdrawalGpm < -ACTIVE_SNOWMAKING_FLOW_GPM
        ? 'receiving' : 'idle', capacityGallons,
      runtimeHours: supplying && capacityGallons != null
        ? capacityGallons / (netWithdrawalGpm * 60) : null };
  }).sort((left, right) => left.sourceKey.localeCompare(right.sourceKey));
}

export function analyzeSnowmakingSystems(input: SnowmakingAnalysisInput): SnowmakingAnalysisResult {
  const diagnostics: SnowmakingAnalysisDiagnostic[] = [];
  const stage = snowgunStageForWetBulb(input.wetBulbF);
  if (!Number.isFinite(input.wetBulbF)) addDiagnostic(diagnostics, 'invalid-wet-bulb',
    'Enter a valid wet-bulb temperature.');
  const gunById = new Map(input.guns.map((gun) => [gun.id, gun]));
  const selectedGuns: SavedSnowgun[] = [];
  for (const id of [...new Set(input.selectedGunIds)]) {
    const gun = gunById.get(id);
    if (!gun) addDiagnostic(diagnostics, 'unknown-gun', 'A selected snowgun no longer exists.', id);
    else selectedGuns.push(gun);
  }
  if (selectedGuns.length === 0) addDiagnostic(diagnostics, 'no-guns',
    'Select at least one connected snowgun to analyze.');
  if (diagnostics.some((entry) => entry.code === 'invalid-wet-bulb' || entry.code === 'no-guns')) {
    return { status: 'failed', diagnostics,
      summary: { systemCount: 0, readySystemCount: 0, selectedGunCount: selectedGuns.length,
        analyzedGunCount: 0, readyGunCount: 0, notAnalyzedGunCount: selectedGuns.length,
        requestedDemandGpm: stage ? selectedGuns.length * stage.waterFlowGpm : 0,
        waterUseGalPerHour: stage ? selectedGuns.length * stage.waterFlowGpm * 60 : 0,
        minimumGunPressurePsi: null, limitingSourceRuntimeHours: null, overallReady: false },
      systems: [], sources: [] };
  }

  const graph = buildPhysicalGraph(input);
  const routing = deriveSnowmakingRoutingForest({ ...input,
    selectedGunIds: selectedGuns.map((gun) => gun.id) });
  const systems: SnowmakingSystemAnalysisResult[] = [];
  for (const tree of routing.trees) {
    const guns = tree.gunIds.flatMap((id) => {
      const gun = gunById.get(id); return gun ? [gun] : [];
    });
    systems.push(analyzeRoutedTree(input, graph, tree, guns, stage));
  }
  for (const failure of routing.failures) {
    const guns = failure.gunIds.flatMap((id) => {
      const gun = gunById.get(id); return gun ? [gun] : [];
    });
    const local = failure.diagnostics.map((entry: SnowmakingRoutingDiagnostic):
    SnowmakingAnalysisDiagnostic => ({ ...entry }));
    systems.push(failedSystem(failure.systemId, failure.componentId, guns, stage, local, []));
  }
  systems.sort((left, right) => left.systemId.localeCompare(right.systemId));
  for (const system of systems) diagnostics.push(...system.diagnostics);
  const sources = aggregateSources(input, systems);
  const analyzedGunCount = systems.reduce((sum, system) => sum + system.summary.analyzedGunCount, 0);
  const readyGunCount = systems.reduce((sum, system) => sum + system.summary.readyGunCount, 0);
  const requestedDemandGpm = systems.reduce((sum, system) => sum + system.summary.requestedDemandGpm, 0);
  const complete = systems.length > 0 && systems.every((system) => system.status !== 'failed');
  const anyAnalyzed = analyzedGunCount > 0;
  const supplying = sources.filter((source) => source.status === 'supplying');
  const limitingSourceRuntimeHours = complete && supplying.every((source) => source.runtimeHours != null)
    ? supplying.length ? Math.min(...supplying.map((source) => source.runtimeHours!)) : null : null;
  const pressures = complete ? systems.flatMap((system) => system.guns.flatMap((gun) =>
    gun.pressurePsi == null ? [] : [gun.pressurePsi])) : [];
  const status: SnowmakingAnalysisResult['status'] = complete ? 'complete'
    : anyAnalyzed ? 'partial' : 'failed';
  return { status, diagnostics,
    summary: { systemCount: systems.length,
      readySystemCount: systems.filter((system) => system.status === 'ready').length,
      selectedGunCount: selectedGuns.length, analyzedGunCount, readyGunCount,
      notAnalyzedGunCount: selectedGuns.length - analyzedGunCount,
      requestedDemandGpm, waterUseGalPerHour: requestedDemandGpm * 60,
      minimumGunPressurePsi: pressures.length ? Math.min(...pressures) : null,
      limitingSourceRuntimeHours,
      overallReady: complete && readyGunCount === selectedGuns.length && selectedGuns.length > 0 },
    systems, sources };
}

/** Backward-compatible name retained for domain callers while the result is now a batch. */
export const analyzeSnowmakingSystem = analyzeSnowmakingSystems;
