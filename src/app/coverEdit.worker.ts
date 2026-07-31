/// <reference lib="webworker" />
import { processCoverEdit } from './coverEditEngine';
import type { CoverEditRequest } from './coverEditProtocol';

const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;
scope.onmessage = (event: MessageEvent<CoverEditRequest>) => {
  const response = processCoverEdit(event.data);
  if (!response.ok) {
    scope.postMessage(response);
    return;
  }
  const transfers: Transferable[] = [response.gridData.buffer];
  if (response.displayGeometry) transfers.push(response.displayGeometry.buffer);
  scope.postMessage(response, transfers);
};
