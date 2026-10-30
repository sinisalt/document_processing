/**
 * Test doubles for screen tests. A test file mocks the shell and the router with
 *
 *   vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
 *   vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));
 *
 * and steers them through `shell`: the signed-in roles, the API's answers per `METHOD /path`, the route's search and
 * params. Everything else of the shell (DataTable, ConfirmDialog, queryKeys, unwrap, the policy table) is the real one.
 */
import type { ReactNode } from "react";
import { render, type RenderResult } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { vi } from "vitest";
import type * as AppShell from "@docproc/app-shell";
import type * as Router from "@tanstack/react-router";

export interface FakeResponse {
  status?: number;
  body?: unknown;
}

type Handler = (request: { path: string; init: unknown }) => FakeResponse;

export interface DevUser {
  name: string;
  roles: string[];
  tenantId: string | null;
}

/** The dev users of `DevAuth` (README "Running without Docker"). */
export const devUsers = {
  superadmin: { name: "superadmin", roles: ["SuperAdmin"], tenantId: null },
  operator: { name: "operator", roles: ["Operator"], tenantId: null },
  tenantAAdmin: { name: "tenant-a-admin", roles: ["TenantAdmin"], tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" },
  tenantBViewer: { name: "tenant-b-viewer", roles: ["Viewer"], tenantId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
} satisfies Record<string, DevUser>;

export const shell = {
  user: devUsers.superadmin as DevUser,
  tenantId: null as string | null,
  search: {} as Record<string, unknown>,
  params: {} as Record<string, string>,
  handlers: new Map<string, Handler>(),
  /** Every request the screen made, as `GET /api/v1/tenants`. */
  calls: [] as string[],
  /** The same requests with what the screen passed (`params`, `body`). */
  requests: [] as { route: string; init: unknown }[],
  navigate: vi.fn(),
  queryClient: new QueryClient(),
};

/** Resets `shell` for the next test; call it from `beforeEach`. */
export function resetShell(user: DevUser = devUsers.superadmin) {
  shell.user = user;
  shell.tenantId = null;
  shell.search = {};
  shell.params = {};
  shell.handlers = new Map();
  shell.calls = [];
  shell.requests = [];
  shell.navigate = vi.fn();
  shell.queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

/** `shell.handlers.set(...)` with a fixed answer. */
export function answer(route: string, body: unknown, status = 200) {
  shell.handlers.set(route, () => ({ status, body }));
}

function respond(method: string, path: string, init: unknown) {
  const route = `${method} ${path}`;
  shell.calls.push(route);
  shell.requests.push({ route, init });
  const handler = shell.handlers.get(route);
  const { status = 200, body } = handler ? handler({ path, init }) : { status: 404, body: { title: "Not found", status: 404 } };
  const response = { ok: status < 400, status, url: path } as Response;
  return Promise.resolve(status < 400 ? { data: body, error: undefined, response } : { data: undefined, error: body ?? { title: "Error", status }, response });
}

const client = {
  GET: (path: string, init?: unknown) => respond("GET", path, init),
  POST: (path: string, init?: unknown) => respond("POST", path, init),
  PUT: (path: string, init?: unknown) => respond("PUT", path, init),
  PATCH: (path: string, init?: unknown) => respond("PATCH", path, init),
  DELETE: (path: string, init?: unknown) => respond("DELETE", path, init),
  use: () => {},
};

export function mockShell(actual: typeof AppShell): typeof AppShell {
  return {
    ...actual,
    useApi: () => ({ client: client as unknown as AppShell.ApiContextValue["client"], queryClient: shell.queryClient, baseUrl: "" }),
    useAuth: () => {
      const { roles, name, tenantId } = shell.user;
      const permissions = actual.policiesOf(roles);
      return {
        mode: "dev",
        status: "authenticated",
        isAuthenticated: true,
        accessToken: "token",
        user: { subject: name, name, displayName: name, email: null, roles, permissions, tenantId, scope: tenantId ? "tenant" : "platform", authMethod: "bearer" },
        error: null,
        signIn: async () => {},
        signOut: async () => {},
        hasRole: (...wanted: string[]) => wanted.some((role) => roles.includes(role)),
        hasPolicy: (policy: AppShell.Policy) => permissions.includes(policy),
        refetchUser: async () => {},
      } as unknown as AppShell.UseAuthResult;
    },
    useTenantSwitcherOptional: () => (shell.user.roles.includes("SuperAdmin") ? { tenantId: shell.tenantId, setTenantId: (id: string | null) => void (shell.tenantId = id) } : null),
  };
}

export function mockRouter(actual: typeof Router): typeof Router {
  return {
    ...actual,
    Link: (({ to, children, params: _params, search: _search, ...rest }: { to?: string; children?: ReactNode; params?: unknown; search?: unknown }) => (
      <a href={to} {...rest}>
        {children}
      </a>
    )) as unknown as typeof Router.Link,
    useNavigate: (() => shell.navigate) as unknown as typeof Router.useNavigate,
    createFileRoute: ((path: string) => (options: Record<string, unknown>) => ({
      options,
      fullPath: path,
      useSearch: () => shell.search,
      useParams: () => shell.params,
    })) as unknown as typeof Router.createFileRoute,
  };
}

/** Renders inside the test QueryClient. */
export function renderScreen(ui: ReactNode): RenderResult {
  return render(<QueryClientProvider client={shell.queryClient}>{ui}</QueryClientProvider>);
}

/** The component of a route file's `Route` under the mocked `createFileRoute`. */
export function routeComponent(route: unknown): () => ReactNode {
  return (route as { options: { component: () => ReactNode } }).options.component;
}
