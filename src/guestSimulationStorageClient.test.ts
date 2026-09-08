import { beforeEach, describe, expect, it, vi } from 'vitest';

describe('guest simulation storage client', () => {
  beforeEach(() => {
    vi.resetModules();
    if (typeof window !== 'undefined') delete (window as Window & { desktop?: unknown }).desktop;
  });

  it('does not create a browser persistence fallback', async () => {
    const client = await import('./guestSimulationStorageClient');
    await expect(client.loadGuestSimulationCheckpoint('save-a', '2026-01-01T00:00:00Z')).resolves.toEqual({ status: 'missing' });
    await expect(client.saveGuestSimulationCheckpoint('save-a', '2026-01-01T00:00:00Z', Uint8Array.of(1, 2, 3))).resolves.toEqual({
      ok: false, error: 'Guest simulation persistence requires the desktop app.',
    });
  });
});
