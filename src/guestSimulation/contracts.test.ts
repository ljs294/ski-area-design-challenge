import { describe, expect, it } from 'vitest';
import {
  GUEST_SIMULATION_CONTRACT_VERSION,
  GUEST_SIMULATION_PROTOCOL_VERSION,
  asSimulatedSecond,
  isGuestSimulationRequest,
  isGuestSimulationSnapshot,
  isHalfOpenTickInterval,
  isSimulatedSecond,
} from './contracts';
import type { GuestSimulationRequest, GuestSimulationResponse } from './contracts';
import { createGuestSimulationFixture } from './fixtures';

describe('guest simulation contracts', () => {
  it('pins contract and protocol versions at v1', () => {
    expect(GUEST_SIMULATION_CONTRACT_VERSION).toBe(1);
    expect(GUEST_SIMULATION_PROTOCOL_VERSION).toBe(1);
  });

  it('accepts only finite non-negative integer simulated seconds', () => {
    expect(isSimulatedSecond(0)).toBe(true);
    expect(isSimulatedSecond(60)).toBe(true);
    expect(isSimulatedSecond(1.5)).toBe(false);
    expect(isSimulatedSecond(-1)).toBe(false);
    expect(isSimulatedSecond(Number.NaN)).toBe(false);
    expect(isSimulatedSecond(Number.POSITIVE_INFINITY)).toBe(false);
    expect(() => asSimulatedSecond(1.5)).toThrow(RangeError);
  });

  it('keeps protocol messages discriminated and future-party state nullable', () => {
    const request: GuestSimulationRequest = { version: 1, type: 'snapshot', requestId: 'req-1', sequence: 0 };
    const response: GuestSimulationResponse = { version: 1, type: 'error', requestId: 'req-1', sequence: 0,
      error: { code: 'invalid-request', message: 'test', retryable: false } };
    expect(request.type).toBe('snapshot');
    expect(response.type).toBe('error');
  });

  it('validates half-open intervals and load compatibility fields at the protocol boundary', () => {
    expect(isHalfOpenTickInterval(0, 1)).toBe(true);
    expect(isHalfOpenTickInterval(1, 1)).toBe(false);
    expect(isHalfOpenTickInterval(2, 1)).toBe(false);
    const snapshot = createGuestSimulationFixture(1_000).createSnapshot();
    expect(isGuestSimulationSnapshot(snapshot)).toBe(true);
    const load: GuestSimulationRequest = { version: 1, type: 'load', requestId: 'load-1', sequence: 1,
      expectedEnvironmentRevision: snapshot.environmentRevision,
      expectedTopologyRevision: snapshot.topologyRevision,
      expectedRunId: snapshot.runId, expectedConfigVersion: snapshot.configVersion,
      expectedDemandSeed: snapshot.demandPlan.seed, expectedSnapshotChecksum: snapshot.checksum, snapshot };
    expect(isGuestSimulationRequest(load)).toBe(true);
    expect(isGuestSimulationRequest({ ...load, expectedDemandSeed: 'wrong-seed' })).toBe(false);
    expect(isGuestSimulationSnapshot({ ...snapshot, demandPlan: { ...snapshot.demandPlan,
      endTick: snapshot.demandPlan.startTick } })).toBe(false);
  });
});
