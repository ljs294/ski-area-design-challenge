import type { PatrolSimulationOptions } from './patrol.ts';

function assertNonEmpty(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new TypeError(`${label} must be a non-empty string`);
}

function assertTick(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

function assertPositiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${label} must be a positive safe integer`);
}

function assertOptionalWindow(from: number | undefined, until: number | undefined, label: string): void {
  if (from !== undefined) assertTick(from, `${label}.openFromTick`);
  if (until !== undefined) assertTick(until, `${label}.openUntilTick`);
  if (from !== undefined && until !== undefined && until <= from) {
    throw new RangeError(`${label}.openUntilTick must be greater than openFromTick`);
  }
}

/** Validate patrol topology and capacity before they cross into a worker. */
export function assertPatrolSimulationOptions(options: PatrolSimulationOptions): void {
  if (!options || typeof options !== 'object') throw new TypeError('patrol options are required');
  const { graph } = options;
  if (!graph || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) throw new TypeError('patrol graph is required');
  if (graph.nodes.length > 10_000 || graph.edges.length > 25_000) throw new RangeError('patrol graph exceeds bounded limits');
  const nodeIds = new Set<string>();
  for (const node of graph.nodes) {
    assertNonEmpty(node.id, 'graph node id');
    if (nodeIds.has(node.id)) throw new RangeError(`duplicate patrol graph node ${node.id}`);
    nodeIds.add(node.id);
  }
  const edgeIds = new Set<string>();
  for (const edge of graph.edges) {
    assertNonEmpty(edge.id, 'graph edge id');
    if (edgeIds.has(edge.id)) throw new RangeError(`duplicate patrol graph edge ${edge.id}`);
    edgeIds.add(edge.id);
    if (!nodeIds.has(edge.fromNodeId) || !nodeIds.has(edge.toNodeId)) throw new RangeError(`edge ${edge.id} references an unknown node`);
    assertPositiveInteger(edge.travelSeconds, `edge ${edge.id}.travelSeconds`);
  }
  const stationIds = new Set<string>();
  const responderIds = new Set<string>();
  const stationResponderIds = new Set<string>();
  for (const station of options.stations) {
    assertNonEmpty(station.id, 'station id');
    if (stationIds.has(station.id)) throw new RangeError(`duplicate patrol station ${station.id}`);
    stationIds.add(station.id);
    if (!nodeIds.has(station.nodeId)) throw new RangeError(`station ${station.id} references an unknown node`);
    assertOptionalWindow(station.openFromTick, station.openUntilTick, `station ${station.id}`);
    if (station.responderIds.length === 0) throw new RangeError(`station ${station.id} must have at least one responder`);
    for (const responderId of station.responderIds) {
      assertNonEmpty(responderId, 'station responder id');
      if (stationResponderIds.has(responderId)) throw new RangeError(`responder ${responderId} is assigned to multiple stations`);
      stationResponderIds.add(responderId);
    }
  }
  for (const responder of options.responders) {
    assertNonEmpty(responder.id, 'responder id');
    if (responderIds.has(responder.id)) throw new RangeError(`duplicate patrol responder ${responder.id}`);
    responderIds.add(responder.id);
    if (!stationIds.has(responder.stationId)) throw new RangeError(`responder ${responder.id} references an unknown station`);
    if (!stationResponderIds.has(responder.id)) throw new RangeError(`responder ${responder.id} is not listed by its station`);
    assertPositiveInteger(responder.capacityUnits, `responder ${responder.id}.capacityUnits`);
  }
  for (const station of options.stations) for (const responderId of station.responderIds) {
    if (!responderIds.has(responderId)) throw new RangeError(`station ${station.id} references an unknown responder ${responderId}`);
  }
  const destinationIds = new Set<string>();
  for (const destination of options.destinations) {
    assertNonEmpty(destination.id, 'destination id');
    if (destinationIds.has(destination.id)) throw new RangeError(`duplicate patrol destination ${destination.id}`);
    destinationIds.add(destination.id);
    if (!nodeIds.has(destination.nodeId)) throw new RangeError(`destination ${destination.id} references an unknown node`);
  }
  if (options.startTick !== undefined) assertTick(options.startTick, 'startTick');
  if (options.maxIncidents !== undefined) assertPositiveInteger(options.maxIncidents, 'maxIncidents');
  if (options.fallback) {
    assertPositiveInteger(options.fallback.travelSeconds, 'fallback.travelSeconds');
    if (options.fallback.sourceNodeId !== undefined && !nodeIds.has(options.fallback.sourceNodeId)) {
      throw new RangeError(`fallback references an unknown source node ${options.fallback.sourceNodeId}`);
    }
    if (options.fallback.destinationId !== undefined && !destinationIds.has(options.fallback.destinationId)) {
      throw new RangeError(`fallback references an unknown destination ${options.fallback.destinationId}`);
    }
  }
}
