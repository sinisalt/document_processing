import { test, expect, type Page } from "@playwright/test";
import { adminUrl, apiUrl, portalUrl } from "../playwright.config";
import { devUser, reachable, type DevUser } from "./support/dev-api";

/**
 * Smoke: the development sign-in page of each app lists GET /dev/users, one click signs in through POST /dev/token, and
 * the header shows who is signed in. Needs the API in dev mode (README "Running without Docker") and both Vite dev
 * servers; set E2E_API_URL (and E2E_PORTAL_URL / E2E_ADMIN_URL when not on the default ports) to opt in.
 */

async function signInAs(page: Page, appUrl: string, user: DevUser) {
  await page.goto(`${appUrl}/sign-in`);
  await page.getByRole("button", { name: `Sign in as ${user.displayName}` }).click();
  // The shell redirects to the dashboard once /me answered; the header (role=banner) names the user.
  const banner = page.getByRole("banner");
  await expect(banner.getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Dashboard");
  return banner;
}

test.describe("dev sign-in", () => {
  test.beforeEach(async () => {
    test.skip(!process.env.E2E_API_URL, "E2E_API_URL is not set: start the API in dev mode and the two apps, then set E2E_API_URL, E2E_PORTAL_URL and E2E_ADMIN_URL.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable (API not running, or dev auth disabled).`);
  });

  test("customer portal: tenant-a-admin sees their name in the header", async ({ page }) => {
    test.skip(!(await reachable(portalUrl)), `${portalUrl} is not reachable (run pnpm dev:portal).`);
    const user = await devUser("tenant-a-admin");
    const banner = await signInAs(page, portalUrl, user);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByText(`Welcome, ${user.displayName}`)).toBeVisible();

    await banner.getByRole("button", { name: `Account menu for ${user.displayName}` }).click();
    await expect(page.getByRole("menu")).toContainText("Tenant admin");
    await expect(page.getByRole("menu")).toContainText("Tenant scope");
    await page.keyboard.press("Escape");

    // Still signed in after a reload (sessionStorage), and signed out after choosing Sign out.
    await page.reload();
    await expect(page.getByRole("banner").getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
    await page.getByRole("button", { name: `Account menu for ${user.displayName}` }).click();
    await page.getByRole("menuitem", { name: "Sign out" }).click();
    await expect(page).toHaveURL(/\/sign-in/);
  });

  test("admin console: superadmin sees their name and the tenant switcher", async ({ page }) => {
    test.skip(!(await reachable(adminUrl)), `${adminUrl} is not reachable (run pnpm dev:admin).`);
    const user = await devUser("superadmin");
    const banner = await signInAs(page, adminUrl, user);
    await expect(banner.getByRole("combobox", { name: "Act as tenant" })).toBeVisible();
  });

  test("an anonymous visitor is sent to sign-in and back", async ({ page }) => {
    test.skip(!(await reachable(portalUrl)), `${portalUrl} is not reachable (run pnpm dev:portal).`);
    const user = await devUser("tenant-a-reviewer");
    await page.goto(`${portalUrl}/?from=deep-link`);
    await expect(page).toHaveURL(/\/sign-in\?redirect=/);
    await page.getByRole("button", { name: `Sign in as ${user.displayName}` }).click();
    await expect(page).toHaveURL(/\/\?from=deep-link$/);
    await expect(page.getByRole("banner").getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
  });
});
