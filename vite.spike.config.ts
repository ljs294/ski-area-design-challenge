// Spike-only Vite config: serves the static spike page + TS transform with NO
// electron plugin, so the dev server stays up in a headless/browser context
// for data-source verification. Not part of the real build.
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
});
