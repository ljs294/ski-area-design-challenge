import fs from 'fs';
import path from 'path';
import type { DesignStorageAdapter } from '../src/designSaveRepository';
import { parseDesignStorage, stringifyDesignStorage } from '../src/designSaveSerialization';
import type { DesignSaveDocument, DesignSaveSummary, DesignTerrainGeneration } from '../src/types/designSave';

interface DesignHead { saveKey: string; manifestId: string }

function safeSegment(value: string): string {
  return Buffer.from(value, 'utf8').toString('base64url');
}

function readJson<T>(file: string): T | null {
  try { return parseDesignStorage<T>(fs.readFileSync(file, 'utf8')); }
  catch { return null; }
}

function atomicWrite(file: string, value: unknown): void {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.${Date.now()}.tmp`;
  const displaced = `${file}.${process.pid}.${Date.now()}.old`;
  fs.writeFileSync(temporary, stringifyDesignStorage(value), { encoding: 'utf8', flag: 'wx' });
  let movedPrior = false;
  try {
    if (fs.existsSync(file)) { fs.renameSync(file, displaced); movedPrior = true; }
    fs.renameSync(temporary, file);
    if (movedPrior) {
      try { fs.rmSync(displaced, { force: true }); } catch { /* stale cleanup must not reverse publication */ }
    }
  } catch (error) {
    try { fs.rmSync(temporary, { force: true }); } catch { /* best effort */ }
    if (movedPrior && !fs.existsSync(file)) {
      try { fs.renameSync(displaced, file); } catch { /* previous head remains recoverable separately */ }
    }
    throw error;
  }
}

export class FileDesignStorage implements DesignStorageAdapter {
  private readonly root: string;

  constructor(root: string) { this.root = root; }

  async readCurrentManifest(saveKey: string): Promise<DesignSaveDocument | null> {
    const current = this.readHead(this.headPath(saveKey));
    const head = current ?? this.readHead(this.previousHeadPath(saveKey));
    return head ? readJson<DesignSaveDocument>(this.manifestPath(head.manifestId)) : null;
  }
  async readTerrainGeneration(generationId: string): Promise<DesignTerrainGeneration | null> {
    return readJson<DesignTerrainGeneration>(this.generationPath(generationId));
  }
  async writeTerrainGeneration(generation: DesignTerrainGeneration): Promise<void> {
    const file = this.generationPath(generation.generationId);
    if (fs.existsSync(file)) throw new Error('Terrain generation already exists.');
    atomicWrite(file, generation);
  }
  async publishManifestAndHead(manifest: DesignSaveDocument): Promise<void> {
    const manifestFile = this.manifestPath(manifest.manifestId);
    if (fs.existsSync(manifestFile)) throw new Error('Design manifest already exists.');
    atomicWrite(manifestFile, manifest);
    const headFile = this.headPath(manifest.key);
    const prior = readJson<DesignHead>(headFile);
    if (prior) atomicWrite(this.previousHeadPath(manifest.key), prior);
    atomicWrite(headFile, { saveKey: manifest.key, manifestId: manifest.manifestId } satisfies DesignHead);
  }
  async writeSummary(summary: DesignSaveSummary): Promise<void> {
    const summaries = (await this.listIndex()).filter((entry) => entry.key !== summary.key);
    summaries.push(summary);
    atomicWrite(this.indexPath(), summaries);
  }
  async listSummaries(): Promise<DesignSaveSummary[]> {
    const summaries = new Map((await this.listIndex()).map((summary) => [summary.key, summary]));
    const directory = path.join(this.root, 'heads');
    if (!fs.existsSync(directory)) return [...summaries.values()];
    for (const entry of fs.readdirSync(directory)) {
      if (!entry.endsWith('.head.json')) continue;
      const head = readJson<DesignHead>(path.join(directory, entry));
      if (!head) continue;
      const save = readJson<DesignSaveDocument>(this.manifestPath(head.manifestId));
      if (save) summaries.set(save.key, { key: save.key, name: save.name,
        createdAt: save.createdAt, updatedAt: save.updatedAt,
        revisions: save.revisions, terrain: save.terrain });
    }
    return [...summaries.values()];
  }

  private readHead(file: string): DesignHead | null {
    const head = readJson<DesignHead>(file);
    if (!head || !readJson<DesignSaveDocument>(this.manifestPath(head.manifestId))) return null;
    return head;
  }
  private async listIndex(): Promise<DesignSaveSummary[]> {
    return readJson<DesignSaveSummary[]>(this.indexPath()) ?? [];
  }
  private generationPath(id: string): string {
    return path.join(this.root, 'terrain-generations', `${safeSegment(id)}.json`);
  }
  private manifestPath(id: string): string {
    return path.join(this.root, 'manifests', `${safeSegment(id)}.json`);
  }
  private headPath(key: string): string {
    return path.join(this.root, 'heads', `${safeSegment(key)}.head.json`);
  }
  private previousHeadPath(key: string): string {
    return path.join(this.root, 'heads', `${safeSegment(key)}.previous.json`);
  }
  private indexPath(): string { return path.join(this.root, 'index.json'); }
}
