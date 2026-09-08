import { describe, expect, it } from 'vitest';
import {
  PATROL_PROTOCOL_VERSION,
  PatrolSimulation,
  findPatrolPath,
  type PatrolCommand,
  type PatrolGraph,
  type PatrolSimulationOptions,
} from './patrol.ts';

const graph: PatrolGraph = {
  nodes: [
    { id: 'station-node', kind: 'station' },
    { id: 'trail-node', kind: 'trail' },
    { id: 'junction', kind: 'junction' },
    { id: 'medical-node', kind: 'destination' },
    { id: 'fallback-node', kind: 'portal' },
  ],
  edges: [
    { id: 'station-to-junction', fromNodeId: 'station-node', toNodeId: 'junction', travelSeconds: 10 },
    { id: 'junction-to-trail', fromNodeId: 'junction', toNodeId: 'trail-node', travelSeconds: 15 },
    { id: 'trail-to-medical', fromNodeId: 'trail-node', toNodeId: 'medical-node', travelSeconds: 20 },
    { id: 'fallback-to-trail', fromNodeId: 'fallback-node', toNodeId: 'trail-node', travelSeconds: 40 },
  ],
};

const options: PatrolSimulationOptions = {
  graph,
  stations: [{ id: 'station-a', nodeId: 'station-node', responderIds: ['r1', 'r2'] }],
  responders: [
    { id: 'r1', stationId: 'station-a', capacityUnits: 1 },
    { id: 'r2', stationId: 'station-a', capacityUnits: 1 },
  ],
  destinations: [{ id: 'clinic', nodeId: 'medical-node', kind: 'medical' }],
  fallback: { enabled: true, travelSeconds: 100, sourceNodeId: 'fallback-node' },
};

function command(sequence: number, value: Record<string, unknown> & { type: PatrolCommand['type'] }): PatrolCommand {
  return { ...value, version: PATROL_PROTOCOL_VERSION, requestId: `request-${sequence}`, sequence } as PatrolCommand;
}

function report(sequence: number, incidentId: string, nodeId = 'trail-node', severity: 'minor' | 'critical' = 'minor'): PatrolCommand {
  return command(sequence, { type: 'report-incident', incidentId, guestId: `guest-${incidentId}`, nodeId, tick: 0, severity });
}

describe('patrol graph and dispatch domain', () => {
  it('chooses the deterministic shortest route and rejects closed edges', () => {
    expect(findPatrolPath(graph, 'station-node', 'medical-node')).toMatchObject({
      edgeIds: ['station-to-junction', 'junction-to-trail', 'trail-to-medical'],
      travelSeconds: 45,
    });
    expect(findPatrolPath({ ...graph, edges: graph.edges.map((edge) => edge.id === 'station-to-junction' ? { ...edge, open: false } : edge) }, 'station-node', 'medical-node')).toBeNull();
  });

  it('dispatches FIFO and conserves responder capacity', () => {
    const simulation = new PatrolSimulation(options);
    simulation.apply(report(0, 'i-1'));
    simulation.apply(report(1, 'i-2'));
    simulation.apply(report(2, 'i-3'));
    let snapshot = simulation.snapshot();
    expect(snapshot.queue).toEqual(['i-3']);
    expect(snapshot.metrics.assignedResponderUnits).toBe(2);
    expect(snapshot.metrics.availableResponderUnits).toBe(0);
    expect(snapshot.metrics.responderCapacityConserved).toBe(true);
    expect(snapshot.incidents.filter((incident) => incident.status === 'dispatched').map((incident) => incident.id)).toEqual(['i-1', 'i-2']);
    simulation.apply(command(3, { type: 'advance', tick: 200 }));
    snapshot = simulation.snapshot();
    expect(snapshot.incidents.find((incident) => incident.id === 'i-1')?.status).toBe('resolved');
    expect(snapshot.incidents.find((incident) => incident.id === 'i-2')?.status).toBe('resolved');
    expect(snapshot.incidents.find((incident) => incident.id === 'i-3')?.status).toBe('transporting');
    expect(snapshot.metrics.responderCapacityConserved).toBe(true);
  });

  it('records route and rescue destination response timing', () => {
    const simulation = new PatrolSimulation(options);
    simulation.apply(report(0, 'i-1'));
    const dispatch = simulation.snapshot().dispatches[0];
    expect(dispatch).toMatchObject({ responseSeconds: 25, onSceneTick: 25, transportStartTick: 85, completeTick: 105, destinationId: 'clinic' });
    expect(dispatch.responsePath?.edgeIds).toEqual(['station-to-junction', 'junction-to-trail']);
    simulation.apply(command(1, { type: 'advance', tick: 25 }));
    expect(simulation.snapshot().incidents[0].status).toBe('on-scene');
    simulation.apply(command(2, { type: 'advance', tick: 105 }));
    expect(simulation.snapshot().incidents[0].status).toBe('resolved');
  });

  it('uses deterministic auto-rescue when no patrol station can reach the guest', () => {
    const simulation = new PatrolSimulation({ ...options, stations: [], responders: [] });
    const result = simulation.apply(report(0, 'i-auto'));
    expect(result.ok).toBe(true);
    expect(result.events.some((event) => event.type === 'dispatch-created')).toBe(true);
    expect(simulation.snapshot().dispatches[0]).toMatchObject({ source: 'auto-rescue', responseSeconds: 40, destinationId: 'clinic' });
    simulation.apply(command(1, { type: 'advance', tick: 140 }));
    expect(simulation.snapshot().incidents[0].status).toBe('resolved');
  });

  it('exposes unreachable when fallback is disabled', () => {
    const simulation = new PatrolSimulation({ ...options, stations: [], responders: [], fallback: { enabled: false, travelSeconds: 100 } });
    simulation.apply(report(0, 'i-unreachable'));
    expect(simulation.snapshot().incidents[0]).toMatchObject({ status: 'unreachable', reason: 'no-reachable-patrol-station' });
    expect(simulation.snapshot().dispatches).toHaveLength(0);
  });

  it('exposes failed when responders reach terrain but no destination is reachable', () => {
    const noDestinationOptions = { ...options, destinations: [], requireRescueDestination: true };
    const simulation = new PatrolSimulation(noDestinationOptions);
    simulation.apply(report(0, 'i-failed'));
    simulation.apply(command(1, { type: 'advance', tick: 85 }));
    expect(simulation.snapshot().incidents[0].status).toBe('failed');
    expect(simulation.snapshot().incidents[0].reason).toBe('no-reachable-rescue-destination');
    expect(simulation.snapshot().metrics.responderCapacityConserved).toBe(true);
  });

  it('releases assigned capacity when an active incident is cancelled', () => {
    const simulation = new PatrolSimulation(options);
    simulation.apply(report(0, 'i-cancel'));
    expect(simulation.snapshot().metrics.assignedResponderUnits).toBe(1);
    simulation.apply(command(1, { type: 'cancel-incident', incidentId: 'i-cancel' }));
    expect(simulation.snapshot().incidents[0].status).toBe('cancelled');
    expect(simulation.snapshot().metrics.assignedResponderUnits).toBe(0);
    expect(simulation.snapshot().metrics.responderCapacityConserved).toBe(true);
  });

  it('rejects stale commands without changing authoritative state', () => {
    const simulation = new PatrolSimulation(options);
    const accepted = simulation.apply(report(2, 'i-1'));
    const before = simulation.snapshot();
    const rejected = simulation.apply(report(1, 'i-2'));
    expect(accepted.ok).toBe(true);
    expect(rejected.ok).toBe(false);
    expect(rejected.error?.code).toBe('stale-command');
    expect(rejected.snapshot.checksum).toBe(before.checksum);
  });

  it('produces the same snapshot checksum for the same command stream', () => {
    const left = new PatrolSimulation(options);
    const right = new PatrolSimulation(options);
    const commands = [report(0, 'i-1'), report(1, 'i-2'), command(2, { type: 'advance', tick: 500 })];
    for (const next of commands) {
      left.apply(next);
      right.apply(next);
    }
    expect(left.snapshot().checksum).toBe(right.snapshot().checksum);
  });
});
