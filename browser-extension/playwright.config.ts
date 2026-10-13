import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: 'tests',
  timeout: 120_000,
  expect: { timeout: 30_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }], ['json', { outputFile: 'reports/e2e-results.json' }]],
  use: { trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  globalSetup: './tests/e2e/global-setup.ts',
  projects: [
    { name: 'e2e', testMatch: /e2e\/.*\.spec\.ts/ },
    { name: 'perf', testMatch: /perf\/.*\.spec\.ts/, timeout: 600_000 },
  ],
});
