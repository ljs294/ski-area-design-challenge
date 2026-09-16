import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

/** Explicit heavy diagnostic build; never used for production timing trials. */
export default defineConfig({
  plugins: [react()],
  base: '/ski-area-design-challenge/',
  define: { 'import.meta.env.VITE_INTEGRATED_REACT_PROFILING': JSON.stringify('1') },
  resolve: { alias: { 'react-dom/client': 'react-dom/profiling' } },
  build: { outDir: 'dist-integrated-profiling', emptyOutDir: true, manifest: true },
});
