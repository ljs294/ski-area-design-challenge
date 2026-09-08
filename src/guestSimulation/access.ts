/**
 * Phase 6 road access domain.
 *
 * This module is deliberately independent of the map and save layers.  A
 * road-like input is compiled into a small directed graph, vehicle demand is
 * generated from keyed randomness, and the access runtime keeps an occupant
 * ledger whose terminal buckets are mutually exclusive.  The ledger is the
 * handoff boundary: a guest can be accepted by a GuestPortal exactly once.
 */

import { eventCalendarChecksum } from './eventCalendar.ts';
import { encodeBinarySidecar, decodeBinarySidecar } from './binaryCodec.ts';
import { keyedRandomInt, type RandomSeed } from './random.ts';
import type { GuestPortal, SimulatedSecond } from './contracts.ts';

export const PHASE6_ACCESS_VERSION = 1 as const;
export type Phase6AccessVersion = typeof PHASE6_ACCESS_VERSION;
export type AccessCoordinate = readonly [number, number];
export type AccessNodeKind = 'edge-of-map' | 'road' | 'parking' | 'drop-off' | 'portal';

export interface AccessRoadLike {
  readonly id: string;
  readonly points: readonly AccessCoordinate[];
  /** Seconds per segment. A scalar is applied to every segment. */
  readonly travelSeconds?: number;
  readonly segmentTravelSeconds?: readonly number[];
  readonly capacityVehicles?: number;
  readonly closed?: boolean;
}

export interface AccessExplicitNode {
  readonly id: string;
  readonly kind?: AccessNodeKind;
  readonly coordinate?: AccessCoordinate;
}

export interface AccessExplicitEdge {
  readonly id: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly travelSeconds: number;
  readonly capacityVehicles?: number;
  readonly closed?: boolean;
}

export interface EdgeOfMapInput {
  readonly id: string;
  readonly coordinate?: AccessCoordinate;
}

export interface ParkingAreaInput {
  readonly id: string;
  readonly capacityVehicles: number;
  readonly roadNodeId?: string;
  readonly roadId?: string;
  readonly pointIndex?: number;
  readonly coordinate?: AccessCoordinate;
}

export interface DropOffZoneInput {
  readonly id: string;
  readonly capacityVehiclesPerTick: number;
  readonly roadNodeId?: string;
  readonly roadId?: string;
  readonly pointIndex?: number;
  readonly coordinate?: AccessCoordinate;
}

export interface AccessPortalInput {
  readonly portal: GuestPortal;
  readonly roadNodeId?: string;
  readonly roadId?: string;
  readonly pointIndex?: number;
  readonly coordinate?: AccessCoordinate;
}

export interface AccessGraphInput {
  readonly roads?: readonly AccessRoadLike[];
  readonly nodes?: readonly AccessExplicitNode[];
  readonly edges?: readonly AccessExplicitEdge[];
  readonly edgeOfMapNodes?: readonly EdgeOfMapInput[];
  readonly parkingAreas?: readonly ParkingAreaInput[];
  readonly dropOffZones?: readonly DropOffZoneInput[];
  readonly portals?: readonly AccessPortalInput[];
}

export interface AccessGraphNode {
  readonly id: string;
  readonly kind: AccessNodeKind;
  readonly coordinate?: AccessCoordinate;
}

export interface AccessGraphEdge {
  readonly id: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly travelSeconds: number;
  readonly capacityVehicles: number;
  readonly closed: boolean;
}

export interface AccessParkingArea {
  readonly id: string;
  readonly nodeId: string;
  readonly capacityVehicles: number;
}

export interface AccessDropOffZone {
  readonly id: string;
  readonly nodeId: string;
  readonly capacityVehiclesPerTick: number;
}

export interface AccessPortalConnection {
  readonly portalId: string;
  readonly nodeId: string;
  readonly portalNodeId: string;
}

export interface AccessGraph {
  readonly version: Phase6AccessVersion;
  readonly nodes: readonly AccessGraphNode[];
  readonly edges: readonly AccessGraphEdge[];
  readonly parkingAreas: readonly AccessParkingArea[];
  readonly dropOffZones: readonly AccessDropOffZone[];
  readonly portals: readonly GuestPortal[];
  readonly portalConnections: readonly AccessPortalConnection[];
  readonly checksum: string;
}

export interface AccessCongestionInput {
  readonly edgeFlowVehicles?: Readonly<Record<string, number>>;
  readonly edgeCapacityVehicles?: Readonly<Record<string, number>>;
}

export interface AccessRoute {
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly nodeIds: readonly string[];
  readonly edgeIds: readonly string[];
  readonly baseTravelSeconds: number;
  readonly congestionDelaySeconds: number;
  readonly travelSeconds: number;
  readonly accessibility: number;
}

export interface VehicleOccupant {
  readonly guestId: string;
  readonly visitorKey?: string;
}

export interface VehicleTrip {
  readonly id: string;
  readonly edgeOfMapNodeId: string;
  readonly destinationPortalId: string;
  readonly destinationFacilityId?: string;
  readonly departureTick: SimulatedSecond;
  readonly occupants: readonly VehicleOccupant[];
}

export interface VehicleTripPlanOptions {
  readonly seed: RandomSeed;
  readonly tripCount: number;
  readonly edgeOfMapNodeIds: readonly string[];
  readonly destinationPortalIds: readonly string[];
  readonly startTick?: SimulatedSecond;
  readonly endTick?: SimulatedSecond;
  readonly minimumOccupants?: number;
  readonly maximumOccupants?: number;
  readonly guestIdPrefix?: string;
}

export interface VehicleTripChunk {
  readonly offset: number;
  readonly trips: readonly VehicleTrip[];
}

export type OccupantAccessStatus = 'queued' | 'parked' | 'dropped-off' | 'handed-off' | 'departed' | 'turned-away';

export interface OccupantAccessRecord extends VehicleOccupant {
  readonly vehicleId: string;
  readonly status: OccupantAccessStatus;
  readonly portalId?: string;
  readonly tick?: SimulatedSecond;
}

export type VehicleAccessStatus = 'queued' | 'parked' | 'dropped-off' | 'handed-off' | 'departed' | 'turned-away';

export interface VehicleAccessRecord {
  readonly vehicleId: string;
  readonly status: VehicleAccessStatus;
  readonly trip: VehicleTrip;
  readonly route?: AccessRoute;
  readonly arrivalTick?: SimulatedSecond;
  readonly facilityId?: string;
}

export interface AccessConservation {
  readonly occupants: number;
  readonly queued: number;
  readonly parked: number;
  readonly droppedOff: number;
  readonly handedOff: number;
  readonly departed: number;
  readonly turnedAway: number;
}

export interface VehicleAccessLedger {
  readonly version: Phase6AccessVersion;
  readonly tick: SimulatedSecond;
  readonly vehicles: readonly VehicleAccessRecord[];
  readonly occupants: readonly OccupantAccessRecord[];
  readonly handoffs: readonly { readonly guestId: string; readonly portalId: string; readonly tick: SimulatedSecond }[];
  readonly conservation: AccessConservation;
  readonly checksum: string;
}

export interface AccessSimulationOptions {
  readonly graph: AccessGraph;
  readonly trips: readonly VehicleTrip[];
  readonly congestion?: AccessCongestionInput;
  readonly tick?: SimulatedSecond;
}

function finiteInteger(value: number, label: string, minimum = 0): void {
  if (!Number.isSafeInteger(value) || value < minimum) throw new RangeError(`${label} must be a safe integer >= ${minimum}`);
}

function finitePositive(value: number, label: string): void { finiteInteger(value, label, 1); }

function text(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be non-empty`);
}

function coordKey(coordinate: AccessCoordinate): string {
  if (!Array.isArray(coordinate) || coordinate.length !== 2 || !coordinate.every(Number.isFinite)) {
    throw new RangeError('coordinates must be finite [x,y] pairs');
  }
  return `${coordinate[0]!.toFixed(6)},${coordinate[1]!.toFixed(6)}`;
}

function compareId(left: { readonly id: string }, right: { readonly id: string }): number { return left.id.localeCompare(right.id); }

function freezeArray<T>(value: readonly T[]): readonly T[] { return Object.freeze([...value]); }

function checksumProjection(value: unknown): string { return eventCalendarChecksum(value); }

function facilityNode(inputId: string, kind: 'parking' | 'drop-off' | 'portal'): string { return `${kind}:${inputId}`; }

function chooseAttachment(
  source: ParkingAreaInput | DropOffZoneInput | AccessPortalInput,
  nodeIds: ReadonlySet<string>,
  nodeByCoordinate: ReadonlyMap<string, string>,
  roadPoints: ReadonlyMap<string, readonly string[]>,
): string {
  if (source.roadNodeId !== undefined) {
    if (!nodeIds.has(source.roadNodeId)) throw new RangeError(`unknown access road node ${source.roadNodeId}`);
    return source.roadNodeId;
  }
  if (source.roadId !== undefined) {
    const points = roadPoints.get(source.roadId);
    const index = source.pointIndex ?? 0;
    if (!points || points[index] === undefined) throw new RangeError(`unknown road attachment ${source.roadId}:${index}`);
    return points[index]!;
  }
  if (source.coordinate !== undefined) {
    const id = nodeByCoordinate.get(coordKey(source.coordinate));
    if (!id) throw new RangeError('coordinate attachment does not touch an access road');
    return id;
  }
  throw new RangeError('access facility must specify roadNodeId, roadId, or coordinate');
}

function validatePortal(portal: GuestPortal): void {
  text(portal.id, 'portal id');
  finitePositive(portal.capacityGuestsPerTick, 'portal capacityGuestsPerTick');
  finiteInteger(portal.openFromTick, 'portal openFromTick');
  finiteInteger(portal.openUntilTick, 'portal openUntilTick');
  if (portal.openUntilTick <= portal.openFromTick) throw new RangeError('portal operating interval must be non-empty');
}

/** Compile SavedRoad-like centerlines and explicit graph pieces into a canonical graph. */
export function compileAccessGraph(input: AccessGraphInput): AccessGraph {
  const roads = [...(input.roads ?? [])].sort(compareId);
  const nodes = new Map<string, AccessGraphNode>();
  const coordinateNodes = new Map<string, string>();
  const roadPoints = new Map<string, readonly string[]>();
  const edges: AccessGraphEdge[] = [];
  const addNode = (node: AccessGraphNode): void => {
    text(node.id, 'access node id');
    const existing = nodes.get(node.id);
    if (existing && (existing.kind !== node.kind || (existing.coordinate && node.coordinate
      && coordKey(existing.coordinate) !== coordKey(node.coordinate)))) throw new RangeError(`conflicting access node ${node.id}`);
    if (!existing) nodes.set(node.id, node.coordinate === undefined ? { id: node.id, kind: node.kind } : {
      id: node.id, kind: node.kind, coordinate: [node.coordinate[0], node.coordinate[1]],
    });
  };
  for (const node of [...(input.nodes ?? [])].sort(compareId)) {
    addNode({ id: node.id, kind: node.kind ?? 'road', ...(node.coordinate ? { coordinate: node.coordinate } : {}) });
    if (node.coordinate) coordinateNodes.set(coordKey(node.coordinate), node.id);
  }
  for (const edgeNode of [...(input.edgeOfMapNodes ?? [])].sort(compareId)) {
    addNode({ id: edgeNode.id, kind: 'edge-of-map', ...(edgeNode.coordinate ? { coordinate: edgeNode.coordinate } : {}) });
    if (edgeNode.coordinate) coordinateNodes.set(coordKey(edgeNode.coordinate), edgeNode.id);
  }
  const edgeById = new Set<string>();
  const addEdge = (edge: AccessGraphEdge): void => {
    text(edge.id, 'access edge id');
    if (!nodes.has(edge.fromNodeId) || !nodes.has(edge.toNodeId)) throw new RangeError(`access edge ${edge.id} references unknown node`);
    finiteInteger(edge.travelSeconds, `edge ${edge.id} travelSeconds`);
    finitePositive(edge.capacityVehicles, `edge ${edge.id} capacityVehicles`);
    if (edgeById.has(edge.id)) throw new RangeError(`duplicate access edge ${edge.id}`);
    edgeById.add(edge.id); edges.push(edge);
  };
  for (const road of roads) {
    text(road.id, 'road id');
    if (!Array.isArray(road.points) || road.points.length < 2) throw new RangeError(`road ${road.id} needs at least two points`);
    const pointIds: string[] = [];
    for (let index = 0; index < road.points.length; index += 1) {
      const coordinate = road.points[index]!;
      const key = coordKey(coordinate);
      let nodeId = coordinateNodes.get(key);
      if (!nodeId) {
        nodeId = `road:${road.id}:${index}`;
        addNode({ id: nodeId, kind: 'road', coordinate }); coordinateNodes.set(key, nodeId);
      }
      pointIds.push(nodeId);
    }
    roadPoints.set(road.id, freezeArray(pointIds));
    const defaultSeconds = road.travelSeconds ?? 1;
    finitePositive(defaultSeconds, `road ${road.id} travelSeconds`);
    if (road.segmentTravelSeconds && road.segmentTravelSeconds.length !== road.points.length - 1) {
      throw new RangeError(`road ${road.id} segmentTravelSeconds length must match segments`);
    }
    const capacity = road.capacityVehicles ?? 30;
    finitePositive(capacity, `road ${road.id} capacityVehicles`);
    for (let index = 0; index < pointIds.length - 1; index += 1) {
      const travelSeconds = road.segmentTravelSeconds?.[index] ?? defaultSeconds;
      finiteInteger(travelSeconds, `road ${road.id} segment travelSeconds`);
      const segmentId = `${road.id}:${index}`;
      addEdge({ id: segmentId, fromNodeId: pointIds[index]!, toNodeId: pointIds[index + 1]!, travelSeconds, capacityVehicles: capacity, closed: road.closed === true });
      addEdge({ id: `${segmentId}:reverse`, fromNodeId: pointIds[index + 1]!, toNodeId: pointIds[index]!, travelSeconds, capacityVehicles: capacity, closed: road.closed === true });
    }
  }
  for (const edge of [...(input.edges ?? [])].sort(compareId)) {
    addEdge({ id: edge.id, fromNodeId: edge.fromNodeId, toNodeId: edge.toNodeId, travelSeconds: edge.travelSeconds,
      capacityVehicles: edge.capacityVehicles ?? 30, closed: edge.closed === true });
  }
  const parkingAreas: AccessParkingArea[] = [];
  const dropOffZones: AccessDropOffZone[] = [];
  const portalConnections: AccessPortalConnection[] = [];
  const portalList: GuestPortal[] = [];
  const addFacilityConnector = (id: string, sourceNodeId: string, kind: 'parking' | 'drop-off' | 'portal'): string => {
    const idNode = facilityNode(id, kind);
    addNode({ id: idNode, kind });
    addEdge({ id: `${idNode}:in`, fromNodeId: sourceNodeId, toNodeId: idNode, travelSeconds: 0, capacityVehicles: Number.MAX_SAFE_INTEGER, closed: false });
    addEdge({ id: `${idNode}:out`, fromNodeId: idNode, toNodeId: sourceNodeId, travelSeconds: 0, capacityVehicles: Number.MAX_SAFE_INTEGER, closed: false });
    return idNode;
  };
  for (const parking of [...(input.parkingAreas ?? [])].sort(compareId)) {
    finitePositive(parking.capacityVehicles, `parking ${parking.id} capacityVehicles`);
    const attached = chooseAttachment(parking, new Set(nodes.keys()), coordinateNodes, roadPoints);
    const nodeId = addFacilityConnector(parking.id, attached, 'parking');
    parkingAreas.push({ id: parking.id, nodeId, capacityVehicles: parking.capacityVehicles });
  }
  for (const dropOff of [...(input.dropOffZones ?? [])].sort(compareId)) {
    finitePositive(dropOff.capacityVehiclesPerTick, `drop-off ${dropOff.id} capacityVehiclesPerTick`);
    const attached = chooseAttachment(dropOff, new Set(nodes.keys()), coordinateNodes, roadPoints);
    const nodeId = addFacilityConnector(dropOff.id, attached, 'drop-off');
    dropOffZones.push({ id: dropOff.id, nodeId, capacityVehiclesPerTick: dropOff.capacityVehiclesPerTick });
  }
  for (const portalInput of [...(input.portals ?? [])].sort((left, right) => left.portal.id.localeCompare(right.portal.id))) {
    validatePortal(portalInput.portal);
    if (portalList.some((portal) => portal.id === portalInput.portal.id)) throw new RangeError(`duplicate access portal ${portalInput.portal.id}`);
    const attached = chooseAttachment(portalInput, new Set(nodes.keys()), coordinateNodes, roadPoints);
    const portalNodeId = addFacilityConnector(portalInput.portal.id, attached, 'portal');
    portalList.push(portalInput.portal);
    portalConnections.push({ portalId: portalInput.portal.id, nodeId: attached, portalNodeId });
  }
  const orderedNodes = [...nodes.values()].sort((left, right) => left.id.localeCompare(right.id));
  const orderedEdges = edges.slice().sort((left, right) => left.id.localeCompare(right.id));
  const graphBase = { version: PHASE6_ACCESS_VERSION, nodes: freezeArray(orderedNodes), edges: freezeArray(orderedEdges),
    parkingAreas: freezeArray(parkingAreas), dropOffZones: freezeArray(dropOffZones), portals: freezeArray(portalList),
    portalConnections: freezeArray(portalConnections) };
  return Object.freeze({ ...graphBase, checksum: checksumProjection(graphBase) });
}

function congestionFor(edge: AccessGraphEdge, congestion: AccessCongestionInput | undefined): { travelSeconds: number; delay: number } {
  const flow = congestion?.edgeFlowVehicles?.[edge.id] ?? 0;
  const capacity = congestion?.edgeCapacityVehicles?.[edge.id] ?? edge.capacityVehicles;
  finiteInteger(flow, `flow for ${edge.id}`); finitePositive(capacity, `capacity for ${edge.id}`);
  const extra = Math.ceil((flow / capacity) * Math.max(1, edge.travelSeconds));
  return { travelSeconds: edge.travelSeconds + extra, delay: extra };
}

/** Return a deterministic shortest route; lexical IDs break every tie. */
export function routeAccessGraph(graph: AccessGraph, fromNodeId: string, toNodeId: string, congestion?: AccessCongestionInput): AccessRoute | null {
  if (!graph.nodes.some((node) => node.id === fromNodeId) || !graph.nodes.some((node) => node.id === toNodeId)) return null;
  const distances = new Map<string, number>([[fromNodeId, 0]]);
  const delays = new Map<string, number>([[fromNodeId, 0]]);
  const paths = new Map<string, readonly string[]>([[fromNodeId, []]]);
  const visited = new Set<string>();
  const outgoing = new Map<string, AccessGraphEdge[]>();
  for (const edge of graph.edges) {
    if (!edge.closed) outgoing.set(edge.fromNodeId, [...(outgoing.get(edge.fromNodeId) ?? []), edge]);
  }
  for (const list of outgoing.values()) list.sort((left, right) => left.id.localeCompare(right.id));
  for (;;) {
    let current: string | undefined;
    for (const [id, distance] of distances) {
      if (visited.has(id)) continue;
      if (current === undefined || distance < distances.get(current)! || (distance === distances.get(current)! && id < current)) current = id;
    }
    if (current === undefined) break;
    visited.add(current);
    if (current === toNodeId) break;
    for (const edge of outgoing.get(current) ?? []) {
      const congestionCost = congestionFor(edge, congestion);
      const candidate = distances.get(current)! + congestionCost.travelSeconds;
      const candidatePath = [...(paths.get(current) ?? []), edge.id];
      const existing = distances.get(edge.toNodeId);
      const priorPath = paths.get(edge.toNodeId) ?? [];
      const better = existing === undefined || candidate < existing || (candidate === existing && candidatePath.join('\u0000') < priorPath.join('\u0000'));
      if (better) {
        distances.set(edge.toNodeId, candidate); delays.set(edge.toNodeId, (delays.get(current) ?? 0) + congestionCost.delay);
        paths.set(edge.toNodeId, candidatePath);
      }
    }
  }
  const travelSeconds = distances.get(toNodeId);
  if (travelSeconds === undefined) return null;
  const edgeIds = paths.get(toNodeId) ?? [];
  const nodeIds = [fromNodeId];
  for (const edgeId of edgeIds) nodeIds.push(graph.edges.find((edge) => edge.id === edgeId)!.toNodeId);
  const baseTravelSeconds = edgeIds.reduce((total, edgeId) => total + graph.edges.find((edge) => edge.id === edgeId)!.travelSeconds, 0);
  const congestionDelaySeconds = delays.get(toNodeId) ?? 0;
  return Object.freeze({ fromNodeId, toNodeId, nodeIds: freezeArray(nodeIds), edgeIds: freezeArray(edgeIds), baseTravelSeconds,
    congestionDelaySeconds, travelSeconds, accessibility: travelSeconds === 0 ? 1 : baseTravelSeconds / travelSeconds });
}

export const findAccessRoute = routeAccessGraph;
export const routeVehicle = routeAccessGraph;
export const compileRoadAccessGraph = compileAccessGraph;

/** Congestion response used by planners that do not need a full route. */
export function congestionAdjustedTravelSeconds(baseTravelSeconds: number, flowVehicles: number, capacityVehicles: number): number {
  finitePositive(baseTravelSeconds, 'baseTravelSeconds'); finiteInteger(flowVehicles, 'flowVehicles'); finitePositive(capacityVehicles, 'capacityVehicles');
  return baseTravelSeconds + Math.ceil((flowVehicles / capacityVehicles) * baseTravelSeconds);
}

export function congestionAccessibility(baseTravelSeconds: number, flowVehicles: number, capacityVehicles: number): number {
  const travel = congestionAdjustedTravelSeconds(baseTravelSeconds, flowVehicles, capacityVehicles);
  return baseTravelSeconds / travel;
}

function validateTrip(trip: VehicleTrip): void {
  text(trip.id, 'vehicle trip id'); text(trip.edgeOfMapNodeId, 'edge-of-map node id'); text(trip.destinationPortalId, 'destination portal id');
  finiteInteger(trip.departureTick, 'vehicle departureTick');
  if (!Array.isArray(trip.occupants) || trip.occupants.length === 0) throw new RangeError(`vehicle ${trip.id} must contain occupants`);
  const seen = new Set<string>();
  for (const occupant of trip.occupants) { text(occupant.guestId, 'guest id'); if (seen.has(occupant.guestId)) throw new RangeError(`vehicle ${trip.id} repeats guest ${occupant.guestId}`); seen.add(occupant.guestId); }
}

/** Stateless demand generation; every trip remains identical when generated in chunks. */
export function createDeterministicVehicleTrips(options: VehicleTripPlanOptions): readonly VehicleTrip[] {
  finiteInteger(options.tripCount, 'tripCount');
  if (options.edgeOfMapNodeIds.length === 0 || options.destinationPortalIds.length === 0) throw new RangeError('vehicle trip plans need edge nodes and portals');
  const startTick = options.startTick ?? 0; const endTick = options.endTick ?? startTick + 1;
  finiteInteger(startTick, 'startTick'); finiteInteger(endTick, 'endTick'); if (endTick <= startTick) throw new RangeError('endTick must be after startTick');
  const minimum = options.minimumOccupants ?? 1; const maximum = options.maximumOccupants ?? 4;
  finitePositive(minimum, 'minimumOccupants'); finitePositive(maximum, 'maximumOccupants'); if (maximum < minimum) throw new RangeError('maximumOccupants must be >= minimumOccupants');
  const prefix = options.guestIdPrefix ?? 'vehicle-guest';
  const trips: VehicleTrip[] = [];
  for (let index = 0; index < options.tripCount; index += 1) {
    const id = `vehicle-${index.toString().padStart(8, '0')}`;
    const occupantsCount = keyedRandomInt(options.seed, id, 'occupancy', 0, minimum, maximum);
    const occupants = Array.from({ length: occupantsCount }, (_, member) => ({ guestId: `${prefix}-${index.toString().padStart(8, '0')}-${member}`, visitorKey: `${prefix}:${index}:${member}` }));
    trips.push(Object.freeze({ id, edgeOfMapNodeId: options.edgeOfMapNodeIds[keyedRandomInt(options.seed, id, 'origin', 0, 0, options.edgeOfMapNodeIds.length - 1)]!, destinationPortalId: options.destinationPortalIds[keyedRandomInt(options.seed, id, 'destination', 0, 0, options.destinationPortalIds.length - 1)]!, departureTick: keyedRandomInt(options.seed, id, 'departure', 0, startTick, endTick - 1), occupants: freezeArray(occupants) }));
  }
  return freezeArray(trips);
}

export const generateVehicleTrips = createDeterministicVehicleTrips;
export const createVehicleTrips = createDeterministicVehicleTrips;
export const createEdgeOfMapVehicleTrips = createDeterministicVehicleTrips;

export function chunkVehicleTrips(options: VehicleTripPlanOptions, chunkSize: number): readonly VehicleTripChunk[] {
  finitePositive(chunkSize, 'chunkSize');
  const trips = createDeterministicVehicleTrips(options); const chunks: VehicleTripChunk[] = [];
  for (let offset = 0; offset < trips.length; offset += chunkSize) chunks.push(Object.freeze({ offset, trips: freezeArray(trips.slice(offset, offset + chunkSize)) }));
  return freezeArray(chunks);
}

function portalFor(graph: AccessGraph, id: string): GuestPortal | undefined { return graph.portals.find((portal) => portal.id === id); }
function portalNodeFor(graph: AccessGraph, id: string): string | undefined { return graph.portalConnections.find((entry) => entry.portalId === id)?.portalNodeId; }

function joinRoutes(first: AccessRoute, second: AccessRoute): AccessRoute {
  const edgeIds = [...first.edgeIds, ...second.edgeIds];
  const nodeIds = [...first.nodeIds, ...second.nodeIds.slice(1)];
  return Object.freeze({ fromNodeId: first.fromNodeId, toNodeId: second.toNodeId, nodeIds: freezeArray(nodeIds), edgeIds: freezeArray(edgeIds),
    baseTravelSeconds: first.baseTravelSeconds + second.baseTravelSeconds, congestionDelaySeconds: first.congestionDelaySeconds + second.congestionDelaySeconds,
    travelSeconds: first.travelSeconds + second.travelSeconds,
    accessibility: first.travelSeconds + second.travelSeconds === 0 ? 1
      : (first.baseTravelSeconds + second.baseTravelSeconds) / (first.travelSeconds + second.travelSeconds) });
}

function deriveVehicleStatus(records: readonly OccupantAccessRecord[]): VehicleAccessStatus {
  if (records.every((record) => record.status === 'departed')) return 'departed';
  if (records.every((record) => record.status === 'turned-away')) return 'turned-away';
  if (records.every((record) => record.status === 'handed-off' || record.status === 'departed')) return 'handed-off';
  if (records.some((record) => record.status === 'parked')) return 'parked';
  if (records.some((record) => record.status === 'dropped-off')) return 'dropped-off';
  return 'queued';
}

function conservation(records: readonly OccupantAccessRecord[]): AccessConservation {
  const count = (status: OccupantAccessStatus): number => records.filter((record) => record.status === status).length;
  const result = { occupants: records.length, queued: count('queued'), parked: count('parked'), droppedOff: count('dropped-off'), handedOff: count('handed-off'), departed: count('departed'), turnedAway: count('turned-away') };
  if (result.occupants !== result.queued + result.parked + result.droppedOff + result.handedOff + result.departed + result.turnedAway) throw new Error('access occupant conservation violated');
  return Object.freeze(result);
}

function ledgerFrom(tick: number, vehicles: readonly VehicleAccessRecord[], occupants: readonly OccupantAccessRecord[], handoffs: readonly { readonly guestId: string; readonly portalId: string; readonly tick: number }[]): VehicleAccessLedger {
  const orderedVehicles = vehicles.slice().sort((left, right) => left.vehicleId.localeCompare(right.vehicleId));
  const orderedOccupants = occupants.slice().sort((left, right) => left.guestId.localeCompare(right.guestId));
  const orderedHandoffs = handoffs.slice().sort((left, right) => left.guestId.localeCompare(right.guestId));
  const base = { version: PHASE6_ACCESS_VERSION, tick, vehicles: freezeArray(orderedVehicles), occupants: freezeArray(orderedOccupants), handoffs: freezeArray(orderedHandoffs), conservation: conservation(orderedOccupants) };
  return Object.freeze({ ...base, checksum: checksumProjection(base) });
}

/** Simulate arrival, facility capacity, and exact portal handoff in stable order. */
export function simulateVehicleAccess(options: AccessSimulationOptions): VehicleAccessLedger {
  const currentTick = options.tick ?? 0;
  finiteInteger(currentTick, 'access tick');
  const trips = options.trips.slice().sort((left, right) => left.departureTick - right.departureTick || left.id.localeCompare(right.id));
  const occupantIds = new Set<string>(); const vehicleIds = new Set<string>();
  for (const trip of trips) {
    validateTrip(trip); if (vehicleIds.has(trip.id)) throw new RangeError(`duplicate vehicle ${trip.id}`); vehicleIds.add(trip.id);
    if (!options.graph.nodes.some((node) => node.id === trip.edgeOfMapNodeId)) throw new RangeError(`unknown edge-of-map node ${trip.edgeOfMapNodeId}`);
    if (!portalFor(options.graph, trip.destinationPortalId)) throw new RangeError(`unknown destination portal ${trip.destinationPortalId}`);
    for (const occupant of trip.occupants) { if (occupantIds.has(occupant.guestId)) throw new RangeError(`guest ${occupant.guestId} appears in more than one vehicle`); occupantIds.add(occupant.guestId); }
  }
  const parkingUse = new Map<string, number>(); const dropUse = new Map<string, number>(); const portalUse = new Map<string, number>();
  const records: OccupantAccessRecord[] = []; const recordIndexByGuest = new Map<string, number>();
  const recordIndexesByVehicle = new Map<string, number[]>();
  const vehicles: VehicleAccessRecord[] = []; const handoffs: { guestId: string; portalId: string; tick: number }[] = [];
  const parkingById = new Map(options.graph.parkingAreas.map((entry) => [entry.id, entry])); const dropById = new Map(options.graph.dropOffZones.map((entry) => [entry.id, entry]));
  for (const trip of trips) {
    if (trip.destinationFacilityId !== undefined && !parkingById.has(trip.destinationFacilityId) && !dropById.has(trip.destinationFacilityId)) throw new RangeError(`unknown destination facility ${trip.destinationFacilityId}`);
    const portalNode = portalNodeFor(options.graph, trip.destinationPortalId);
    const targetNode = trip.destinationFacilityId ? (parkingById.get(trip.destinationFacilityId)?.nodeId ?? dropById.get(trip.destinationFacilityId)?.nodeId) : portalNode;
    const facilityRoute = targetNode ? routeAccessGraph(options.graph, trip.edgeOfMapNodeId, targetNode, options.congestion) : null;
    const portalRoute = facilityRoute && targetNode !== undefined && portalNode !== undefined && targetNode !== portalNode
      ? routeAccessGraph(options.graph, targetNode, portalNode, options.congestion) : null;
    const route = trip.destinationFacilityId !== undefined
      ? (facilityRoute && portalRoute ? joinRoutes(facilityRoute, portalRoute) : null)
      : facilityRoute;
    const facilityArrivalTick = facilityRoute ? trip.departureTick + facilityRoute.travelSeconds : undefined;
    const arrivalTick = route ? trip.departureTick + route.travelSeconds : undefined;
    let status: OccupantAccessStatus = route ? 'queued' : 'turned-away';
    let facilityId = trip.destinationFacilityId;
    if (route && facilityArrivalTick !== undefined && facilityArrivalTick <= currentTick
      && targetNode !== portalNodeFor(options.graph, trip.destinationPortalId)) {
      const parking = trip.destinationFacilityId ? parkingById.get(trip.destinationFacilityId) : undefined;
      const dropOff = trip.destinationFacilityId ? dropById.get(trip.destinationFacilityId) : undefined;
      if (parking) {
        const used = parkingUse.get(parking.id) ?? 0;
        if (used < parking.capacityVehicles) { parkingUse.set(parking.id, used + 1); status = 'parked'; }
      } else if (dropOff) {
        const tickUsed = dropUse.get(`${dropOff.id}:${facilityArrivalTick}`) ?? 0;
        if (tickUsed < dropOff.capacityVehiclesPerTick) { dropUse.set(`${dropOff.id}:${facilityArrivalTick}`, tickUsed + 1); status = 'dropped-off'; }
      } else status = 'turned-away';
    }
    const nextRecords = trip.occupants.map((occupant) => ({ ...occupant, vehicleId: trip.id, status, ...(arrivalTick === undefined ? {} : { tick: arrivalTick }) }));
    const vehicleRecordIndexes: number[] = [];
    for (const record of nextRecords) {
      vehicleRecordIndexes.push(records.length); recordIndexByGuest.set(record.guestId, records.length); records.push(record);
    }
    recordIndexesByVehicle.set(trip.id, vehicleRecordIndexes);
    vehicles.push({ vehicleId: trip.id, status: deriveVehicleStatus(nextRecords), trip, ...(route ? { route, arrivalTick } : {}), ...(facilityId ? { facilityId } : {}) });
    if (route && (targetNode === portalNodeFor(options.graph, trip.destinationPortalId) || status === 'parked' || status === 'dropped-off')) {
      const portal = portalFor(options.graph, trip.destinationPortalId)!;
      const firstTick = arrivalTick!;
      for (const occupant of trip.occupants) {
        const index = recordIndexByGuest.get(occupant.guestId)!;
        const record = records[index]!;
        if (record.status !== 'parked' && record.status !== 'dropped-off' && record.status !== 'queued') continue;
        let handoffTick = firstTick;
        while (handoffTick < portal.openUntilTick && handoffTick <= currentTick) {
          const used = portalUse.get(`${portal.id}:${handoffTick}`) ?? 0;
          if (handoffTick >= portal.openFromTick && used < portal.capacityGuestsPerTick) break;
          handoffTick += 1;
        }
        if (handoffTick > currentTick) continue;
        if (handoffTick >= portal.openUntilTick) { records[index] = { ...record, status: 'turned-away' as const }; continue; }
        portalUse.set(`${portal.id}:${handoffTick}`, (portalUse.get(`${portal.id}:${handoffTick}`) ?? 0) + 1);
        records[index] = { ...record, status: 'handed-off', portalId: portal.id, tick: handoffTick };
        handoffs.push({ guestId: occupant.guestId, portalId: portal.id, tick: handoffTick });
      }
    }
  }
  const finalVehicles = vehicles.map((vehicle) => {
    const vehicleRecords = (recordIndexesByVehicle.get(vehicle.vehicleId) ?? []).map((index) => records[index]!);
    return { ...vehicle, status: deriveVehicleStatus(vehicleRecords) };
  });
  return ledgerFrom(currentTick, finalVehicles, records, handoffs);
}

/** Idempotent handoff helper for a persisted or incrementally-run ledger. */
export function handoffVehicleOccupants(ledger: VehicleAccessLedger, vehicleId: string, portalId: string, tick: SimulatedSecond, graph: AccessGraph): VehicleAccessLedger {
  finiteInteger(tick, 'handoff tick'); const portal = portalFor(graph, portalId); if (!portal) throw new RangeError(`unknown destination portal ${portalId}`);
  const vehicle = ledger.vehicles.find((candidate) => candidate.vehicleId === vehicleId); if (!vehicle) throw new RangeError(`unknown vehicle ${vehicleId}`);
  const used = ledger.handoffs.filter((handoff) => handoff.portalId === portalId && handoff.tick === tick).length;
  let slots = Math.max(0, portal.capacityGuestsPerTick - used); const nextOccupants = ledger.occupants.map((record) => {
    if (record.vehicleId !== vehicleId || (record.status !== 'queued' && record.status !== 'parked' && record.status !== 'dropped-off')) return record;
    if (ledger.handoffs.some((handoff) => handoff.guestId === record.guestId)) return record;
    if (slots <= 0 || tick < portal.openFromTick || tick >= portal.openUntilTick) return record.status === 'queued' ? record : { ...record, status: 'turned-away' as const, tick };
    slots -= 1; return { ...record, status: 'handed-off' as const, portalId, tick };
  });
  const newHandoffs = nextOccupants.filter((record) => record.status === 'handed-off' && !ledger.handoffs.some((entry) => entry.guestId === record.guestId)).map((record) => ({ guestId: record.guestId, portalId: portalId, tick }));
  const vehicles = ledger.vehicles.map((entry) => entry.vehicleId === vehicleId ? { ...entry, status: deriveVehicleStatus(nextOccupants.filter((record) => record.vehicleId === vehicleId)) } : entry);
  return ledgerFrom(Math.max(ledger.tick, tick), vehicles, nextOccupants, [...ledger.handoffs, ...newHandoffs]);
}

export const handoffVehicleToGuestPortal = handoffVehicleOccupants;

export function departVehicleOccupants(ledger: VehicleAccessLedger, vehicleId: string, tick: SimulatedSecond): VehicleAccessLedger {
  finiteInteger(tick, 'departure tick');
  const next = ledger.occupants.map((record) => record.vehicleId === vehicleId && record.status === 'handed-off' ? { ...record, status: 'departed' as const, tick } : record);
  const vehicles = ledger.vehicles.map((entry) => entry.vehicleId === vehicleId ? { ...entry, status: deriveVehicleStatus(next.filter((record) => record.vehicleId === vehicleId)) } : entry);
  return ledgerFrom(Math.max(ledger.tick, tick), vehicles, next, ledger.handoffs);
}

export function accessLedgerChecksum(ledger: VehicleAccessLedger): string {
  return checksumProjection({ version: ledger.version, tick: ledger.tick, vehicles: ledger.vehicles, occupants: ledger.occupants, handoffs: ledger.handoffs, conservation: ledger.conservation });
}

export function isAccessGraph(value: unknown): value is AccessGraph {
  if (!value || typeof value !== 'object') return false;
  const graph = value as Partial<AccessGraph>;
  return graph.version === PHASE6_ACCESS_VERSION && Array.isArray(graph.nodes) && Array.isArray(graph.edges)
    && Array.isArray(graph.parkingAreas) && Array.isArray(graph.dropOffZones) && Array.isArray(graph.portals)
    && Array.isArray(graph.portalConnections) && typeof graph.checksum === 'string'
    && graph.checksum === checksumProjection({ version: graph.version, nodes: graph.nodes, edges: graph.edges,
      parkingAreas: graph.parkingAreas, dropOffZones: graph.dropOffZones, portals: graph.portals, portalConnections: graph.portalConnections });
}

export function isVehicleAccessLedger(value: unknown): value is VehicleAccessLedger {
  if (!value || typeof value !== 'object') return false;
  const ledger = value as Partial<VehicleAccessLedger>;
  if (ledger.version !== PHASE6_ACCESS_VERSION || !Number.isSafeInteger(ledger.tick) || !Array.isArray(ledger.vehicles)
    || !Array.isArray(ledger.occupants) || !Array.isArray(ledger.handoffs) || !ledger.conservation || typeof ledger.checksum !== 'string') return false;
  try {
    const expected = conservation(ledger.occupants);
    const actual = ledger.conservation;
    if (expected.occupants !== actual.occupants || expected.queued !== actual.queued || expected.parked !== actual.parked
      || expected.droppedOff !== actual.droppedOff || expected.handedOff !== actual.handedOff || expected.departed !== actual.departed
      || expected.turnedAway !== actual.turnedAway) return false;
  } catch { return false; }
  return ledger.checksum === accessLedgerChecksum(ledger as VehicleAccessLedger);
}

export interface Phase6AccessSnapshot { readonly version: Phase6AccessVersion; readonly graph: AccessGraph; readonly ledger: VehicleAccessLedger; readonly checksum: string; }

export function createPhase6AccessSnapshot(graph: AccessGraph, ledger: VehicleAccessLedger): Phase6AccessSnapshot {
  if (!isAccessGraph(graph) || !isVehicleAccessLedger(ledger)) throw new RangeError('invalid Phase 6 access graph or ledger');
  const base = { version: PHASE6_ACCESS_VERSION, graph, ledger };
  return Object.freeze({ ...base, checksum: checksumProjection(base) });
}

export function isPhase6AccessSnapshot(value: unknown): value is Phase6AccessSnapshot {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<Phase6AccessSnapshot>;
  return candidate.version === PHASE6_ACCESS_VERSION && typeof candidate.checksum === 'string'
    && isAccessGraph(candidate.graph) && isVehicleAccessLedger(candidate.ledger)
    && candidate.checksum === checksumProjection({ version: candidate.version, graph: candidate.graph, ledger: candidate.ledger });
}

export function encodePhase6AccessSnapshot(snapshot: Phase6AccessSnapshot): Uint8Array { if (!isPhase6AccessSnapshot(snapshot)) throw new RangeError('invalid Phase 6 access snapshot'); return encodeBinarySidecar(snapshot); }
export function decodePhase6AccessSnapshot(bytes: Uint8Array): Phase6AccessSnapshot { const snapshot = decodeBinarySidecar<Phase6AccessSnapshot>(bytes); if (!isPhase6AccessSnapshot(snapshot)) throw new RangeError('invalid Phase 6 access snapshot'); return snapshot; }
