import { spawn } from 'node:child_process';

spawn('npx', ['vite'], {
  stdio: 'inherit', shell: true,
  env: { ...process.env, WEATHER_LAB: '1' },
});
