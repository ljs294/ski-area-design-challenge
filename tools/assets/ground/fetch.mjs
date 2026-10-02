// Downloads the ground textures listed in sources.json (CC0, Poly Haven) into cache/<id>/: the 2k colour
// (Diffuse, jpg), OpenGL normal (nor_gl, jpg) and height (Displacement, png). Already-downloaded files are kept.
// Usage: node tools/assets/ground/fetch.mjs
import { readFile, mkdir, writeFile, stat } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const sources = JSON.parse(await readFile(join(here, 'sources.json'), 'utf8'));
const maps = [['Diffuse', 'jpg', 'colour'], ['nor_gl', 'jpg', 'normal'], ['Displacement', 'png', 'height']];

for (const asset of sources.assets) {
  const files = await (await fetch(`https://api.polyhaven.com/files/${asset.id}`)).json();
  await mkdir(join(here, 'cache', asset.id), { recursive: true });
  for (const [map, format, name] of maps) {
    const entry = files[map]?.[sources.resolution]?.[format];
    if (!entry) throw new Error(`${asset.id}: no ${map} ${sources.resolution} ${format}`);
    const out = join(here, 'cache', asset.id, `${name}.${format}`);
    try { if ((await stat(out)).size === entry.size) { console.log(`kept ${asset.id}/${name}`); continue; } } catch { }
    const bytes = Buffer.from(await (await fetch(entry.url)).arrayBuffer());
    if (bytes.length !== entry.size) throw new Error(`${asset.id}/${name}: ${bytes.length} bytes, expected ${entry.size}`);
    await writeFile(out, bytes);
    console.log(`fetched ${asset.id}/${name} (${(bytes.length / 1048576).toFixed(1)} MB)`);
  }
}
