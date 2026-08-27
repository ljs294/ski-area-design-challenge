import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { WeatherServiceError, invariant } from './errors.mjs';
import { sha256, stableJson } from './contract.mjs';

function safeSegment(value, label = 'path segment') {
  invariant(typeof value === 'string' && /^[a-z0-9][a-z0-9_.-]*$/i.test(value), 'INVALID_REQUEST', `${label} contains unsupported characters.`);
  return value;
}

async function ensureDirectory(filePath) {
  await mkdir(path.dirname(filePath), { recursive: true });
}

async function atomicWrite(filePath, data) {
  await ensureDirectory(filePath);
  const temporary = `${filePath}.${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}.tmp`;
  try {
    await writeFile(temporary, data);
    await rename(temporary, filePath);
  } catch (error) {
    await rm(temporary, { force: true }).catch(() => undefined);
    throw error;
  }
}

async function readJson(filePath) {
  try {
    return JSON.parse(await readFile(filePath, 'utf8'));
  } catch (error) {
    if (error?.code === 'ENOENT') return null;
    throw new WeatherServiceError('PACKAGE_INTEGRITY', `Unable to read cached JSON '${path.basename(filePath)}'.`, { cause: error });
  }
}

/** Persistent central cache for normalized source subsets, not raw provider files. */
export class SourceCache {
  constructor(rootDirectory) {
    this.rootDirectory = path.resolve(rootDirectory, 'sources');
    this.inFlight = new Map();
  }

  file(provider, cacheKey) {
    return path.join(this.rootDirectory, safeSegment(provider, 'provider'), `${safeSegment(cacheKey, 'cache key')}.json`);
  }

  async read(provider, cacheKey) {
    const envelope = await readJson(this.file(provider, cacheKey));
    if (!envelope || envelope.schemaVersion !== 1 || envelope.cacheKey !== cacheKey) return null;
    return envelope.value;
  }

  async write(provider, cacheKey, value) {
    const envelope = { schemaVersion: 1, provider, cacheKey, createdAt: new Date().toISOString(), value };
    await atomicWrite(this.file(provider, cacheKey), stableJson(envelope));
    return value;
  }

  async getOrCreate(provider, cacheKey, factory) {
    const cached = await this.read(provider, cacheKey);
    if (cached !== null) return { value: cached, cacheHit: true };
    const promiseKey = `${provider}:${cacheKey}`;
    let pending = this.inFlight.get(promiseKey);
    if (!pending) {
      pending = (async () => {
        const secondRead = await this.read(provider, cacheKey);
        if (secondRead !== null) return { value: secondRead, cacheHit: true };
        const value = await factory();
        await this.write(provider, cacheKey, value);
        return { value, cacheHit: false };
      })().finally(() => this.inFlight.delete(promiseKey));
      this.inFlight.set(promiseKey, pending);
    }
    return pending;
  }
}

/** Content-addressed package artifact storage.  A ready manifest is written last. */
export class PackageArtifactStore {
  constructor(rootDirectory) {
    this.rootDirectory = path.resolve(rootDirectory);
    this.packageDirectory = path.join(this.rootDirectory, 'packages');
    this.requestDirectory = path.join(this.rootDirectory, 'requests');
  }

  packagePath(contentHash) {
    return path.join(this.packageDirectory, safeSegment(contentHash, 'content hash'));
  }

  requestPath(fingerprint) {
    return path.join(this.requestDirectory, `${safeSegment(fingerprint, 'request fingerprint')}.json`);
  }

  async findByRequestFingerprint(fingerprint) {
    const pointer = await readJson(this.requestPath(fingerprint));
    if (!pointer?.contentHash) return null;
    const manifest = await this.readManifest(pointer.contentHash);
    if (!manifest?.complete) return null;
    return { contentHash: pointer.contentHash, manifest };
  }

  async readManifest(contentHash) {
    const manifest = await readJson(path.join(this.packagePath(contentHash), 'manifest.json'));
    if (!manifest) return null;
    if (!manifest.complete || manifest.immutable !== true || manifest.contentHash !== contentHash) {
      throw new WeatherServiceError('PACKAGE_INTEGRITY', 'Cached weather package manifest is incomplete or corrupt.');
    }
    return manifest;
  }

  async readChunk(contentHash, chunkId) {
    const manifest = await this.readManifest(contentHash);
    if (!manifest) return null;
    const descriptor = manifest.chunks?.find((candidate) => candidate.id === chunkId);
    if (!descriptor) return null;
    const data = await readFile(path.join(this.packagePath(contentHash), 'chunks', `${safeSegment(chunkId, 'chunk id')}.gz`));
    const checksum = sha256(data);
    if (checksum !== descriptor.checksumSha256) {
      throw new WeatherServiceError('PACKAGE_INTEGRITY', `Checksum validation failed for weather chunk ${chunkId}.`);
    }
    return { descriptor, data };
  }

  async install(fingerprint, packageArtifact) {
    const manifest = packageArtifact.manifest;
    const contentHash = safeSegment(manifest.contentHash, 'content hash');
    const root = this.packagePath(contentHash);
    const existing = await this.readManifest(contentHash);
    if (!existing) {
      for (const chunk of packageArtifact.chunks) {
        const descriptor = chunk.descriptor;
        const data = Buffer.isBuffer(chunk.data) ? chunk.data : Buffer.from(chunk.data);
        const checksum = sha256(data);
        invariant(checksum === descriptor.checksumSha256, 'PACKAGE_INTEGRITY', `Chunk ${descriptor.id} checksum does not match its descriptor.`);
        await atomicWrite(path.join(root, 'chunks', `${safeSegment(descriptor.id, 'chunk id')}.gz`), data);
      }
      // This is deliberately last: readers only accept complete manifests.
      await atomicWrite(path.join(root, 'manifest.json'), stableJson(manifest));
    }
    await atomicWrite(this.requestPath(fingerprint), stableJson({ schemaVersion: 1, contentHash, createdAt: new Date().toISOString() }));
    return manifest;
  }

  async packageWithChunks(contentHash) {
    const manifest = await this.readManifest(contentHash);
    if (!manifest) return null;
    const chunks = [];
    for (const descriptor of manifest.chunks ?? []) {
      const chunk = await this.readChunk(contentHash, descriptor.id);
      if (chunk) chunks.push({ descriptor: chunk.descriptor, dataBase64: chunk.data.toString('base64') });
    }
    return { manifest, chunks, historicalYears: [] };
  }
}

export { atomicWrite };
