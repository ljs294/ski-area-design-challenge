import { expect, test } from '@playwright/test';

test('real IndexedDB publication recovers old or new complete pairs', async ({ page }) => {
  await page.goto('/tests/p1c/harness.html');
  const evidence = await page.evaluate(async () => {
    const repositoryPath = '/src/designSaveRepository.ts';
    const storagePath = '/src/browserDesignStorage.ts';
    const packagePath = '/src/terrainPackage.ts';
    const modelPath = '/src/types/designSave.ts';
    const { DesignSaveRepository } = await import(/* @vite-ignore */ repositoryPath) as
      typeof import('../../src/designSaveRepository');
    const { BrowserDesignStorage } = await import(/* @vite-ignore */ storagePath) as
      typeof import('../../src/browserDesignStorage');
    const { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf, manifestOf } =
      await import(/* @vite-ignore */ packagePath) as typeof import('../../src/terrainPackage');
    const { DESIGN_SAVE_FORMAT, DESIGN_SAVE_SCHEMA_VERSION } =
      await import(/* @vite-ignore */ modelPath) as typeof import('../../src/types/designSave');

    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.deleteDatabase('mountain-planner-three-design');
      request.onsuccess = () => resolve();
      request.onerror = () => reject(request.error);
    });

    const terrain = (offset = 0) => {
      const coverGrid = {
        bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
        width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true,
        nodataCount: 0, source: 'esa-worldcover-2021-v200' as const, vintage: '2021',
      };
      const coverBoundarySegments = [0, 0, 1, 0, 10];
      const contourSegments = [0, 0, 1, 1, 1500];
      let record = {
        schemaVersion: 4 as const, key: 'mountain', mountainName: 'Mountain',
        latitude: 46.905, longitude: -121.495, areaSizeMeters: 2000, bounds: coverGrid.bounds,
        sampleGridSize: 2, sampleHeights: new Float32Array([1000 + offset, 1010, 1020, 1030]),
        coverGrid, coverMetadata: coverMetadataOf(coverGrid), coverBoundarySegments,
        coverGeometryMetadata: coverGeometryMetadataOf(coverBoundarySegments), contourSegments,
        contourMetadata: contourMetadataOf(contourSegments, 2, 6.096), climate: { monthly: [] },
        sourceType: 'live' as const, createdAt: '2026-01-01', updatedAt: `2026-01-${offset + 1}`,
      };
      record = { ...record, packageManifest: manifestOf(record) };
      return record;
    };
    const draft = (designRevision: number, terrainRevision = 1, offset = 0) => ({
      format: DESIGN_SAVE_FORMAT, schemaVersion: DESIGN_SAVE_SCHEMA_VERSION,
      key: 'browser-design', name: `Design ${designRevision}`,
      createdAt: '2026-01-01', updatedAt: `2026-02-${designRevision + 1}`,
      site: null, camera: { center: [-121.495, 46.905] as [number, number], zoom: 14,
        bearing: 0, pitch: 45, is3D: true },
      revisions: { design: designRevision, terrain: terrainRevision,
        topology: designRevision, lifts: designRevision },
      terrainRecord: terrain(offset), lifts: [], trails: [], nodes: [], paths: [], junctions: [],
    });
    const storage = new BrowserDesignStorage();
    let id = 0;
    const repository = new DesignSaveRepository(storage, {
      createId: () => `browser-${++id}`, now: () => '2026-02-01',
    });
    const first = await repository.save(draft(0));

    const interruptedStorage = {
      readCurrentManifest: storage.readCurrentManifest.bind(storage),
      readTerrainGeneration: storage.readTerrainGeneration.bind(storage),
      writeTerrainGeneration: storage.writeTerrainGeneration.bind(storage),
      publishManifestAndHead: async () => { throw new Error('injected before head publication'); },
      writeSummary: storage.writeSummary.bind(storage),
      listSummaries: storage.listSummaries.bind(storage),
    };
    const interruptedRepository = new DesignSaveRepository(interruptedStorage, {
      createId: () => `interrupted-${++id}`, now: () => '2026-02-02',
    });
    const interrupted = await interruptedRepository.save(draft(1, 2, 10));
    const afterInterruption = await repository.load('browser-design');

    const secondaryFailureStorage = {
      readCurrentManifest: storage.readCurrentManifest.bind(storage),
      readTerrainGeneration: storage.readTerrainGeneration.bind(storage),
      writeTerrainGeneration: storage.writeTerrainGeneration.bind(storage),
      publishManifestAndHead: storage.publishManifestAndHead.bind(storage),
      writeSummary: async () => { throw new Error('injected summary failure'); },
      listSummaries: storage.listSummaries.bind(storage),
    };
    const secondaryRepository = new DesignSaveRepository(secondaryFailureStorage, {
      createId: () => `secondary-${++id}`, now: () => '2026-02-03',
    });
    const secondary = await secondaryRepository.save(draft(2));
    const summaries = await secondaryRepository.list();

    const beforeAtomicFailure = await storage.readCurrentManifest('browser-design');
    let atomicRejected = false;
    try {
      await storage.publishManifestAndHead({ ...beforeAtomicFailure!, name: 'must not publish' });
    } catch { atomicRejected = true; }
    const afterAtomicFailure = await storage.readCurrentManifest('browser-design');
    const databases = typeof indexedDB.databases === 'function'
      ? (await indexedDB.databases()).map((entry) => entry.name)
      : [];

    return {
      firstOk: first.ok,
      interrupted,
      recoveredName: afterInterruption?.ok ? afterInterruption.bundle.save.name : null,
      recoveredTerrainRevision: afterInterruption?.ok
        ? afterInterruption.bundle.save.terrain.sourceRevision : null,
      secondary,
      listedName: summaries[0]?.name,
      atomicRejected,
      beforeAtomicName: beforeAtomicFailure?.name,
      afterAtomicName: afterAtomicFailure?.name,
      databases,
    };
  });

  expect(evidence).toMatchObject({
    firstOk: true,
    interrupted: { ok: false, error: 'injected before head publication' },
    recoveredName: 'Design 0',
    recoveredTerrainRevision: 1,
    secondary: { ok: true, receipt: { warnings: [expect.stringContaining('summary failure')] } },
    listedName: 'Design 2',
    atomicRejected: true,
    beforeAtomicName: 'Design 2',
    afterAtomicName: 'Design 2',
  });
  expect(evidence.databases).toContain('mountain-planner-three-design');
  expect(evidence.databases).not.toContain('mountain-planner-terrain');
  expect(evidence.databases).not.toContain('mountain-planner-dual-saves');
});
