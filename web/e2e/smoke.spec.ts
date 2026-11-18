import { expect, test } from '@playwright/test';
import { faultyAsset, ruleName } from './global-setup';

test('signs in, loads the map and shows the Alert of a simulated Fault', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign in as Demo Operator' }).click();

  // Leaflet titles each marker "<Asset name> · <Asset Status>".
  await expect(page.locator('.leaflet-container')).toBeVisible();
  await expect(page.locator(`.leaflet-marker-icon[title^="${faultyAsset} ·"]`)).toBeVisible();

  const alert = page.locator('li.alert').filter({ hasText: faultyAsset }).filter({ hasText: ruleName });
  await expect(alert).toBeVisible();
  await expect(alert).toContainText('Open');
});
