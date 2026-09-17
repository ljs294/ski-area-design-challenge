import { buildSkiNetwork, type SkiNetwork } from '../../network';
import type { SavedLift } from '../../types/lifts';
import type { DeepReadonly } from '../../types/readonly';
import type { TerrainRecord } from '../../types/terrain';
import { commitDocuments, type CommittedDocumentResult } from '../committedDocumentTransaction';
import { TerrainDocument, type TerrainCommitRequest, type TerrainDocumentPorts,
  type TerrainPublicationFailure, type TerrainSnapshot } from '../terrainDocument';
import { TopologyDocument, topologyProjection, type TopologyChange,
  type TopologyPublicationFailure, type TopologySnapshot, type TopologyState,
  type TopologyTransaction } from '../topologyDocument';

export interface DesignSessionIdentity {
  readonly id: string;
  readonly terrainKey: string | null;
}

export interface DesignSessionCapabilities {
  readonly terrain: true;
  readonly lifts: true;
  readonly trails: true;
  readonly nodesAndPaths: true;
  readonly simulation: false;
  readonly weather: false;
}

export const DESIGN_SESSION_CAPABILITIES: DesignSessionCapabilities = Object.freeze({
  terrain: true,
  lifts: true,
  trails: true,
  nodesAndPaths: true,
  simulation: false,
  weather: false,
});

export type DesignSessionChangeKind = 'terrain' | 'topology' | 'lifts';

export interface DesignSessionFailure {
  readonly source: 'terrain' | 'topology' | 'session';
  readonly target: string;
  readonly revision: number;
  readonly message: string;
}

export interface DesignSessionSnapshot {
  readonly identity: DesignSessionIdentity;
  readonly revision: number;
  readonly terrain: TerrainSnapshot;
  readonly liftRevision: number;
  readonly lifts: readonly DeepReadonly<SavedLift>[];
  readonly topology: TopologySnapshot;
  /** Derived from the committed lift/topology models; never persisted. */
  readonly network: SkiNetwork;
  readonly capabilities: DesignSessionCapabilities;
}

/** Coherent source data for P1-B persistence. Camera, renderer, simulation and
 * React state are deliberately absent. All arrays remain session-owned views. */
export interface DesignPersistenceSnapshot {
  readonly identity: DesignSessionIdentity;
  readonly revision: number;
  readonly terrainRevision: number;
  readonly topologyRevision: number;
  readonly liftRevision: number;
  readonly terrain: TerrainSnapshot['record'];
  readonly lifts: readonly DeepReadonly<SavedLift>[];
  readonly trails: TopologySnapshot['trails'];
  readonly nodes: TopologySnapshot['nodes'];
  readonly paths: TopologySnapshot['paths'];
  readonly junctions: TopologySnapshot['junctions'];
}

export interface DesignSessionPorts {
  readonly terrain: TerrainDocumentPorts;
  publishTopology?(change: TopologyChange): void;
  publishLifts?(lifts: readonly DeepReadonly<SavedLift>[], revision: number): void;
  publishSession?(snapshot: DesignSessionSnapshot, kind: DesignSessionChangeKind): void;
  publishFailure?(failure: DesignSessionFailure): void;
}

export interface DesignSessionOptions {
  readonly identity: DesignSessionIdentity;
  readonly topology: TopologyState;
  readonly lifts: readonly SavedLift[];
  readonly terrain?: TerrainRecord | null;
  readonly ports: DesignSessionPorts;
}

export type LiftCommitResult =
  | { ok: true; revision: number; changed: boolean }
  | { ok: false; reason: 'stale' };

export interface DesignSessionReadPort {
  snapshot(): DesignSessionSnapshot;
  persistenceSnapshot(): DesignPersistenceSnapshot;
  publicationFailures(): readonly DesignSessionFailure[];
}

export interface DesignSessionCommandPort {
  beginTopology(): TopologyTransaction;
  commitDocuments(topology: TopologyTransaction, terrainCommit?: TerrainCommitRequest): CommittedDocumentResult;
  commitLifts(expectedRevision: number, lifts: readonly SavedLift[]): LiftCommitResult;
  addLift(lift: SavedLift): LiftCommitResult;
  patchLift(id: string, patch: Partial<SavedLift>): LiftCommitResult;
  removeLift(id: string): LiftCommitResult;
}

function ownedLifts(lifts: readonly SavedLift[]): readonly SavedLift[] {
  const owned = structuredClone(lifts);
  for (const lift of owned) {
    Object.freeze(lift.points[0]); Object.freeze(lift.points[1]);
    Object.freeze(lift.points); Object.freeze(lift.endpointElevM); Object.freeze(lift);
  }
  return Object.freeze(owned);
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Renderer-independent owner of the first-release committed design.
 * Documents retain their specialized validation/transaction behavior; this
 * owner supplies stable identity, lift ownership, derived reads and coherent
 * persistence snapshots without importing React, MapLibre or Three.js. */
export class DesignSession {
  readonly terrain: TerrainDocument;
  readonly topology: TopologyDocument;
  readonly capabilities = DESIGN_SESSION_CAPABILITIES;
  readonly read: DesignSessionReadPort;
  readonly commands: DesignSessionCommandPort;
  private readonly ports: DesignSessionPorts;
  private currentIdentity: DesignSessionIdentity;
  private designRevision = 0;
  private liftRevision = 0;
  private currentLifts: readonly SavedLift[];
  private currentNetwork: SkiNetwork;
  private failures: readonly DesignSessionFailure[] = [];

  constructor(options: DesignSessionOptions) {
    this.currentIdentity = Object.freeze({ ...options.identity });
    this.ports = options.ports;
    this.currentLifts = ownedLifts(options.lifts);
    this.currentNetwork = buildSkiNetwork([], this.mutableLiftProjection());
    this.topology = new TopologyDocument(options.topology,
      (change) => this.topologyPublished(change),
      (failure) => this.publicationFailed(failure));
    this.rebuildNetwork();
    const terrainPorts = options.ports.terrain;
    this.terrain = new TerrainDocument({
      cacheDisplayAssets: (record) => terrainPorts.cacheDisplayAssets(record),
      activateProtocols: (record) => terrainPorts.activateProtocols(record),
      publishState: (publication) => {
        try { terrainPorts.publishState(publication); }
        finally { this.documentPublished('terrain'); }
      },
      refreshSources: (publication) => terrainPorts.refreshSources(publication),
      publishPersisted: () => terrainPorts.publishPersisted(),
      publishConstruction: (activity) => terrainPorts.publishConstruction(activity),
      reportPublicationFailure: (failure) => {
        this.publicationFailed(failure);
        terrainPorts.reportPublicationFailure?.(failure);
      },
    });
    if (options.terrain) this.terrain.replace(options.terrain);
    const read: DesignSessionReadPort = {
      snapshot: () => this.snapshot(),
      persistenceSnapshot: () => this.persistenceSnapshot(),
      publicationFailures: () => this.publicationFailures(),
    };
    this.read = Object.freeze(read);
    const commands: DesignSessionCommandPort = {
      beginTopology: () => this.topology.begin(),
      commitDocuments: (topology, terrainCommit) => this.commitDocuments(topology, terrainCommit),
      commitLifts: (expectedRevision, lifts) => this.commitLifts(expectedRevision, lifts),
      addLift: (lift) => this.addLift(lift),
      patchLift: (id, patch) => this.patchLift(id, patch),
      removeLift: (id) => this.removeLift(id),
    };
    this.commands = Object.freeze(commands);
  }

  get identity(): DesignSessionIdentity {
    return this.currentIdentity;
  }

  snapshot(): DesignSessionSnapshot {
    return Object.freeze({
      identity: this.identity,
      revision: this.designRevision,
      terrain: this.terrain.snapshot(),
      liftRevision: this.liftRevision,
      lifts: this.currentLifts,
      topology: this.topology.snapshot(),
      network: this.currentNetwork,
      capabilities: this.capabilities,
    });
  }

  persistenceSnapshot(): DesignPersistenceSnapshot {
    const snapshot = this.snapshot();
    return Object.freeze({
      identity: snapshot.identity,
      revision: snapshot.revision,
      terrainRevision: snapshot.terrain.revision,
      topologyRevision: snapshot.topology.revision,
      liftRevision: snapshot.liftRevision,
      terrain: snapshot.terrain.record,
      lifts: snapshot.lifts,
      trails: snapshot.topology.trails,
      nodes: snapshot.topology.nodes,
      paths: snapshot.topology.paths,
      junctions: snapshot.topology.junctions,
    });
  }

  publicationFailures(): readonly DesignSessionFailure[] {
    return this.failures;
  }

  commitDocuments(topology: TopologyTransaction, terrainCommit?: TerrainCommitRequest): CommittedDocumentResult {
    return commitDocuments({ terrain: this.terrain, topology, terrainCommit });
  }

  commitLifts(expectedRevision: number, lifts: readonly SavedLift[]): LiftCommitResult {
    if (expectedRevision !== this.liftRevision) return { ok: false, reason: 'stale' };
    const next = ownedLifts(lifts);
    const changed = next.length !== this.currentLifts.length
      || next.some((lift, index) => JSON.stringify(lift) !== JSON.stringify(this.currentLifts[index]));
    if (!changed) return { ok: true, revision: this.liftRevision, changed: false };
    this.currentLifts = next;
    this.liftRevision++;
    this.rebuildNetwork();
    this.designRevision++;
    this.safePublish('publishLifts', () => this.ports.publishLifts?.(this.currentLifts, this.liftRevision));
    this.safePublish('publishSession', () => this.ports.publishSession?.(this.snapshot(), 'lifts'));
    return { ok: true, revision: this.liftRevision, changed: true };
  }

  addLift(lift: SavedLift): LiftCommitResult {
    return this.commitLifts(this.liftRevision, [...this.currentLifts, lift] as SavedLift[]);
  }

  patchLift(id: string, patch: Partial<SavedLift>): LiftCommitResult {
    return this.commitLifts(this.liftRevision, this.currentLifts.map((lift) =>
      lift.id === id ? { ...lift, ...patch } as SavedLift : lift as SavedLift));
  }

  removeLift(id: string): LiftCommitResult {
    return this.commitLifts(this.liftRevision,
      this.currentLifts.filter((lift) => lift.id !== id) as SavedLift[]);
  }

  updateLifts(update: (lifts: SavedLift[]) => SavedLift[]): LiftCommitResult {
    return this.commitLifts(this.liftRevision, update(this.mutableLiftProjection()));
  }

  dispose(): void {
    this.terrain.dispose();
  }

  private topologyPublished(change: TopologyChange): void {
    this.rebuildNetwork();
    this.designRevision++;
    this.safePublish('publishTopology', () => this.ports.publishTopology?.(change));
    this.safePublish('publishSession', () => this.ports.publishSession?.(this.snapshot(), 'topology'));
  }

  private documentPublished(kind: 'terrain'): void {
    const terrainKey = this.terrain.snapshot().record?.key ?? this.currentIdentity.terrainKey;
    if (terrainKey !== this.currentIdentity.terrainKey) {
      this.currentIdentity = Object.freeze({ id: this.currentIdentity.id, terrainKey });
    }
    this.designRevision++;
    this.safePublish('publishSession', () => this.ports.publishSession?.(this.snapshot(), kind));
  }

  private rebuildNetwork(): void {
    const topology = topologyProjection(this.topology.snapshot());
    this.currentNetwork = buildSkiNetwork(topology.trails, this.mutableLiftProjection(), {
      nodes: topology.nodes, paths: topology.paths, junctions: topology.junctions,
    });
  }

  private mutableLiftProjection(): SavedLift[] {
    return structuredClone(this.currentLifts) as SavedLift[];
  }

  private safePublish(target: string, publish: () => void): void {
    try { publish(); }
    catch (error) { this.recordFailure('session', target, this.designRevision, error); }
  }

  private publicationFailed(failure: TerrainPublicationFailure | TopologyPublicationFailure): void {
    this.recordFailure(failure.document, failure.target, failure.revision, failure.error);
  }

  private recordFailure(source: DesignSessionFailure['source'], target: string,
    revision: number, error: unknown): void {
    const failure = Object.freeze({ source, target, revision,
      message: messageOf(error) }) satisfies DesignSessionFailure;
    this.failures = Object.freeze([...this.failures, failure]);
    try { this.ports.publishFailure?.(failure); }
    catch { /* Status reporting remains non-authoritative. */ }
  }
}
