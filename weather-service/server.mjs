// Local weather-package cache/proxy. It intentionally has no fallback
// generator: an upstream archive builder must return a complete, provenance
// carrying package before a map can become available offline.
import { createServer } from 'node:http';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';

const port = Number(process.env.WEATHER_SERVICE_PORT || 8787);
const upstream = process.env.WEATHER_SOURCE_URL;
const cacheDir = path.resolve(process.env.WEATHER_CACHE_DIR || '.weather-cache');
await mkdir(cacheDir, { recursive: true });

function send(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
  response.end(JSON.stringify(body));
}

function safeFile(terrainKey) {
  if (typeof terrainKey !== 'string' || !/^[a-z0-9][a-z0-9_.-]*$/i.test(terrainKey)) return null;
  return path.join(cacheDir, `${terrainKey}.json`);
}

async function readBody(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

const server = createServer(async (request, response) => {
  if (request.method === 'OPTIONS') return send(response, 204, {});
  if (request.method === 'GET' && request.url === '/health') return send(response, 200, { ok: true, upstreamConfigured: Boolean(upstream) });
  if (request.method !== 'POST' || request.url !== '/v1/weather-packages') return send(response, 404, { error: 'Not found.' });
  try {
    const requestBody = await readBody(request);
    const file = safeFile(requestBody.terrainKey);
    if (!file) return send(response, 400, { error: 'Invalid terrain key.' });
    try {
      const cached = JSON.parse(await readFile(file, 'utf8'));
      if (cached?.manifest?.complete === true) return send(response, 200, cached);
    } catch { /* The cache simply has no completed package yet. */ }
    if (!upstream) return send(response, 503, {
      error: 'No WEATHER_SOURCE_URL is configured. Configure an ERA5/Daymet/NOAA package builder before preparing maps.',
    });
    const upstreamResponse = await fetch(`${upstream.replace(/\/$/, '')}/v1/weather-packages`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(requestBody),
    });
    if (!upstreamResponse.ok) return send(response, upstreamResponse.status, { error: `Upstream weather builder returned ${upstreamResponse.status}.` });
    const weatherPackage = await upstreamResponse.json();
    if (weatherPackage?.manifest?.complete !== true || weatherPackage.manifest.terrainKey !== requestBody.terrainKey) {
      return send(response, 502, { error: 'Upstream returned an incomplete or mismatched weather package.' });
    }
    const temporary = `${file}.${process.pid}-${Date.now()}.tmp`;
    await writeFile(temporary, JSON.stringify(weatherPackage), 'utf8');
    await rename(temporary, file);
    return send(response, 200, weatherPackage);
  } catch (error) {
    return send(response, 500, { error: error instanceof Error ? error.message : 'Weather package preparation failed.' });
  }
});

server.listen(port, '127.0.0.1', () => {
  process.stdout.write(`Weather service listening at http://127.0.0.1:${port}\n`);
});
