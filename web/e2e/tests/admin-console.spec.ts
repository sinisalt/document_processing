import { test, expect, type Page } from "@playwright/test";
import { adminUrl, apiUrl } from "../playwright.config";
import { reachable, signIn } from "./support/dev-api";

/**
 * Admin console against the real API in dev mode (README "Running without Docker"): a SuperAdmin creates a tenant
 * through the UI and finds it in the list, then acts inside seeded tenant A through the tenant switcher and sees its
 * suppliers. Opt in with E2E_API_URL (and E2E_ADMIN_URL when the console is not on the default port).
 */

const TENANT_A_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

async function signInAsSuperAdmin(page: Page) {
  await signIn(page, "superadmin", "/", adminUrl);
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Dashboard");
}

test.describe("admin console", () => {
  test.beforeEach(async () => {
    test.skip(!process.env.E2E_API_URL, "E2E_API_URL is not set: start the API in dev mode and the admin console first.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable (API not running, or dev auth disabled).`);
    test.skip(!(await reachable(adminUrl)), `${adminUrl} is not reachable (run pnpm dev:admin).`);
  });

  test("a superadmin creates a tenant and finds it in the list", async ({ page }) => {
    const suffix = Date.now().toString(36);
    const name = `E2E Bistro ${suffix}`;
    await signInAsSuperAdmin(page);

    await page.getByRole("navigation").getByRole("link", { name: "Tenants" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Tenants" })).toBeVisible();
    await page.getByRole("button", { name: "New tenant" }).click();

    const dialog = page.getByRole("dialog", { name: "Create a tenant" });
    await dialog.getByLabel("Name", { exact: true }).fill(name);
    await expect(dialog.getByLabel("Slug")).toHaveValue(`e2e-bistro-${suffix}`);
    await dialog.getByLabel("Legal name").fill(`${name} GmbH`);
    await dialog.getByLabel("VAT ID").fill("DE999999999");
    await dialog.getByLabel("Currency").fill("EUR");
    await dialog.getByLabel("Country").fill("AT");
    await dialog.getByLabel("Time zone").fill("Europe/Vienna");
    await dialog.getByRole("button", { name: "Create tenant" }).click();

    // The console opens the new tenant, then the list shows it (searchable by name).
    await expect(page.getByRole("heading", { level: 1 })).toContainText(name);
    await page.getByRole("link", { name: "All tenants" }).click();
    await page.getByRole("searchbox", { name: "Search by name, slug or legal name" }).fill(suffix);
    const table = page.getByRole("table", { name: "Tenants of the platform" });
    await expect(table.getByRole("link", { name })).toBeVisible();
    await expect(table.getByRole("row").filter({ hasText: name })).toContainText("Active");
  });

  test("a superadmin switches into tenant A and sees its suppliers", async ({ page }) => {
    await signInAsSuperAdmin(page);

    const switcher = page.getByRole("banner").getByRole("combobox", { name: "Act as tenant" });
    await switcher.click();
    await page.getByRole("option", { name: "Gasthaus zur Post GmbH" }).click();
    await expect(switcher).toContainText("Gasthaus zur Post GmbH");

    await page.goto(`${adminUrl}/tenants/${TENANT_A_ID}?tab=suppliers`);
    await expect(page.getByRole("tab", { name: "Suppliers", selected: true })).toBeVisible();
    const suppliers = page.getByRole("table", { name: "Suppliers of the tenant" });
    await expect(suppliers.getByRole("row").filter({ hasText: "METRO Deutschland GmbH" })).toBeVisible();
    await expect(suppliers.getByRole("row").filter({ hasText: "Getränke Hoffmann GmbH" })).toBeVisible();
  });
});
