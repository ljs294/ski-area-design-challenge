import { haversineMeters } from './geo';
import { HKD_IMPULSE_R5, type SnowgunPerformanceStage } from './snowmakingGuns';
import { snowmakingPipeSpans } from './snowmakingNetwork';
import type { SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SavedSnowmakingPipeVertex } from './types/snowmaking';

export const FEET_PER_METER = 3.280839895013123;
export const GALLONS_PER_CUBIC_METER = 264.1720523581484;
export const FEET_HEAD_PER_PSI = 2.31;
export const SNOWGUN_MINIMUM_PRESSURE_PSI = HKD_IMPULSE_R5.minimumWaterPressurePsi;
export const HAZEN_WILLIAMS_C = 120;

export interface SnowmakingPumpAnalysisSetting {
  on: boolean;
  horsepowerHp: number | null;
  efficiency: number;
}

export interface SnowmakingAnalysisInput {
  nodes: readonly SavedSnowmakingNode[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  selectedPipeIds: readonly string[];
  selectedGunIds: readonly string[];
  wetBulbF: number;
  pumpSettings: Readonly<Record<string, SnowmakingPumpAnalysisSetting | undefined>>;
  sourceCapacitiesM3?: Readonly<Record<string, number | null | undefined>>;
}

export type SnowmakingAnalysisDiagnosticCode =
  | 'invalid-wet-bulb'
  | 'no-pipes'
  | 'disconnected-network'
  | 'cyclic-network'
  | 'missing-intake'
  | 'multiple-intakes'
  | 'missing-elevation'
  | 'no-guns'
  | 'unknown-gun'
  | 'disconnected-gun'
  | 'gun-outside-network'
  | 'no-active-pump'
  | 'invalid-pump';

export interface SnowmakingAnalysisDiagnostic {
  code: SnowmakingAnalysisDiagnosticCode;
  message: string;
  entityId?: string;
}

export type SnowmakingGunAnalysisStatus =
  | 'ready'
  | 'too-warm'
  | 'no-flow-path'
  | 'insufficient-pressure';

export interface SnowmakingGunAnalysisResult {
  gunId: string;
  hydrantId: string;
  stage: SnowgunPerformanceStage | null;
  demandGpm: number;
  pressurePsi: number;
  status: SnowmakingGunAnalysisStatus;
}

export interface SnowmakingPumpAnalysisResult {
  nodeId: string;
  on: boolean;
  flowGpm: number;
  horsepowerHp: number | null;
  efficiency: number;
  headAddedFt: number;
  pressureAddedPsi: number;
}

export interface SnowmakingSpanAnalysisResult {
  id: string;
  pipeId: string;
  spanIndex: number;
  fromNodeKey: string;
  toNodeKey: string;
  flowGpm: number;
  lengthFt: number;
  staticHeadFt: number;
  frictionHeadFt: number;
  upstreamPressurePsi: number;
  downstreamPressurePsi: number;
}

export interface SnowmakingAnalysisSummary {
  selectedPipeCount: number;
  selectedGunCount: number;
  totalDemandGpm: number;
  waterUseGalPerHour: number;
  sourceCapacityGallons: number | null;
  sourceRuntimeHours: number | null;
  minimumGunPressurePsi: number | null;
  readyGunCount: number;
  overallReady: boolean;
}

export type SnowmakingAnalysisResult =
  | { ok: false; diagnostics: SnowmakingAnalysisDiagnostic[] }
  | { ok: true; diagnostics: []; intakeNodeId: string; summary: SnowmakingAnalysisSummary;
      pumps: SnowmakingPumpAnalysisResult[]; spans: SnowmakingSpanAnalysisResult[];
      guns: SnowmakingGunAnalysisResult[] };

interface HydraulicEdge {
  id: string;
  pipeId: string;
  spanIndex: number;
  a: string;
  b: string;
  vertices: readonly SavedSnowmakingPipeVertex[];
  diameterIn: number;
}

interface HydraulicGraph {
  edges: HydraulicEdge[];
  adjacency: Map<string, HydraulicEdge[]>;
  nodesByKey: Map<string, SavedSnowmakingNode | null>;
  elevationMByKey: Map<string, number>;
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

function endpointKey(pipeId: string, spanIndex: number, side: 'a' | 'b',
  vertex: SavedSnowmakingPipeVertex): string {
  return vertex.nodeId ?? `pipe:${pipeId}:span:${spanIndex}:${side}`;
}

function addDiagnostic(diagnostics: SnowmakingAnalysisDiagnostic[],
  code: SnowmakingAnalysisDiagnosticCode, message: string, entityId?: string): void {
  if (!diagnostics.some((entry) => entry.code === code && entry.entityId === entityId)) {
    diagnostics.push({ code, message, ...(entityId ? { entityId } : {}) });
  }
}

function buildGraph(selectedPipes: readonly SavedSnowmakingPipe[],
  nodes: readonly SavedSnowmakingNode[], diagnostics: SnowmakingAnalysisDiagnostic[]): HydraulicGraph {
  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const edges: HydraulicEdge[] = [];
  const adjacency = new Map<string, HydraulicEdge[]>();
  const nodesByKey = new Map<string, SavedSnowmakingNode | null>();
  const elevationMByKey = new Map<string, number>();

  const rememberEndpoint = (key: string, vertex: SavedSnowmakingPipeVertex,
    pipe: SavedSnowmakingPipe) => {
    const node = vertex.nodeId ? nodeById.get(vertex.nodeId) ?? null : null;
    nodesByKey.set(key, node);
    const elevationM = node?.elevM ?? vertex.elevM;
    if (elevationM == null || !Number.isFinite(elevationM)) {
      addDiagnostic(diagnostics, 'missing-elevation',
        `${pipe.name} has unresolved elevation data.`, pipe.id);
    } else elevationMByKey.set(key, elevationM);
  };

  for (const pipe of selectedPipes) {
    const spans = snowmakingPipeSpans(pipe);
    spans.forEach((vertices, spanIndex) => {
      const first = vertices[0], last = vertices.at(-1)!;
      const a = endpointKey(pipe.id, spanIndex, 'a', first);
      const b = endpointKey(pipe.id, spanIndex, 'b', last);
      rememberEndpoint(a, first, pipe); rememberEndpoint(b, last, pipe);
      if (vertices.some((vertex) => vertex.elevM == null || !Number.isFinite(vertex.elevM))) {
        addDiagnostic(diagnostics, 'missing-elevation',
          `${pipe.name} has unresolved elevation data.`, pipe.id);
      }
      const edge: HydraulicEdge = { id: `${pipe.id}:${spanIndex}`, pipeId: pipe.id,
        spanIndex, a, b, vertices, diameterIn: pipe.diameterIn };
      edges.push(edge);
      adjacency.set(a, [...(adjacency.get(a) ?? []), edge]);
      adjacency.set(b, [...(adjacency.get(b) ?? []), edge]);
    });
  }
  return { edges, adjacency, nodesByKey, elevationMByKey };
}

function spanLengthFt(edge: HydraulicEdge, graph: HydraulicGraph): number {
  let lengthM = 0;
  for (let index = 1; index < edge.vertices.length; index += 1) {
    const before = edge.vertices[index - 1], after = edge.vertices[index];
    const beforeM = index === 1 ? graph.elevationMByKey.get(edge.a)! : before.elevM!;
    const afterM = index === edge.vertices.length - 1
      ? graph.elevationMByKey.get(edge.b)! : after.elevM!;
    lengthM += Math.hypot(haversineMeters(before.point, after.point), afterM - beforeM);
  }
  return lengthM * FEET_PER_METER;
}

export function analyzeSnowmakingSystem(input: SnowmakingAnalysisInput): SnowmakingAnalysisResult {
  const diagnostics: SnowmakingAnalysisDiagnostic[] = [];
  if (!Number.isFinite(input.wetBulbF)) addDiagnostic(diagnostics, 'invalid-wet-bulb',
    'Enter a valid wet-bulb temperature.');
  const selectedPipeIds = new Set(input.selectedPipeIds);
  const selectedPipes = input.pipes.filter((pipe) => selectedPipeIds.has(pipe.id));
  if (selectedPipes.length === 0) addDiagnostic(diagnostics, 'no-pipes',
    'Select at least one snowmaking pipe.');
  if (input.selectedGunIds.length === 0) addDiagnostic(diagnostics, 'no-guns',
    'Select at least one snowgun.');

  const graph = buildGraph(selectedPipes, input.nodes, diagnostics);
  const graphKeys = [...graph.adjacency.keys()];
  if (graphKeys.length > 0) {
    const visited = new Set<string>(), stack = [graphKeys[0]];
    while (stack.length) {
      const key = stack.pop()!;
      if (visited.has(key)) continue;
      visited.add(key);
      for (const edge of graph.adjacency.get(key) ?? []) stack.push(edge.a === key ? edge.b : edge.a);
    }
    if (visited.size !== graphKeys.length) addDiagnostic(diagnostics, 'disconnected-network',
      'Selected pipes must form one connected network.');
    if (graph.edges.length !== graphKeys.length - 1) addDiagnostic(diagnostics, 'cyclic-network',
      'Selected pipes must form an acyclic tree; loops and parallel paths are not supported.');
  }

  const intakes = graphKeys.flatMap((key) => {
    const node = graph.nodesByKey.get(key); return node?.kind === 'intake' ? [{ key, node }] : [];
  });
  if (intakes.length === 0) addDiagnostic(diagnostics, 'missing-intake',
    'The selected network must connect to one intake.');
  if (intakes.length > 1) addDiagnostic(diagnostics, 'multiple-intakes',
    'The selected network contains multiple intakes; select a single-source tree.');

  const gunById = new Map(input.guns.map((gun) => [gun.id, gun]));
  const selectedGuns: SavedSnowgun[] = [];
  for (const id of [...new Set(input.selectedGunIds)]) {
    const gun = gunById.get(id);
    if (!gun) { addDiagnostic(diagnostics, 'unknown-gun', 'A selected snowgun no longer exists.', id); continue; }
    selectedGuns.push(gun);
    if (!gun.hydrantId) addDiagnostic(diagnostics, 'disconnected-gun',
      'A selected snowgun is not connected to a hydrant.', gun.id);
    else if (!graph.adjacency.has(gun.hydrantId)) addDiagnostic(diagnostics, 'gun-outside-network',
      'A selected snowgun is not served by the selected pipe tree.', gun.id);
  }

  let activePumpCount = 0;
  for (const key of graphKeys) {
    const node = graph.nodesByKey.get(key);
    if (node?.kind !== 'pump') continue;
    const setting = input.pumpSettings[node.id];
    if (!setting?.on) continue;
    activePumpCount += 1;
    if (setting.horsepowerHp == null || !Number.isFinite(setting.horsepowerHp) ||
      setting.horsepowerHp <= 0 || !Number.isFinite(setting.efficiency) ||
      setting.efficiency <= 0 || setting.efficiency > 1) {
      addDiagnostic(diagnostics, 'invalid-pump',
        `${node.name} needs positive horsepower and efficiency between 1% and 100%.`, node.id);
    }
  }
  if (activePumpCount === 0) addDiagnostic(diagnostics, 'no-active-pump',
    'Turn on and rate at least one pump in the selected network.');
  if (diagnostics.length > 0 || intakes.length !== 1) return { ok: false, diagnostics };

  const root = intakes[0].key;
  const parent = new Map<string, { key: string; edge: HydraulicEdge }>();
  const order: string[] = [];
  const queue = [root], seen = new Set([root]);
  while (queue.length) {
    const key = queue.shift()!; order.push(key);
    for (const edge of graph.adjacency.get(key) ?? []) {
      const other = edge.a === key ? edge.b : edge.a;
      if (seen.has(other)) continue;
      seen.add(other); parent.set(other, { key, edge }); queue.push(other);
    }
  }

  const stage = snowgunStageForWetBulb(input.wetBulbF);
  const demandAtNode = new Map<string, number>();
  for (const gun of selectedGuns) if (gun.hydrantId && stage) {
    demandAtNode.set(gun.hydrantId,
      (demandAtNode.get(gun.hydrantId) ?? 0) + stage.waterFlowGpm);
  }
  const subtreeDemand = new Map(demandAtNode);
  for (const key of [...order].reverse()) {
    const upstream = parent.get(key)?.key;
    if (upstream) subtreeDemand.set(upstream,
      (subtreeDemand.get(upstream) ?? 0) + (subtreeDemand.get(key) ?? 0));
  }

  const headAtNode = new Map<string, number>([[root, 0]]);
  const hasPumpAtNode = new Map<string, boolean>([[root, false]]);
  const pumps: SnowmakingPumpAnalysisResult[] = [];
  const spans: SnowmakingSpanAnalysisResult[] = [];
  for (const key of order) {
    const node = graph.nodesByKey.get(key);
    const setting = node?.kind === 'pump' ? input.pumpSettings[node.id] : undefined;
    const flowGpm = subtreeDemand.get(key) ?? 0;
    const pumpOn = !!setting?.on;
    const addedFt = pumpOn ? pumpHeadFt(setting!.horsepowerHp!, setting!.efficiency, flowGpm) : 0;
    const inletHeadFt = headAtNode.get(key) ?? 0;
    const dischargeHeadFt = inletHeadFt + addedFt;
    const pumpInPath = (hasPumpAtNode.get(key) ?? false) || pumpOn;
    if (node?.kind === 'pump') pumps.push({ nodeId: node.id, on: pumpOn, flowGpm,
      horsepowerHp: setting?.horsepowerHp ?? null, efficiency: setting?.efficiency ?? 0.85,
      headAddedFt: addedFt, pressureAddedPsi: addedFt / FEET_HEAD_PER_PSI });

    for (const edge of graph.adjacency.get(key) ?? []) {
      const child = edge.a === key ? edge.b : edge.a;
      if (parent.get(child)?.key !== key) continue;
      const q = subtreeDemand.get(child) ?? 0;
      const lengthFt = spanLengthFt(edge, graph);
      const frictionHeadFt = hazenWilliamsHeadLossFt(lengthFt, q, edge.diameterIn);
      const staticHeadFt = (graph.elevationMByKey.get(child)! -
        graph.elevationMByKey.get(key)!) * FEET_PER_METER;
      const downstreamHeadFt = Math.max(0, dischargeHeadFt - staticHeadFt - frictionHeadFt);
      headAtNode.set(child, downstreamHeadFt); hasPumpAtNode.set(child, pumpInPath);
      spans.push({ id: edge.id, pipeId: edge.pipeId, spanIndex: edge.spanIndex,
        fromNodeKey: key, toNodeKey: child, flowGpm: q, lengthFt, staticHeadFt,
        frictionHeadFt, upstreamPressurePsi: dischargeHeadFt / FEET_HEAD_PER_PSI,
        downstreamPressurePsi: downstreamHeadFt / FEET_HEAD_PER_PSI });
    }
  }

  const gunResults = selectedGuns.map((gun): SnowmakingGunAnalysisResult => {
    const hydrantId = gun.hydrantId!;
    const pressurePsi = (headAtNode.get(hydrantId) ?? 0) / FEET_HEAD_PER_PSI;
    const hasPump = hasPumpAtNode.get(hydrantId) ?? false;
    const status: SnowmakingGunAnalysisStatus = !stage ? 'too-warm'
      : !hasPump ? 'no-flow-path'
        : pressurePsi + 1e-9 < SNOWGUN_MINIMUM_PRESSURE_PSI
          ? 'insufficient-pressure' : 'ready';
    return { gunId: gun.id, hydrantId, stage, demandGpm: stage?.waterFlowGpm ?? 0,
      pressurePsi, status };
  });
  const totalDemandGpm = subtreeDemand.get(root) ?? 0;
  const waterUseGalPerHour = totalDemandGpm * 60;
  const capacityM3 = input.sourceCapacitiesM3?.[intakes[0].node.id] ?? null;
  const sourceCapacityGallons = capacityM3 != null && Number.isFinite(capacityM3)
    ? Math.max(0, capacityM3) * GALLONS_PER_CUBIC_METER : null;
  const sourceRuntimeHours = sourceCapacityGallons != null && waterUseGalPerHour > 0
    ? sourceCapacityGallons / waterUseGalPerHour : null;
  const operatingPressures = gunResults.filter((gun) => gun.stage).map((gun) => gun.pressurePsi);
  const readyGunCount = gunResults.filter((gun) => gun.status === 'ready').length;
  return { ok: true, diagnostics: [], intakeNodeId: intakes[0].node.id,
    summary: { selectedPipeCount: selectedPipes.length, selectedGunCount: selectedGuns.length,
      totalDemandGpm, waterUseGalPerHour, sourceCapacityGallons, sourceRuntimeHours,
      minimumGunPressurePsi: operatingPressures.length ? Math.min(...operatingPressures) : null,
      readyGunCount, overallReady: readyGunCount === gunResults.length && gunResults.length > 0 },
    pumps, spans, guns: gunResults };
}
