import { _electron as electron, expect, test } from '@playwright/test';

test('packaged Electron release opens the main menu', async () => {
  const executablePath = process.env.ELECTRON_RELEASE_PATH;
  test.skip(
    process.env.RUN_ELECTRON_E2E !== '1' || !executablePath,
    'Set RUN_ELECTRON_E2E=1 and ELECTRON_RELEASE_PATH to run the release smoke test.',
  );

  const application = await electron.launch({ executablePath: executablePath! });
  try {
    const window = await application.firstWindow();
    await expect(window.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
    await expect(window.getByRole('button', { name: 'New Resort' })).toBeEnabled();

    await application.evaluate(({ session }) => {
      const probe = globalThis as typeof globalThis & { overpassIdentityProbe?: string | null };
      probe.overpassIdentityProbe = null;
      session.defaultSession.webRequest.onSendHeaders(
        { urls: ['https://overpass-api.de/api/interpreter'] },
        (details) => {
          const entry = Object.entries(details.requestHeaders)
            .find(([name]) => name.toLowerCase() === 'user-agent');
          probe.overpassIdentityProbe = entry?.[1] ?? null;
        },
      );
    });
    await window.evaluate(() => {
      void fetch('https://overpass-api.de/api/interpreter', {
        method: 'POST',
        headers: { 'Content-Type': 'text/plain' },
        body: '[out:json][timeout:5];node(43.47,-110.78,43.471,-110.779);out 1;',
      }).catch(() => {});
    });
    await expect.poll(() => application.evaluate(() =>
      (globalThis as typeof globalThis & { overpassIdentityProbe?: string | null })
        .overpassIdentityProbe,
    )).toMatch(/^Mountain-Planner\/.+\(\+https:\/\/github\.com\/ljs294\/ski-area-design-challenge\)$/);
  } finally {
    await application.close();
  }
});
