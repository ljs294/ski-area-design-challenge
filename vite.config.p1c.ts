import { defineConfig } from 'vite';

/** Source-serving harness for real-browser P1-C adapter evidence only. */
export default defineConfig({
  base: '/',
  server: { strictPort: true },
});
