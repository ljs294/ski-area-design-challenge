import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

// The Lab is useful only with its explicit package-preparation service. Keep
// this development convenience separate from production/game runtime: the
// installed package itself never needs this process or a network connection.
async function existingWeatherService() {
  try {
    const response = await fetch(process.env.VITE_WEATHER_SERVICE_URL ?? 'http://127.0.0.1:8787/health');
    return response.ok;
  } catch {
    return false;
  }
}

const service = (await existingWeatherService()) ? null : spawn(process.execPath, ['weather-service/server.mjs'], {
  stdio: 'inherit',
  cwd: projectRoot,
  env: { ...process.env, WEATHER_SERVICE_MODE: process.env.WEATHER_SERVICE_MODE ?? 'fixture' },
});
const vite = spawn(process.execPath, [path.join(projectRoot, 'node_modules', 'vite', 'bin', 'vite.js'), '--config', 'vite.config.weatherLab.ts'], {
  stdio: 'inherit',
  cwd: projectRoot,
  env: { ...process.env },
});

let stopping = false;
function stop(exitCode = 0) {
  if (stopping) return;
  stopping = true;
  if (service && !service.killed) service.kill();
  if (!vite.killed) vite.kill();
  process.exitCode = exitCode;
}

process.on('SIGINT', () => stop());
process.on('SIGTERM', () => stop());
service?.once('exit', (code) => { if (!stopping && code && code !== 0) stop(code); });
vite.once('exit', (code) => { if (!stopping) stop(code ?? 0); });
