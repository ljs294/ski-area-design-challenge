import { DualClockEngine } from '../dualClock/engine';
import type { DualWorkerRequest, DualWorkerResponse } from './dualClockProtocol';
import type { SnowAddResult } from '../types/dualClock';
import { DualSnowPublisher } from './dualSnowPublication';
import { DualMovementPublisher } from './dualMovementPublication';

let engine: DualClockEngine | null = null;
let generation = 0, operation = 0, lastPublication = 0, lastSnowPublication = 0;
let work: Generator<void, void> | null = null;
let activeRequest = 0;
let headlessOperation = false;
const snowPublisher = new DualSnowPublisher();
const movementPublisher = new DualMovementPublisher();
let publishedGeometry = -1;
let benchmarkTelemetry = false, publicationSequence = 0;
const slices = new MessageChannel();
const scheduled: (() => void)[] = [];
slices.port1.onmessage = () => scheduled.shift()?.();
function scheduleSlice(task: () => void): void { scheduled.push(task); slices.port2.postMessage(null); }
function publish(id: number, snow = false, checkpoint = false, geometry = false, snowAdd?: SnowAddResult,
  weatherAck?: { acceptedFrom: string; acceptedTo: string }): void {
  if (!engine) return;
  const publicationStarted = benchmarkTelemetry ? performance.now() : 0;
  const response: DualWorkerResponse = { generation, id, type: checkpoint ? 'checkpoint' : 'publication',
    committedRevision: engine.state.clock.revision, operationGeneration: operation,
    publication: engine.publication(), busy: work !== null };
  response.movement = movementPublisher.frame(response.publication!.points);
  response.publication!.points = [];
  if (checkpoint) response.checkpoint = engine.checkpoint();
  if (snowAdd) response.snowAdd = snowAdd;
  if (weatherAck) response.weatherAck = { requestId: id, generation, ...weatherAck };
  if (geometry || publishedGeometry !== engine.geometryRevision) { response.geometry = engine.geometry(); publishedGeometry = engine.geometryRevision; }
  if (snow && engine.snow) Object.assign(response, snowPublisher.frame(engine.snow, checkpoint || headlessOperation));
  const transfer: Transferable[] = response.snow ? [response.snow.depthM.buffer, response.snow.surface.buffer] : [];
  if (response.snowPatch) transfer.push(response.snowPatch.depthM.buffer, response.snowPatch.surface.buffer);
  if (response.movement) transfer.push(response.movement.buffer);
  if (benchmarkTelemetry) {
    let transferBytes = 0;
    for (const item of transfer) if (item instanceof ArrayBuffer) transferBytes += item.byteLength;
    const workerAt = performance.now();
    response.benchmarkTelemetry = { publicationSequence: ++publicationSequence,
      workerTimeOrigin: performance.timeOrigin, workerAt,
      publicationBuildMs: workerAt - publicationStarted, transferBytes };
  }
  self.postMessage(response, { transfer }); lastPublication = performance.now();
  if (snow) lastSnowPublication = lastPublication;
}
function publishWeatherAck(id: number, acceptedFrom: string, acceptedTo: string): void {
  if (!engine) return;
  const response: DualWorkerResponse = { generation, id, type: 'publication',
    committedRevision: engine.state.clock.revision, operationGeneration: operation,
    busy: work !== null, weatherAck: { requestId: id, generation, acceptedFrom, acceptedTo } };
  self.postMessage(response);
}
function stop(): void { operation++; work?.return(); work = null; }
function pump(token: number): void {
  if (token !== operation || !engine || !work) return;
  try {
    const start = performance.now();
    do { if (work.next().done) { work = null; break; } } while (performance.now() - start < 8);
    if (!work) { publish(activeRequest, headlessOperation || performance.now() - lastSnowPublication >= 1000); return; }
    if (performance.now() - lastPublication >= 100) {
      const headless = engine.state.advance?.state === 'running';
      publish(activeRequest, !headless && performance.now() - lastSnowPublication >= 1000);
    }
    // A macrotask yield is essential: a Promise-only loop starves worker messages.
    scheduleSlice(() => pump(token));
  } catch (error) {
    stop(); engine.pause();
    self.postMessage({ generation, id: activeRequest, type: 'error', error: error instanceof Error ? error.message : 'Simulation failed.',
      committedRevision: engine.state.clock.revision, operationGeneration: operation,
      publication: engine.publication() } satisfies DualWorkerResponse);
  }
}
self.onmessage = (event: MessageEvent<DualWorkerRequest>) => {
  const request = event.data;
  if (request.type !== 'initialize' && request.generation !== generation) return;
  if (request.type === 'recycle-movement') { movementPublisher.recycle(request.buffer); return; }
  try {
    if (request.type === 'initialize') {
      stop(); snowPublisher.invalidate(); generation = request.generation;
      benchmarkTelemetry = request.benchmarkTelemetry === true;
      publicationSequence = 0; engine = new DualClockEngine(request.input);
      const initialWeather = request.input.weather;
      const initialCoverage = initialWeather.length ? {
        acceptedFrom: initialWeather.reduce((min, hour) => Date.parse(hour.at) < Date.parse(min.at) ? hour : min).at,
        acceptedTo: initialWeather.reduce((max, hour) => Date.parse(hour.at) > Date.parse(max.at) ? hour : max).at,
      } : undefined;
      publish(request.requestId, true, false, true, undefined, initialCoverage); return;
    }
    if (!engine) throw new Error('Simulation worker is not initialized.');
    if (request.type === 'snow-add' && work && headlessOperation) throw new Error('Wait for the active bulk advance to finish or cancel it before adding snow.');
    if (request.type === 'advance' && work) return;
    if (request.type === 'advance' && engine.state.clock.paused) { publish(request.requestId); return; }
    if (request.type === 'advance' || request.type === 'skip' || request.type === 'resume') {
      stop();
      if (request.type === 'skip') engine.beginAdvance(request.request);
      if (request.type === 'resume') engine.resumeAdvance();
      const target = request.type === 'advance' ? request.targetMs : Date.parse(engine.state.advance!.request.target);
      activeRequest = request.requestId; work = engine.advanceTo(target, request.type !== 'advance');
      headlessOperation = request.type !== 'advance';
      pump(operation); return;
    }
    stop();
    // A control command can supersede an in-flight presentation reply on the main thread.
    // Start a fresh snow baseline rather than building a patch on an unseen revision.
    snowPublisher.invalidate();
    const geometryRevisionBeforeResort = engine.geometryRevision;
    switch (request.type) {
      case 'resort': engine.setResort(request.input); break;
      case 'weather': engine.setWeather(request.hours); break;
      case 'timezone': engine.setInitialTimezone(request.timezone); break;
      case 'terrain': engine.setTerrain(request.terrain); break;
      case 'snow-add': {
        engine.pause();
        const result = engine.addSnow(request.meters, request.area);
        engine.settlePresentation();
        publish(request.requestId, true, false, false, result);
        return;
      }
      case 'pause': engine.pause(); break;
      case 'play': engine.play(); break;
      case 'speed': engine.setSpeed(request.speed); break;
      case 'cancel': engine.cancelAdvance(); break;
      case 'follow': engine.follow(); break;
      case 'select': engine.select(request.id, request.autoTrack); break;
      case 'acknowledge': engine.acknowledge(request.id); break;
      case 'signal': engine.signal(request.signal); break;
      case 'checkpoint': engine.pause(); break;
    }
    engine.settlePresentation();
    const weatherCoverage = request.type === 'weather' && request.hours.length ? {
      acceptedFrom: request.hours.reduce((min, hour) => Date.parse(hour.at) < Date.parse(min.at) ? hour : min).at,
      acceptedTo: request.hours.reduce((max, hour) => Date.parse(hour.at) > Date.parse(max.at) ? hour : max).at,
    } : undefined;
    if (weatherCoverage) {
      publishWeatherAck(request.requestId, weatherCoverage.acceptedFrom, weatherCoverage.acceptedTo);
      // Read-only weather updates must not silently stop a running bulk operation.
      if (engine.state.advance?.state === 'running') {
        activeRequest = request.requestId; work = engine.advanceTo(Date.parse(engine.state.advance.request.target), true); pump(operation);
      }
      return;
    }
    const resortGeometryChanged = request.type === 'resort' && engine.geometryRevision !== geometryRevisionBeforeResort;
    publish(request.requestId,
      request.type === 'checkpoint' || request.type === 'cancel' || request.type === 'pause',
      request.type === 'checkpoint', request.type === 'resort' && resortGeometryChanged, undefined, weatherCoverage);
    // Read-only inspection and acknowledgment must not silently cancel a pending advance.
    if (engine.state.advance?.state === 'running' && ['select', 'acknowledge', 'weather', 'terrain', 'resort', 'signal'].includes(request.type)) {
      activeRequest = request.requestId; work = engine.advanceTo(Date.parse(engine.state.advance.request.target), true); pump(operation);
    }
  } catch (error) {
    stop(); engine?.pause();
    self.postMessage({ generation: request.generation, id: request.requestId, type: 'error',
      committedRevision: engine?.state.clock.revision ?? 0, operationGeneration: operation,
      error: error instanceof Error ? error.message : 'Simulation command failed.' } satisfies DualWorkerResponse);
  }
};
