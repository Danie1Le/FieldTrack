import { defineConfig, devices } from '@playwright/test';

/**
 * The browser smoke test (#49), run against a fresh docker compose stack: `npx playwright test`.
 * Addresses default to compose's ports; see e2e/global-setup.ts for the environment it needs.
 */
export default defineConfig({
  testDir: 'e2e',
  globalSetup: './e2e/global-setup.ts',
  // Generous but bounded: a shared CI runner can be slow, a hang must still fail.
  timeout: 2 * 60_000,
  expect: { timeout: 30_000 },
  // No retries: a smoke test that needs one is flaky and should fail loudly.
  retries: 0,
  forbidOnly: !!process.env['CI'],
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:8081',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
