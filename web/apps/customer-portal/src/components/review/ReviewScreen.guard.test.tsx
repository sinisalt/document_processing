import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { Outlet, RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter } from "@tanstack/react-router";
import { i18n } from "@/i18n";
import { fieldDomId, type CanonicalInvoice } from "@/lib/canonical";
import type { DocumentDetail } from "@/lib/queries";

const client = {
  GET: vi.fn(async () => ({ data: undefined, error: { title: "Not Found", status: 404 }, response: new Response(null, { status: 404 }) })),
  PATCH: vi.fn(),
  POST: vi.fn(),
};
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));

const { ReviewScreen } = await import("./ReviewScreen");

const extracted = <T,>(value: T) => ({ value, status: "Extracted" as const, confidence: 1, source: { method: "StructuredXml" as const } });
const canonical: CanonicalInvoice = {
  kind: extracted("Invoice"),
  invoiceNumber: extracted({ verbatim: "INV-1001" }),
  issueDate: extracted("2026-10-01"),
  currency: extracted("EUR"),
  totalGross: extracted({ amount: 303.45, currency: "EUR" }),
};

const document = {
  id: "6f1c1d2e-0000-4000-8000-000000000001",
  type: "Invoice",
  status: "NeedsReview",
  canonical,
  documentNumber: "INV-1001",
  supplierName: null,
  issueDate: "2026-10-01",
  totalGross: 303.45,
  currency: "EUR",
  pageCount: 0,
  pageImageUrls: [],
  flags: [{ id: "f1", field: "totalGross", severity: "Warning", code: "TOTALS_MISMATCH", message: "Totals", createdAt: "2026-10-30T10:00:00Z", createdBy: "Pipeline", resolvedAt: null, resolvedBy: null }],
  blockingFlagCount: 0,
  warningFlagCount: 1,
  createdAt: "2026-10-30T10:00:00Z",
  updatedAt: "2026-10-30T10:00:00Z",
} as unknown as DocumentDetail;

/** The review screen under a real router, so leaving it goes through TanStack Router's blocker. */
function renderInRouter() {
  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const reviewRoute = createRoute({ getParentRoute: () => rootRoute, path: "/documents/$documentId", component: () => <ReviewScreen document={document} canReview threshold={0.8} locale="en" /> });
  const inboxRoute = createRoute({ getParentRoute: () => rootRoute, path: "/documents", component: () => <p>inbox</p> });
  const router = createRouter({ routeTree: rootRoute.addChildren([reviewRoute, inboxRoute]), history: createMemoryHistory({ initialEntries: [`/documents/${document.id}`] }) });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

function input(path: string): HTMLInputElement {
  return window.document.getElementById(fieldDomId(path)) as HTMLInputElement;
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView ??= () => {};
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
  window.matchMedia ??= ((query: string) => ({ matches: false, media: query, onchange: null, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false })) as typeof window.matchMedia;
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(cleanup);

describe("leaving the review screen", () => {
  it("leaves at once when nothing is unsaved", async () => {
    const router = renderInRouter();
    await screen.findByTestId("review-screen");
    fireEvent.keyDown(window.document.body, { key: "Escape" });
    expect(await screen.findByText("inbox")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/documents");
  });

  it("asks before discarding unsaved corrections, from Esc and from the back button", async () => {
    const router = renderInRouter();
    await screen.findByTestId("review-screen");
    fireEvent.change(input("invoiceNumber"), { target: { value: "INV-1002" } });
    await waitFor(() => expect(screen.getByTestId("save-button")).toBeEnabled());

    // Esc in the input only leaves the input; the next Esc would leave the screen, and asks first.
    input("invoiceNumber").focus();
    fireEvent.keyDown(input("invoiceNumber"), { key: "Escape" });
    fireEvent.keyDown(window.document.body, { key: "Escape" });
    expect(await screen.findByText("Discard your corrections?")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(`/documents/${document.id}`);

    fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));
    await waitFor(() => expect(screen.queryByText("Discard your corrections?")).not.toBeInTheDocument());
    expect(input("invoiceNumber")).toHaveValue("INV-1002");
    expect(router.state.location.pathname).toBe(`/documents/${document.id}`);

    fireEvent.click(screen.getByRole("button", { name: "Back to the inbox" }));
    expect(await screen.findByText("Discard your corrections?")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Discard and leave" }));
    expect(await screen.findByText("inbox")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/documents");
  });
});
