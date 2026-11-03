import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@/i18n";
import type { DocumentDetail } from "@/lib/queries";
import deliveryNoteTemplate from "../../../../../../schemas/templates/std.delivery-note.v1.json";
import creditNoteTemplate from "../../../../../../schemas/templates/std.credit-note.v1.json";

/** The review screen over definition-driven documents (docs/11 §12.2): the form comes from `definition.formSpec`. */
const client = {
  GET: vi.fn(async () => ({ data: [], error: undefined, response: new Response(null, { status: 200 }) })),
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

const extracted = <T,>(value: T) => ({ value, status: "Extracted" as const, confidence: 1, source: { method: "Llm" as const } });
const missing = () => ({ status: "Missing" as const, history: [] });

const deliveryNoteTree = {
  deliveryNoteNumber: extracted({ verbatim: "LS-4711", normalized: "LS4711" }),
  deliveryDate: missing(),
  supplier: missing(),
  recipient: missing(),
  orderReferences: [],
  carrier: missing(),
  signedBy: missing(),
  temperatureReadings: [],
  notes: missing(),
  lines: [{ description: extracted("Kabeljau"), quantityDelivered: extracted(4) }],
};

function documentOf(patch: Partial<DocumentDetail> = {}): DocumentDetail {
  return {
    id: "6f1c1d2e-0000-4000-8000-000000000009",
    tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    tenantName: "Gasthaus zur Post GmbH",
    type: "DeliveryNote",
    status: "NeedsReview",
    canonical: deliveryNoteTree as unknown as DocumentDetail["canonical"],
    canonicalSchemaVersion: 2,
    documentNumber: "LS-4711",
    supplierId: null,
    supplierName: null,
    issueDate: null,
    totalGross: null,
    currency: null,
    pageCount: 0,
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
    lineCount: 1,
    typeName: "Delivery note",
    typeVersion: 1,
    definition: {
      key: "DeliveryNote",
      version: 1,
      versionId: "11111111-1111-4111-8111-111111111111",
      name: { en: "Delivery note", de: "Lieferschein" },
      formSpec: deliveryNoteTemplate.formSpec as unknown as JsonElement,
    },
    ...patch,
  };
}

/** A timeline that reached review, so every reprocess stage is offered. */
const reviewTimeline = [
  { id: 1, eventType: "document.stage_completed", fromStatus: "Extracted", toStatus: "Validated", actor: "pipeline", details: null, at: "2026-11-03T10:00:01Z" },
  { id: 2, eventType: "document.review_requested", fromStatus: "Validated", toStatus: "NeedsReview", actor: "pipeline", details: null, at: "2026-11-03T10:00:02Z" },
];

function renderScreen(document: DocumentDetail) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <ReviewScreen document={document} canReview threshold={0.8} locale="en" />
    </QueryClientProvider>,
  );
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView = vi.fn();
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  client.GET.mockReset();
  client.GET.mockImplementation(async () => ({ data: [], error: undefined, response: new Response(null, { status: 200 }) }));
  client.PATCH.mockReset();
  client.PATCH.mockImplementation(async () => ({ data: documentOf({ updatedAt: "2026-11-03T10:01:00Z" }), error: undefined, response: new Response(null, { status: 200 }) }));
});

afterEach(cleanup);

describe("review screen of a definition-driven document", () => {
  it("renders a delivery note from its form spec, with type and version in the header", () => {
    renderScreen(documentOf());
    expect(screen.getByTestId("document-type")).toHaveTextContent("Delivery note");
    expect(screen.getByTestId("type-version")).toHaveTextContent("v1");
    expect(screen.getByLabelText("Delivery note no.")).toHaveValue("LS-4711");
    expect(screen.getByLabelText("Line 1, Quantity delivered")).toHaveValue("4");
  });

  it("saves value corrections through PATCH /fields", async () => {
    renderScreen(documentOf());
    fireEvent.change(screen.getByLabelText("Carrier"), { target: { value: "DHL" } });
    await waitFor(() => expect(screen.getByTestId("save-button")).toBeEnabled());
    fireEvent.click(screen.getByTestId("save-button"));
    await waitFor(() => expect(client.PATCH).toHaveBeenCalledTimes(1));
    expect(client.PATCH.mock.calls[0]).toEqual(["/api/v1/documents/{id}/fields", { params: { path: { id: "6f1c1d2e-0000-4000-8000-000000000009" } }, body: { corrections: [{ path: "carrier", value: "DHL", reason: null }] } }]);
  });

  it("adds a row after the unsaved corrections in one request", async () => {
    renderScreen(documentOf());
    fireEvent.change(screen.getByLabelText("Line 1, Quantity delivered"), { target: { value: "5" } });
    fireEvent.click(screen.getByRole("button", { name: "Add row" }));
    await waitFor(() => expect(client.PATCH).toHaveBeenCalledTimes(1));
    const body = (client.PATCH.mock.calls[0] as unknown[])[1] as { body: { corrections: unknown[] } };
    expect(body.body.corrections).toEqual([
      { path: "lines[0].quantityDelivered", value: 5, reason: null },
      { op: "append", path: "lines", value: null, reason: null },
    ]);
  });

  it("removes a row", async () => {
    renderScreen(documentOf());
    fireEvent.click(screen.getByRole("button", { name: "Remove row 1" }));
    await waitFor(() => expect(client.PATCH).toHaveBeenCalledTimes(1));
    expect(((client.PATCH.mock.calls[0] as unknown[])[1] as { body: { corrections: unknown[] } }).body.corrections).toEqual([{ op: "remove", path: "lines[0]", value: null, reason: null }]);
  });

  it("shows a custom field of a credit note fork and labels its flag", () => {
    const formSpec = {
      ...creditNoteTemplate.formSpec,
      key: "CreditNote",
      version: 2,
      fields: [...creditNoteTemplate.formSpec.fields, { path: "supplierCustomerRef", key: "supplierCustomerRef", kind: "identifier", section: "document", indexed: true, label: { en: "Our customer no. at supplier", de: "Unsere Kundennummer beim Lieferanten" } }],
    };
    const tree = { kind: extracted("CreditNote"), invoiceNumber: extracted({ verbatim: "GS-1" }), supplierCustomerRef: extracted({ verbatim: "0815" }), lines: [] };
    renderScreen(
      documentOf({
        type: "CreditNote",
        canonical: tree as unknown as DocumentDetail["canonical"],
        definition: { key: "CreditNote", version: 2, versionId: null, name: { en: "Credit note" }, formSpec: formSpec as unknown as JsonElement },
        flags: [{ id: "f1", code: "FIELD_REQUIRED", field: "supplierCustomerRef", severity: "Blocking", message: "required", params: {}, createdAt: "2026-11-03T10:00:00Z", createdBy: "Pipeline", resolvedAt: null, resolvedBy: null }],
      }),
    );
    expect(screen.getByLabelText("Our customer no. at supplier")).toHaveValue("0815");
    expect(within(screen.getByRole("region", { name: /flag/i })).getByText(/Our customer no\. at supplier/)).toBeInTheDocument();
  });

  it("offers no row edits on an invoice of a tenant without definitions", () => {
    const invoice = { kind: extracted("Invoice"), invoiceNumber: extracted({ verbatim: "INV-1" }), lines: [{ lineNumber: 1, description: extracted("x") }] };
    renderScreen(documentOf({ type: "Invoice", canonical: invoice as unknown as DocumentDetail["canonical"], canonicalSchemaVersion: 1, definition: null }));
    expect(screen.getByLabelText("Invoice number")).toHaveValue("INV-1");
    expect(screen.queryByRole("button", { name: "Add row" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("type-version")).not.toBeInTheDocument();
  });

  it("reprocesses onto the published version of the type from Extract when a newer one is published", async () => {
    client.GET.mockImplementation((async (path: string) =>
      path === "/api/v1/document-types/{key}"
        ? { data: { key: "DeliveryNote", publishedVersion: 2 }, error: undefined, response: new Response(null, { status: 200 }) }
        : path === "/api/v1/documents/{id}/events"
          ? { data: reviewTimeline, error: undefined, response: new Response(null, { status: 200 }) }
          : { data: [], error: undefined, response: new Response(null, { status: 200 }) }) as unknown as typeof client.GET);
    client.POST.mockReset();
    client.POST.mockImplementation(async () => ({ data: documentOf({ status: "Extracted" }), error: undefined, response: new Response(null, { status: 200 }) }));
    renderScreen(documentOf());
    fireEvent.click(screen.getByTestId("reprocess-button"));
    const dialog = await screen.findByRole("alertdialog");
    const option = within(dialog).getByTestId("reprocess-config-mode");
    await waitFor(() => expect(option).toHaveTextContent("This document was read with Delivery note v1; v2 is published."));
    expect(within(option).getByRole("checkbox")).toBeChecked();
    await waitFor(() => expect(within(dialog).getByRole("combobox")).toHaveTextContent("Extract"));
    fireEvent.click(within(dialog).getByRole("button", { name: "Reprocess" }));
    await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
    expect((client.POST.mock.calls[0] as unknown[])[1]).toEqual({ params: { path: { id: "6f1c1d2e-0000-4000-8000-000000000009" } }, body: { fromStage: "Extract", configMode: "current" } });
  });

  it("keeps the pinned version when the reviewer leaves the published one unticked", async () => {
    client.GET.mockImplementation((async (path: string) =>
      path === "/api/v1/document-types/{key}"
        ? { data: { key: "DeliveryNote", publishedVersion: 1 }, error: undefined, response: new Response(null, { status: 200 }) }
        : path === "/api/v1/documents/{id}/events"
          ? { data: reviewTimeline, error: undefined, response: new Response(null, { status: 200 }) }
          : { data: [], error: undefined, response: new Response(null, { status: 200 }) }) as unknown as typeof client.GET);
    client.POST.mockReset();
    client.POST.mockImplementation(async () => ({ data: documentOf({ status: "Validated" }), error: undefined, response: new Response(null, { status: 200 }) }));
    renderScreen(documentOf());
    fireEvent.click(screen.getByTestId("reprocess-button"));
    const dialog = await screen.findByRole("alertdialog");
    await waitFor(() => expect(within(dialog).getByTestId("reprocess-config-mode")).toHaveTextContent("the published version."));
    expect(within(dialog).getByRole("checkbox")).not.toBeChecked();
    await waitFor(() => expect(within(dialog).getByRole("combobox")).toHaveTextContent("Validate"));
    fireEvent.click(within(dialog).getByRole("button", { name: "Reprocess" }));
    await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
    expect(((client.POST.mock.calls[0] as unknown[])[1] as { body: unknown }).body).toEqual({ fromStage: "Validate", configMode: "pinned" });
  });

  it("opens a delivery note read before the switch with the empty tree the API answers, so it can be filled", () => {
    const empty = { ...deliveryNoteTree, deliveryNoteNumber: missing(), lines: [] };
    renderScreen(documentOf({ canonical: empty as unknown as DocumentDetail["canonical"], documentNumber: null, lineCount: null }));
    expect(screen.getByLabelText("Delivery note no.")).toHaveValue("");
    expect(screen.getByRole("button", { name: "Add row" })).toBeInTheDocument();
    expect(screen.queryByText(/No extracted data/)).not.toBeInTheDocument();
  });

  it("says a type without a definition has no form", () => {
    renderScreen(documentOf({ type: "PurchaseOrder", canonical: null, definition: null, canonicalSchemaVersion: null }));
    expect(screen.getByText(/no form to review yet/)).toBeInTheDocument();
  });
});
