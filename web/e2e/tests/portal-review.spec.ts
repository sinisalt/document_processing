import { test, expect, type Browser } from "@playwright/test";
import { apiUrl, portalUrl } from "../playwright.config";
import { FIXTURE_NUMBER, SUPPLIER_NAME, SUPPLIER_VAT, devToken, ensureAlwaysReviewSupplier, fixture, reachable, signIn, uploadInvoice } from "./support/dev-api";

/**
 * Customer portal, end to end against the real API (dev mode: SQLite, in-process pipeline, dev tokens): a tenant admin
 * marks the fixture's supplier "always review"; a reviewer uploads an XRechnung UBL invoice through the inbox, waits for
 * the pipeline to leave Processing, opens the document in the review screen, checks the number, totals and line items,
 * and approves it (Ctrl+Enter opens the confirmation). In another tenant, a viewer opens a document uploaded through
 * the API and sees the review screen read-only, and the review screen fits a 390 px phone.
 *
 * Opt in with E2E_API_URL and E2E_PORTAL_URL (README "Running without Docker"; `vite --port <n>` with VITE_API_PROXY).
 */

/** A document of tenant B waiting for review, uploaded by its admin through the API (the viewer cannot upload). */
async function tenantBDocument(prefix: string) {
  const token = await devToken("tenant-b-admin");
  await ensureAlwaysReviewSupplier(token);
  const number = `${prefix}-${Date.now()}`;
  const document = await uploadInvoice(token, number);
  expect(document.status).toBe("NeedsReview");
  return { ...document, number };
}

/** The suppliers screen as a tenant admin: the fixture's supplier exists and has "always review" on. */
async function suppliersAlwaysReview(browser: Browser) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, "tenant-a-admin", "/suppliers");
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
  await context.close();
}

test.describe("customer portal review flow", () => {
  test.beforeEach(async () => {
    test.skip(!process.env.E2E_API_URL || !process.env.E2E_PORTAL_URL, "Set E2E_API_URL and E2E_PORTAL_URL to run against a dev-mode API and the portal dev server.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable.`);
    test.skip(!(await reachable(portalUrl)), `${portalUrl} is not reachable.`);
  });

  test("upload a UBL invoice, review it and approve it", async ({ browser, page }) => {
    test.setTimeout(120_000);
    // A unique number per run: the API deduplicates uploads by SHA-256, and the review screen is found by it.
    const number = `E2E-${Date.now()}`;
    const xml = fixture.replace(`<cbc:ID>${FIXTURE_NUMBER}</cbc:ID>`, `<cbc:ID>${number}</cbc:ID>`);
    expect(xml).toContain(number);

    // 1. Suppliers, as the tenant admin: the fixture's supplier is always reviewed (else XML invoices auto-approve).
    await suppliersAlwaysReview(browser);

    // Everything after that is a reviewer's job: upload, correct, claim, approve.
    await signIn(page, "tenant-a-reviewer", "/documents");
    await expect(page.getByRole("heading", { level: 1, name: "Inbox" })).toBeVisible();
    await expect(page.getByRole("navigation").getByRole("link", { name: "Settings" })).toHaveCount(0);

    // 2. Inbox: upload through the dialog's file input.
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
    // Amounts as the invoice prints them, with two decimals.
    await expect(form.getByLabel("Total (gross)")).toHaveValue("142.80");
    await expect(form.getByLabel("Subtotal (net)")).toHaveValue("130.00");
    await expect(form.locator('[data-section="supplier"]').getByLabel("VAT ID")).toHaveValue(SUPPLIER_VAT);
    const grid = page.getByTestId("line-item-grid");
    await expect(grid.getByRole("table", { name: "Line items" })).toBeVisible();
    await expect(grid.locator("tr[data-arithmetic]").first()).toHaveAttribute("data-arithmetic", "ok");
    // Linux Chromium: the approve button names Ctrl+Enter, not the Mac's ⌘↵.
    await expect(page.getByTestId("approve-shortcut")).toHaveText("Ctrl+Enter");

    // The fixture passes every rule (it is in review only because its supplier is "always review"), so there are no
    // flags to walk with j/k here; ReviewScreen.test.tsx covers j/k. Validate ran on what is shown, so the empty list
    // may say that every rule passed.
    const flagsEmpty = page.getByTestId("flags-empty");
    await expect(flagsEmpty).toHaveText("No flags: every validation rule passed.");

    // Esc closes an open select (Radix) and nothing more: the screen's own Esc ("back to the inbox") stays out of it.
    const kind = form.getByRole("combobox", { name: "Kind" });
    await kind.click();
    await expect(page.getByRole("listbox")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("listbox")).toBeHidden();
    await expect(page).toHaveURL(/\/documents\/[0-9a-f-]{36}$/);

    // A correction: only the changed path is sent, the API marks it Corrected, approve waits until it is saved.
    const dueDate = form.getByLabel("Due date", { exact: true });
    // Typed as the English UI writes dates; the correction is sent as the ISO date.
    await dueDate.fill("12/15/2026");
    await expect(page.getByTestId("approve-button")).toBeDisabled();
    // Unsaved: the first Esc leaves the input, the second would leave the screen and asks first.
    await page.keyboard.press("Escape");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("alertdialog", { name: "Discard your corrections?" })).toBeVisible();
    await page.getByRole("button", { name: "Keep editing" }).click();
    await expect(page.getByRole("alertdialog")).toBeHidden();
    await expect(page).toHaveURL(/\/documents\/[0-9a-f-]{36}$/);
    await expect(dueDate).toHaveValue("12/15/2026");
    const patch = page.waitForRequest((request) => request.method() === "PATCH" && request.url().endsWith("/fields"));
    await page.getByTestId("save-button").click();
    expect((await patch).postDataJSON()).toEqual({ corrections: [{ path: "dueDate", value: "2026-12-15", reason: null }] });
    await expect(page.getByText("1 correction saved.")).toBeVisible();
    await expect(form.getByLabel("Due date", { exact: true })).toHaveValue("12/15/2026");
    await expect(form.locator('[data-field="dueDate"]')).toHaveAttribute("data-field-status", "Corrected");
    await expect(form.locator('[data-field="dueDate"]')).toContainText("Corrected");
    await expect(page.getByTestId("approve-button")).toBeEnabled();
    // The API does not re-validate a correction, so the empty flag list no longer claims that every rule passed.
    await expect(flagsEmpty).toHaveText("Corrections not re-validated: reprocess from Validate to re-check.");

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
    test.setTimeout(120_000);
    const document = await tenantBDocument("E2E-VIEW");

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

    // The review queue by direct link: an answer, without a request that would end in a 403 toast.
    const queueRequests: string[] = [];
    page.on("request", (request) => {
      if (request.url().includes("/api/v1/review-tasks")) queueRequests.push(request.url());
    });
    await page.goto(`${portalUrl}/review`);
    await expect(page.getByText("The review queue is for reviewers")).toBeVisible();
    await expect(page.getByText("The queue is empty")).toHaveCount(0);
    expect(queueRequests).toEqual([]);

    // The review screen of a document waiting for review: everything visible, nothing to change or decide.
    await page.goto(`${portalUrl}/documents/${document.id}`);
    const screen = page.getByTestId("review-screen");
    await expect(screen).toHaveAttribute("data-status", "NeedsReview");
    await expect(page.getByTestId("document-number")).toHaveText(document.number);
    const form = page.getByRole("form", { name: "Canonical data" });
    await expect(form.getByLabel("Invoice number")).toHaveValue(document.number);
    await expect(form.getByLabel("Invoice number")).toBeDisabled();
    await expect(form.getByLabel("Total (gross)")).toBeDisabled();
    await expect(form.locator("input:not([disabled])")).toHaveCount(0);
    for (const name of ["Save corrections", "Approve", "Reject", "Reprocess"]) {
      await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0);
    }
    for (const id of ["save-button", "approve-button", "reject-button", "reprocess-button"]) {
      await expect(page.getByTestId(id)).toHaveCount(0);
    }
    // Ctrl+Enter opens no approval for a viewer.
    await page.getByRole("heading", { level: 1 }).click();
    await page.keyboard.press("Control+Enter");
    await expect(page.getByRole("alertdialog")).toHaveCount(0);
  });

  test("the review screen fits a phone", async ({ browser }) => {
    test.setTimeout(120_000);
    const phoneDocument = await tenantBDocument("E2E-PHONE");
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const page = await context.newPage();
    await signIn(page, "tenant-b-admin", `/documents/${phoneDocument.id}`);
    await expect(page.getByTestId("review-screen")).toHaveAttribute("data-status", "NeedsReview");
    const form = page.getByRole("form", { name: "Canonical data" });
    await expect(form.getByLabel("Invoice number")).toHaveValue(phoneDocument.number);

    // No sideways scrolling, and every action is on screen.
    const size = await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth }));
    expect(size.scrollWidth).toBeLessThanOrEqual(size.innerWidth);
    for (const id of ["save-button", "approve-button", "reject-button", "reprocess-button"]) {
      const box = await page.getByTestId(id).boundingBox();
      expect(box, id).not.toBeNull();
      expect(box!.x, id).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width, id).toBeLessThanOrEqual(size.innerWidth);
    }
    // The page viewer sits above the form instead of beside it.
    const viewer = await page.getByTestId("viewer-panel").boundingBox();
    const fields = await form.boundingBox();
    expect(viewer).not.toBeNull();
    expect(fields).not.toBeNull();
    expect(viewer!.y + viewer!.height).toBeLessThanOrEqual(fields!.y);
    await context.close();
  });
});
