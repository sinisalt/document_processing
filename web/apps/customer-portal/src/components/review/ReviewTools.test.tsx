import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { ApiError } from "@docproc/app-shell";
import { i18n } from "@/i18n";
import type { DocumentDetail } from "@/lib/queries";
import { clearReviewSessions, readReviewSession } from "@/lib/reviewSession";
import priceListTemplate from "../../../../../../schemas/templates/std.price-list.v1.json";
import type { FormSpec, FieldSpec } from "@docproc/review";

/**
 * The review tools of P2 (docs/11 §12): map mode's reading of a selection, the table tool's calls, the review actions'
 * dialogs (set type, supplier, split, join) and the approval gate's refusal.
 */
const client = { GET: vi.fn(), POST: vi.fn(), PATCH: vi.fn() };
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("sonner", () => ({ toast }));

const { useMapMode } = await import("./useMapMode");
const { useTableTool } = await import("./useTableTool");
const { JoinDialog, SetTypeForm, SplitDialog, CreateSupplierDialog, partsFromStarts, supplierDraftOf, usableTypes } = await import("./ReviewDialogs");
const { ReviewActions, approvalRefusalCode } = await import("./ReviewActions");

const ok = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) });
const fail = (status: number, problem: unknown) => ({ data: undefined, error: problem, response: new Response(null, { status }) });

const formSpec = priceListTemplate.formSpec as unknown as FormSpec;

function documentOf(patch: Partial<DocumentDetail> = {}): DocumentDetail {
  return {
    id: "6f1c1d2e-0000-4000-8000-0000000000aa",
    tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    tenantName: "Gasthaus zur Post GmbH",
    type: "Other",
    status: "NeedsReview",
    canonical: null,
    canonicalSchemaVersion: null,
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
    typeConfidence: 0.88,
    validFrom: null,
    validUntil: null,
    lineCount: null,
    configurableTypes: true,
    typeName: null,
    typeVersion: null,
    appliedConfig: null,
    definition: null,
    ...patch,
  };
}

let queryClient: QueryClient;
function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView = vi.fn();
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
});

beforeEach(() => {
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.GET.mockReset();
  client.POST.mockReset();
  client.PATCH.mockReset();
  for (const fn of Object.values(toast)) fn.mockReset();
  clearReviewSessions();
});
afterEach(cleanup);

const validFrom: FieldSpec = { path: "validFrom", fieldPath: "validFrom", kind: "date", definitionKind: "date", required: true };

describe("map mode (useMapMode)", () => {
  it("reads a word's box with /words and offers the reading of the field's kind with its source", async () => {
    client.GET.mockResolvedValue(
      ok({ page: 1, source: "pdfText", locale: "de-DE", words: [{ index: 17, text: "01.01.2027", bBox: { x0: 150, y0: 200, x1: 210, y1: 212 } }], text: "01.01.2027", bBox: { x0: 150, y0: 200, x1: 210, y1: 212 }, candidates: [{ kind: "date", value: "2027-01-01" }, { kind: "text", value: "01.01.2027" }] }),
    );
    const { result } = renderHook(() => useMapMode("doc-1", validFrom, "en"), { wrapper });
    act(() => result.current.pick(1, { kind: "word", index: 17, box: { x0: 150, y0: 200, x1: 210, y1: 212 }, additive: false }));
    await waitFor(() => expect(result.current.text).toBe("01.01.2027"));
    expect(client.GET).toHaveBeenCalledWith("/api/v1/documents/{id}/pages/{n}/words", { params: { path: { id: "doc-1", n: 1 }, query: { bbox: "149,199,211,213" } } });
    expect(result.current.value).toBe("2027-01-01");
    expect(result.current.fits).toBe(true);
    expect(result.current.selectionBoxes(1)).toHaveLength(1);
    expect(result.current.selectionBoxes(2)).toHaveLength(0);

    let taken: ReturnType<typeof result.current.take> = null;
    act(() => {
      taken = result.current.take();
    });
    expect(taken).toEqual({ path: "validFrom", text: "2027-01-01", source: { method: "click", page: 1, bBox: { x0: 150, y0: 200, x1: 210, y1: 212 }, words: [[1, 17]], rawText: "01.01.2027" } });
    expect(result.current.selection).toBeNull();
  });

  it("shift-click adds words to the selection; a box without words offers OCR, which says when the server has none", async () => {
    client.GET.mockResolvedValue(ok({ page: 1, source: "none", locale: "de-DE", words: [], text: null, bBox: null, candidates: [] }));
    client.POST.mockResolvedValue(fail(501, { title: "OCR not configured", status: 501, code: "OCR_NOT_CONFIGURED" }));
    const { result } = renderHook(() => useMapMode("doc-1", validFrom, "en"), { wrapper });
    act(() => result.current.pick(1, { kind: "word", index: 1, box: { x0: 10, y0: 10, x1: 20, y1: 20 }, additive: false }));
    act(() => result.current.pick(1, { kind: "word", index: 2, box: { x0: 30, y0: 10, x1: 40, y1: 20 }, additive: true }));
    expect(result.current.selection?.words.map((w) => w.index)).toEqual([1, 2]);
    expect(client.GET).toHaveBeenLastCalledWith("/api/v1/documents/{id}/pages/{n}/words", expect.objectContaining({ params: expect.objectContaining({ query: { bbox: "9,9,41,21" } }) }));

    act(() => result.current.pick(1, { kind: "region", box: { x0: 100, y0: 100, x1: 300, y1: 140 } }));
    await waitFor(() => expect(result.current.noWords).toBe(true));
    await act(async () => result.current.ocr());
    expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}/regions:read", { params: { path: { id: "doc-1" } }, body: { page: 1, bBox: { x0: 100, y0: 100, x1: 300, y1: 140 } } });
    expect(result.current.error).toMatch(/No OCR is configured/);
  });
});

describe("table tool (useTableTool)", () => {
  const document = documentOf({ type: "price-list", canonicalSchemaVersion: 2, canonical: { lines: [] } as unknown as DocumentDetail["canonical"] });
  const columns = [
    { x0: 50, x1: 150, field: "supplierSku", header: "Art.-Nr." },
    { x0: 150, x1: 500, field: "description", header: "Bezeichnung" },
  ];
  const report = (rows: number) => ({ rows, rejectedRows: 0, pages: [1], pagesWithoutRows: [], stoppedOnPage: null, perPage: [{ page: 1, headerFound: true, rows, rejected: 0, rejectedLines: [], skippedLines: 0, groupHeadings: 0, continuationLines: 0, columns: [{ x0: 50, x1: 150 }, { x0: 150, x1: 500 }], columnsFrom: "mapping", firstRows: [] }] });

  it("proposes from a box, previews all pages, writes once and keeps the mapping across remounts until then", async () => {
    client.GET.mockResolvedValue(ok([]));
    client.POST.mockResolvedValueOnce(ok({ path: "lines", proposed: true, columns, headerRow: { y0: 285, y1: 297 }, rowAnchor: "supplierSku", report: report(23), rows: [], rowsTruncated: false, written: false }));
    const { result } = renderHook(() => useTableTool(document, formSpec, "lines", true), { wrapper });
    act(() => result.current.onRegion(1, { x0: 45, y0: 280, x1: 945, y1: 935 }));
    await waitFor(() => expect(result.current.state).not.toBeNull());
    expect(client.POST).toHaveBeenLastCalledWith("/api/v1/documents/{id}/regions:table", { params: { path: { id: document.id } }, body: { page: 1, bBox: { x0: 45, y0: 280, x1: 945, y1: 935 }, path: "lines", groupHeader: { target: "category" } } });
    expect(result.current.state).toMatchObject({ page: 1, allPages: true, groupHeadingTarget: "category", rowAnchor: "supplierSku" });
    expect(result.current.reportKind).toBe("proposal");
    expect(result.current.drawing(1)?.dividers).toEqual([50, 150, 500]);
    expect(readReviewSession(document.id).table?.columns).toHaveLength(2);

    // A box drawn while a mapping exists is ignored.
    act(() => result.current.onRegion(2, { x0: 1, y0: 1, x1: 500, y1: 500 }));
    expect(client.POST).toHaveBeenCalledTimes(1);

    client.POST.mockResolvedValueOnce(ok({ path: "lines", proposed: false, columns, headerRow: null, rowAnchor: "supplierSku", report: report(412), rows: [], rowsTruncated: false, written: false }));
    act(() => result.current.onPreview());
    await waitFor(() => expect(result.current.reportKind).toBe("preview"));
    expect(client.POST.mock.calls[1]![1].body).toMatchObject({ pages: "1-*", groupHeader: { target: "category" }, write: false });

    // Moving a divider makes the preview stale.
    act(() => result.current.drawing(1)!.onDividerMove!(1, 140));
    expect(result.current.reportKind).toBe("stale");
    expect(result.current.state!.columns[0]!.x1).toBe(140);

    const written = documentOf({ ...document, lineCount: 412, updatedAt: "2026-11-03T10:05:00Z" });
    client.POST.mockResolvedValueOnce(ok({ path: "lines", proposed: false, columns, headerRow: null, rowAnchor: "supplierSku", report: report(412), rows: [], rowsTruncated: false, written: true, document: written }));
    act(() => result.current.onWrite());
    await waitFor(() => expect(result.current.state).toBeNull());
    // Written at once: the screen remounts for the new version in the same render.
    expect(readReviewSession(document.id).table).toBeNull();
    expect(client.POST.mock.calls[2]![1].body).toMatchObject({ write: true, reason: "Table captured in review" });
    expect(queryClient.getQueryData(["documents", "detail", document.id])).toEqual(written);
    expect(toast.success).toHaveBeenCalledWith("412 rows written as one correction.");
  });

  it("shows the stored report (the page strip after a reload)", async () => {
    client.GET.mockResolvedValue(ok([{ path: "lines", origin: "ReviewTool", correctionId: 7, producedAtVersion: 3, createdAt: "2026-11-03T10:05:00Z", report: report(412) }]));
    const { result } = renderHook(() => useTableTool(document, formSpec, "lines", true), { wrapper });
    await waitFor(() => expect(result.current.reportKind).toBe("stored"));
    expect(result.current.report?.rows).toBe(412);
    expect(result.current.drawing(1)?.onDividerMove).toBeUndefined();
  });
});

describe("review dialogs", () => {
  it("cuts pages into parts and reads the supplier party for a new supplier", () => {
    expect(partsFromStarts(5, new Set([2, 4]))).toEqual([[1], [2, 3], [4, 5]]);
    expect(partsFromStarts(3, new Set())).toEqual([[1, 2, 3]]);
    const document = documentOf({ canonical: { supplier: { status: "Corrected", value: { name: "Nordwind Großhandel GmbH", vatId: "DE312456789", email: "verkauf@nordwind.example" } } } as unknown as DocumentDetail["canonical"] });
    expect(supplierDraftOf(document)).toEqual({ name: "Nordwind Großhandel GmbH", vatId: "DE312456789", iban: "", emailDomain: "nordwind.example" });
    expect(supplierDraftOf(documentOf({ supplierName: "Routed GmbH" })).name).toBe("Routed GmbH");
    expect(usableTypes([{ key: "a", publishedVersion: 1, status: "Active" }, { key: "b", publishedVersion: null, status: "Active" }, { key: "c", publishedVersion: 2, status: "Retired" }] as never).map((t) => t.key)).toEqual(["a"]);
  });

  it("sets the type with the prefill chosen", async () => {
    client.GET.mockResolvedValue(ok([{ key: "price-list", name: { en: "Price list" }, publishedVersion: 1, status: "Active", versions: [] }, { key: "Invoice", name: { en: "Invoice" }, publishedVersion: 1, status: "Active", versions: [] }]));
    client.POST.mockResolvedValue(ok(documentOf({ type: "price-list" })));
    render(<SetTypeForm document={documentOf()} locale="en" />, { wrapper });
    const trigger = await screen.findByTestId("set-type-key");
    await waitFor(() => expect(trigger).not.toBeDisabled());
    fireEvent.pointerDown(trigger, { button: 0, pointerType: "mouse" });
    fireEvent.click(await screen.findByRole("option", { name: /Price list/ }));
    fireEvent.click(screen.getByLabelText("Let AI pre-fill"));
    fireEvent.click(screen.getByTestId("set-type-submit"));
    await waitFor(() => expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:set-type", { params: { path: { id: documentOf().id } }, body: { typeKey: "price-list", prefill: "auto" } }));
    expect(toast.success).toHaveBeenCalledWith("The document is read again as a Price list.");
  });

  it("creates a supplier from the document", async () => {
    client.POST.mockResolvedValue(ok({ supplier: { id: "s1", name: "Nordwind Großhandel GmbH" }, document: documentOf({ supplierName: "Nordwind Großhandel GmbH" }) }, 201));
    const document = documentOf({ canonical: { supplier: { status: "Corrected", value: { name: "Nordwind Großhandel GmbH", vatId: "DE312456789" } } } as unknown as DocumentDetail["canonical"] });
    render(<CreateSupplierDialog document={document} open onOpenChange={() => undefined} />, { wrapper });
    expect(screen.getByLabelText("Name")).toHaveValue("Nordwind Großhandel GmbH");
    fireEvent.change(screen.getByLabelText("E-mail domain"), { target: { value: "nordwind.example" } });
    fireEvent.click(screen.getByTestId("create-supplier-submit"));
    await waitFor(() => expect(client.POST).toHaveBeenCalled());
    expect(client.POST.mock.calls[0]![0]).toBe("/api/v1/documents/{id}:create-supplier");
    expect(client.POST.mock.calls[0]![1].body).toMatchObject({ name: "Nordwind Großhandel GmbH", vatId: "DE312456789", emailDomains: ["nordwind.example"], ibans: null });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Supplier Nordwind Großhandel GmbH created and assigned."));
  });

  it("splits at the pages ticked", async () => {
    client.GET.mockResolvedValue(ok([]));
    client.POST.mockResolvedValue(ok({ parts: [{}, {}] }, 202));
    render(<SplitDialog document={documentOf({ pageCount: 3 })} locale="en" open onOpenChange={() => undefined} />, { wrapper });
    expect(screen.getByTestId("split-submit")).toBeDisabled();
    fireEvent.click(screen.getByLabelText("A new document starts at page 3"));
    expect(screen.getByTestId("split-part-1")).toHaveTextContent("Part 2: pages 3–3");
    fireEvent.click(screen.getByTestId("split-submit"));
    await waitFor(() => expect(client.POST).toHaveBeenCalled());
    expect(client.POST.mock.calls[0]![1].body).toEqual({ parts: [{ pages: [1, 2], typeKey: null }, { pages: [3], typeKey: null }] });
  });

  it("joins the other parts of the attachment", async () => {
    const id = documentOf().id;
    client.GET.mockResolvedValue(
      ok([
        { documentId: id, pageStart: 1, pageEnd: 2, pageCount: 2, status: "NeedsReview", type: "Other", documentNumber: null, joinedIntoDocumentId: null },
        { documentId: "p2", pageStart: 3, pageEnd: 5, pageCount: 3, status: "NeedsReview", type: "Other", documentNumber: "PL-2", joinedIntoDocumentId: null },
        { documentId: "p0", pageStart: 6, pageEnd: 6, pageCount: 1, status: "Cancelled", type: "Other", documentNumber: null, joinedIntoDocumentId: "x" },
      ]),
    );
    client.POST.mockResolvedValue(ok(documentOf(), 202));
    render(<JoinDialog document={documentOf()} open onOpenChange={() => undefined} />, { wrapper });
    const list = await screen.findByTestId("join-parts");
    expect(within(list).getAllByRole("checkbox")).toHaveLength(1);
    fireEvent.click(within(list).getByLabelText(/Pages 3–5: PL-2/));
    fireEvent.click(screen.getByTestId("join-submit"));
    await waitFor(() => expect(client.POST).toHaveBeenCalledWith("/api/v1/documents/{id}:join", { params: { path: { id } }, body: { documentIds: ["p2"] } }));
  });
});

describe("review actions", () => {
  it("offers change type, supplier, split and join for a document in review", async () => {
    client.GET.mockResolvedValue(ok([]));
    render(<ReviewActions document={documentOf()} approveOpen={false} onApproveOpenChange={() => undefined} hasUnsavedChanges={false} />, { wrapper });
    fireEvent.pointerDown(screen.getByTestId("more-actions"), { button: 0, pointerType: "mouse" });
    for (const id of ["action-set-type", "action-assign-supplier", "action-create-supplier", "action-split", "action-join"]) expect(await screen.findByTestId(id)).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("action-set-type"));
    expect(await screen.findByRole("dialog", { name: "Change type" })).toBeInTheDocument();
  });

  it("shows the approval gate's refusal in the UI language", async () => {
    const refusal = new ApiError(409, { title: "Cannot approve", status: 409, detail: "The document type is Unknown.", code: "TYPE_REQUIRED" } as never, "/x");
    expect(approvalRefusalCode(refusal)).toBe("TYPE_REQUIRED");
    expect(approvalRefusalCode(new ApiError(409, { title: "Document changed" }, "/x"))).toBeNull();
    expect(approvalRefusalCode(new Error("x"))).toBeNull();

    client.GET.mockResolvedValue(ok([]));
    client.POST.mockResolvedValue(fail(409, { title: "Cannot approve", status: 409, detail: "The document type is Unknown.", code: "TYPE_REQUIRED" }));
    render(<ReviewActions document={documentOf()} approveOpen onApproveOpenChange={() => undefined} hasUnsavedChanges={false} />, { wrapper });
    fireEvent.click(within(await screen.findByRole("alertdialog")).getByRole("button", { name: "Approve" }));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The document cannot be approved", { description: expect.stringMatching(/Set the type of this document/) }));
  });
});
