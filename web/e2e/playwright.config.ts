import { defineConfig, devices } from "@playwright/test";

// End-to-end runs against the API in dependency-free dev mode (SQLite + dev tokens) and the two Vite dev servers; how to
// start them: README "Running without Docker" and "Web apps and end-to-end tests". Every test skips without E2E_API_URL.
const apiUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:5100";
const portalUrl = process.env.E2E_PORTAL_URL ?? "http://127.0.0.1:5173";
const adminUrl = process.env.E2E_ADMIN_URL ?? "http://127.0.0.1:5174";

export default defineConfig({
  testDir: "./tests",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    ...devices["Desktop Chrome"],
    viewport: { width: 1440, height: 900 },
    // On the cloud dev image the matching browser is preinstalled; point PLAYWRIGHT_CHROMIUM_PATH at
    // /opt/pw-browsers/chromium-*/chrome-linux64/chrome there. Elsewhere `pnpm exec playwright install chromium` once.
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {},
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium" }],
  metadata: { apiUrl, portalUrl, adminUrl },
});

export { apiUrl, portalUrl, adminUrl };
