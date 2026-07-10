import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Web-only build (no Electron plugin) for GitHub Pages.
// `base` must match the repo name so assets resolve under
// https://ljs294.github.io/ski-area-design-challenge/
// The app entry (index.html -> src/app/main.tsx) is pure browser MapLibre
// and pulls all tiles from public remote URLs, so it runs fully static.
export default defineConfig({
  plugins: [react()],
  base: '/ski-area-design-challenge/',
  build: {
    outDir: 'dist-web',
    emptyOutDir: true,
  },
});
