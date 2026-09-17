import { validateTerrainPackage } from './terrainPackage';
import {
  DESIGN_SAVE_FORMAT,
  DESIGN_SAVE_SCHEMA_VERSION,
  type DesignLoadResponse,
  type DesignSaveBundle,
  type DesignSaveDocument,
  type DesignSaveDraft,
  type DesignSaveResponse,
  type DesignSaveSummary,
  type DesignTerrainGeneration,
  type DesignTerrainReference,
} from './types/designSave';

export interface DesignStorageAdapter {
  readCurrentManifest(saveKey: string): Promise<DesignSaveDocument | null>;
  readTerrainGeneration(generationId: string): Promise<DesignTerrainGeneration | null>;
  writeTerrainGeneration(generation: DesignTerrainGeneration): Promise<void>;
  /** Write the immutable manifest and atomically replace the save head. */
  publishManifestAndHead(manifest: DesignSaveDocument): Promise<void>;
  writeSummary(summary: DesignSaveSummary): Promise<void>;
  listSummaries(): Promise<DesignSaveSummary[]>;
}

export interface DesignSaveRepositoryOptions {
  createId?: () => string;
  now?: () => string;
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function summaryOf(save: DesignSaveDocument): DesignSaveSummary {
  return { key: save.key, name: save.name, createdAt: save.createdAt, updatedAt: save.updatedAt,
    revisions: save.revisions, terrain: save.terrain };
}

function validateDraft(draft: DesignSaveDraft): string | null {
  if (draft.format !== DESIGN_SAVE_FORMAT || draft.schemaVersion !== DESIGN_SAVE_SCHEMA_VERSION) {
    return 'Unsupported design save format.';
  }
  if (!draft.key || !draft.name.trim()) return 'Design identity and name are required.';
  if (draft.terrainRecord.key.length === 0) return 'Terrain identity is required.';
  const validation = validateTerrainPackage(draft.terrainRecord);
  return validation.ok ? null : validation.errors.join(' ');
}

function terrainReference(generation: DesignTerrainGeneration): DesignTerrainReference {
  return Object.freeze({
    logicalKey: generation.logicalKey,
    generationId: generation.generationId,
    sourceRevision: generation.sourceRevision,
    updatedAt: generation.record.updatedAt,
    elevationChecksum: generation.record.packageManifest?.elevationChecksum,
    coverChecksum: generation.record.coverMetadata?.checksum,
  });
}

/** Serialized, recoverable publication of a design manifest and the exact
 * immutable terrain generation it references. Secondary summaries are never
 * authoritative and cannot reverse an already-published save. */
export class DesignSaveRepository {
  private readonly storage: DesignStorageAdapter;
  private readonly queues = new Map<string, Promise<void>>();
  private readonly createId: () => string;
  private readonly now: () => string;

  constructor(storage: DesignStorageAdapter, options: DesignSaveRepositoryOptions = {}) {
    this.storage = storage;
    this.createId = options.createId ?? (() => crypto.randomUUID());
    this.now = options.now ?? (() => new Date().toISOString());
  }

  save(draft: DesignSaveDraft): Promise<DesignSaveResponse> {
    const captured = structuredClone(draft);
    const previous = this.queues.get(captured.key) ?? Promise.resolve();
    let release!: () => void;
    const queued = new Promise<void>((resolve) => { release = resolve; });
    const tail = previous.catch(() => undefined).then(() => queued);
    this.queues.set(captured.key, tail);
    return previous.catch(() => undefined).then(() => this.saveCaptured(captured)).finally(() => {
      release();
      if (this.queues.get(captured.key) === tail) this.queues.delete(captured.key);
    });
  }

  async load(key: string): Promise<DesignLoadResponse> {
    try {
      const save = await this.storage.readCurrentManifest(key);
      if (!save) return null;
      if (save.format !== DESIGN_SAVE_FORMAT || save.schemaVersion !== DESIGN_SAVE_SCHEMA_VERSION) {
        return { ok: false, error: 'Unsupported design save format.' };
      }
      const generation = await this.storage.readTerrainGeneration(save.terrain.generationId);
      if (!generation || generation.logicalKey !== save.terrain.logicalKey ||
          generation.sourceRevision !== save.terrain.sourceRevision) {
        return { ok: false, error: 'The design terrain generation is missing or mismatched.' };
      }
      const validation = validateTerrainPackage(generation.record);
      if (!validation.ok) return { ok: false, error: validation.errors.join(' ') };
      return { ok: true, bundle: { save, terrain: generation.record } };
    } catch (error) {
      return { ok: false, error: messageOf(error) };
    }
  }

  list(): Promise<DesignSaveSummary[]> {
    return this.storage.listSummaries();
  }

  private async saveCaptured(draft: DesignSaveDraft): Promise<DesignSaveResponse> {
    const invalid = validateDraft(draft);
    if (invalid) return { ok: false, error: invalid };
    try {
      const current = await this.storage.readCurrentManifest(draft.key);
      let generation: DesignTerrainGeneration | null = null;
      if (current?.terrain.logicalKey === draft.terrainRecord.key &&
          current.terrain.sourceRevision === draft.revisions.terrain) {
        generation = await this.storage.readTerrainGeneration(current.terrain.generationId);
      }
      if (!generation) {
        const id = `${draft.terrainRecord.key}-${draft.revisions.terrain}-${this.createId()}`;
        generation = {
          format: 'mountain-planner-three-terrain-generation', schemaVersion: 1,
          generationId: id, logicalKey: draft.terrainRecord.key,
          sourceRevision: draft.revisions.terrain, createdAt: this.now(),
          record: draft.terrainRecord,
        };
        await this.storage.writeTerrainGeneration(generation);
        const verified = await this.storage.readTerrainGeneration(id);
        if (!verified || verified.logicalKey !== generation.logicalKey ||
            verified.sourceRevision !== generation.sourceRevision) {
          throw new Error('Terrain generation verification failed.');
        }
        generation = verified;
      }
      if (generation.logicalKey !== draft.terrainRecord.key ||
          generation.sourceRevision !== draft.revisions.terrain) {
        throw new Error('Terrain generation verification failed.');
      }
      const verification = validateTerrainPackage(generation.record);
      if (!verification.ok) throw new Error(verification.errors.join(' '));
      const { terrainRecord: _terrainRecord, ...document } = draft;
      const manifest: DesignSaveDocument = {
        ...document,
        manifestId: `${draft.key}-${draft.revisions.design}-${this.createId()}`,
        terrain: terrainReference(generation),
      };
      await this.storage.publishManifestAndHead(manifest);
      const warnings: string[] = [];
      try { await this.storage.writeSummary(summaryOf(manifest)); }
      catch (error) { warnings.push(`Saved, but the library index was not updated: ${messageOf(error)}`); }
      return { ok: true, receipt: { key: manifest.key, manifestId: manifest.manifestId,
        terrainGenerationId: generation.generationId, revisions: manifest.revisions, warnings } };
    } catch (error) {
      return { ok: false, error: messageOf(error) };
    }
  }
}

export function bundleOf(save: DesignSaveDocument, terrain: DesignTerrainGeneration): DesignSaveBundle {
  return { save, terrain: terrain.record };
}
