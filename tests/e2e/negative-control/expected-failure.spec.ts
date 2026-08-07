import { expect, test } from '@playwright/test';

test('E2E_NEGATIVE_CONTROL_EXPECTED_FAILURE', () => {
  expect(true, 'E2E_NEGATIVE_CONTROL_SENTINEL').toBe(false);
});
