// Launch the dev app straight into the Graphics Lab (two-map graphics tool),
// bypassing the menu. Cross-platform: sets GRAPHICS_LAB, which electron/main.ts
// reads to append the #graphics-lab deep link. Run via `npm run dev:lab`.
import { spawn } from 'node:child_process';

spawn('npx', ['vite'], {
  stdio: 'inherit',
  shell: true,
  env: { ...process.env, GRAPHICS_LAB: '1' },
});
