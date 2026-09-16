import { createHash } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { IntegratedBenchmarkArtifact } from '../../../../src/integratedBenchmarkFixture';
import { createIntegratedFixtureTransport, readSelectedCheckpoint,
  type IntegratedBenchmarkFixtureSelection } from './integratedFixtureTransport';

const temporary: string[] = [];
afterEach(async () => { while (temporary.length) await rm(temporary.pop()!, { recursive: true, force: true }); });

function descriptor(file: string, bytes: Buffer): IntegratedBenchmarkArtifact {
  return { path: file, bytes: bytes.byteLength, sha256: createHash('sha256').update(bytes).digest('hex') };
}

describe('integrated fixture streaming transport', () => {
  it('serves verified terrain/weather bytes without loading unrelated checkpoints', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'integrated-transport-')); temporary.push(root);
    const terrain = Buffer.from('{"key":"terrain"}'), weather = Buffer.from('{"manifest":{"contentHash":"weather"}}');
    const selected = Buffer.from('{"key":"selected","dualClock":{"seed":"seed"}}');
    await Promise.all([writeFile(path.join(root, 'terrain.json'), terrain), writeFile(path.join(root, 'weather.json'), weather),
      writeFile(path.join(root, 'selected.json'), selected)]);
    const selection = { root, target: 1000, saves: {}, manifest: { requiredPresentationAssets: [], artifacts: {
      terrainRecord: descriptor('terrain.json', terrain), weatherPackage: descriptor('weather.json', weather),
    } } } as unknown as IntegratedBenchmarkFixtureSelection;
    const transport = await createIntegratedFixtureTransport(selection);
    try {
      expect(await fetch(transport.urls.terrain).then(response => response.text())).toBe(terrain.toString());
      expect(await fetch(transport.urls.weather).then(response => response.text())).toBe(weather.toString());
      expect((await readSelectedCheckpoint(root, descriptor('selected.json', selected))).key).toBe('selected');
    } finally { await transport.close(); }
    await expect(fetch(transport.urls.terrain)).rejects.toThrow();
  });

  it('rejects corrupted content before opening the transport', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'integrated-transport-corrupt-')); temporary.push(root);
    const bytes = Buffer.from('{}'); await writeFile(path.join(root, 'terrain.json'), bytes);
    const bad = { ...descriptor('terrain.json', bytes), sha256: '0'.repeat(64) };
    const selection = { root, target: 0, saves: {}, manifest: { requiredPresentationAssets: [], artifacts: {
      terrainRecord: bad, weatherPackage: descriptor('terrain.json', bytes),
    } } } as unknown as IntegratedBenchmarkFixtureSelection;
    await expect(createIntegratedFixtureTransport(selection)).rejects.toThrow('integrity failed');
  });
});
