import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  forbidOnly: process.env['CI'] != null,
  retries: process.env['CI'] != null ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: process.env['CI'] != null ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    testIdAttribute: 'data-test',
  },
});
