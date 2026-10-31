import { expect, type Page } from "@playwright/test";
import { apiUrl } from "../../playwright.config";

/**
 * What the end-to-end specs share against the dev-mode API (README "Running without Docker"): reachability checks,
 * the dev users (GET /dev/users), dev bearer tokens (POST /dev/token) for set-up calls, and signing in through an
 * app's development sign-in page.
 */

export interface DevUser {
  username: string;
  displayName: string;
  roles: string[];
}

export async function reachable(url: string): Promise<boolean> {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(3_000) });
    return response.status < 500;
  } catch {
    return false;
  }
}

export async function devUser(username: string): Promise<DevUser> {
  const response = await fetch(`${apiUrl}/dev/users`);
  const users = (await response.json()) as DevUser[];
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

/** One JSON call to `/api/v1`; throws with the problem body on a non-2xx answer. */
export async function api<T>(token: string, method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${apiUrl}/api/v1${path}`, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  return (await response.json()) as T;
}

/**
 * Signs in on an app's development sign-in page (English UI) with one click and waits until the header names the
 * user; `path` is where the app goes afterwards (`?redirect=`).
 */
export async function signIn(page: Page, appUrl: string, username: string, path = "/"): Promise<DevUser> {
  const user = await devUser(username);
  await page.goto(path === "/" ? `${appUrl}/sign-in` : `${appUrl}/sign-in?redirect=${encodeURIComponent(path)}`);
  await page.getByRole("button", { name: `Sign in as ${user.displayName}` }).click();
  await expect(page.getByRole("banner").getByRole("button", { name: `Account menu for ${user.displayName}` })).toBeVisible();
  return user;
}
