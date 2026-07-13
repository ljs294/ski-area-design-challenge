import { defineConfig } from 'vite';
import electron from 'vite-plugin-electron';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [
    react(),
    electron([
      { entry: 'electron/main.ts' },
      {
        entry: 'electron/preload.ts',
        onstart({ reload }) {
          // Reload the renderer when the preload rebuilds (it can't hot-swap).
          reload();
        },
        // Emit as preload.mjs. Vite 8 (rolldown) outputs ESM regardless of a
        // format override, so we lean into it: an ESM preload loads fine under
        // Electron 43 when the window sets `sandbox: false` (see electron/main.ts).
        vite: {
          build: {
            rollupOptions: {
              output: { entryFileNames: 'preload.mjs' },
            },
          },
        },
      },
    ]),
  ],
});
