import { createHash } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

function digest(bytes) { return createHash('sha256').update(bytes).digest('hex'); }

export function orderedReactProfileEntries(ring) {
  if (!Array.isArray(ring?.entries) || !Number.isInteger(ring.size) || ring.size <= 0) return [];
  const capacity = ring.entries.length;
  if (!capacity || !Number.isInteger(ring.writeIndex) || ring.size > capacity) return [];
  const first = ring.size === capacity ? ring.writeIndex : 0, entries = [];
  for (let index = 0; index < ring.size; index++) {
    const entry = ring.entries[(first + index) % capacity];
    if (!entry || typeof entry.id !== 'string' || !['mount', 'update', 'nested-update'].includes(entry.phase)
      || ['actualDuration', 'baseDuration', 'startTime', 'commitTime'].some(key => !Number.isFinite(entry[key]))) return [];
    entries.push(entry);
  }
  return entries;
}

async function artifact(file, role, availability = 'available') {
  if (availability !== 'available') return { role, availability };
  const bytes = await readFile(file);
  if (bytes.byteLength === 0) throw new Error(`${role} artifact is empty.`);
  return { role, availability, path: file, bytes: bytes.byteLength, sha256: digest(bytes) };
}

export function diagnosticArtifactErrors(records) {
  const required = ['allocationProfile', 'gcTrace', 'heapSnapshot', 'reactProfile'];
  const byRole = new Map(Array.isArray(records) ? records.map(record => [record?.role, record]) : []);
  const errors = [];
  for (const role of required) {
    const record = byRole.get(role);
    if (!record) { errors.push(`Diagnostic profile is missing ${role}.`); continue; }
    if (record.availability === 'unavailable') {
      if (typeof record.reason !== 'string' || !record.reason) errors.push(`Unavailable ${role} lacks a reason.`);
      continue;
    }
    if (record.availability !== 'available' || typeof record.path !== 'string' || !record.path
      || !Number.isInteger(record.bytes) || record.bytes <= 0 || !/^[a-f0-9]{64}$/.test(record.sha256 ?? '')) {
      errors.push(`Available ${role} lacks a nonempty hashed artifact.`);
    }
  }
  return errors;
}

export async function startIntegratedDiagnosticProfile(page, { outputDir, runId }) {
  const directory = path.resolve(outputDir);
  await mkdir(directory, { recursive: true });
  const session = await page.context().newCDPSession(page);
  const traceEvents = [];
  let tracingComplete;
  const tracingDone = new Promise(resolve => { tracingComplete = resolve; });
  session.on('Tracing.dataCollected', event => traceEvents.push(...event.value));
  session.on('Tracing.tracingComplete', () => tracingComplete());
  await session.send('Profiler.enable');
  await session.send('HeapProfiler.enable');
  await session.send('HeapProfiler.startSampling', { samplingInterval: 32_768 });
  await session.send('Tracing.start', { categories: 'devtools.timeline,v8,disabled-by-default-v8.gc',
    options: 'sampling-frequency=10000' });
  let stopped = false;
  return async function stop() {
    if (stopped) throw new Error('Integrated diagnostic profile was already stopped.');
    stopped = true;
    const records = [];
    try {
      const allocation = await session.send('HeapProfiler.stopSampling');
      const allocationFile = path.join(directory, `${runId}.allocation-profile.json`);
      await writeFile(allocationFile, `${JSON.stringify(allocation.profile)}\n`);
      records.push(await artifact(allocationFile, 'allocationProfile'));
    } catch (error) {
      records.push({ role: 'allocationProfile', availability: 'unavailable',
        reason: error instanceof Error ? error.message : String(error) });
    }
    try {
      await session.send('Tracing.end');
      await Promise.race([tracingDone, new Promise((_, reject) => setTimeout(() => reject(new Error('Tracing stop timed out.')), 30_000))]);
      const traceFile = path.join(directory, `${runId}.gc-trace.json`);
      await writeFile(traceFile, `${JSON.stringify({ traceEvents })}\n`);
      records.push(await artifact(traceFile, 'gcTrace'));
    } catch (error) {
      records.push({ role: 'gcTrace', availability: 'unavailable',
        reason: error instanceof Error ? error.message : String(error) });
    }
    try {
      const snapshotFile = path.join(directory, `${runId}.heapsnapshot`);
      const stream = createWriteStream(snapshotFile, { encoding: 'utf8' });
      let write = Promise.resolve();
      const onChunk = event => {
        write = write.then(() => stream.write(event.chunk)
          ? undefined : new Promise((resolve, reject) => {
            const onDrain = () => { stream.off('error', onError); resolve(); };
            const onError = error => { stream.off('drain', onDrain); reject(error); };
            stream.once('drain', onDrain); stream.once('error', onError);
          }));
      };
      session.on('HeapProfiler.addHeapSnapshotChunk', onChunk);
      await session.send('HeapProfiler.takeHeapSnapshot', { reportProgress: false });
      session.off('HeapProfiler.addHeapSnapshotChunk', onChunk);
      await write;
      await new Promise((resolve, reject) => { stream.end(resolve); stream.once('error', reject); });
      records.push(await artifact(snapshotFile, 'heapSnapshot'));
    } catch (error) {
      records.push({ role: 'heapSnapshot', availability: 'unavailable',
        reason: error instanceof Error ? error.message : String(error) });
    }
    const reactEntries = await page.evaluate(() => {
      const ring = globalThis.__MOUNTAIN_PLANNER_INTEGRATED_REACT_PROFILE__;
      if (!Array.isArray(ring?.entries) || !Number.isInteger(ring.size) || ring.size <= 0) return null;
      const capacity = ring.entries.length;
      if (!capacity || !Number.isInteger(ring.writeIndex)) return null;
      const first = ring.size === capacity ? ring.writeIndex : 0, entries = [];
      for (let index = 0; index < ring.size; index++) {
        const entry = ring.entries[(first + index) % capacity]; if (entry) entries.push(entry);
      }
      return entries.length && entries.every(entry => entry && typeof entry.id === 'string'
        && ['mount', 'update', 'nested-update'].includes(entry.phase)
        && ['actualDuration', 'baseDuration', 'startTime', 'commitTime'].every(key => Number.isFinite(entry[key])))
        ? { entries, dropped: ring.dropped ?? 0 } : null;
    }).catch(() => null);
    if (reactEntries) {
      const reactFile = path.join(directory, `${runId}.react-profile.json`);
      await writeFile(reactFile, `${JSON.stringify(reactEntries)}\n`);
      records.push(await artifact(reactFile, 'reactProfile'));
    } else records.push({ role: 'reactProfile', availability: 'unavailable',
      reason: 'React profiling data requires the dedicated profiling renderer build.' });
    await session.detach().catch(() => undefined);
    const errors = diagnosticArtifactErrors(records);
    if (errors.length) throw new Error(errors.join(' '));
    const manifest = { schemaVersion: 1, runId, diagnosticOnly: true, artifacts: records };
    const manifestFile = path.join(directory, `${runId}.diagnostic-profile.json`);
    await writeFile(manifestFile, `${JSON.stringify(manifest, null, 2)}\n`);
    return { manifestFile, manifest, artifact: await artifact(manifestFile, 'diagnosticProfileManifest') };
  };
}
