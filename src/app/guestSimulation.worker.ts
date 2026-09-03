/// <reference lib="webworker" />
import { GuestSimulationWorkerEngine } from './guestSimulationWorkerEngine';
import type { GuestSimulationWorkerRequest } from './guestSimulationWorkerProtocol';

const runtime = new GuestSimulationWorkerEngine();
self.onmessage = (event: MessageEvent<GuestSimulationWorkerRequest>) => {
  self.postMessage(runtime.handle(event.data));
};
