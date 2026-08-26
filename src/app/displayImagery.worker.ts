/// <reference lib="webworker" />
import { displayImageryDimensions, type DisplayImageryRequest } from './displayImagery';

const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;

scope.onmessage = (event: MessageEvent<DisplayImageryRequest>) => {
  void (async () => {
    try {
      const request = event.data;
      const sourceBytes = request.bytes.byteOffset === 0 &&
        request.bytes.byteLength === request.bytes.buffer.byteLength
        ? request.bytes.buffer as ArrayBuffer
        : request.bytes.slice().buffer as ArrayBuffer;
      const bitmap = await createImageBitmap(new Blob([sourceBytes], { type: request.mimeType }));
      const size = displayImageryDimensions(request.width, request.height, request.maxSide);
      const canvas = new OffscreenCanvas(size.width, size.height);
      const context = canvas.getContext('2d');
      if (!context) throw new Error('Offscreen 2D canvas is unavailable.');
      context.drawImage(bitmap, 0, 0, size.width, size.height);
      bitmap.close();
      const blob = await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.84 });
      const bytes = await blob.arrayBuffer();
      scope.postMessage({ ok: true, bytes }, [bytes]);
    } catch (error) {
      scope.postMessage({ ok: false, error: error instanceof Error ? error.message : String(error) });
    }
  })();
};
