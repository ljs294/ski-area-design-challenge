import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchElevationGrid } from './elevation';

describe('elevation cancellation', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('passes cancellation to the network request without retrying', async () => {
    const fetchMock = vi.fn((_url: string, init?: RequestInit) => {
      if (init?.signal?.aborted) return Promise.reject(new DOMException('cancelled', 'AbortError'));
      return new Promise<Response>((_resolve, reject) =>
        init?.signal?.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')), { once: true })
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    controller.abort();
    await expect(fetchElevationGrid(
      { west: -121.5, south: 46.9, east: -121.48, north: 46.92 },
      2000,
      undefined,
      controller.signal
    )).rejects.toMatchObject({ name: 'AbortError' });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
