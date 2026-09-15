import { defineConfig } from 'vite';

export default defineConfig({
  build: {
    ssr: 'scripts/prepareIntegratedBenchmarkFixture.ts',
    outDir: 'test-results/integrated-fixture-preparer',
    emptyOutDir: true,
    rollupOptions: { output: { entryFileNames: 'prepare.mjs' } },
  },
});
