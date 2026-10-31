import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, waitFor } from "@testing-library/react";
import { AuthProvider, useAuthSession } from "../auth/AuthProvider";
import { createTokenStore } from "../auth/tokenStore";
import type { AuthSession } from "../auth/types";
import { ApiProvider, TENANT_OVERRIDE_REFUSED_TITLE, useApi, type ApiContextValue } from "./ApiProvider";
import { TENANT_OVERRIDE_KEY, TenantSwitcherProvider, tokenSubject, useTenantSwitcher, type TenantSwitcherContextValue } from "./tenantContext";

const API = "http://api.test";
const TENANT = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

/** An unsigned JWT with the given subject: the shell only reads `sub`, the API verifies the signature. */
function jwt(sub: string): string {
  const encode = (value: object) => window.btoa(JSON.stringify(value)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${encode({ alg: "none", typ: "JWT" })}.${encode({ sub, name: sub })}.signature`;
}

interface Handles {
  session: AuthSession;
  switcher: TenantSwitcherContextValue;
  api: ApiContextValue;
}

function Probe({ handles }: { handles: { current: Handles | null } }) {
  handles.current = { session: useAuthSession(), switcher: useTenantSwitcher(), api: useApi() };
  return <span data-testid="tenant">{handles.current.switcher.tenantId ?? "platform"}</span>;
}

function renderShell() {
  const handles: { current: Handles | null } = { current: null };
  const view = render(
    <AuthProvider config={{ mode: "dev" }} apiBaseUrl={API}>
      <TenantSwitcherProvider>
        <ApiProvider baseUrl={API}>
          <Probe handles={handles} />
        </ApiProvider>
      </TenantSwitcherProvider>
    </AuthProvider>,
  );
  return { ...view, h: () => handles.current! };
}

/** Requests the stubbed fetch saw, with the override header each carried. */
const sent: { url: string; tenant: string | null }[] = [];
let refuseOverride = false;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

beforeEach(() => {
  window.sessionStorage.clear();
  sent.length = 0;
  refuseOverride = false;
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const tenant = request.headers.get("X-Tenant-Id");
      sent.push({ url: request.url, tenant });
      if (request.url.endsWith("/dev/token")) {
        const { username } = (await request.json()) as { username: string };
        return json({ access_token: jwt(username), token_type: "Bearer", expires_in: 3600, user: { username, displayName: username } });
      }
      if (tenant && refuseOverride) {
        return json({ title: TENANT_OVERRIDE_REFUSED_TITLE, status: 403, detail: "Only a SuperAdmin may send X-Tenant-Id." }, 403);
      }
      return json({ subject: "x", roles: [], permissions: [], scope: "platform", authMethod: "Bearer" });
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function signedInAs(sub: string) {
  createTokenStore(window.sessionStorage).save({ accessToken: jwt(sub), expiresIn: 3600, username: sub, displayName: sub });
}

async function getMe(h: () => Handles) {
  await act(async () => {
    await h().api.client.GET("/api/v1/me");
  });
  return sent.filter((r) => r.url.endsWith("/api/v1/me")).at(-1)!;
}

describe("tokenSubject", () => {
  it("reads sub from a JWT and nothing from anything else", () => {
    expect(tokenSubject(jwt("sam-super"))).toBe("sam-super");
    expect(tokenSubject(null)).toBeNull();
    expect(tokenSubject("opaque-token")).toBeNull();
    expect(tokenSubject("a.!!!.c")).toBeNull();
  });
});

describe("the SuperAdmin tenant override", () => {
  it("is sent while the SuperAdmin who chose it is signed in, and survives a reload for them", async () => {
    signedInAs("sam-super");
    const first = renderShell();
    act(() => first.h().switcher.setTenantId(TENANT));
    expect(first.getByTestId("tenant")).toHaveTextContent(TENANT);
    expect((await getMe(first.h)).tenant).toBe(TENANT);
    first.unmount();

    const reloaded = renderShell();
    expect(reloaded.getByTestId("tenant")).toHaveTextContent(TENANT);
    expect((await getMe(reloaded.h)).tenant).toBe(TENANT);
  });

  it("is cleared on sign-out, so the next user of the tab never sends it", async () => {
    signedInAs("sam-super");
    const { h, getByTestId } = renderShell();
    act(() => h().switcher.setTenantId(TENANT));
    expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).not.toBeNull();

    await act(() => h().session.signOut());
    expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).toBeNull();
    expect(getByTestId("tenant")).toHaveTextContent("platform");

    await act(() => h().session.signIn("olga-operator"));
    expect(h().session.accessToken).toBe(jwt("olga-operator"));
    expect((await getMe(h)).tenant).toBeNull();
    expect(getByTestId("tenant")).toHaveTextContent("platform");
  });

  it("is dropped when another user signs in over the session without signing out", async () => {
    signedInAs("sam-super");
    const { h } = renderShell();
    act(() => h().switcher.setTenantId(TENANT));

    await act(() => h().session.signIn("olga-operator"));
    expect((await getMe(h)).tenant).toBeNull();
    await waitFor(() => expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).toBeNull());
  });

  it("stored by one user is not picked up by another, nor is an unowned value from an older build", async () => {
    window.sessionStorage.setItem(TENANT_OVERRIDE_KEY, JSON.stringify({ owner: "sam-super", tenantId: TENANT }));
    signedInAs("olga-operator");
    const foreign = renderShell();
    expect(foreign.getByTestId("tenant")).toHaveTextContent("platform");
    expect((await getMe(foreign.h)).tenant).toBeNull();
    await waitFor(() => expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).toBeNull());
    foreign.unmount();

    window.sessionStorage.setItem(TENANT_OVERRIDE_KEY, TENANT);
    const legacy = renderShell();
    expect(legacy.getByTestId("tenant")).toHaveTextContent("platform");
    expect((await getMe(legacy.h)).tenant).toBeNull();
  });

  it("is dropped when the API refuses it, so the user is not locked out", async () => {
    signedInAs("sam-super");
    const { h, getByTestId } = renderShell();
    act(() => h().switcher.setTenantId(TENANT));
    refuseOverride = true;

    const refused = await getMe(h);
    expect(refused.tenant).toBe(TENANT);
    await waitFor(() => expect(getByTestId("tenant")).toHaveTextContent("platform"));
    expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).toBeNull();
    expect((await getMe(h)).tenant).toBeNull();
  });
});
