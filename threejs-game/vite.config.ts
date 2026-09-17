import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import electron from 'vite-plugin-electron';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = fileURLToPath(new URL('.', import.meta.url));
const repoRoot = path.resolve(appRoot, '..');
const electronOutput = path.join(appRoot, 'dist-electron');

export default defineConfig({
  root: appRoot,
  publicDir: path.join(repoRoot, 'public'),
  plugins: [react(), electron([
    {
      entry: path.join(appRoot, 'electron/main.ts'),
      vite: { build: { outDir: electronOutput, emptyOutDir: true,
        rollupOptions: { output: { entryFileNames: 'main.js' } } } },
    },
    {
      entry: path.join(repoRoot, 'electron/preload.ts'),
      onstart({ reload }) { reload(); },
      vite: { build: { outDir: electronOutput, emptyOutDir: false,
        rollupOptions: { output: { entryFileNames: 'preload.mjs' } } } },
    },
  ])],
  server: { port: 44176 },
  build: { outDir: path.join(appRoot, 'dist'), emptyOutDir: true, manifest: true,
    rollupOptions: { input: path.join(appRoot, 'index.html') } },
});
