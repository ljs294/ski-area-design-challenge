import { expect, test as base, type Page } from '@playwright/test';

/**
 * Deterministic browser projects block every non-preview HTTP request. Map tiles
 * are decorative for these shell tests and their expected console failures are
 * deliberately not promoted to product failures. Uncaught page exceptions are.
 */
export const test = base.extend({
  page: async ({ page, baseURL }, provide) => {
    const previewOrigin = new URL(baseURL ?? 'http://127.0.0.1:44173').origin;
    const pageErrors: string[] = [];

    page.on('pageerror', (error) => pageErrors.push(error.message));
    await page.route('**/*', async (route) => {
      const url = new URL(route.request().url());
      if (url.protocol !== 'http:' && url.protocol !== 'https:') {
        await route.continue();
      } else if (url.origin === previewOrigin) {
        await route.continue();
      } else {
        await route.abort('blockedbyclient');
      }
    });

    await provide(page);
    expect(pageErrors, 'the app must not raise uncaught page exceptions').toEqual([]);
  },
});

export { expect } from '@playwright/test';

export async function openMenu(page: Page): Promise<void> {
  await page.goto('/?flat', { waitUntil: 'load' });
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
}
