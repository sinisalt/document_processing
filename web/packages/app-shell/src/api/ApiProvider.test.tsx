import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, waitFor } from "@testing-library/react";
import { AuthProvider, useAuthSession } from "../auth/AuthProvider";
import { DEFAULT_TOKEN_KEY } from "../auth/tokenStore";
import { ApiProvider, mayOverrideTenant, useApi, type ApiContextValue } from "./ApiProvider";
import { queryKeys } from "./queryKeys";
import { TENANT_OVERRIDE_KEY, TenantSwitcherProvider, useTenantSwitcher } from "./tenantContext";

const TENANT = "aaaaaaaa-0000-4000-8000-000000000001";

/** An unsigned JWT for `sub`: the override is bound to the token subject that set it. */
function jwt(sub: string): string {
  const encode = (value: object) => window.btoa(JSON.stringify(value)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${encode({ alg: "none", typ: "JWT" })}.${encode({ sub })}.`;
}

const fetchSpy = vi.fn(async (_request: Request) => new Response(JSON.stringify({ items: [], nextCursor: null }), { status: 200, headers: { "Content-Type": "application/json" } }));

interface Probe {
  api: ApiContextValue;
  tenantId: string | null;
  signOut: () => Promise<void>;
}

function renderShell() {
  const probe: { current: Probe | null } = { current: null };
  function Capture() {
    const api = useApi();
    const { tenantId } = useTenantSwitcher();
    const { signOut } = useAuthSession();
    probe.current = { api, tenantId, signOut };
    return null;
  }
  render(
    <AuthProvider config={{ mode: "dev" }} apiBaseUrl="http://api.test">
      <TenantSwitcherProvider>
        <ApiProvider baseUrl="http://api.test">
          <Capture />
        </ApiProvider>
      </TenantSwitcherProvider>
    </AuthProvider>,
  );
  return probe;
}

async function sentTenantHeader(probe: { current: Probe | null }): Promise<string | null> {
  fetchSpy.mockClear();
  await probe.current!.api.client.GET("/api/v1/documents", {});
  const request = fetchSpy.mock.calls.at(-1)![0];
  return request.headers.get("X-Tenant-Id");
}

beforeEach(() => {
  vi.stubGlobal("fetch", fetchSpy);
  window.sessionStorage.clear();
  window.sessionStorage.setItem(DEFAULT_TOKEN_KEY, JSON.stringify({ accessToken: jwt("sam"), expiresAt: Date.now() + 3_600_000, username: "sam" }));
  window.sessionStorage.setItem(TENANT_OVERRIDE_KEY, JSON.stringify({ owner: "sam", tenantId: TENANT }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.sessionStorage.clear();
});

describe("the SuperAdmin tenant override", () => {
  it("goes out as X-Tenant-Id for a SuperAdmin and never for another role", async () => {
    const probe = renderShell();
    expect(probe.current!.tenantId).toBe(TENANT);
    // Before /me answered: the tab's own choice is sent.
    expect(await sentTenantHeader(probe)).toBe(TENANT);

    probe.current!.api.queryClient.setQueryData(queryKeys.me(), { roles: ["SuperAdmin"] });
    expect(await sentTenantHeader(probe)).toBe(TENANT);

    // A stale override can never lock out another role (the API answers its X-Tenant-Id with 403, even on /me).
    probe.current!.api.queryClient.setQueryData(queryKeys.me(), { roles: ["Operator"] });
    expect(await sentTenantHeader(probe)).toBeNull();
  });

  it("is dropped on sign-out, so the next user in the tab starts in their own scope", async () => {
    const probe = renderShell();
    expect(probe.current!.tenantId).toBe(TENANT);

    await act(() => probe.current!.signOut());

    await waitFor(() => expect(probe.current!.tenantId).toBeNull());
    expect(window.sessionStorage.getItem(TENANT_OVERRIDE_KEY)).toBeNull();
    expect(await sentTenantHeader(probe)).toBeNull();
  });

  it("is not applied when the tab opens without a session", async () => {
    // The stored override stays bound to its owner (see tenantContext): it only comes back for the user who set it.
    window.sessionStorage.removeItem(DEFAULT_TOKEN_KEY);
    const probe = renderShell();
    await waitFor(() => expect(probe.current!.tenantId).toBeNull());
    expect(await sentTenantHeader(probe)).toBeNull();
  });
});

describe("mayOverrideTenant", () => {
  it("allows the header only for a SuperAdmin once the roles are known", () => {
    expect(mayOverrideTenant(undefined)).toBe(true);
    expect(mayOverrideTenant({ roles: ["SuperAdmin"] })).toBe(true);
    expect(mayOverrideTenant({ roles: ["TenantAdmin", "Reviewer"] })).toBe(false);
    expect(mayOverrideTenant({ roles: [] })).toBe(false);
  });
});
