import { expect, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

test('the diagnostic console opens and runs commands in the gameplay shell', async ({ page }) => {
  await seedPreparedResort(page);
  await page.goto('/?flat&dev-console', { waitUntil: 'load' });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await expect(page.getByRole('button', { name: /Open developer console/ })).toBeVisible();
  await page.keyboard.press('F10');
  const console = page.getByRole('dialog', { name: 'Developer console' });
  await expect(console).toBeVisible();
  const command = console.getByRole('textbox', { name: 'Developer command' });
  await command.fill('time');
  await command.press('Enter');
  await expect(console).toContainText('2026');
  await command.fill('help');
  await command.press('Enter');
  await expect(console).toContainText('skip <duration>');
  await expect(console).toContainText('Jump forward without simulating elapsed world time');
});
