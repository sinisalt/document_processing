import { readFileSync } from "node:fs";
import { test, expect, type Page } from "@playwright/test";
import { apiUrl, portalUrl } from "../playwright.config";

/**
 * Customer portal, end to end against the real API (dev mode: SQLite, in-process pipeline, dev tokens): a tenant admin
 * marks the fixture's supplier "always review", uploads an XRechnung UBL invoice through the inbox, waits for the
 * pipeline to leave Processing, opens the document in the review screen, checks the number, totals and line items,
 * and approves it (Ctrl+Enter opens the confirmation). A viewer of another tenant sees the screens read-only.
 *
 * Opt in with E2E_API_URL and E2E_PORTAL_URL (README "Running without Docker"; `vite --port <n>` with VITE_API_PROXY).
 */

const fixture = readFileSync(new URL("../fixtures/xrechnung-ubl-invoice.xml", import.meta.url), "utf8");
const FIXTURE_NUMBER = "2026/4471";
const SUPPLIER_NAME = "Getränke Huber GmbH & Co. KG";
const SUPPLIER_VAT = "DE276543210";

interface DevUser {
  username: string;
  displayName: string;
}

async function reachable(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(3_000) });
    return response.status < 500;
  } catch {
    return false;
  }
}

async function devUser(username: string): Promise<DevUser> {
  const users = (await (await fetch(`${apiUrl}/dev/users`)).json()) as DevUser[];
  const user = users.find((u) => u.username === username);
  if (!user) throw new Error(`Dev user '${username}' is not in GET /dev/users`);
  return user;
}

async function signIn(page: Page, username: string, path = "/") {
  const user = await devUser(username);
  await page.goto(`${portalUrl}/sign-in?redirect=${encodeURIComponent(path)}`);
  await page.getByRole("button", { name: `Sign in as ${user.displayName}` }).click();
  await expect(page.getByRole("banner").getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
  return user;
}

test.describe("customer portal review flow", () => {
  test.beforeEach(async () => {
    test.skip(!process.env.E2E_API_URL || !process.env.E2E_PORTAL_URL, "Set E2E_API_URL and E2E_PORTAL_URL to run against a dev-mode API and the portal dev server.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable.`);
    test.skip(!(await reachable(portalUrl)), `${portalUrl} is not reachable.`);
  });

  test("upload a UBL invoice, review it and approve it", async ({ page }) => {
    test.setTimeout(120_000);
    // A unique number per run: the API deduplicates uploads by SHA-256, and the review screen is found by it.
    const number = `E2E-${Date.now()}`;
    const xml = fixture.replace(`<cbc:ID>${FIXTURE_NUMBER}</cbc:ID>`, `<cbc:ID>${number}</cbc:ID>`);
    expect(xml).toContain(number);

    await signIn(page, "tenant-a-admin", "/suppliers");

    // 1. Suppliers: make sure the fixture's supplier exists and is always reviewed (else XML invoices auto-approve).
    await expect(page.getByRole("heading", { level: 1, name: "Suppliers" })).toBeVisible();
    await page.getByRole("textbox", { name: "Search" }).fill(SUPPLIER_VAT);
    await page.getByRole("search").getByRole("button", { name: "Search" }).click();
    const supplierTable = page.getByRole("table", { name: "Suppliers" });
    await expect(supplierTable.getByRole("row").nth(1)).toBeVisible();
    const existing = supplierTable.getByRole("row", { name: new RegExp(SUPPLIER_NAME.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")) });
    if ((await existing.count()) === 0) {
      await page.getByRole("button", { name: "New supplier" }).first().click();
      const sheet = page.getByRole("dialog", { name: "New supplier" });
      await sheet.getByRole("textbox", { name: "Name" }).fill(SUPPLIER_NAME);
      await sheet.getByRole("textbox", { name: "VAT ID" }).fill(SUPPLIER_VAT);
      await sheet.getByRole("switch", { name: "Always review" }).click();
      await sheet.getByRole("button", { name: "New supplier" }).click();
      await expect(page.getByText(`Supplier ${SUPPLIER_NAME} created.`)).toBeVisible();
    } else {
      await existing.first().click();
      const sheet = page.getByRole("dialog", { name: SUPPLIER_NAME });
      const always = sheet.getByRole("switch", { name: "Always review" });
      if ((await always.getAttribute("aria-checked")) !== "true") {
        await always.click();
        await sheet.getByRole("button", { name: "Save" }).click();
        await expect(page.getByText(`Supplier ${SUPPLIER_NAME} saved.`)).toBeVisible();
      } else {
        await page.keyboard.press("Escape");
      }
    }

    // 2. Inbox: upload through the dialog's file input.
    await page.getByRole("navigation").getByRole("link", { name: "Inbox" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Inbox" })).toBeVisible();
    await page.getByRole("button", { name: "Upload", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Upload documents" });
    await dialog.getByLabel("Choose files", { exact: true }).setInputFiles({ name: `${number}.xml`, mimeType: "application/xml", buffer: Buffer.from(xml, "utf8") });
    await expect(dialog.getByRole("list", { name: "1 file selected" })).toContainText(`${number}.xml`);
    await dialog.getByRole("button", { name: "Upload 1 file" }).click();
    await expect(page.getByText("1 file received; processing has started.")).toBeVisible();
    await expect(dialog).toBeHidden();

    // 3. Wait in the "All" tab until the pipeline is done with it (the list polls every 3 s while anything is in flight).
    await page.getByRole("tab", { name: "All" }).click();
    await page.getByLabel("Search", { exact: true }).fill(number);
    await page.getByRole("button", { name: "Apply" }).click();
    const row = page.getByRole("table", { name: "Documents" }).getByRole("row", { name: new RegExp(number) });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await expect(row).toContainText("Needs review", { timeout: 60_000 });
    await expect(row).toContainText("€142.80");

    // 4. Review screen.
    await row.click();
    await expect(page).toHaveURL(/\/documents\/[0-9a-f-]{36}$/);
    const screen = page.getByTestId("review-screen");
    await expect(screen).toHaveAttribute("data-status", "NeedsReview");
    await expect(page.getByTestId("document-number")).toHaveText(number);
    await expect(page.getByTestId("document-total")).toHaveText("€142.80");
    const form = page.getByRole("form", { name: "Canonical data" });
    await expect(form.getByLabel("Invoice number")).toHaveValue(number);
    await expect(form.getByLabel("Total (gross)")).toHaveValue("142.8");
    await expect(form.getByLabel("Subtotal (net)")).toHaveValue("130");
    await expect(form.locator('[data-section="supplier"]').getByLabel("VAT ID")).toHaveValue(SUPPLIER_VAT);
    const grid = page.getByTestId("line-item-grid");
    await expect(grid.getByRole("table", { name: "Line items" })).toBeVisible();
    await expect(grid.locator("tr[data-arithmetic]").first()).toHaveAttribute("data-arithmetic", "ok");

    // A correction: only the changed path is sent, the API marks it Corrected, approve waits until it is saved.
    const dueDate = form.getByLabel("Due date", { exact: true });
    await dueDate.fill("2026-12-15");
    await expect(page.getByTestId("approve-button")).toBeDisabled();
    const patch = page.waitForRequest((request) => request.method() === "PATCH" && request.url().endsWith("/fields"));
    await page.getByTestId("save-button").click();
    expect((await patch).postDataJSON()).toEqual({ corrections: [{ path: "dueDate", value: "2026-12-15", reason: null }] });
    await expect(page.getByText("1 correction saved.")).toBeVisible();
    await expect(form.getByLabel("Due date", { exact: true })).toHaveValue("2026-12-15");
    await expect(form.locator('[data-field="dueDate"]')).toHaveAttribute("data-field-status", "Corrected");
    await expect(form.locator('[data-field="dueDate"]')).toContainText("Corrected");
    await expect(page.getByTestId("approve-button")).toBeEnabled();

    // Timeline tab lists the pipeline's events and the correction.
    await page.getByRole("tab", { name: "Timeline" }).click();
    await expect(page.getByTestId("timeline")).toContainText("Review requested");
    await expect(page.getByTestId("timeline")).toContainText("Fields corrected");
    await page.getByRole("tab", { name: "Fields" }).click();
    const reviewUrl = page.url();

    // 5. Review queue: claim the task, see it under "Only mine", release it, and open the document from there.
    await page.getByRole("navigation").getByRole("link", { name: "Review queue" }).click();
    const task = page.getByRole("table", { name: "Review tasks" }).getByRole("row", { name: new RegExp(number) });
    await expect(task).toContainText("Unassigned");
    await task.getByRole("button", { name: `Claim ${number}` }).click();
    await expect(page.getByText("Task claimed.")).toBeVisible();
    await expect(task).toContainText("You");
    await page.getByRole("checkbox", { name: "Only mine" }).check();
    await expect(task).toBeVisible();
    await task.getByRole("button", { name: `Release ${number}` }).click();
    await expect(page.getByText("Task released.")).toBeVisible();
    await expect(page.getByRole("table", { name: "Review tasks" }).getByRole("row", { name: new RegExp(number) })).toHaveCount(0);
    await page.getByRole("checkbox", { name: "Only mine" }).uncheck();
    await task.getByRole("link", { name: `Open ${number}` }).click();
    await expect(page).toHaveURL(reviewUrl);
    await expect(screen).toHaveAttribute("data-status", "NeedsReview");

    // 6. Approve: Ctrl+Enter opens the confirmation, confirming moves the document to Approved.
    await page.getByRole("heading", { level: 1 }).click();
    await page.keyboard.press("Control+Enter");
    const confirm = page.getByRole("alertdialog", { name: "Approve this document?" }).or(page.getByRole("dialog", { name: "Approve this document?" }));
    await expect(confirm).toBeVisible();
    await confirm.getByRole("button", { name: "Approve" }).click();
    await expect(page.getByText("Document approved.")).toBeVisible();
    await expect(screen).toHaveAttribute("data-status", "Approved");
    await expect(page.getByText("This document is settled")).toBeVisible();
    await expect(form.getByLabel("Invoice number")).toBeDisabled();
    await expect(page.getByTestId("approve-button")).toBeDisabled();

    // 7. Back in the inbox (Esc), the document is under Approved.
    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(/\/documents(\?|$)/);
    await page.goto(`${portalUrl}/documents?tab=approved&q=${encodeURIComponent(number)}`);
    await expect(page.getByRole("table", { name: "Documents" }).getByRole("row", { name: new RegExp(number) })).toContainText("Approved");
  });

  test("a viewer sees read-only screens", async ({ page }) => {
    await signIn(page, "tenant-b-viewer", "/documents");
    await expect(page.getByRole("heading", { level: 1, name: "Inbox" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Upload", exact: true })).toHaveCount(0);
    const nav = page.getByRole("navigation");
    await expect(nav.getByRole("link", { name: "Inbox" })).toBeVisible();
    await expect(nav.getByRole("link", { name: "Review queue" })).toHaveCount(0);
    await expect(nav.getByRole("link", { name: "Settings" })).toHaveCount(0);
    await nav.getByRole("link", { name: "Suppliers" }).click();
    await expect(page.getByRole("heading", { level: 1, name: "Suppliers" })).toBeVisible();
    await expect(page.getByRole("button", { name: "New supplier" })).toHaveCount(0);
  });
});
