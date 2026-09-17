import { useCallback, useEffect, useState } from 'react';
import type { TerrainRecord, TerrainSummary } from '../../../src/types/terrain';
import { PrototypeViewport, type PrototypeMetrics } from './PrototypeViewport';
import { listPrototypeTerrains, loadPrototypeTerrain } from './terrainSource';

function normalizeRecord(value: TerrainRecord): TerrainRecord {
  return {
    ...value,
    sampleHeights: value.sampleHeights instanceof Float32Array
      ? value.sampleHeights : Float32Array.from(value.sampleHeights),
    ...(value.surround ? { surround: { ...value.surround,
      heights: Array.from(value.surround.heights) } } : {}),
    ...(value.coverGrid ? { coverGrid: { ...value.coverGrid,
      data: value.coverGrid.data instanceof Uint8Array
        ? value.coverGrid.data : Uint8Array.from(value.coverGrid.data) } } : {}),
  };
}

export function App() {
  const [summaries, setSummaries] = useState<TerrainSummary[]>([]);
  const [record, setRecord] = useState<TerrainRecord | null>(null);
  const [metrics, setMetrics] = useState<PrototypeMetrics | null>(null);
  const [error, setError] = useState<string | null>(null);
  const updateMetrics = useCallback((next: PrototypeMetrics) => setMetrics(next), []);
  useEffect(() => { void listPrototypeTerrains().then(setSummaries, (reason: unknown) =>
    setError(reason instanceof Error ? reason.message : 'Unable to list packages.')); }, []);
  const choose = async (key: string) => {
    setError(null); setMetrics(null);
    const loaded = await loadPrototypeTerrain(key);
    if (!loaded) { setError('Terrain package was not found.'); return; }
    setRecord(normalizeRecord(loaded));
  };
  const importJson = async (file: File | undefined) => {
    if (!file) return;
    try {
      const parsed = JSON.parse(await file.text()) as TerrainRecord;
      if (!parsed.key || !parsed.bounds || parsed.sampleHeights.length !== parsed.sampleGridSize ** 2)
        throw new Error('The JSON file is not a complete TerrainRecord.');
      setRecord(normalizeRecord(parsed)); setError(null); setMetrics(null);
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to import terrain JSON.'); }
  };
  return <main>
    <header>
      <div><p className="eyebrow">P0 terrain interaction prototype</p><h1>Three.js alpine diorama</h1></div>
      <div className="package-picker">
        <label>Prepared package <select value={record?.key ?? ''} onChange={(event) => void choose(event.target.value)}>
          <option value="">Select a real package…</option>
          {summaries.map((summary) => <option key={summary.key} value={summary.key}>{summary.mountainName}</option>)}
        </select></label>
        <label className="file-button">Import TerrainRecord JSON<input type="file" accept="application/json,.json"
          onChange={(event) => void importJson(event.target.files?.[0])} /></label>
      </div>
    </header>
    {error && <div className="error" role="alert">{error}</div>}
    {!record ? <section className="empty">
      <h2>Load representative terrain</h2>
      <p>This standalone prototype intentionally has no synthetic acceptance fixture. Prepare a mountain in the main browser app on the same origin, or import an exported TerrainRecord JSON file.</p>
      <p>Run from this folder with <code>npm run dev</code>.</p>
    </section> : <>
      <PrototypeViewport record={record} onMetrics={updateMetrics} />
      <aside className="metrics" aria-live="polite">
        <strong>{record.mountainName}</strong>
        <span>{record.sampleGridSize}² analytical grid</span>
        <span>{metrics?.chunks ?? '—'} fixed-detail chunks</span>
        <span>{metrics ? `${metrics.triangles.toLocaleString()} triangles` : '— triangles'}</span>
        <span>{metrics ? `${(metrics.uploadedBytes / 1_048_576).toFixed(1)} MiB buffers` : '— buffers'}</span>
        <span>{metrics ? `${metrics.surfaceMaxErrorM.toFixed(1)} m max surface error` : '— surface error'}</span>
        <span>{metrics ? `${metrics.frameP95Ms.toFixed(1)} ms frame p95` : 'warming up…'}</span>
        <span>{metrics ? `${metrics.pickP95Ms.toFixed(2)} ms pick p95` : '— pick p95'}</span>
      </aside>
    </>}
  </main>;
}
