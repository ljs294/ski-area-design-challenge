// A minimal headless-Edge client over the DevTools protocol (Node 22's own WebSocket, no dependencies), after the
// pattern in tools/ui-parity/parity.mjs. Used by the brand tools to render boards and to measure marks.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';

export const EDGE = [
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].find((p) => existsSync(p));

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export async function launch() {
  if (!EDGE) throw new Error('Microsoft Edge was not found');
  const profile = mkdtempSync(path.join(os.tmpdir(), 'brand-edge-'));   // a fresh profile, so nothing is cached
  const port = 9800 + Math.floor(Math.random() * 500);
  const proc = spawn(EDGE, ['--headless', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run',
    '--no-default-browser-check', '--hide-scrollbars', '--force-color-profile=srgb', '--disable-gpu', 'about:blank'], { stdio: 'ignore' });
  let page = null;
  for (let i = 0; i < 100 && !page; i++) {
    await sleep(150);
    try { page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === 'page'); } catch { /* not up yet */ }
  }
  if (!page) { proc.kill(); throw new Error('Edge did not open its DevTools port'); }
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const pending = new Map(), waiters = [];
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(msg.error.message)); else resolve(msg.result);
    } else if (msg.method) for (const w of waiters.splice(0)) if (w.method === msg.method) w.resolve(msg.params); else waiters.push(w);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => { const n = ++id; pending.set(n, { resolve, reject }); ws.send(JSON.stringify({ id: n, method, params })); });
  const once = (method) => new Promise((resolve) => waiters.push({ method, resolve }));
  const close = async () => {
    try { ws.close(); } catch { /* closed */ }
    const exited = new Promise((r) => proc.once('exit', r));
    // Killing Edge's browser process leaves its renderer and utility children running on Windows: end the whole tree.
    if (process.platform === 'win32') spawn('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { stdio: 'ignore' });
    else proc.kill();
    await Promise.race([exited, sleep(3000)]);
    for (let i = 0; i < 5; i++) {   // Edge's helpers let go of the profile a moment after it exits
      try { rmSync(profile, { recursive: true, force: true }); break; } catch { await sleep(500); }
    }
  };
  await send('Page.enable');
  await send('Runtime.enable');
  return { send, once, close };
}

export async function evaluate(b, expression) {
  const r = await b.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
  return r.result.value;
}

// Open a page (a file path or URL) at a width, wait for it to load and for its web fonts; fail loudly if a declared
// font family didn't arrive (boards load Google Fonts by CSS).
export async function open(b, target, width = 1300, height = 900) {
  await b.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  const url = /^[a-z]+:/i.test(target) ? target : 'file:///' + path.resolve(target).replace(/\\/g, '/');
  const loaded = b.once('Page.loadEventFired');
  await b.send('Page.navigate', { url });
  await Promise.race([loaded, sleep(30000)]);
  const fonts = await evaluate(b, `(async () => {
    await document.fonts.ready;
    await new Promise((r) => setTimeout(r, 300));
    const missing = [...document.fonts].filter((f) => f.status === 'error').map((f) => f.family);
    if (window.boardReady) await window.boardReady;
    return { missing, families: [...new Set([...document.fonts].filter((f) => f.status === 'loaded').map((f) => f.family))] };
  })()`);
  return fonts;
}
