import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The Weather Lab is a browser-only developer harness. Keeping it separate
// from vite.config.ts avoids booting Electron when npm runs this command.
export default defineConfig({
  plugins: [react()],
  root: 'weather-lab',
  build: { outDir: '../dist-weather-lab', emptyOutDir: true },
  server: { port: 4174 },
});
