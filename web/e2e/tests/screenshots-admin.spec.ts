import { mkdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, expect, type Browser, type BrowserContextOptions, type Page } from "@playwright/test";
import { adminUrl, apiUrl } from "../playwright.config";
import { api, devToken, reachable, signIn } from "./support/dev-api";

/**
 * Visual proof of the admin console against the live dev-mode API (README "Running without Docker"): every screen with
 * the seeded demo data, written to e2e/screenshots/admin as NN-<slug>.png in capture order. `pnpm e2e:screenshots`
 * runs it (every title carries @screenshot). Set-up goes through the API only and is idempotent, so a second run on
 * the same database writes the same set of files: a corrupt PDF in tenant B (the dashboard's failed document) and an
 * XRechnung invoice in tenant B waiting for review (the global queue then spans two tenants), each uploaded only when
 * it is not there yet.
 *
 * Opt in with E2E_API_URL and E2E_ADMIN_URL.
 */

const OUT = fileURLToPath(new URL("../screenshots/admin/", import.meta.url));
const TENANT_A_ID = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const TENANT_B_ID = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
const IN_FLIGHT = ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted"];

// Tenant B's failed document: a PDF header followed by garbage, which the normalizer cannot open.
const CORRUPT_PDF_NAME = "scan-kassa-0815.pdf";
const CORRUPT_PDF = Buffer.from("%PDF-1.7\n%corrupt screenshot fixture: truncated before the first object\n1 0 obj << /Type /Catalog", "latin1");

// Tenant B's invoice in review: the e2e fixture under a fixed number, its supplier marked "always review".
const FIXTURE_NUMBER = "2026/4471";
const REVIEW_NUMBER = "GH-2026-10-0042";
const REVIEW_SUPPLIER = { name: "Getränke Huber GmbH & Co. KG", vatId: "DE276543210" };

type Theme = "light" | "dark";
type Language = "en" | "de";

interface DocumentRow {
  id: string;
  status: string;
  documentNumber: string | null;
  tenantId: string;
}

/** Fonts and layout without motion: no transitions, no animations (skeleton pulse, dialog zoom), no blinking caret. */
const NO_MOTION_CSS = `*, *::before, *::after { transition: none !important; animation: none !important; caret-color: transparent !important; scroll-behavior: auto !important; }`;

async function documents(token: string, query: string): Promise<DocumentRow[]> {
  return (await api<{ items: DocumentRow[] }>(token, "GET", `/documents?${query}`)).items;
}

/** Polls the cross-tenant list until no document is in flight and `ready` holds (an upload becomes a document later). */
async function waitUntilSettled(token: string, what: string, ready: (rows: DocumentRow[]) => boolean = (rows) => rows.length > 0, timeoutMs = 90_000): Promise<DocumentRow[]> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const rows = await documents(token, "limit=200");
    if (ready(rows) && rows.every((row) => !IN_FLIGHT.includes(row.status))) return rows;
    if (Date.now() > deadline) throw new Error(`${what} did not settle: ${rows.map((row) => `${row.documentNumber ?? row.id} ${row.status}`).join(", ") || "no documents"}`);
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
}

async function upload(token: string, fileName: string, contentType: string, bytes: Buffer) {
  await api(token, "POST", "/ingestions", { fileName, contentType, base64: bytes.toString("base64"), channelId: null });
}

/** Everything the screens need beyond the seed, created only when missing; then waits until no document is in flight. */
async function seed(): Promise<{ failedId: string; reviewId: string }> {
  const superadmin = await devToken("superadmin");
  const tenantB = await devToken("tenant-b-admin");

  // The seeded demo documents first: the pipeline runs in-process right after start-up.
  await waitUntilSettled(superadmin, "The demo documents");

  if ((await documents(tenantB, "status=Failed&limit=5")).length === 0) {
    await upload(tenantB, CORRUPT_PDF_NAME, "application/pdf", CORRUPT_PDF);
  }

  const suppliers = await api<{ items: { id: string; vatId: string | null; alwaysReview: boolean }[] }>(tenantB, "GET", `/suppliers?q=${REVIEW_SUPPLIER.vatId}&limit=50`);
  const supplier = suppliers.items.find((s) => s.vatId === REVIEW_SUPPLIER.vatId);
  if (!supplier) await api(tenantB, "POST", "/suppliers", { ...REVIEW_SUPPLIER, alwaysReview: true });
  else if (!supplier.alwaysReview) await api(tenantB, "PATCH", `/suppliers/${supplier.id}`, { alwaysReview: true });

  if ((await documents(tenantB, `q=${encodeURIComponent(REVIEW_NUMBER)}&limit=5`)).length === 0) {
    const fixture = readFileSync(new URL("../fixtures/xrechnung-ubl-invoice.xml", import.meta.url), "utf8");
    const xml = fixture.replace(`<cbc:ID>${FIXTURE_NUMBER}</cbc:ID>`, `<cbc:ID>${REVIEW_NUMBER}</cbc:ID>`);
    await upload(tenantB, `${REVIEW_NUMBER}.xml`, "application/xml", Buffer.from(xml, "utf8"));
  }

  const isFailed = (row: DocumentRow) => row.tenantId === TENANT_B_ID && row.status === "Failed";
  const isReview = (row: DocumentRow) => row.documentNumber === REVIEW_NUMBER;
  const all = await waitUntilSettled(superadmin, "The set-up documents", (rows) => rows.some(isFailed) && rows.some(isReview));
  const failed = all.find(isFailed);
  const review = all.find(isReview);
  if (!failed) throw new Error("No failed document in tenant B after set-up");
  if (!review || review.status !== "NeedsReview") throw new Error(`${REVIEW_NUMBER} is ${review?.status ?? "missing"}, expected NeedsReview`);
  return { failedId: failed.id, reviewId: review.id };
}

interface ShotOptions {
  theme?: Theme;
  language?: Language;
  viewport?: { width: number; height: number };
  mobile?: boolean;
}

/** A fresh context per screen (own sessionStorage: sign-in and tenant switcher), with theme and language preset. */
async function openContext(browser: Browser, { theme = "light", language = "en", viewport = { width: 1440, height: 900 }, mobile = false }: ShotOptions = {}) {
  const options: BrowserContextOptions = {
    viewport,
    deviceScaleFactor: 1,
    reducedMotion: "reduce",
    colorScheme: theme,
    locale: language === "de" ? "de-DE" : "en-US",
    timezoneId: "Europe/Berlin",
    ...(mobile ? { isMobile: true, hasTouch: true } : {}),
  };
  const context = await browser.newContext(options);
  // The shell keeps theme and language in localStorage (docproc.theme, docproc.lang); sign-in happens in English and
  // the language is switched afterwards, see `useLanguage`.
  await context.addInitScript(
    ({ theme, css }) => {
      window.localStorage.setItem("docproc.theme", theme);
      if (!window.localStorage.getItem("docproc.lang")) window.localStorage.setItem("docproc.lang", "en");
      const inject = () => {
        const style = document.createElement("style");
        style.setAttribute("data-e2e", "no-motion");
        style.textContent = css;
        document.head.appendChild(style);
      };
      if (document.head) inject();
      else document.addEventListener("DOMContentLoaded", inject, { once: true });
    },
    { theme, css: NO_MOTION_CSS },
  );
  const page = await context.newPage();
  return { context, page };
}

/** Switches the UI language the way the user menu stores it, then reloads `path`. */
async function useLanguage(page: Page, language: Language, path: string) {
  await page.evaluate((lng) => window.localStorage.setItem("docproc.lang", lng), language);
  await page.goto(`${adminUrl}${path}`);
  await expect(page.locator("html")).toHaveAttribute("lang", language);
}

/** Network idle, then no skeleton, spinner or busy region left on screen. */
async function settle(page: Page) {
  await page.waitForLoadState("networkidle");
  await expect(page.locator('[data-slot="skeleton"]:visible')).toHaveCount(0, { timeout: 20_000 });
  await expect(page.locator('.animate-spin:visible, [aria-busy="true"]:visible')).toHaveCount(0, { timeout: 20_000 });
  await page.evaluate(() => document.fonts.ready);
}

/**
 * Writes `<name>.png`. A whole-page shot grows the viewport to the document's height instead of using Playwright's
 * `fullPage`: that one stitches beyond the viewport and paints the fixed sidebar (`h-svh`) halfway down the page.
 */
async function shot(page: Page, name: string, fullPage = false) {
  await settle(page);
  const viewport = page.viewportSize()!;
  if (fullPage) {
    const height = await page.evaluate(() => document.documentElement.scrollHeight);
    if (height > viewport.height) {
      await page.setViewportSize({ width: viewport.width, height });
      await settle(page);
    }
  }
  // Nothing hovered by accident: the pointer goes to a corner that holds no control.
  const size = page.viewportSize()!;
  await page.mouse.move(size.width - 1, size.height - 1);
  await page.screenshot({ path: `${OUT}${name}.png`, animations: "disabled", caret: "hide" });
  if (fullPage) await page.setViewportSize(viewport);
}

async function heading(page: Page, name: string | RegExp) {
  await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
}

let ids: { failedId: string; reviewId: string };

test.describe("admin console screenshots", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeAll(async () => {
    test.skip(!process.env.E2E_API_URL || !process.env.E2E_ADMIN_URL, "Set E2E_API_URL and E2E_ADMIN_URL to run against a dev-mode API and the admin console dev server.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable.`);
    test.skip(!(await reachable(adminUrl)), `${adminUrl} is not reachable.`);
    test.setTimeout(180_000);
    mkdirSync(OUT, { recursive: true });
    ids = await seed();
  });

  test("01 sign-in page @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await page.goto(`${adminUrl}/sign-in`);
    await expect(page.getByRole("button", { name: "Sign in as Sam Super" })).toBeVisible();
    await shot(page, "01-sign-in");
    await context.close();
  });

  test("02 dashboard as superadmin @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", "/", adminUrl);
    await heading(page, "Dashboard");
    await expect(page.getByRole("table", { name: "Failed documents" }).getByRole("row")).not.toHaveCount(1);
    await shot(page, "02-dashboard-superadmin", true);
    await context.close();
  });

  test("03-04 tenants list and the create-tenant dialog @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", "/tenants", adminUrl);
    await heading(page, "Tenants");
    await expect(page.getByRole("table", { name: "Tenants of the platform" }).getByRole("link", { name: "Gasthaus zur Post GmbH" })).toBeVisible();
    await shot(page, "03-tenants");

    await page.getByRole("button", { name: "New tenant" }).click();
    const dialog = page.getByRole("dialog", { name: "Create a tenant" });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("button", { name: "Create tenant" }).click();
    await expect(dialog.locator('[data-slot="form-message"]').first()).toBeVisible();
    await shot(page, "04-tenant-create-validation");
    await context.close();
  });

  const tabs = [
    { tab: "overview", label: "Overview", slug: "overview" },
    { tab: "channels", label: "Channels", slug: "channels" },
    { tab: "members", label: "Members", slug: "members" },
    { tab: "suppliers", label: "Suppliers", slug: "suppliers" },
    { tab: "settings", label: "Settings", slug: "settings" },
    { tab: "api-keys", label: "API keys", slug: "api-keys" },
    { tab: "llm", label: "AI", slug: "llm" },
  ] as const;

  test("05-11 tenant A detail, one image per tab @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", `/tenants/${TENANT_A_ID}`, adminUrl);
    await heading(page, /Gasthaus zur Post GmbH/);
    for (const [index, { tab, label, slug }] of tabs.entries()) {
      await page.getByRole("tab", { name: label, exact: true }).click();
      await expect(page).toHaveURL(tab === "overview" ? /\/tenants\/[0-9a-f-]+(\?tab=overview)?$/ : new RegExp(`tab=${tab}`));
      await expect(page.getByRole("tab", { name: label, exact: true, selected: true })).toBeVisible();
      await shot(page, `${String(5 + index).padStart(2, "0")}-tenant-${slug}`, tab === "llm");
    }
    await context.close();
  });

  test("12 cross-tenant documents list @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", "/documents", adminUrl);
    await heading(page, "Documents");
    await expect(page.getByRole("columnheader", { name: "Tenant" })).toBeVisible();
    await shot(page, "12-documents");
    await context.close();
  });

  test("13-14 document detail with an expanded timeline row, and the reprocess dialog @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", `/documents/${ids.failedId}`, adminUrl);
    const timeline = page.getByRole("table", { name: "Timeline" });
    await expect(timeline).toBeVisible();
    await settle(page);
    const expanders = timeline.getByRole("button", { name: "Show details" });
    await expect(expanders.first()).toBeVisible();
    await expanders.last().click();
    await expect(timeline.getByRole("button", { name: "Hide details" })).toBeVisible();
    await shot(page, "13-document-timeline", true);

    await page.getByRole("button", { name: "Reprocess", exact: true }).click();
    await expect(page.getByRole("dialog").or(page.getByRole("alertdialog"))).toBeVisible();
    await shot(page, "14-document-reprocess");
    await context.close();
  });

  test("15 global review queue @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", "/review", adminUrl);
    await heading(page, "Review queue");
    const tasks = page.getByRole("table").first();
    const row = tasks.getByRole("row", { name: new RegExp(REVIEW_NUMBER) });
    await expect(row).toBeVisible();
    // The queue's own actions sit on screen, not behind a horizontal scroll.
    const claim = row.getByRole("button", { name: `Claim review task for ${REVIEW_NUMBER}` }).or(row.getByRole("button", { name: `Release review task for ${REVIEW_NUMBER}` }));
    await expect(claim).toBeInViewport({ ratio: 1 });
    await expect(row.getByRole("link", { name: "Open", exact: true })).toBeInViewport({ ratio: 1 });
    const overflow = await tasks.evaluate((table) => {
      const scroller = table.parentElement!;
      return scroller.scrollWidth - scroller.clientWidth;
    });
    expect(overflow).toBeLessThanOrEqual(1);
    await shot(page, "15-review-queue");
    await context.close();
  });

  test("16-18 AI providers, the add-provider dialog and usage @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "superadmin", "/llm", adminUrl);
    await heading(page, "AI providers");
    await shot(page, "16-llm-providers");

    await page.getByRole("button", { name: "Add provider" }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await shot(page, "17-llm-add-provider");
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toBeHidden();

    // The explorer is the second card, below the providers: scrolled to its top edge.
    const usage = page.locator('[data-slot="card"]', { has: page.getByText("Usage explorer", { exact: true }) });
    await expect(usage).toBeVisible();
    await usage.evaluate((card) => card.scrollIntoView({ block: "start" }));
    await shot(page, "18-llm-usage");
    await context.close();
  });

  test("19 dashboard as operator @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "operator", "/", adminUrl);
    await heading(page, "Dashboard");
    await shot(page, "19-dashboard-operator", true);
    await context.close();
  });

  test("20 dashboard as tenant-b-viewer @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser);
    await signIn(page, "tenant-b-viewer", "/", adminUrl);
    await heading(page, "Dashboard");
    await expect(page.getByTestId("platform-only-notice")).toBeVisible();
    await shot(page, "20-dashboard-tenant-viewer", true);
    await context.close();
  });

  test("21-22 German locale: dashboard and tenants @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser, { language: "de" });
    await signIn(page, "superadmin", "/", adminUrl);
    await useLanguage(page, "de", "/");
    await heading(page, "Dashboard");
    await shot(page, "21-de-dashboard", true);
    await page.goto(`${adminUrl}/tenants`);
    await heading(page, "Mandanten");
    await shot(page, "22-de-tenants");
    await context.close();
  });

  test("23-24 dark theme: dashboard and tenant detail @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser, { theme: "dark" });
    await signIn(page, "superadmin", "/", adminUrl);
    await heading(page, "Dashboard");
    await expect(page.locator("html")).toHaveClass(/dark/);
    await shot(page, "23-dark-dashboard", true);
    await page.goto(`${adminUrl}/tenants/${TENANT_A_ID}?tab=members`);
    await heading(page, /Gasthaus zur Post GmbH/);
    await shot(page, "24-dark-tenant-members");
    await context.close();
  });

  test("25 phone: dashboard with the sidebar sheet open @screenshot", async ({ browser }) => {
    const { context, page } = await openContext(browser, { viewport: { width: 390, height: 844 }, mobile: true });
    await signIn(page, "superadmin", "/", adminUrl);
    await heading(page, "Dashboard");
    await settle(page);
    await page.getByRole("banner").getByRole("button", { name: "Toggle sidebar" }).click();
    const sheet = page.getByRole("dialog");
    await expect(sheet).toBeVisible();
    await expect(sheet.getByRole("link", { name: "Tenants" })).toBeVisible();
    await shot(page, "25-phone-dashboard-sidebar");
    await context.close();
  });
});
