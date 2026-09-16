import { defineConfig } from 'vite';
import path from 'node:path';

export default defineConfig({ root: 'tests/e2e/performance/snow-reproducer', server: { host: '127.0.0.1', port: 44619,
  strictPort: true, fs: { allow: [path.resolve('.')] } } });
