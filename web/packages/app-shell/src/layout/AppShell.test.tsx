import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { useQuery } from "@tanstack/react-query";
import { RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { AuthProvider, useAuthSession } from "../auth/AuthProvider";
import { DEFAULT_TOKEN_KEY } from "../auth/tokenStore";
import { ApiProvider, useApi } from "../api/ApiProvider";
import { TenantSwitcherProvider } from "../api/tenantContext";
import { TooltipProvider } from "@docproc/ui";
import { AppShell } from "./AppShell";

/** An unsigned JWT for `sub`. */
function jwt(sub: string): string {
  const encode = (value: object) => window.btoa(JSON.stringify(value)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${encode({ alg: "none", typ: "JWT" })}.${encode({ sub })}.`;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const requests: { url: string; authorized: boolean }[] = [];
const fetchSpy = vi.fn(async (request: Request) => {
  requests.push({ url: new URL(request.url).pathname, authorized: request.headers.has("Authorization") });
  if (!request.headers.has("Authorization")) return json({ title: "Unauthorized", status: 401 }, 401);
  if (request.url.includes("/api/v1/me")) return json({ subject: "sam", name: "Sam", roles: ["SuperAdmin"], permissions: [], scope: "platform", authMethod: "bearer" });
  return json({ items: [], nextCursor: null });
});

let signOut: (() => Promise<void>) | null = null;
let dashboardMounts = 0;

function Dashboard() {
  const { client } = useApi();
  const session = useAuthSession();
  signOut = session.signOut;
  useQuery({ queryKey: ["documents", "list", {}], queryFn: async () => (await client.GET("/api/v1/documents", {})).data ?? null });
  return <h1>Dashboard</h1>;
}

/** The sign-in route stays pending (like a code-split chunk still loading) until the test opens the gate. */
let openGate: () => void = () => {};

function renderApp() {
  const gate = new Promise<void>((resolve) => (openGate = resolve));
  const rootRoute = createRootRoute({
    component: () => (
      <AuthProvider config={{ mode: "dev" }} apiBaseUrl="http://api.test">
        <TenantSwitcherProvider>
          <ApiProvider baseUrl="http://api.test">
            <TooltipProvider>
              <AppShell nav={[]} appName="Test" />
            </TooltipProvider>
          </ApiProvider>
        </TenantSwitcherProvider>
      </AuthProvider>
    ),
  });
  const indexRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: "/",
    component: function Index() {
      dashboardMounts += 1;
      return <Dashboard />;
    },
  });
  const signInRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sign-in", loader: () => gate, component: () => <p>Sign-in page</p> });
  const router = createRouter({ routeTree: rootRoute.addChildren([indexRoute, signInRoute]), history: createMemoryHistory({ initialEntries: ["/"] }) });
  render(<RouterProvider router={router} />);
  return router;
}

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({ matches: false, media: query, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false })) as typeof window.matchMedia;
});

beforeEach(() => {
  vi.stubGlobal("fetch", fetchSpy);
  requests.length = 0;
  dashboardMounts = 0;
  window.sessionStorage.clear();
  window.sessionStorage.setItem(DEFAULT_TOKEN_KEY, JSON.stringify({ accessToken: jwt("sam"), expiresAt: Date.now() + 3_600_000, username: "sam" }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.sessionStorage.clear();
});

describe("AppShell on sign-out", () => {
  it("does not mount the previous screen again while the sign-in route is still loading", async () => {
    const router = renderApp();
    expect(await screen.findByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
    await waitFor(() => expect(requests.some((r) => r.url === "/api/v1/documents")).toBe(true));
    const mountsBefore = dashboardMounts;
    requests.length = 0;

    await act(() => signOut!());
    // The location is the sign-in page now, but its route has not resolved: the dashboard must not come back for it.
    await waitFor(() => expect(router.state.location.pathname).toBe("/sign-in"));
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    expect(dashboardMounts).toBe(mountsBefore);
    expect(requests).toEqual([]);

    await act(async () => openGate());
    expect(await screen.findByText("Sign-in page")).toBeInTheDocument();
    expect(requests).toEqual([]);
  });
});
