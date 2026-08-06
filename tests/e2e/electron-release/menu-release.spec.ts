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
    await expect(window.getByRole('button', { name: 'New Game' })).toBeEnabled();
  } finally {
    await application.close();
  }
});
