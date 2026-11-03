import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@/i18n";
import type { DocumentDetail } from "@/lib/queries";
import { clearReviewSessions, readReviewSession } from "@/lib/reviewSession";
import priceListTemplate from "../../../../../../schemas/templates/std.price-list.v1.json";

/** The review screen of a price list whose type a reviewer just set (docs/11 §12.1, §12.3, §12.4): map mode and the table tool. */
const client = {
  GET: vi.fn(async (path: string) => {
    if (path === "/api/v1/documents/{id}/pages/{n}.png") return { data: new Blob(["png"], { type: "image/png" }), error: undefined, response: new Response(null, { status: 200 }) };
    return { data: [], error: undefined, response: new Response(null, { status: 200 }) };
  }),
  PATCH: vi.fn(),
  POST: vi.fn(),
};
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));
vi.mock("@tanstack/react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-router")>()),
  useNavigate: () => vi.fn(),
  useBlocker: () => ({ status: "idle" }),
}));

const { ReviewScreen } = await import("./ReviewScreen");

type JsonElement = NonNullable<DocumentDetail["definition"]>["formSpec"];
const missing = () => ({ status: "Missing" as const, history: [] });

const emptyPriceList = {
  priceListNumber: missing(),
  title: missing(),
  issueDate: missing(),
  validFrom: missing(),
  validUntil: missing(),
  listScope: missing(),
  currency: missing(),
  priceBasis: missing(),
  declaredLineCount: missing(),
  supplier: missing(),
  customer: missing(),
  supplierCustomerRef: missing(),
  lines: [],
  ext: {},
};

function documentOf(patch: Partial<DocumentDetail> = {}): DocumentDetail {
  return {
    id: "6f1c1d2e-0000-4000-8000-0000000000bb",
    tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    tenantName: "Gasthaus zur Post GmbH",
    type: "price-list",
    status: "NeedsReview",
    canonical: emptyPriceList as unknown as DocumentDetail["canonical"],
    canonicalSchemaVersion: 2,
    documentNumber: null,
    supplierId: null,
    supplierName: null,
    issueDate: null,
    totalGross: null,
    currency: null,
    pageCount: 14,
    pageImageUrls: [],
    flags: [],
    blockingFlagCount: 0,
    warningFlagCount: 0,
    approvedAt: null,
    approvedBy: null,
    costs: {} as DocumentDetail["costs"],
    createdAt: "2026-11-03T10:00:00Z",
    updatedAt: "2026-11-03T10:00:00Z",
    extractionMethod: null,
    failureReason: null,
    ingestion: null,
    typeConfidence: 1,
    validFrom: null,
    validUntil: null,
    lineCount: 0,
    configurableTypes: true,
    typeName: "Price list",
    typeVersion: 1,
    appliedConfig: null,
    definition: { key: "price-list", version: 1, versionId: "22222222-2222-4222-8222-222222222222", name: { en: "Price list", de: "Preisliste" }, formSpec: priceListTemplate.formSpec as unknown as JsonElement },
    ...patch,
  };
}

function renderScreen(document: DocumentDetail, canReview = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ReviewScreen key={`${document.id}:${document.updatedAt}`} document={document} canReview={canReview} threshold={0.8} locale="en" />
    </QueryClientProvider>,
  );
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView = vi.fn();
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  URL.createObjectURL ??= () => "blob:page";
  URL.revokeObjectURL ??= () => undefined;
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  clearReviewSessions();
  client.GET.mockClear();
});
afterEach(cleanup);

describe("review screen: map mode and the table tool", () => {
  it("starts in map mode on a type just set, names the focused field, and M switches it off", async () => {
    renderScreen(documentOf());
    const toggle = await screen.findByTestId("map-mode-toggle");
    expect(toggle).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("map-preview")).toHaveAttribute("data-state", "no-field");

    fireEvent.focus(document.getElementById("field-validFrom")!);
    expect(screen.getByTestId("map-preview")).toHaveTextContent("Valid from: click a word");

    fireEvent.keyDown(document.body, { key: "m" });
    expect(toggle).toHaveAttribute("aria-pressed", "false");
    expect(screen.queryByTestId("map-preview")).toBeNull();
    expect(readReviewSession(documentOf().id)).toMatchObject({ mapMode: false, mapTarget: "validFrom" });
  });

  it("keeps the tool and the field across a remount for a new server version", async () => {
    const first = renderScreen(documentOf());
    fireEvent.focus(document.getElementById("field-currency")!);
    first.unmount();
    renderScreen(documentOf({ updatedAt: "2026-11-03T10:01:00Z" }));
    expect(await screen.findByTestId("map-mode-toggle")).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("map-preview")).toHaveTextContent("Currency: click a word");
  });

  it("opens the table tool in its own tab, which asks for the table to be drawn", async () => {
    renderScreen(documentOf());
    fireEvent.click(await screen.findByTestId("table-tool-toggle"));
    expect(screen.getByTestId("table-tool-toggle")).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("map-mode-toggle")).toHaveAttribute("aria-pressed", "false");
    const tab = screen.getByTestId("table-tab");
    expect(tab).toHaveAttribute("data-state", "active");
    expect(screen.getByTestId("table-tool")).toHaveTextContent("Drag a box around the table");
    await waitFor(() => expect(client.GET).toHaveBeenCalledWith("/api/v1/documents/{id}/table-reports", expect.anything()));
  });

  it("offers neither tool to a viewer", async () => {
    renderScreen(documentOf(), false);
    expect(await screen.findByTestId("page-viewer")).toBeInTheDocument();
    expect(screen.queryByTestId("map-mode-toggle")).toBeNull();
    expect(screen.queryByTestId("table-tab")).toBeNull();
  });

  it("gives an unrecognised document the Map this document panel", async () => {
    renderScreen(documentOf({ type: "Other", typeName: null, canonical: null, canonicalSchemaVersion: null, definition: null }));
    const panel = await screen.findByTestId("map-document-panel");
    expect(within(panel).getByRole("heading", { name: "Map this document" })).toBeInTheDocument();
    expect(screen.queryByTestId("map-mode-toggle")).toBeNull();
  });

  // docs/11 principle 6: a tenant without configurableTypes reviews as before P2 (docs/09 "Demo: types as data", step 1).
  it("offers none of the P2 tools to a tenant without configurable types", async () => {
    renderScreen(documentOf({ type: "DeliveryNote", typeName: null, canonical: null, canonicalSchemaVersion: null, definition: null, configurableTypes: false }));
    expect(await screen.findByText("This document type has no form to review yet: its fields can be reviewed once the type is set up under Settings > Document types.")).toBeInTheDocument();
    expect(screen.queryByTestId("map-document-panel")).toBeNull();
    expect(screen.queryByTestId("more-actions")).toBeNull();
    expect(screen.queryByTestId("map-mode-toggle")).toBeNull();
  });

  it("offers neither map mode nor the table tool on a form of a tenant without configurable types", async () => {
    renderScreen(documentOf({ configurableTypes: false }));
    expect(await screen.findByTestId("page-viewer")).toBeInTheDocument();
    expect(screen.queryByTestId("map-mode-toggle")).toBeNull();
    expect(screen.queryByTestId("table-tab")).toBeNull();
    expect(screen.queryByTestId("more-actions")).toBeNull();
  });
});
