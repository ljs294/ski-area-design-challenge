import maplibregl from 'maplibre-gl';

interface BackgroundManifest {
  version: number;
  files: Record<string, { bytes: number; sha256: string }>;
}
const base = () => new URL(`${import.meta.env.BASE_URL}menu-background/`, window.location.href);
let manifest: Promise<BackgroundManifest> | undefined;
let registered = false;

export function registerMenuBackgroundProtocol(): void {
  if (registered) return;
  registered = true;
  maplibregl.addProtocol('menu-background', async (params, abort) => {
    manifest ??= fetch(new URL('manifest.json', base())).then(async (response) => {
      if (!response.ok) throw new Error('Bundled menu manifest is missing');
      const value = await response.json() as BackgroundManifest;
      if (value.version !== 1 || !value.files) throw new Error('Invalid menu manifest');
      return value;
    }).catch((error) => { manifest = undefined; throw error; });
    const { files } = await manifest;
    const match = /^menu-background:\/\/(terrain|cover)\/(\d+)\/(\d+)\/(\d+)$/.exec(params.url);
    if (!match) throw new Error('Invalid bundled menu tile request');
    const [, requestedKind, zs, xs, ys] = match;
    const kind = requestedKind === 'cover' ? 'smooth-cover' : 'terrain';
    const z = Number(zs), x = Number(xs), y = Number(ys);
    let level = z, tx = x, ty = y;
    while (level > 0 && !files[`${kind}/${level}/${tx}/${ty}.png`]) {
      level--; tx = Math.floor(tx / 2); ty = Math.floor(ty / 2);
    }
    const name = `${kind}/${level}/${tx}/${ty}.png`;
    const entry = files[name];
    if (!entry) throw new Error(`Bundled menu tile unavailable: ${name}`);
    const response = await fetch(new URL(name, base()), { signal: abort.signal });
    if (!response.ok) throw new Error(`Bundled menu tile missing: ${name}`);
    const data = await response.arrayBuffer();
    const digest = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', data)),
      (byte) => byte.toString(16).padStart(2, '0')).join('');
    if (data.byteLength !== entry.bytes || digest !== entry.sha256) throw new Error(`Corrupt bundled menu tile: ${name}`);
    if (level === z) return { data };
    const bitmap = await createImageBitmap(new Blob([data], { type: 'image/png' }));
    try {
      const factor = 2 ** (z - level), size = bitmap.width / factor;
      const canvas = new OffscreenCanvas(256, 256), ctx = canvas.getContext('2d')!;
      ctx.imageSmoothingEnabled = kind !== 'terrain';
      ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(bitmap, (x - tx * factor) * size, (y - ty * factor) * size, size, size, 0, 0, 256, 256);
      return { data: await (await canvas.convertToBlob({ type: 'image/png' })).arrayBuffer() };
    } finally { bitmap.close(); }
  });
}
