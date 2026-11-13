import { defineConfig } from '@playwright/test';

import base from '../playwright.config.js';

export default defineConfig({
  ...base,
  testDir: '.',
  timeout: 120_000,
  expect: { timeout: 60_000 },
});
