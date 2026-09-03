import { createDailyGuestRoster, GuestSimulationEngine } from '../guestSimulation/engine';
import { workerEnvironment, type GuestSimulationWorkerRequest, type GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';
import { decodeGuestSimulationReplayState, encodeGuestSimulationReplayState } from '../guestSimulation/replayPersistence';
import { replayStateFromGuestSimulationEngine, restoreGuestSimulationEngine } from '../guestSimulation/enginePersistence';

/** Stateful worker body. Its only clock input is an explicit game-time command. */
export class GuestSimulationWorkerEngine {
  private engine: GuestSimulationEngine | null = null;
  private lastSequence = -1;

  handle(request: GuestSimulationWorkerRequest): GuestSimulationWorkerResponse {
    if (!Number.isSafeInteger(request.sequence) || request.sequence <= this.lastSequence) {
      return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'stale-sequence', message: 'Guest simulation request sequence is stale.' };
    }
    this.lastSequence = request.sequence;
    try {
      if (request.type === 'initialize') {
        if (!Number.isSafeInteger(request.guestCount) || request.guestCount <= 0 || request.guestCount > 50_000 ||
          !Number.isSafeInteger(request.startTick) || !Number.isSafeInteger(request.endTick) || request.endTick < request.startTick) {
          return { type: 'error', requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
            message: 'Guest count or simulation horizon is outside supported bounds.' };
        }
        const roster = createDailyGuestRoster({ seed: request.seed, guestCount: request.guestCount,
          portals: request.network.portals, startTick: request.startTick, endTick: request.endTick });
        this.engine = new GuestSimulationEngine({ network: request.network, roster, runId: request.runId,
          environment: workerEnvironment(request), conditionSnapshot: request.conditionSnapshot });
        return { type: 'ready', requestId: request.requestId, sequence: request.sequence,
          snapshot: this.engine.snapshot() };
      }
      if (request.type === 'restore') {
        if (!(request.bytes instanceof Uint8Array) || request.bytes.byteLength > 128 * 1024 * 1024) return { type: 'error',
          requestId: request.requestId, sequence: request.sequence, code: 'invalid-request', message: 'Invalid guest checkpoint payload.' };
        const state = decodeGuestSimulationReplayState(request.bytes);
        if (state.snapshot.topologyRevision !== request.expectedTopologyRevision) return { type: 'error', requestId: request.requestId,
          sequence: request.sequence, code: 'stale-revision', message: 'Guest checkpoint topology does not match the resort.' };
        this.engine = restoreGuestSimulationEngine(state);
        return { type: 'ready', requestId: request.requestId, sequence: request.sequence, snapshot: this.engine.snapshot() };
      }
      if (!this.engine) return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'not-initialized', message: 'Guest simulation is not initialized.' };
      const snapshot = this.engine.snapshot();
      if (request.type === 'advance' && (request.expectedEnvironmentRevision !== snapshot.environmentRevision ||
        request.expectedTopologyRevision !== snapshot.topologyRevision)) {
        return { type: 'error', requestId: request.requestId, sequence: request.sequence,
          code: 'stale-revision', message: 'Guest simulation environment or topology revision is stale.' };
      }
      if (request.type === 'checkpoint') return { type: 'checkpoint', requestId: request.requestId, sequence: request.sequence,
        snapshot, bytes: encodeGuestSimulationReplayState(replayStateFromGuestSimulationEngine(this.engine)) };
      if (request.type === 'advance' && request.conditionSnapshot && request.conditionSnapshot.tick > request.toTick) return { type: 'error',
        requestId: request.requestId, sequence: request.sequence, code: 'invalid-request',
        message: 'Condition snapshot tick cannot be later than the requested target tick.' };
      if (request.type === 'advance' && request.conditionSnapshot) this.engine.applyConditionSnapshot(request.conditionSnapshot);
      const next = request.type === 'advance' ? this.engine.advanceTo(request.toTick) : snapshot;
      return { type: request.type === 'advance' ? 'advanced' : 'snapshot', requestId: request.requestId,
        sequence: request.sequence, snapshot: next };
    } catch (error) {
      return { type: 'error', requestId: request.requestId, sequence: request.sequence,
        code: 'simulation-failed', message: error instanceof Error ? error.message : 'Guest simulation failed.' };
    }
  }
}
