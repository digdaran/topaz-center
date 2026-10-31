import { defineConfig, devices } from '@playwright/test';

// E2E against a running stack: E2E_BASE_URL (default http://localhost:8080), E2E_LOGIN / E2E_PASSWORD of an owner.
export default defineConfig({
  testDir: 'e2e',
  timeout: 60_000,
  retries: 0,
  reporter: [['list']],
  use: { baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8080', locale: 'ru-RU', timezoneId: 'Europe/Moscow', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'mobile', use: { ...devices['Pixel 7'] } },
  ],
});
