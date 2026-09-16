import { createHash } from 'node:crypto';
import { createReadStream, statSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import path from 'node:path';
import type { IntegratedBenchmarkArtifact, IntegratedBenchmarkFixtureManifest } from '../../../../src/integratedBenchmarkFixture';
import type { GameSave } from '../../../../src/types/gameSave';

export interface IntegratedBenchmarkFixtureSelection {
  root: string;
  manifest: IntegratedBenchmarkFixtureManifest;
  saves: Partial<Record<0 | 1000 | 3000, GameSave>>;
  target: 0 | 1000 | 3000;
}

async function sha256File(file: string): Promise<string> {
  const hash = createHash('sha256'), stream = createReadStream(file);
  for await (const chunk of stream) hash.update(chunk);
  return hash.digest('hex');
}

function artifactFile(root: string, artifact: IntegratedBenchmarkArtifact): string {
  const file = path.resolve(root, artifact.path);
  if (!file.startsWith(path.resolve(root) + path.sep)) throw new Error(`Fixture artifact escapes its root: ${artifact.path}`);
  return file;
}

async function verify(root: string, artifact: IntegratedBenchmarkArtifact): Promise<string> {
  const file = artifactFile(root, artifact), info = statSync(file);
  if (info.size !== artifact.bytes || await sha256File(file) !== artifact.sha256) {
    throw new Error(`Fixture artifact integrity failed: ${artifact.path}`);
  }
  return file;
}

export async function createIntegratedFixtureTransport(selection: IntegratedBenchmarkFixtureSelection): Promise<{
  origin: string; urls: { terrain: string; weather: string; imagery?: string }; close(): Promise<void>;
}> {
  const { root, manifest } = selection;
  const files = {
    terrain: await verify(root, manifest.artifacts.terrainRecord),
    weather: await verify(root, manifest.artifacts.weatherPackage),
    ...(manifest.artifacts.imagery ? { imagery: await verify(root, manifest.artifacts.imagery) } : {}),
  };
  for (const asset of manifest.requiredPresentationAssets) await verify(root, asset);
  let server: Server;
  const routes = new Map(Object.entries(files).map(([name, file]) => [`/${name}`, file]));
  server = createServer((request, response) => {
    const file = routes.get(new URL(request.url ?? '/', 'http://127.0.0.1').pathname);
    if (!file) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'access-control-allow-origin': '*', 'cache-control': 'no-store',
      'content-type': file.endsWith('.jpg') ? 'image/jpeg' : 'application/json' });
    createReadStream(file).pipe(response);
  });
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject); server.listen({ host: '127.0.0.1', port: 0, exclusive: true }, resolve);
  });
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Fixture transport did not bind a loopback port.');
  const origin = `http://127.0.0.1:${address.port}`;
  return { origin, urls: { terrain: `${origin}/terrain`, weather: `${origin}/weather`,
    ...(files.imagery ? { imagery: `${origin}/imagery` } : {}) },
  close: () => new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve())) };
}

export async function readSelectedCheckpoint(root: string, artifact: IntegratedBenchmarkArtifact): Promise<GameSave> {
  const file = await verify(root, artifact);
  try { return JSON.parse(await readFile(file, 'utf8')) as GameSave; }
  catch { throw new Error(`Fixture checkpoint is not valid JSON: ${artifact.path}`); }
}
