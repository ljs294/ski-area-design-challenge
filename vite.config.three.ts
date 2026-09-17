import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: { port: 44176 },
  build: { outDir: 'dist-three', emptyOutDir: true, manifest: true,
    rollupOptions: { input: 'three.html' } },
});
