/// <reference lib="webworker" />
import { compileTrailPresentation } from '../trailPresentation';
import type { TrailPresentationRequest, TrailPresentationResponse } from './trailPresentationProtocol';

self.onmessage = (event: MessageEvent<TrailPresentationRequest>) => {
  const request = event.data;
  try {
    if (request.type !== 'compile') throw new Error('Unknown trail presentation request.');
    post({ id: request.id, ok: true, result: compileTrailPresentation(request.input) });
  } catch (error) {
    post({ id: request.id, ok: false,
      error: error instanceof Error ? error.message : 'Trail presentation failed.' });
  }
};

function post(response: TrailPresentationResponse): void {
  self.postMessage(response);
}
