import { _electron as electron, expect, test, type ElectronApplication } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf, manifestOf } from '../../src/terrainPackage';
import { DESIGN_SAVE_FORMAT, DESIGN_SAVE_SCHEMA_VERSION, type DesignSaveDraft } from '../../src/types/designSave';
import type { TerrainRecord } from '../../src/types/terrain';

function terrain(): TerrainRecord {
  const coverGrid = { bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
    width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200' as const, vintage: '2021' };
  const coverBoundarySegments = [0, 0, 1, 0, 10];
  const contourSegments = [0, 0, 1, 1, 1500];
  let record: TerrainRecord = { schemaVersion: 4, key: 'electron-mountain', mountainName: 'Mountain',
    latitude: 46.905, longitude: -121.495, areaSizeMeters: 2000, bounds: coverGrid.bounds,
    sampleGridSize: 2, sampleHeights: new Float32Array([1000, 1010, 1020, 1030]),
    coverGrid, coverMetadata: coverMetadataOf(coverGrid), coverBoundarySegments,
    coverGeometryMetadata: coverGeometryMetadataOf(coverBoundarySegments), contourSegments,
    contourMetadata: contourMetadataOf(contourSegments, 2, 6.096), climate: { monthly: [] },
    sourceType: 'live', createdAt: '2026-01-01', updatedAt: '2026-01-01' };
  record = { ...record, packageManifest: manifestOf(record) };
  return record;
}

function draft(revision: number): DesignSaveDraft {
  return { format: DESIGN_SAVE_FORMAT, schemaVersion: DESIGN_SAVE_SCHEMA_VERSION,
    key: 'electron-design', name: revision === 0 ? 'First' : 'Second',
    createdAt: '2026-01-01', updatedAt: `2026-02-0${revision + 1}`, site: null,
    camera: { center: [-121.495, 46.905], zoom: 14, bearing: 0, pitch: 45, is3D: true },
    revisions: { design: revision, terrain: 1, topology: revision, lifts: revision },
    terrainRecord: terrain(), lifts: [], trails: [], nodes: [], paths: [], junctions: [] };
}

async function launch(root: string): Promise<ElectronApplication> {
  const environment = { ...process.env };
  delete environment.ELECTRON_RUN_AS_NODE;
  return electron.launch({
    executablePath: path.resolve('node_modules/electron/dist/electron.exe'),
    args: ['--no-sandbox', path.resolve('tests/e2e/support/electronSaveLoadingBootstrap.cjs')],
    env: { ...environment, SAVE_LOADING_ELECTRON_USER_DATA: root,
      SAVE_LOADING_ELECTRON_MAIN: path.resolve('dist-electron/main.js') },
  });
}

test('real preload and IPC recover the previous complete desktop save', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mountain-planner-p1c-electron-'));
  let application: ElectronApplication | null = null;
  try {
    application = await launch(root);
    let window = await application.firstWindow();
    await expect.poll(() => window.evaluate(() => Boolean(window.desktop?.designs))).toBe(true);
    const saveThroughPreload = (value: DesignSaveDraft) => {
      const serializable = { ...value, terrainRecord: { ...value.terrainRecord,
        sampleHeights: Array.from(value.terrainRecord.sampleHeights) } } as unknown as DesignSaveDraft;
      return window.evaluate(async (input) => {
        input.terrainRecord.sampleHeights = new Float32Array(
          input.terrainRecord.sampleHeights as unknown as number[],
        );
        return window.desktop!.designs.save(input);
      }, serializable);
    };

    expect(await saveThroughPreload(draft(0))).toMatchObject({ ok: true,
      receipt: { revisions: { design: 0, terrain: 1 } } });
    expect(await saveThroughPreload(draft(1))).toMatchObject({ ok: true,
      receipt: { revisions: { design: 1, terrain: 1 } } });
    expect(await window.evaluate(() => window.desktop!.designs.list())).toMatchObject([
      { key: 'electron-design', name: 'Second' },
    ]);

    await application.close();
    application = null;
    const designRoot = path.join(root, 'three-designs');
    const heads = path.join(designRoot, 'heads');
    const currentHead = fs.readdirSync(heads).find((entry) => entry.endsWith('.head.json'));
    expect(currentHead).toBeDefined();
    fs.writeFileSync(path.join(heads, currentHead!), '{injected interruption', 'utf8');

    application = await launch(root);
    window = await application.firstWindow();
    await expect.poll(() => window.evaluate(() => Boolean(window.desktop?.designs))).toBe(true);
    const recovered = await window.evaluate(() => window.desktop!.designs.load('electron-design'));

    expect(recovered).toMatchObject({ ok: true, bundle: { save: { name: 'First',
      revisions: { design: 0 }, terrain: { sourceRevision: 1 } },
      terrain: { key: 'electron-mountain' } } });
    expect(fs.existsSync(designRoot)).toBe(true);
    expect(fs.readdirSync(path.join(designRoot, 'manifests'))).not.toHaveLength(0);
    expect(fs.readdirSync(path.join(root, 'saves'))).toHaveLength(0);
    const terrainRoot = path.join(root, 'terrains');
    expect(fs.existsSync(terrainRoot) ? fs.readdirSync(terrainRoot) : []).toHaveLength(0);
  } finally {
    await application?.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});
