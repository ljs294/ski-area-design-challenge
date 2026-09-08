import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

const root = path.resolve(process.argv[2] ?? 'public/menu-background');
const manifest = JSON.parse(await readFile(path.join(root, 'manifest.json'), 'utf8'));
if (manifest.version !== 1 || !Object.keys(manifest.files).length) throw new Error('Invalid menu background manifest');
for (const [name, expected] of Object.entries(manifest.files)) {
  if (!/^(terrain|cover|smooth-cover)\/\d+\/\d+\/\d+\.png$/.test(name)) throw new Error(`Invalid tile path: ${name}`);
  const data = await readFile(path.join(root, name));
  if (data.length !== expected.bytes || createHash('sha256').update(data).digest('hex') !== expected.sha256)
    throw new Error(`Menu tile checksum mismatch: ${name}`);
}
for (const name of ['NOTICE.md', 'TERRAIN-SOURCES.md']) await readFile(path.join(root, name));
console.log(`Verified ${Object.keys(manifest.files).length} bundled menu tiles and notices.`);
