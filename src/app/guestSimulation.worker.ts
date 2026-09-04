/// <reference lib="webworker" />
import { GuestSimulationWorkerEngine } from './guestSimulationWorkerEngine';
import { type GuestSimulationWorkerRequest, type GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';

const runtime = new GuestSimulationWorkerEngine();
self.onmessage = (event: MessageEvent<GuestSimulationWorkerRequest>) => {
  const response = runtime.handle(event.data);
  self.postMessage(response, responseTransferables(response));
};

/** Transfer only newly allocated binary columns; no rich state crosses routine advances. */
function responseTransferables(response: GuestSimulationWorkerResponse): Transferable[] {
  const buffers: ArrayBuffer[] = [];
  if (response.type === 'advanced' && 'renderFrame' in response) {
    buffers.push(response.renderFrame.ids.buffer as ArrayBuffer,
      response.renderFrame.edgeIndices.buffer as ArrayBuffer,
      response.renderFrame.progress.buffer as ArrayBuffer,
      response.renderFrame.statusFlags.buffer as ArrayBuffer);
  }
  if (response.type === 'checkpoint') buffers.push(response.bytes.buffer as ArrayBuffer);
  return [...new Set(buffers)];
}
