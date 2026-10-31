import { mkdirSync } from "node:fs";
import { test, expect, type Browser, type BrowserContextOptions, type Page } from "@playwright/test";
import { apiUrl, portalUrl } from "../playwright.config";
import { readFileSync } from "node:fs";
import { alwaysReview, api, devToken, reachable, signIn, uploadXml, waitUntilSettled } from "./support/dev-api";

/**
 * Visual proof of the customer portal: every screen against the live dev-mode API with the demo documents of
 * fixtures/demo, written to web/e2e/screenshots/portal/NN-<slug>.png in capture order. Run with `pnpm e2e:screenshots`
 * (E2E_API_URL and E2E_PORTAL_URL set, as for the other specs). What a screen needs beyond the demo seed (a claimed task,
 * a tenant B document waiting for review, an API key) is set up through the API in beforeAll, idempotently, so a
 * second run on the same database writes the same files.
 */

const OUT_DIR = new URL("../screenshots/portal/", import.meta.url);
/**
 * The Bio-Hof Müller PDF invoice of the demo seed: LLM-extracted, two pages, a low-confidence flag on the due date. The
 * due date is not printed (the model worked it out from "14 Tagen"), so that flag's field has no box on the page.
 */
const PDF_INVOICE = "2026-131";
/** The Bio-Hof Müller XRechnung of the demo seed: no page images, a blocking totals mismatch. */
const XML_INVOICE = "2026-118";
/**
 * The tenant B document the viewer opens: the demo seed's Konzum plus e-invoice to Pizzeria Napoli as the next one of
 * the series (its own number, date and order), so it is a real supplier invoice to tenant B. A fixed number, so reruns
 * find it instead of uploading another.
 */
const TENANT_B_NUMBER = "04603-2026-P12";
const KONZUM_VAT = "HR62226620908";
const konzumInvoice = readFileSync(new URL("../../../fixtures/demo/tenant-b/Konzum_Racun_04518-2026-P12.xml", import.meta.url), "utf8")
  .replace("<cbc:ID>04518-2026-P12</cbc:ID>", `<cbc:ID>${TENANT_B_NUMBER}</cbc:ID>`)
  .replace("<cbc:IssueDate>2026-10-28</cbc:IssueDate>", "<cbc:IssueDate>2026-10-30</cbc:IssueDate>")
  .replace("<cbc:DueDate>2026-11-12</cbc:DueDate>", "<cbc:DueDate>2026-11-14</cbc:DueDate>")
  .replace("<cbc:ID>NAR-2026-0391</cbc:ID>", "<cbc:ID>NAR-2026-0402</cbc:ID>");
const API_KEY_NAME = "ERP export";

const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };

/** No animation or transition, no blinking caret: what is captured is the settled screen. */
const NO_MOTION_CSS = "*, *::before, *::after { animation-duration: 0s !important; animation-delay: 0s !important; transition-duration: 0s !important; transition-delay: 0s !important; caret-color: transparent !important; scroll-behavior: auto !important; }";

interface ShotOptions {
  viewport?: { width: number; height: number };
  theme?: "light" | "dark";
  /** The browser's locale: native controls such as date inputs format by it, not by the UI language. */
  locale?: string;
  mobile?: boolean;
}

interface Seed {
  pdfInvoiceId: string;
  xmlInvoiceId: string;
  tenantBDocumentId: string;
}

let seed: Seed;

async function documentId(token: string, number: string): Promise<string> {
  const list = await api<{ items: { id: string; documentNumber: string | null }[] }>(token, "GET", `/documents?q=${encodeURIComponent(number)}&limit=10`);
  const found = list.items.find((d) => d.documentNumber === number);
  if (!found) throw new Error(`Document ${number} is not in GET /documents: is the demo seed loaded?`);
  return found.id;
}

/**
 * A context with the screenshot settings and the theme in localStorage before the app starts. The language starts as
 * English, so the dev sign-in finds its English buttons; `useLanguage` switches afterwards.
 */
async function openPage(browser: Browser, { viewport = DESKTOP, theme = "light", locale = "en-US", mobile = false }: ShotOptions = {}): Promise<Page> {
  const options: BrowserContextOptions = {
    viewport,
    deviceScaleFactor: 1,
    reducedMotion: "reduce",
    colorScheme: theme,
    locale,
    timezoneId: "Europe/Berlin",
    ...(mobile ? { isMobile: true, hasTouch: true } : {}),
  };
  const context = await browser.newContext(options);
  await context.addInitScript(
    ({ css, theme }) => {
      try {
        window.localStorage.setItem("docproc.theme", theme);
        if (!window.localStorage.getItem("docproc.lang")) window.localStorage.setItem("docproc.lang", "en");
      } catch {
        // storage blocked: the app falls back to its defaults
      }
      const inject = () => {
        const style = document.createElement("style");
        style.dataset.screenshot = "no-motion";
        style.textContent = css;
        document.head.appendChild(style);
      };
      if (document.head) inject();
      else document.addEventListener("DOMContentLoaded", inject, { once: true });
    },
    { css: NO_MOTION_CSS, theme },
  );
  return context.newPage();
}

/** Switches the signed-in app to another language the way the user menu does (`docproc.lang`), and reloads. */
async function useLanguage(page: Page, language: "en" | "de") {
  await page.evaluate((lng) => window.localStorage.setItem("docproc.lang", lng), language);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("lang", language);
}

/** Scrolls the review form panel (not the window) so `locator` is at its top, as the wheel over the form would. */
async function scrollFormPanelTo(locator: ReturnType<Page["locator"]>) {
  await locator.evaluate((element) => {
    const scroller = element.closest(".overflow-y-auto");
    if (scroller) scroller.scrollTop += element.getBoundingClientRect().top - scroller.getBoundingClientRect().top - 8;
  });
}

/** Waits for the network, skeletons, spinners and busy markers to be gone, then writes NN-<slug>.png. */
async function shot(page: Page, name: string, { fullPage = false }: { fullPage?: boolean } = {}) {
  await page.waitForLoadState("networkidle");
  await expect(page.locator('[data-slot="skeleton"], .animate-spin, [aria-busy="true"]')).toHaveCount(0, { timeout: 20_000 });
  await page.evaluate(() => document.fonts.ready);
  // The pointer stays where the last click left it; park it where it hovers nothing.
  await page.mouse.move(page.viewportSize()!.width - 2, page.viewportSize()!.height - 2);
  await page.screenshot({ path: new URL(`${name}.png`, OUT_DIR).pathname, fullPage, animations: "disabled", caret: "hide" });
}

/** A PDF review screen is ready when its page image and the overlay over it are there. */
async function pageImageReady(page: Page) {
  await expect(page.getByTestId("page-viewer").locator("img")).toBeVisible();
  await expect(page.getByTestId("field-overlay")).toBeVisible();
}

/** Selects the review screen's first flag, which highlights its field in the form and on the page. */
async function selectFirstFlag(page: Page) {
  await page.getByTestId("flags-list").locator("[data-flag-index='0']").click();
  await expect(page.getByTestId("flags-list").locator("[data-flag-index='0']")).toHaveAttribute("aria-current", "true");
}

test.describe("customer portal screenshots", () => {

  test.beforeAll(async () => {
    test.skip(!process.env.E2E_API_URL || !process.env.E2E_PORTAL_URL, "Set E2E_API_URL and E2E_PORTAL_URL to run against a dev-mode API and the portal dev server.");
    test.skip(!(await reachable(`${apiUrl}/dev/users`)), `${apiUrl}/dev/users is not reachable.`);
    test.skip(!(await reachable(portalUrl)), `${portalUrl} is not reachable.`);
    test.setTimeout(180_000);
    mkdirSync(OUT_DIR, { recursive: true });

    // The demo seed is ingested at startup: capture only once the pipeline is done with every document.
    await waitUntilSettled(await devToken("superadmin"));

    const admin = await devToken("tenant-a-admin");
    const reviewer = await devToken("tenant-a-reviewer");
    const pdfInvoiceId = await documentId(admin, PDF_INVOICE);
    const xmlInvoiceId = await documentId(admin, XML_INVOICE);

    // Review queue: the reviewer holds the XML invoice's task (claiming one's own task again is a no-op).
    const tasks = await api<{ items: { id: string; documentId: string; assignedTo: string | null }[] }>(reviewer, "GET", "/review-tasks?limit=50");
    const xmlTask = tasks.items.find((task) => task.documentId === xmlInvoiceId);
    if (!xmlTask) throw new Error(`No open review task for ${XML_INVOICE}`);
    await api(reviewer, "POST", `/review-tasks/${xmlTask.id}:claim`);

    // Settings > API keys: one key, created once.
    const keys = await api<{ name: string; revokedAt?: string | null }[]>(admin, "GET", "/api-keys");
    if (!keys.some((key) => key.name === API_KEY_NAME && !key.revokedAt)) await api(admin, "POST", "/api-keys", { name: API_KEY_NAME, scopes: ["ingest", "read"] });

    // Tenant B: a document waiting for review for the viewer (the demo seed's only one there auto-approves): the next
    // Konzum plus invoice, with Konzum plus set to "always review".
    const tenantB = await devToken("tenant-b-admin");
    await alwaysReview(tenantB, KONZUM_VAT);
    const existing = await api<{ items: { id: string; documentNumber: string | null }[] }>(tenantB, "GET", `/documents?q=${TENANT_B_NUMBER}&limit=5`);
    const tenantBDocumentId =
      existing.items.find((d) => d.documentNumber === TENANT_B_NUMBER)?.id ?? (await uploadXml(tenantB, konzumInvoice, `Konzum_Racun_${TENANT_B_NUMBER}.xml`, TENANT_B_NUMBER)).id;

    seed = { pdfInvoiceId, xmlInvoiceId, tenantBDocumentId };
  });

  test("01 sign-in page @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await page.goto(`${portalUrl}/sign-in`);
    await expect(page.getByRole("button", { name: /Sign in as Anna Admin/ })).toBeVisible();
    await shot(page, "01-sign-in");
    await page.context().close();
  });

  test("02 dashboard @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/");
    await expect(page.getByTestId("count-needsReview")).toBeVisible();
    await shot(page, "02-dashboard");
    await page.context().close();
  });

  test("03 inbox, all documents @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/documents?tab=all");
    await expect(page.getByRole("table", { name: "Documents" }).getByRole("row", { name: new RegExp(PDF_INVOICE) })).toBeVisible();
    await shot(page, "03-inbox");
    await page.context().close();
  });

  test("04 inbox filtered to Needs review @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/documents?tab=all");
    await page.getByRole("tab", { name: "Needs review" }).click();
    await expect(page).toHaveURL(/tab=needsReview/);
    const table = page.getByRole("table", { name: "Documents" });
    await expect(table.getByRole("row", { name: new RegExp(XML_INVOICE) })).toBeVisible();
    await expect(table.getByRole("row", { name: /Auto-approved/ })).toHaveCount(0);
    await shot(page, "04-inbox-needs-review");
    await page.context().close();
  });

  test("05-06 review screen of a PDF invoice with flags @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await expect(page.getByTestId("document-number")).toHaveText(PDF_INVOICE);
    await pageImageReady(page);
    // The flag is on the due date, which the model worked out from the payment terms ("14 Tagen"); it is printed
    // nowhere, so it has no box: the flag says so, and selecting it focuses and highlights the form field.
    await selectFirstFlag(page);
    const flags = page.getByTestId("flags-list");
    await expect(flags.getByTestId("flag-not-on-page")).toHaveText("Not on the page image");
    await expect(page.getByRole("form", { name: "Canonical data" }).getByLabel("Due date", { exact: true })).toBeFocused();
    // The field was in sight already: the window stays where it was, with the header and its actions on screen.
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
    // Every box sits on its own value: the delivery date on the Lieferdatum line, not on the Rechnungsdatum above it.
    const overlay = page.getByTestId("field-overlay");
    const issued = await overlay.locator('[data-path="issueDate"]').boundingBox();
    const delivered = await overlay.locator('[data-path="deliveryDate"]').boundingBox();
    expect(delivered!.y).toBeGreaterThan(issued!.y + issued!.height / 2);
    await shot(page, "05-review-pdf-flag-selected");

    const grid = page.getByTestId("line-item-grid");
    await scrollFormPanelTo(grid);
    await expect(grid.locator("tr[data-arithmetic]").first()).toBeInViewport();
    await expect(grid.locator("tr[data-arithmetic]").first()).toHaveAttribute("data-arithmetic", "ok");
    // The whole line fits the panel: no column of amounts is cut off to the right.
    const table = grid.getByRole("table", { name: "Line items" });
    expect(await table.evaluate((element) => element.parentElement!.scrollWidth <= element.parentElement!.clientWidth)).toBe(true);
    await expect(grid.getByLabel("Line 1, Net", { exact: true })).toBeInViewport();
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
    await shot(page, "06-review-pdf-line-items");
    await page.context().close();
  });

  test("07 review screen of an XML e-invoice @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", `/documents/${seed.xmlInvoiceId}`);
    await expect(page.getByTestId("document-number")).toHaveText(XML_INVOICE);
    await expect(page.getByText("No page images")).toBeVisible();
    await shot(page, "07-review-xml");
    await page.context().close();
  });

  test("08 document timeline @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await pageImageReady(page);
    await page.getByRole("tab", { name: "Timeline" }).click();
    const timeline = page.getByTestId("timeline");
    await expect(timeline).toContainText("Review requested");
    // The validation's details, as label and value; then the panel scrolled so the last event is in sight.
    const validated = timeline.getByRole("listitem").filter({ hasText: "Stage completed" }).last();
    await validated.locator("summary").click();
    await expect(validated.locator("dl")).toContainText("Eligible for auto-approval");
    await expect(timeline).toContainText("Processing");
    // The panel scrolled to its end, so the last event is in sight in it, and the window scrolled down as far as it
    // goes: the panel frame (80 % of the viewport, below the header and the flags) reaches below the fold otherwise.
    const lastInSight = await timeline.evaluate((list) => {
      const scroller = list.closest(".overflow-y-auto")!;
      scroller.scrollTop = scroller.scrollHeight;
      window.scrollTo(0, document.documentElement.scrollHeight);
      return list.lastElementChild!.getBoundingClientRect().bottom <= scroller.getBoundingClientRect().bottom + 1;
    });
    expect(lastInSight).toBe(true);
    await expect(timeline.getByRole("listitem").last()).toBeInViewport();
    await shot(page, "08-review-timeline");
    await page.context().close();
  });

  test("09 review queue with a claimed task @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-reviewer", "/review");
    const tasks = page.getByRole("table", { name: "Review tasks" });
    await expect(tasks.getByRole("row", { name: new RegExp(XML_INVOICE) })).toContainText("You");
    await shot(page, "09-review-queue");
    await page.context().close();
  });

  test("10-11 suppliers and the supplier dialog @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/suppliers");
    const table = page.getByRole("table", { name: "Suppliers" });
    await expect(table.getByRole("row", { name: /METRO Deutschland GmbH/ })).toBeVisible();
    await shot(page, "10-suppliers");

    await table.getByRole("row", { name: /METRO Deutschland GmbH/ }).click();
    await expect(page.getByRole("dialog", { name: "METRO Deutschland GmbH" })).toBeVisible();
    await shot(page, "11-supplier-dialog");
    await page.context().close();
  });

  for (const [number, tab, slug, ready] of [
    ["12", "General", "settings-general", "Review policy"],
    ["13", "Channels", "settings-channels", "API upload"],
    ["14", "Members", "settings-members", "Rea Reviewer"],
    ["15", "AI providers", "settings-ai-providers", "AI providers"],
  ] as const) {
    test(`${number} settings: ${tab} @screenshot`, async ({ browser }) => {
      // The AI providers tab runs past 900 px (routing policy, usage chart with its axis): a taller window shows it all.
      const page = await openPage(browser, tab === "AI providers" ? { viewport: { width: DESKTOP.width, height: 1150 } } : {});
      await signIn(page, "tenant-a-admin", "/settings");
      await page.getByRole("tab", { name: tab, exact: true }).click();
      await expect(page.getByRole("tabpanel")).toContainText(ready);
      await shot(page, `${number}-${slug}`);
      await page.context().close();
    });
  }

  test("16 upload dialog @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/documents");
    await page.getByRole("button", { name: "Upload", exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "Upload documents" });
    await dialog.getByLabel("Choose files", { exact: true }).setInputFiles([
      { name: "METRO_Rechnung_RG-2026-901377.pdf", mimeType: "application/pdf", buffer: Buffer.alloc(184_320, 32) },
      { name: "Getraenke-Hoffmann_RE-2026-55940.xml", mimeType: "application/xml", buffer: Buffer.alloc(12_288, 32) },
    ]);
    await expect(dialog.getByRole("list", { name: "2 files selected" })).toBeVisible();
    await shot(page, "16-upload-dialog");
    await page.context().close();
  });

  test("17 German dashboard @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { locale: "de-DE" });
    await signIn(page, "tenant-a-admin", "/");
    await useLanguage(page, "de");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Übersicht");
    await shot(page, "17-de-dashboard");
    await page.context().close();
  });

  test("18 German review screen @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { locale: "de-DE" });
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await useLanguage(page, "de");
    await pageImageReady(page);
    await selectFirstFlag(page);
    // Dates in the form follow the UI language like the header does, whatever the browser's locale.
    const form = page.getByRole("form", { name: "Kanonische Daten" });
    await expect(form.getByLabel("Rechnungsdatum", { exact: true })).toHaveValue("28.10.2026");
    await expect(form.getByLabel("Fälligkeitsdatum", { exact: true })).toHaveValue("11.11.2026");
    await shot(page, "18-de-review");
    await page.context().close();
  });

  test("19 dark inbox @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { theme: "dark" });
    await signIn(page, "tenant-a-admin", "/documents?tab=all");
    await expect(page.locator("html")).toHaveClass(/dark/);
    await expect(page.getByRole("table", { name: "Documents" }).getByRole("row", { name: new RegExp(PDF_INVOICE) })).toBeVisible();
    await shot(page, "19-dark-inbox");
    await page.context().close();
  });

  test("20 dark review screen @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { theme: "dark" });
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await expect(page.locator("html")).toHaveClass(/dark/);
    await pageImageReady(page);
    // Focus on a field with a box: its box on the page turns to the highlight colour.
    await page.getByRole("form", { name: "Canonical data" }).getByLabel("Invoice number").focus();
    await expect(page.getByTestId("field-overlay").locator('[data-path="invoiceNumber"]')).toHaveAttribute("data-active", "true");
    await shot(page, "20-dark-review");
    await page.context().close();
  });

  test("21 phone review screen @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { viewport: PHONE, mobile: true });
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await pageImageReady(page);
    await shot(page, "21-phone-review");
    await page.context().close();
  });

  test("22 phone inbox @screenshot", async ({ browser }) => {
    const page = await openPage(browser, { viewport: PHONE, mobile: true });
    await signIn(page, "tenant-a-admin", "/documents?tab=all");
    await expect(page.getByRole("table", { name: "Documents" }).getByRole("row", { name: new RegExp(PDF_INVOICE) })).toBeVisible();
    // The sidebar is an off-canvas sheet on a phone: closed, the screen belongs to the list.
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await shot(page, "22-phone-inbox");
    await page.context().close();
  });

  test("23 viewer on a review screen @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-b-viewer", `/documents/${seed.tenantBDocumentId}`);
    await expect(page.getByTestId("review-screen")).toHaveAttribute("data-status", "NeedsReview");
    await expect(page.getByTestId("document-number")).toHaveText(TENANT_B_NUMBER);
    for (const id of ["save-button", "approve-button", "reject-button", "reprocess-button"]) await expect(page.getByTestId(id)).toHaveCount(0);
    await shot(page, "23-viewer-review-read-only");
    await page.context().close();
  });

  test("24 empty state: a search without results @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/documents?tab=all&q=ZZ-NO-SUCH-INVOICE");
    await expect(page.getByText("No documents")).toBeVisible();
    await shot(page, "24-empty-inbox-search");
    await page.context().close();
  });

  test("25 settings: API keys @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", "/settings?tab=apiKeys");
    await expect(page.getByRole("tabpanel")).toContainText(API_KEY_NAME);
    await shot(page, "25-settings-api-keys");
    await page.context().close();
  });

  test("26 a box on the page selects its field @screenshot", async ({ browser }) => {
    const page = await openPage(browser);
    await signIn(page, "tenant-a-admin", `/documents/${seed.pdfInvoiceId}`);
    await pageImageReady(page);
    // Zoomed in, so the highlighted box can be told from the others at screenshot scale.
    const zoomIn = page.getByRole("button", { name: "Zoom in" });
    await zoomIn.click();
    await zoomIn.click();
    await pageImageReady(page);
    // The keyboard path of a click on the Lieferdatum box: the box is highlighted and its field focused in the form.
    const box = page.getByTestId("field-overlay").locator('[data-path="deliveryDate"]');
    await box.scrollIntoViewIfNeeded();
    await box.focus();
    await page.keyboard.press("Enter");
    await expect(page.getByRole("form", { name: "Canonical data" }).getByLabel("Delivery date", { exact: true })).toBeFocused();
    await expect(box).toHaveAttribute("data-active", "true");
    await shot(page, "26-review-pdf-box-selects-field");
    await page.context().close();
  });
});
