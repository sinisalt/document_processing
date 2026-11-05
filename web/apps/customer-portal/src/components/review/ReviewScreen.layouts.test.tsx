import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@/i18n";
import type { DocumentDetail, DocumentFlag } from "@/lib/queries";
import { clearReviewSessions, readReviewSession } from "@/lib/reviewSession";
import priceListTemplate from "../../../../../../schemas/templates/std.price-list.v1.json";

/** The review screen of a document held with NEW_LAYOUT (docs/11 §12.9, §12.10): the New layout panel, Identify mode, "Approve and save as layout". */
const client = {
  GET: vi.fn(async (path: string) => {
    if (path === "/api/v1/documents/{id}/pages/{n}.png") return { data: new Blob(["png"], { type: "image/png" }), error: undefined, response: new Response(null, { status: 200 }) };
    return { data: [], error: undefined, response: new Response(null, { status: 200 }) };
  }),
  PATCH: vi.fn(),
  POST: vi.fn(async (path: string) => ({ data: path.includes(":approve") || path.includes(":match-layout") ? documentOf() : {}, error: undefined, response: new Response(null, { status: 200 }) })),
};
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));
vi.mock("@tanstack/react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-router")>()),
  useNavigate: () => vi.fn(),
  useBlocker: () => ({ status: "idle" }),
  Link: ({ children }: { children: unknown }) => children,
}));

const { ReviewScreen } = await import("./ReviewScreen");

type JsonElement = NonNullable<DocumentDetail["definition"]>["formSpec"];
const missing = () => ({ status: "Missing" as const, history: [] });
const SUPPLIER = "5a1c1d2e-0000-4000-8000-0000000000aa";

const newLayoutFlag = (params: Record<string, string> = {}): DocumentFlag =>
  ({
    id: "f-new-layout",
    code: "NEW_LAYOUT",
    severity: "Blocking",
    field: null,
    message: "Nordwind Großhandel GmbH sent a design none of its layouts recognises.",
    params: { stepId: "classify.identification", supplierId: SUPPLIER, supplierName: "Nordwind Großhandel GmbH", proposedType: "price-list", suggestions: "[]", ...params },
    createdBy: "Pipeline",
    createdAt: "2026-11-03T10:00:00Z",
    resolvedAt: null,
    resolvedBy: null,
  }) as DocumentFlag;

function documentOf(patch: Partial<DocumentDetail> = {}): DocumentDetail {
  return {
    id: "6f1c1d2e-0000-4000-8000-0000000000cc",
    tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    tenantName: "Gasthaus zur Post GmbH",
    type: "price-list",
    status: "NeedsReview",
    canonical: { priceListNumber: { status: "Inferred", value: "PL-2027-01", confidence: 0.75, history: [] }, title: missing(), issueDate: missing(), validFrom: missing(), validUntil: missing(), listScope: missing(), currency: missing(), priceBasis: missing(), declaredLineCount: missing(), supplier: missing(), customer: missing(), supplierCustomerRef: missing(), lines: [], ext: {} } as unknown as DocumentDetail["canonical"],
    canonicalSchemaVersion: 2,
    documentNumber: "PL-2027-01",
    supplierId: SUPPLIER,
    supplierName: "Nordwind Großhandel GmbH",
    issueDate: null,
    totalGross: null,
    currency: null,
    pageCount: 2,
    pageImageUrls: [],
    flags: [newLayoutFlag()],
    blockingFlagCount: 1,
    warningFlagCount: 0,
    approvedAt: null,
    approvedBy: null,
    costs: {} as DocumentDetail["costs"],
    createdAt: "2026-11-03T10:00:00Z",
    updatedAt: "2026-11-03T10:00:00Z",
    extractionMethod: "Profile",
    failureReason: null,
    ingestion: null,
    typeConfidence: 0.6,
    typeSource: "Proposed",
    validFrom: null,
    validUntil: null,
    lineCount: 0,
    configurableTypes: true,
    typeName: "Price list",
    typeVersion: 1,
    appliedConfig: { prefill: { step: "layout-prefill", basisVersionId: null, sources: { priceListNumber: "definitionLabel", "supplier.vatId": "masterData" }, filledByLlm: false } } as unknown as DocumentDetail["appliedConfig"],
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
  client.POST.mockClear();
});
afterEach(cleanup);

describe("review screen: a new layout (docs/11 §12.10)", () => {
  it("leads with the New layout panel, its pre-fill sources, and a badge in the header", async () => {
    renderScreen(documentOf());
    const panel = await screen.findByTestId("new-layout-panel");
    expect(within(panel).getByTestId("new-layout-message")).toHaveTextContent("Nordwind Großhandel GmbH sent a document in a design we have not seen.");
    const sources = within(panel).getAllByTestId("prefill-source");
    expect(sources.map((s) => s.getAttribute("data-source"))).toEqual(["definitionLabel", "masterData"]);
    expect(within(panel).getByText("No AI filled the rest (none is configured for this tenant, or it could not read the document).")).toBeInTheDocument();
    expect(screen.getByTestId("new-layout-badge")).toHaveTextContent("New layout");
    expect(within(panel).getByTestId("new-layout-key")).toHaveValue("nordwind-price-list");
  });

  it("matches the document to its type through :match-layout", async () => {
    renderScreen(documentOf());
    fireEvent.click(await screen.findByTestId("match-layout-submit"));
    await waitFor(() => expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:match-layout", expect.objectContaining({ body: expect.objectContaining({ typeKey: "price-list", saveAs: "newLayout" }) })));
  });

  it("offers to confirm a pending Candidate instead of a second layout", async () => {
    renderScreen(documentOf({ flags: [newLayoutFlag({ pendingLayoutVersionId: "99999999-0000-4000-8000-000000000001", pendingLayoutKey: "nordwind-price-list" })], pendingLayoutVersionId: "99999999-0000-4000-8000-000000000001" }));
    const pending = await screen.findByTestId("pending-layout");
    expect(pending).toHaveTextContent("This matches nordwind-price-list, a layout saved in review and waiting for activation.");
    fireEvent.click(within(pending).getByTestId("confirm-pending-layout"));
    await waitFor(() => expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:match-layout", expect.objectContaining({ body: expect.objectContaining({ pendingLayoutVersionId: "99999999-0000-4000-8000-000000000001" }) })));
    expect(readReviewSession(documentOf().id).layoutChoice).toMatchObject({ pendingLayoutVersionId: "99999999-0000-4000-8000-000000000001" });
  });

  it("approves and saves the layout in one request, with the key from the panel", async () => {
    renderScreen(documentOf());
    const panel = await screen.findByTestId("new-layout-panel");
    fireEvent.change(within(panel).getByTestId("new-layout-key"), { target: { value: "nordwind-preisliste-shop" } });
    fireEvent.click(screen.getByTestId("approve-button"));
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByTestId("approve-save-layout-checkbox")).toBeChecked();
    expect(within(dialog).getByTestId("approve-save-layout")).toHaveTextContent("Saves the identifiers and field positions as nordwind-preisliste-shop (rule proposed from the title and the VAT id).");
    fireEvent.click(within(dialog).getByRole("button", { name: "Approve and save as layout" }));
    await waitFor(() =>
      expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:approve", expect.objectContaining({ body: { comment: null, learnLayout: { saveAs: "newLayout", layoutKey: "nordwind-preisliste-shop" } } })),
    );
    expect(readReviewSession(documentOf().id).savedLayout).toMatchObject({ supplierId: SUPPLIER, layoutKey: "nordwind-preisliste-shop", typeKey: "price-list" });
  });

  it("approves without a layout when the box is unticked", async () => {
    renderScreen(documentOf());
    fireEvent.click(await screen.findByTestId("approve-button"));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByTestId("approve-save-layout-checkbox"));
    fireEvent.click(within(dialog).getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:approve", expect.objectContaining({ body: { comment: null } })));
  });

  it("turns Identify mode on with I, opens the Identification card, and says the rule will be proposed", async () => {
    renderScreen(documentOf());
    const toggle = await screen.findByTestId("identify-mode-toggle");
    expect(toggle).toHaveAttribute("aria-pressed", "false");
    fireEvent.keyDown(document.body, { key: "i" });
    expect(toggle).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByTestId("map-mode-toggle")).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByTestId("identify-tab")).toHaveAttribute("data-state", "active");
    expect(screen.getByTestId("identify-empty")).toHaveTextContent("Without any, saving proposes a rule");
    expect(readReviewSession(documentOf().id).identifyMode).toBe(true);
  });

  it("shows the layout that identified a document, and no panel, when there is no NEW_LAYOUT", async () => {
    renderScreen(documentOf({ flags: [], supplierLayoutVersionId: "88888888-0000-4000-8000-000000000001", typeSource: "Identification", appliedConfig: { layout: { layoutId: "l1", layoutKey: "nordwind-price-list", version: 2, identification: [] } } as unknown as DocumentDetail["appliedConfig"] }));
    expect(await screen.findByTestId("layout-chip")).toHaveTextContent("Layout nordwind-price-list v2");
    expect(screen.queryByTestId("new-layout-panel")).toBeNull();
    expect(screen.getByTestId("identify-mode-toggle")).toBeInTheDocument();
  });

  it("changes nothing for a document without layouts (a tenant without supplierLayouts)", async () => {
    renderScreen(documentOf({ flags: [], appliedConfig: null, typeSource: null }));
    expect(await screen.findByTestId("page-viewer")).toBeInTheDocument();
    expect(screen.queryByTestId("new-layout-panel")).toBeNull();
    expect(screen.queryByTestId("identify-mode-toggle")).toBeNull();
    expect(screen.queryByTestId("layout-chip")).toBeNull();
    fireEvent.click(screen.getByTestId("approve-button"));
    expect(within(await screen.findByRole("alertdialog")).queryByTestId("approve-save-layout")).toBeNull();
  });

  it("explains an ambiguous document", async () => {
    renderScreen(documentOf({ flags: [{ ...newLayoutFlag(), code: "LAYOUT_AMBIGUOUS", params: { supplierId: SUPPLIER, layouts: "a@1, b@1" } } as DocumentFlag] }));
    expect(await screen.findByTestId("layout-ambiguous-panel")).toHaveTextContent("a@1, b@1");
  });
});
