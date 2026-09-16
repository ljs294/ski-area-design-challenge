import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { request } from 'node:http';
import path from 'node:path';

const viteCli = path.resolve('node_modules/vite/bin/vite.js');

export async function startStaticProductionServer(directory: string, port: number): Promise<ChildProcess> {
  const server = spawn(process.execPath, [viteCli, 'preview', '--outDir', directory, '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
    cwd: process.cwd(), stdio: 'ignore', windowsHide: true,
  });
  server.unref();
  const url = `http://127.0.0.1:${port}/`;
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      const ok = await probe(url);
      if (ok) return server;
    } catch { /* wait for Vite to bind */ }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  server.kill();
  throw new Error(`Static production server did not start on port ${port}.`);
}

function probe(url: string): Promise<boolean> {
  return new Promise((resolve) => {
    const req = request(url, { agent: false }, (response) => {
      response.resume();
      response.once('end', () => resolve(response.statusCode !== undefined && response.statusCode >= 200 && response.statusCode < 400));
    });
    req.once('error', () => resolve(false));
    req.end();
  });
}

export async function stopStaticProductionServer(server: ChildProcess): Promise<void> {
  if (server.exitCode !== null) return;
  if (process.platform === 'win32' && server.pid) {
    spawnSync('taskkill', ['/PID', String(server.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
  } else server.kill();
  server.removeAllListeners();
}

/** Stop the Playwright preview server after a multi-origin benchmark. */
export function stopPreviewServerOnPort(port: number): void {
  if (process.platform !== 'win32') return;
  const output = spawnSync('netstat', ['-ano'], { encoding: 'utf8', windowsHide: true }).stdout ?? '';
  for (const line of output.split(/\r?\n/)) {
    if (!line.includes(`127.0.0.1:${port}`) || !/LISTENING/i.test(line)) continue;
    const match = line.trim().match(/(\d+)$/);
    const pid = match ? Number(match[1]) : 0;
    if (pid > 0 && pid !== process.pid) {
      try { process.kill(pid); } catch { /* the server may have exited already */ }
    }
  }
}

export function verifiedDescendant(root: string, candidate: string): boolean {
  const relative = path.relative(path.resolve(root), path.resolve(candidate));
  return relative.length > 0 && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}
