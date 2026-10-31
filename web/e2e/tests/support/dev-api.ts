import { readFileSync } from "node:fs";
import { expect, type Page } from "@playwright/test";
import { apiUrl, portalUrl } from "../../playwright.config";

/**
 * Helpers shared by the specs that run against the API in dependency-free dev mode (SQLite, in-process pipeline, dev
 * tokens): reachability checks, the dev users and their tokens, plain API calls for the data a screen needs, and the
 * dev sign-in through the portal's sign-in page.
 */

export interface DevUser {
  username: string;
  displayName: string;
  roles: string[];
}

/** Pipeline statuses a document passes through before it settles (NeedsReview, AutoApproved, Failed, ...). */
export const IN_FLIGHT = ["Received", "Normalized", "Split", "Classified", "Matched", "Extracted"];

export async function reachable(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(3_000) });
    return response.status < 500;
  } catch {
    return false;
  }
}

export async function devUser(username: string): Promise<DevUser> {
  const users = (await (await fetch(`${apiUrl}/dev/users`)).json()) as DevUser[];
  const user = users.find((u) => u.username === username);
  if (!user) throw new Error(`Dev user '${username}' is not in GET /dev/users`);
  return user;
}

/** A bearer token for a dev user, for the API calls that set up what the UI then shows. */
export async function devToken(username: string): Promise<string> {
  const response = await fetch(`${apiUrl}/dev/token`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ username }) });
  if (!response.ok) throw new Error(`POST /dev/token for ${username}: ${response.status}`);
  return ((await response.json()) as { access_token: string }).access_token;
}

export async function api<T>(token: string, method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${apiUrl}/api/v1${path}`, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Polls `GET /documents` (as the token's user) until nothing is in the pipeline any more. */
export async function waitUntilSettled(token: string, timeoutMs = 90_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const list = await api<{ items: { status: string }[] }>(token, "GET", "/documents?limit=200");
    if (!list.items.some((d) => IN_FLIGHT.includes(d.status))) return;
    if (Date.now() > deadline) throw new Error("Documents are still in the pipeline");
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
}

/** Signs in through the portal's dev sign-in page and lands on `path`; resolves once the header names the user. */
export async function signIn(page: Page, username: string, path = "/", appUrl = portalUrl): Promise<DevUser> {
  const user = await devUser(username);
  await page.goto(`${appUrl}/sign-in?redirect=${encodeURIComponent(path)}`);
  await page.getByRole("button", { name: `Sign in as ${user.displayName}` }).click();
  await expect(page.getByRole("banner").getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
  return user;
}

/** The XRechnung UBL invoice of web/e2e/fixtures, whose supplier the specs mark "always review". */
export const fixture = readFileSync(new URL("../../fixtures/xrechnung-ubl-invoice.xml", import.meta.url), "utf8");
export const FIXTURE_NUMBER = "2026/4471";
export const SUPPLIER_NAME = "Getränke Huber GmbH & Co. KG";
export const SUPPLIER_VAT = "DE276543210";
export interface SupplierRow {
  id: string;
  vatId: string | null;
  alwaysReview: boolean;
}

/** The fixture's supplier with "always review" in the token's tenant: XML invoices of a known supplier auto-approve otherwise. */
export async function ensureAlwaysReviewSupplier(token: string) {
  const page = await api<{ items: SupplierRow[] }>(token, "GET", `/suppliers?q=${encodeURIComponent(SUPPLIER_VAT)}&limit=50`);
  const existing = page.items.find((s) => s.vatId === SUPPLIER_VAT);
  if (!existing) await api(token, "POST", "/suppliers", { name: SUPPLIER_NAME, vatId: SUPPLIER_VAT, alwaysReview: true });
  else if (!existing.alwaysReview) await api(token, "PATCH", `/suppliers/${existing.id}`, { alwaysReview: true });
}

/** Uploads the fixture under a fresh number through the API and waits until the pipeline is done with it. */
export async function uploadInvoice(token: string, number: string): Promise<{ id: string; status: string }> {
  const xml = fixture.replace(`<cbc:ID>${FIXTURE_NUMBER}</cbc:ID>`, `<cbc:ID>${number}</cbc:ID>`);
  await api(token, "POST", "/ingestions", { fileName: `${number}.xml`, contentType: "application/xml", base64: Buffer.from(xml, "utf8").toString("base64"), channelId: null });
  for (let attempt = 0; attempt < 60; attempt++) {
    const list = await api<{ items: { id: string; status: string }[] }>(token, "GET", `/documents?q=${encodeURIComponent(number)}&limit=5`);
    const document = list.items[0];
    if (document && !IN_FLIGHT.includes(document.status)) return document;
    await new Promise((resolve) => setTimeout(resolve, 1_000));
  }
  throw new Error(`${number} did not leave the pipeline within a minute`);
}
