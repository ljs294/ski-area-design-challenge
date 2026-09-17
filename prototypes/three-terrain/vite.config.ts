import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import fs from 'node:fs';
import path from 'node:path';

const terrainDirectory = process.env.MOUNTAIN_PLANNER_TERRAIN_DIR
  ?? path.join(process.env.APPDATA ?? '', 'ski-area-design-challenge', 'terrains');

function terrainFiles() {
  return {
    name: 'p0-local-terrain-files',
    configureServer(server: { middlewares: { use(handler: (request: { url?: string }, response: {
      statusCode: number; setHeader(name: string, value: string): void; end(body?: string | Buffer): void;
    }, next: () => void) => void): void } }) {
      server.middlewares.use((request, response, next) => {
        const url = request.url?.split('?')[0] ?? '';
        if (!url.startsWith('/__p0_terrain/')) { next(); return; }
        const relative = decodeURIComponent(url.slice('/__p0_terrain/'.length));
        if (!/^[a-zA-Z0-9_.-]+$/.test(relative)) { response.statusCode = 400; response.end('Invalid terrain path'); return; }
        const file = path.join(terrainDirectory, relative);
        if (!fs.existsSync(file)) { response.statusCode = 404; response.end('Not found'); return; }
        response.setHeader('Cache-Control', 'no-store');
        response.setHeader('Content-Type', relative.endsWith('.json') ? 'application/json' : 'application/octet-stream');
        response.end(fs.readFileSync(file));
      });
    },
  };
}

export default defineConfig({
  root: __dirname,
  plugins: [react(), terrainFiles()],
  build: { outDir: 'dist', emptyOutDir: true },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
});
