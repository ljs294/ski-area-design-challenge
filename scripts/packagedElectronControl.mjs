import { execFile } from 'node:child_process';
import { access, mkdir, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { spawn } from 'node:child_process';

const LOOPBACK = '127.0.0.1';
const MAX_LOG_BYTES = 128 * 1024;

function boundedAppend(current, chunk) {
  const combined = current + chunk.toString();
  return combined.length > MAX_LOG_BYTES ? combined.slice(-MAX_LOG_BYTES) : combined;
}

export function validateDebugPort(value) {
  const port = Number(value);
  if (!Number.isInteger(port) || port < 1024 || port > 65_535) {
    throw new Error('Packaged Electron debugging port must be an integer from 1024 through 65535.');
  }
  return port;
}

export async function reserveLoopbackPort(requested = undefined) {
  const port = requested === undefined ? 0 : validateDebugPort(requested);
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen({ host: LOOPBACK, port, exclusive: true }, resolve);
  });
  const address = server.address();
  const selected = typeof address === 'object' && address ? address.port : 0;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return validateDebugPort(selected);
}

async function terminateOwnedTree(child) {
  if (child.exitCode !== null) return;
  if (process.platform === 'win32') {
    await new Promise(resolve => execFile('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'],
      { windowsHide: true }, () => resolve()));
  } else child.kill('SIGKILL');
}

async function terminatePidTree(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return;
  if (process.platform === 'win32') {
    await new Promise(resolve => execFile('taskkill.exe', ['/PID', String(pid), '/T', '/F'],
      { windowsHide: true }, () => resolve()));
  } else {
    try { process.kill(pid, 'SIGKILL'); } catch { /* already exited */ }
  }
}

async function matchesOwnedProcess(record, expectedExecutable) {
  if (!Number.isInteger(record?.pid) || record.pid <= 0 || !record.executable || !record.launchedAt
    || path.resolve(record.executable).toLowerCase() !== path.resolve(expectedExecutable).toLowerCase()) return false;
  if (process.platform === 'win32') {
    const command = `$p=Get-Process -Id ${record.pid} -ErrorAction Stop; @{Path=$p.Path;Start=$p.StartTime.ToUniversalTime().ToString('O')} | ConvertTo-Json -Compress`;
    const output = await new Promise(resolve => execFile('powershell.exe', ['-NoProfile', '-Command', command],
      { windowsHide: true }, (error, stdout) => resolve(error ? '' : stdout)));
    if (!output) return false;
    const actual = JSON.parse(output);
    return path.resolve(actual.Path).toLowerCase() === path.resolve(expectedExecutable).toLowerCase()
      && Math.abs(Date.parse(actual.Start) - Date.parse(record.launchedAt)) < 5_000;
  }
  try { return path.resolve(await realpath(`/proc/${record.pid}/exe`)) === path.resolve(expectedExecutable); }
  catch { return false; }
}

export async function cleanupPackagedElectronFromUserData(userDataDir, expectedExecutable) {
  const marker = path.join(path.resolve(userDataDir), '.integrated-owned-electron.json');
  try {
    const record = JSON.parse(await readFile(marker, 'utf8'));
    if (await matchesOwnedProcess(record, expectedExecutable)) await terminatePidTree(record.pid);
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  } finally { await rm(marker, { force: true }); }
}

function waitForExit(child, timeoutMs) {
  if (child.exitCode !== null) return Promise.resolve(true);
  return new Promise(resolve => {
    const timer = setTimeout(() => { child.off('exit', exited); resolve(false); }, timeoutMs);
    const exited = () => { clearTimeout(timer); resolve(true); };
    child.once('exit', exited);
  });
}

export function cdpPageTargets(value) {
  if (!Array.isArray(value)) return [];
  return value.filter(target => target && target.type === 'page' && typeof target.webSocketDebuggerUrl === 'string')
    .map(target => ({ type: target.type, title: typeof target.title === 'string' ? target.title : '',
      url: typeof target.url === 'string' ? target.url : '' }));
}

async function waitForCdp(child, endpoint, timeoutMs, logs) {
  const deadline = Date.now() + timeoutMs;
  let lastError = '';
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      throw new Error(`Packaged Electron exited ${child.exitCode} before CDP readiness. stderr: ${logs.stderr || '(empty)'}`);
    }
    try {
      const response = await fetch(`${endpoint}/json/version`, { signal: AbortSignal.timeout(750) });
      if (response.ok) {
        const version = await response.json();
        if (typeof version.webSocketDebuggerUrl === 'string' && version.webSocketDebuggerUrl) {
          const targetsResponse = await fetch(`${endpoint}/json/list`, { signal: AbortSignal.timeout(750) });
          const targets = targetsResponse.ok ? await targetsResponse.json() : null;
          const pages = cdpPageTargets(targets);
          if (pages.length) return { version, pages };
          const summary = Array.isArray(targets) ? targets.slice(0, 10).map(target => ({
            type: target?.type, title: target?.title, url: target?.url,
          })) : targetsResponse.ok ? targets : `HTTP ${targetsResponse.status}`;
          lastError = `CDP exposed no page target: ${JSON.stringify(summary)}`;
        }
      } else lastError = `HTTP ${response.status}`;
    } catch (error) { lastError = error instanceof Error ? error.message : String(error); }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Packaged Electron CDP did not become ready within ${timeoutMs}ms (${lastError}). stderr: ${logs.stderr || '(empty)'}`);
}

export function packagedElectronArguments({ userDataDir, debugPort, scenario, telemetryEnabled, devConsole,
  deviceScaleFactor, gpuMode = 'hardware' }) {
  if (gpuMode !== 'hardware' && gpuMode !== 'disabled') {
    throw new Error('Packaged Electron GPU mode must be hardware or disabled.');
  }
  return [`--user-data-dir=${path.resolve(userDataDir)}`, `--remote-debugging-address=${LOOPBACK}`,
    `--remote-debugging-port=${debugPort}`,
    `--integrated-benchmark-scenario=${Buffer.from(JSON.stringify(scenario)).toString('base64url')}`,
    `--integrated-benchmark-telemetry=${telemetryEnabled ? '1' : '0'}`,
    ...(devConsole ? ['--integrated-benchmark-dev-console'] : []),
    ...(deviceScaleFactor === 1 ? [] : [`--force-device-scale-factor=${deviceScaleFactor}`]),
    ...(gpuMode === 'disabled' ? ['--disable-gpu'] : [])];
}

export async function launchPackagedElectron({ executablePath, userDataDir, port, scenario,
  telemetryEnabled, devConsole = true, deviceScaleFactor = 1, gpuMode = 'hardware', timeoutMs = 30_000 }) {
  const executable = path.resolve(executablePath);
  await access(executable);
  if (!(await stat(executable)).isFile()) throw new Error(`Packaged Electron executable is not a file: ${executable}`);
  const normalized = executable.replaceAll('\\', '/').toLowerCase();
  if (normalized.includes('/node_modules/electron/') || path.basename(executable).toLowerCase() === 'electron.exe') {
    throw new Error('Packaged Electron control refuses the development Electron executable.');
  }
  if (!scenario || typeof scenario !== 'object') throw new Error('Packaged Electron launch requires the validated scenario payload.');
  await mkdir(path.resolve(userDataDir), { recursive: true });
  const debugPort = await reserveLoopbackPort(port ?? process.env.INTEGRATED_ELECTRON_DEBUG_PORT);
  const endpoint = `http://${LOOPBACK}:${debugPort}`;
  const args = packagedElectronArguments({ userDataDir, debugPort, scenario, telemetryEnabled, devConsole,
    deviceScaleFactor, gpuMode });
  const environment = { ...process.env, INTEGRATED_USER_DATA_DIR: path.resolve(userDataDir) };
  // Playwright's own Electron transport sets this in some worker launches. A
  // packaged application must start in Electron mode, not Node compatibility mode.
  delete environment.ELECTRON_RUN_AS_NODE;
  delete environment.NODE_OPTIONS;
  const child = spawn(executable, args, { cwd: path.dirname(executable), env: environment,
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, shell: false });
  const marker = path.join(path.resolve(userDataDir), '.integrated-owned-electron.json');
  await writeFile(marker, `${JSON.stringify({ pid: child.pid, executable, launchedAt: new Date().toISOString() })}\n`, 'utf8');
  const emergencyExit = () => { if (child.exitCode === null) child.kill(); };
  process.once('exit', emergencyExit);
  const logs = { stdout: '', stderr: '' };
  const exited = new Promise(resolve => child.once('exit', (exitCode, signal) => resolve({ exitCode, signal })));
  child.stdout.on('data', chunk => { logs.stdout = boundedAppend(logs.stdout, chunk); });
  child.stderr.on('data', chunk => { logs.stderr = boundedAppend(logs.stderr, chunk); });
  let readiness;
  try { readiness = await waitForCdp(child, endpoint, timeoutMs, logs); }
  catch (error) {
    await terminateOwnedTree(child); await waitForExit(child, 5_000);
    process.off('exit', emergencyExit); await rm(marker, { force: true }); throw error;
  }
  let closed = false;
  return {
    pid: child.pid, endpoint, version: readiness.version, pageTargets: readiness.pages, args, exited,
    get exitCode() { return child.exitCode; },
    get stdout() { return logs.stdout; },
    get stderr() { return logs.stderr; },
    async close({ graceMs = 5_000 } = {}) {
      if (closed) return { forced: false, exitCode: child.exitCode };
      closed = true;
      const natural = await waitForExit(child, graceMs);
      if (!natural) await terminateOwnedTree(child);
      await waitForExit(child, 5_000);
      process.off('exit', emergencyExit);
      await rm(marker, { force: true });
      return { forced: !natural, exitCode: child.exitCode };
    },
  };
}
