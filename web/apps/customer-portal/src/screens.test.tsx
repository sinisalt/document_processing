import { Suspense, type ComponentType, type ReactNode } from "react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { hasPolicy, type AuthUser, type Policy } from "@docproc/app-shell";
import { i18n } from "@/i18n";

/**
 * Route screens rendered without a router: `createFileRoute` hands back the options plus the hooks the screens call. The
 * router plugin code-splits `component` into a lazy chunk, hence the Suspense.
 */
vi.mock("@tanstack/react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-router")>()),
  createFileRoute: () => (options: Record<string, unknown>) => ({ options, fullPath: "/", useSearch: () => search.current, useParams: () => ({}) }),
  useNavigate: () => vi.fn(),
  Link: ({ children, to }: { children?: ReactNode; to?: string }) => <a href={to}>{children}</a>,
}));

const client = vi.hoisted(() => ({
  GET: vi.fn(async (path: string) => ({ data: { items: [], nextCursor: null }, error: undefined, response: new Response(null, { status: 200, headers: { "x-path": path } }) })),
  POST: vi.fn(),
  PATCH: vi.fn(),
  PUT: vi.fn(),
  DELETE: vi.fn(),
}));
const auth = vi.hoisted(() => ({ user: null as AuthUser | null }));
const search = vi.hoisted(() => ({ current: {} as Record<string, unknown> }));

vi.mock("@docproc/app-shell", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@docproc/app-shell")>();
  return {
    ...actual,
    useApi: () => ({ client }),
    useAuth: () => ({ user: auth.user, hasPolicy: (policy: Policy) => actual.hasPolicy(auth.user?.roles, policy) }),
  };
});

const reviewRoute = await import("@/routes/review");
const suppliersRoute = await import("@/routes/suppliers");
const inboxRoute = await import("@/routes/documents.index");
const dashboardRoute = await import("@/routes/index");

function userOf(role: string, scope: AuthUser["scope"], tenantId: string | null = scope === "tenant" ? "tenant-b" : null): AuthUser {
  return { subject: role, name: role, displayName: role, email: null, roles: [role], permissions: [], tenantId, scope, authMethod: "dev" };
}

async function renderRoute(route: { Route: unknown }) {
  const Component = (route.Route as { options: { component: ComponentType & { preload?: () => Promise<void> } } }).options.component;
  await Component.preload?.();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Suspense fallback={<p>loading</p>}>
        <Component />
      </Suspense>
    </QueryClientProvider>,
  );
  await waitFor(() => expect(screen.queryByText("loading")).not.toBeInTheDocument(), { timeout: 10_000 });
  // Let enabled queries start.
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
}

const requested = () => client.GET.mock.calls.map(([path]) => path);

beforeAll(async () => {
  await i18n.changeLanguage("en");
});

beforeEach(() => {
  client.GET.mockClear();
  search.current = {};
});

afterEach(cleanup);

describe("review queue gate", () => {
  it("answers a viewer's deep link with an empty state and no request", async () => {
    auth.user = userOf("Viewer", "tenant");
    expect(hasPolicy(auth.user.roles, "Review")).toBe(false);
    await renderRoute(reviewRoute);
    expect(screen.getByText("The review queue is for reviewers")).toBeInTheDocument();
    expect(screen.queryByText("The queue is empty")).not.toBeInTheDocument();
    expect(requested()).not.toContain("/api/v1/review-tasks");
  });

  it("lists the queue for a reviewer", async () => {
    auth.user = userOf("Reviewer", "tenant");
    await renderRoute(reviewRoute);
    expect(screen.getByRole("heading", { level: 1, name: "Review queue" })).toBeInTheDocument();
    expect(requested()).toContain("/api/v1/review-tasks");
  });
});

describe("platform scope", () => {
  it("shows the suppliers screen's platform empty state without asking for suppliers", async () => {
    auth.user = userOf("Operator", "platform");
    await renderRoute(suppliersRoute);
    expect(screen.getByText("Choose a tenant")).toBeInTheDocument();
    expect(requested()).not.toContain("/api/v1/suppliers");
  });

  it("lists suppliers in tenant scope", async () => {
    auth.user = userOf("Viewer", "tenant");
    await renderRoute(suppliersRoute);
    expect(screen.getByRole("heading", { level: 1, name: "Suppliers" })).toBeInTheDocument();
    expect(requested()).toContain("/api/v1/suppliers");
  });

  it("gives a platform account an inbox without the supplier filter or its request", async () => {
    auth.user = userOf("Operator", "platform");
    await renderRoute(inboxRoute);
    expect(screen.getByRole("heading", { level: 1, name: "Inbox" })).toBeInTheDocument();
    expect(requested()).toContain("/api/v1/documents");
    expect(requested()).not.toContain("/api/v1/suppliers");
    expect(screen.queryByLabelText("Supplier")).not.toBeInTheDocument();
  });

  it("keeps the supplier filter in tenant scope", async () => {
    auth.user = userOf("TenantAdmin", "tenant");
    await renderRoute(inboxRoute);
    expect(requested()).toContain("/api/v1/suppliers");
    expect(screen.getByLabelText("Supplier")).toBeInTheDocument();
  });
});

describe("tenant-only actions", () => {
  it("offers no Upload in platform scope, where POST /ingestions answers 400", async () => {
    auth.user = userOf("SuperAdmin", "platform");
    await renderRoute(inboxRoute);
    expect(screen.getByRole("heading", { level: 1, name: "Inbox" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Upload" })).not.toBeInTheDocument();
  });

  it("offers Upload in tenant scope", async () => {
    auth.user = userOf("Reviewer", "tenant");
    await renderRoute(inboxRoute);
    expect(screen.getByRole("button", { name: "Upload" })).toBeInTheDocument();
  });

  it("gives a platform SuperAdmin's dashboard no Settings, Suppliers or Upload link", async () => {
    auth.user = userOf("SuperAdmin", "platform");
    await renderRoute(dashboardRoute);
    const links = screen.getAllByRole("link").map((link) => link.getAttribute("href"));
    expect(links).not.toContain("/settings");
    expect(links).not.toContain("/suppliers");
    expect(screen.queryByText("Upload")).not.toBeInTheDocument();
  });

  it("keeps Settings on a tenant admin's dashboard", async () => {
    auth.user = userOf("TenantAdmin", "tenant");
    await renderRoute(dashboardRoute);
    expect(screen.getAllByRole("link").map((link) => link.getAttribute("href"))).toContain("/settings");
  });
});

describe("list errors", () => {
  it("shows a failed inbox request as an error, not as 'nothing waits for a review'", async () => {
    auth.user = userOf("Reviewer", "tenant");
    // The mock is typed by its success answers; a failure is what openapi-fetch returns for a problem.
    client.GET.mockImplementation((async (path: string) =>
      path === "/api/v1/documents"
        ? { data: undefined, error: { title: "Boom", status: 500, detail: "The database is down." }, response: new Response(null, { status: 500 }) }
        : { data: { items: [], nextCursor: null }, error: undefined, response: new Response(null, { status: 200 }) }) as never);
    try {
      await renderRoute(inboxRoute);
      expect(await screen.findByText("This list could not be loaded")).toBeInTheDocument();
      expect(screen.getByText("The database is down.")).toBeInTheDocument();
      expect(screen.queryByText("No documents")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
    } finally {
      client.GET.mockReset();
      client.GET.mockImplementation(async (path: string) => ({ data: { items: [], nextCursor: null }, error: undefined, response: new Response(null, { status: 200, headers: { "x-path": path } }) }));
    }
  });

  it("does not send a date range that ends before it starts", async () => {
    auth.user = userOf("Reviewer", "tenant");
    search.current = { tab: "all", from: "2026-12-01", to: "2026-01-01" };
    await renderRoute(inboxRoute);
    expect(requested()).not.toContain("/api/v1/documents");
    expect(screen.getAllByText("The end date is before the start date.").length).toBeGreaterThan(0);
    expect(screen.getByLabelText("Issued to")).toHaveAttribute("aria-invalid", "true");
  });
});
