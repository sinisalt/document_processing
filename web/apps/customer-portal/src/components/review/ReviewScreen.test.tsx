import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@/i18n";
import { fieldDomId, type CanonicalInvoice } from "@/lib/canonical";
import type { DocumentDetail, DocumentEvent, DocumentFlag } from "@/lib/queries";

/** What the fake API answers, by path template; anything else is a 404 problem. */
type Handler = (options: unknown) => { status: number; data?: unknown; error?: unknown };
const routes = new Map<string, Handler>();
const client = {
  GET: vi.fn(async (path: string, options: unknown) => {
    const handler = routes.get(path);
    const { status, data, error } = handler ? handler(options) : { status: 404, error: { title: "Not Found", status: 404 } };
    const ok = status < 400;
    return { data: ok ? data : undefined, error: ok ? undefined : error, response: new Response(null, { status }) };
  }),
  PATCH: vi.fn(),
  POST: vi.fn(),
};

vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));
const navigate = vi.hoisted(() => vi.fn());
// No router here: navigation is a spy, and the unsaved-changes blocker (tested with a real router in
// ReviewScreen.guard.test.tsx) stays idle.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-router")>()),
  useNavigate: () => navigate,
  useBlocker: () => ({ status: "idle" }),
}));
const toast = vi.hoisted(() => ({ error: vi.fn(), warning: vi.fn(), success: vi.fn(), info: vi.fn() }));
vi.mock("sonner", async (importOriginal) => ({ ...(await importOriginal<typeof import("sonner")>()), toast }));

const { createQueryClient } = await import("@docproc/app-shell");
const { ReviewScreen } = await import("./ReviewScreen");
const { PageViewer } = await import("./PageViewer");

const money = (amount: number) => ({ amount, currency: "EUR" });
const extracted = <T,>(value: T) => ({ value, status: "Extracted" as const, confidence: 1, source: { method: "StructuredXml" as const } });

const canonical: CanonicalInvoice = {
  kind: extracted("Invoice"),
  invoiceNumber: extracted({ verbatim: "INV-1001" }),
  issueDate: extracted("2026-10-01"),
  currency: extracted("EUR"),
  supplier: extracted({ name: "Nordsee Fisch GmbH", vatId: "DE123456789" }),
  subtotalNet: extracted(money(255)),
  totalTax: extracted(money(48.45)),
  totalGross: extracted(money(303.45)),
  lines: [{ lineNumber: 1, description: extracted("Lachsfilet"), quantity: extracted(10), unitPrice: extracted(money(25.5)), lineNet: extracted(money(255)) }],
};

const flag = (id: string, field: string, severity: DocumentFlag["severity"], code: string): DocumentFlag => ({
  id,
  field,
  severity,
  code,
  message: `${code} on ${field}`,
  params: {},
  createdAt: "2026-10-30T10:00:00Z",
  createdBy: "Pipeline",
  resolvedAt: null,
  resolvedBy: null,
});

function documentOf(patch: Partial<DocumentDetail> = {}): DocumentDetail {
  return {
    id: "6f1c1d2e-0000-4000-8000-000000000001",
    tenantId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    tenantName: "Gasthaus zur Post GmbH",
    type: "Invoice",
    status: "NeedsReview",
    canonical: canonical as unknown as DocumentDetail["canonical"],
    canonicalSchemaVersion: 1,
    documentNumber: "INV-1001",
    supplierId: null,
    supplierName: "Nordsee Fisch GmbH",
    issueDate: "2026-10-01",
    totalGross: 303.45,
    currency: "EUR",
    pageCount: 0,
    pageImageUrls: [],
    flags: [flag("f-vat", "supplier.vatId", "Blocking", "VAT_ID_INVALID"), flag("f-total", "totalGross", "Warning", "TOTAL_MISMATCH")],
    blockingFlagCount: 1,
    warningFlagCount: 1,
    approvedAt: null,
    approvedBy: null,
    costs: {} as DocumentDetail["costs"],
    createdAt: "2026-10-30T10:00:00Z",
    updatedAt: "2026-10-30T10:00:00Z",
    extractionMethod: "StructuredXml",
    failureReason: null,
    ingestion: null,
    typeConfidence: 1,
    ...patch,
  };
}

const event = (id: number, eventType: string, fromStatus: DocumentEvent["fromStatus"], toStatus: DocumentEvent["toStatus"]): DocumentEvent => ({
  id,
  eventType,
  fromStatus,
  toStatus,
  actor: "pipeline",
  details: null,
  at: `2026-10-30T10:00:${String(id).padStart(2, "0")}Z`,
});

function renderScreen(document: DocumentDetail, queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  return render(
    <QueryClientProvider client={queryClient}>
      <ReviewScreen document={document} canReview threshold={0.8} locale="en" />
    </QueryClientProvider>,
  );
}

function input(path: string): HTMLInputElement {
  const element = window.document.getElementById(fieldDomId(path));
  if (!(element instanceof HTMLInputElement)) throw new Error(`no input for ${path}`);
  return element;
}

/** A key press as the browser does it: keydown on the focused element, and unless prevented, the character typed into an input. */
function press(key: string) {
  const target = window.document.activeElement ?? window.document.body;
  const notPrevented = fireEvent.keyDown(target, { key });
  if (notPrevented && target instanceof HTMLInputElement && key.length === 1) fireEvent.change(target, { target: { value: target.value + key } });
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView = vi.fn();
  // Radix Select uses pointer capture, which jsdom lacks.
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  window.matchMedia ??= ((query: string) => ({ matches: false, media: query, onchange: null, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false })) as typeof window.matchMedia;
  globalThis.ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

beforeEach(() => {
  routes.clear();
  client.GET.mockClear();
  navigate.mockClear();
  Object.values(toast).forEach((fn) => fn.mockClear());
  vi.mocked(Element.prototype.scrollIntoView).mockClear();
});

afterEach(cleanup);

describe("review screen flag keys", () => {
  it("moves through the flags with j and k without typing into the fields", async () => {
    renderScreen(documentOf());
    const flagButtons = within(screen.getByTestId("flags-list")).getAllByRole("button");
    const saveButton = screen.getByTestId("save-button");
    expect(saveButton).toBeDisabled();

    press("j");
    expect(flagButtons[0]).toHaveAttribute("aria-current", "true");
    expect(window.document.activeElement).toBe(flagButtons[0]);
    expect(Element.prototype.scrollIntoView).toHaveBeenCalled();

    press("j");
    expect(flagButtons[1]).toHaveAttribute("aria-current", "true");
    expect(flagButtons[0]).not.toHaveAttribute("aria-current");
    expect(window.document.activeElement).toBe(flagButtons[1]);
    // The second flag's field was brought into view, not focused.
    expect(vi.mocked(Element.prototype.scrollIntoView).mock.contexts.at(-1)).toBe(input("totalGross"));
    // Only as far as needed: centring also scrolled the window and hid the header with the actions.
    expect(vi.mocked(Element.prototype.scrollIntoView).mock.calls.at(-1)?.[0]).toMatchObject({ block: "nearest" });

    press("k");
    expect(flagButtons[0]).toHaveAttribute("aria-current", "true");

    // Nothing was typed: the values are the server's and the form is clean, so Approve stays available.
    expect(input("supplier.vatId")).toHaveValue("DE123456789");
    expect(input("totalGross")).toHaveValue("303.45");
    await waitFor(() => expect(saveButton).toBeDisabled());
    expect(screen.getByTestId("approve-button")).toBeEnabled();
  });

  it("focuses the field's input when a flag is clicked", () => {
    renderScreen(documentOf());
    const flagButtons = within(screen.getByTestId("flags-list")).getAllByRole("button");
    fireEvent.click(flagButtons[1]!);
    expect(flagButtons[1]).toHaveAttribute("aria-current", "true");
    expect(window.document.activeElement).toBe(input("totalGross"));
    // Inside an input, j is text again.
    press("j");
    expect(input("totalGross")).toHaveValue("303.45j");
  });
});

describe("review screen flag messages", () => {
  afterEach(async () => {
    await i18n.changeLanguage("en");
  });

  it("phrases a flag in the UI language from its code and parameters, with the field's label", async () => {
    await i18n.changeLanguage("de");
    const low: DocumentFlag = {
      ...flag("f-low", "totalGross", "Warning", "LOW_CONFIDENCE"),
      message: "'totalGross' has confidence 0.40, below the threshold 0.80.",
      params: { field: "totalGross", confidence: "0.40", threshold: "0.80" },
    };
    const unknown = { ...flag("f-new", "supplier.vatId", "Blocking", "SOMETHING_NEW"), message: "A check this build does not know failed." };
    renderScreen(documentOf({ flags: [low, unknown] }));

    const list = screen.getByTestId("flags-list");
    expect(list.textContent?.replace(/[\u00a0\u202f]/g, " ")).toContain("Gesamtbetrag (brutto) wurde mit 40 % Sicherheit gelesen, unter dem Schwellenwert von 80 %.");
    expect(list).not.toHaveTextContent("has confidence");
    // An unknown code keeps the API's English text rather than showing nothing.
    expect(list).toHaveTextContent("A check this build does not know failed.");
  });
});

describe("review screen empty flag list", () => {
  it("says every rule passed only when Validate ran on what is shown", async () => {
    routes.set("/api/v1/documents/{id}/events", () => ({
      status: 200,
      data: [event(1, "document.received", null, "Received"), event(2, "document.stage_completed", "Extracted", "Validated"), event(3, "document.review_requested", "Validated", "NeedsReview")],
    }));
    renderScreen(documentOf({ flags: [], blockingFlagCount: 0, warningFlagCount: 0 }));
    await waitFor(() => expect(screen.getByTestId("flags-empty")).toHaveTextContent("No flags: every validation rule passed."));
  });

  it("does not claim a pass after a correction the API has not re-validated", async () => {
    routes.set("/api/v1/documents/{id}/events", () => ({
      status: 200,
      data: [event(1, "document.stage_completed", "Extracted", "Validated"), event(2, "document.review_requested", "Validated", "NeedsReview"), event(3, "document.corrected", "NeedsReview", "NeedsReview")],
    }));
    renderScreen(documentOf({ flags: [] }));
    await waitFor(() => expect(screen.getByTestId("flags-empty")).toHaveTextContent("Corrections not re-validated: reprocess from Validate to re-check."));
  });

  it("does not claim a pass for a document that failed before validation", async () => {
    routes.set("/api/v1/documents/{id}/events", () => ({ status: 200, data: [event(1, "document.received", null, "Received"), event(2, "document.stage_failed", "Received", "Failed")] }));
    renderScreen(documentOf({ status: "Failed", flags: [], canonical: null }));
    await waitFor(() => expect(client.GET).toHaveBeenCalledWith("/api/v1/documents/{id}/events", expect.anything()));
    expect(screen.getByTestId("flags-empty")).toHaveTextContent("Not validated yet");
  });
});

describe("page viewer text layer", () => {
  it("asks once for a missing text layer, without a toast or a console error", async () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);
    routes.set("/api/v1/documents/{id}/pages/{n}.png", () => ({ status: 200, data: new Blob(["png"], { type: "image/png" }) }));
    routes.set("/api/v1/documents/{id}/text", () => ({ status: 404, error: { title: "No text layer", status: 404 } }));
    // The app's real query client: its global handler is what toasts errors.
    const queryClient = createQueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <PageViewer documentId="doc-1" pageCount={1} boxes={[]} highlightedPath={null} onHover={() => undefined} onSelect={() => undefined} page={1} onPageChange={() => undefined} />
      </QueryClientProvider>,
    );
    await waitFor(() => expect(queryClient.getQueryState(["documents", "detail", "doc-1", "text"])?.status).toBe("error"));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(client.GET.mock.calls.filter(([path]) => path === "/api/v1/documents/{id}/text")).toHaveLength(1);
    expect(toast.error).not.toHaveBeenCalled();
    expect(consoleError).not.toHaveBeenCalled();
    // The word toggle stays off; the page still renders.
    expect(screen.getByRole("button", { name: "Show recognised words" })).toBeDisabled();
    consoleError.mockRestore();
  });
});

describe("approve shortcut hint", () => {
  it("names Ctrl+Enter off Apple platforms and ⌘↵ on them", () => {
    const platform = vi.spyOn(window.navigator, "platform", "get").mockReturnValue("Win32");
    renderScreen(documentOf());
    expect(screen.getByTestId("approve-shortcut")).toHaveTextContent("Ctrl+Enter");
    expect(screen.getByTestId("approve-button")).toHaveAttribute("aria-keyshortcuts", "Control+Enter");
    cleanup();

    platform.mockReturnValue("MacIntel");
    renderScreen(documentOf());
    expect(screen.getByTestId("approve-shortcut")).toHaveTextContent("⌘↵");
    expect(screen.getByTestId("approve-button")).toHaveAttribute("aria-keyshortcuts", "Meta+Enter");
    platform.mockRestore();
  });
});

describe("review screen Esc and Ctrl+Enter", () => {
  it("goes back to the inbox on Esc outside inputs", () => {
    renderScreen(documentOf());
    fireEvent.keyDown(window.document.body, { key: "Escape" });
    expect(navigate).toHaveBeenCalledWith({ to: "/documents" });
  });

  it("leaves an Esc that a popover handled to the popover", () => {
    renderScreen(documentOf());
    // Radix's DismissableLayer listens on the document in the capture phase and marks the Esc it uses.
    const radix = (event: KeyboardEvent) => event.preventDefault();
    window.document.addEventListener("keydown", radix, { capture: true });
    try {
      fireEvent.keyDown(window.document.body, { key: "Escape" });
    } finally {
      window.document.removeEventListener("keydown", radix, { capture: true });
    }
    expect(navigate).not.toHaveBeenCalled();
  });

  it("does not leave while a Select listbox or a menu is open", () => {
    renderScreen(documentOf());
    for (const role of ["listbox", "menu"]) {
      const layer = window.document.createElement("div");
      layer.setAttribute("role", role);
      const option = window.document.createElement("div");
      option.setAttribute("role", role === "listbox" ? "option" : "menuitem");
      option.tabIndex = -1;
      layer.append(option);
      window.document.body.append(layer);
      fireEvent.keyDown(option, { key: "Escape" });
      layer.remove();
    }
    expect(navigate).not.toHaveBeenCalled();
  });

  it("does not stack the Approve confirmation on the Reject dialog", async () => {
    renderScreen(documentOf());
    fireEvent.click(screen.getByTestId("reject-button"));
    const reason = await screen.findByLabelText("Reason");
    fireEvent.keyDown(reason, { key: "Enter", ctrlKey: true });
    expect(screen.getAllByRole("alertdialog")).toHaveLength(1);
    expect(screen.queryByText("Approve this document?")).not.toBeInTheDocument();
  });

  it("opens the Approve confirmation with Ctrl+Enter when no dialog is open", async () => {
    renderScreen(documentOf());
    fireEvent.keyDown(window.document.body, { key: "Enter", ctrlKey: true });
    expect(await screen.findByText("Approve this document?")).toBeInTheDocument();
  });
});

describe("review screen unsaved corrections", () => {
  it("treats an amount retyped in another format as unchanged", async () => {
    renderScreen(documentOf());
    const saveButton = screen.getByTestId("save-button");
    fireEvent.change(input("totalGross"), { target: { value: "303,450" } });
    await waitFor(() => expect(input("totalGross")).toHaveValue("303,450"));
    expect(saveButton).toBeDisabled();
    expect(screen.getByTestId("approve-button")).toBeEnabled();

    fireEvent.change(input("totalGross"), { target: { value: "303,46" } });
    await waitFor(() => expect(saveButton).toBeEnabled());
    // Approve, Reject and Reprocess all wait for the save.
    expect(screen.getByTestId("approve-button")).toBeDisabled();
    expect(screen.getByTestId("reject-button")).toBeDisabled();
    expect(screen.getByTestId("reprocess-button")).toBeDisabled();
  });
});

describe("review screen page follow", () => {
  it("turns to the page of a field focused in the form", async () => {
    const onPage2 = { ...canonical, totalGross: { ...canonical.totalGross!, source: { method: "Llm" as const, page: 2, bBox: { x0: 700, y0: 900, x1: 900, y1: 930 } } } };
    renderScreen(documentOf({ canonical: onPage2 as unknown as DocumentDetail["canonical"], pageCount: 2 }));
    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
    fireEvent.focus(input("totalGross"));
    expect(await screen.findByText("Page 2 of 2")).toBeInTheDocument();
    // Hovering does not turn the page (a pointer crossing the form would flip through pages).
    fireEvent.click(screen.getByRole("button", { name: "Previous page" }));
    fireEvent.mouseEnter(input("totalGross"));
    expect(screen.getByText("Page 1 of 2")).toBeInTheDocument();
  });
});

describe("review screen timeline freshness", () => {
  it("reads the timeline again on mount even when a cached one is fresh", async () => {
    routes.set("/api/v1/documents/{id}/events", () => ({ status: 200, data: [event(1, "document.stage_completed", "Extracted", "Validated"), event(2, "document.review_requested", "Validated", "NeedsReview")] }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 } } });
    const document = documentOf({ flags: [], blockingFlagCount: 0, warningFlagCount: 0 });
    // What the screen saw before a reprocess: the timeline ends in document.reprocessed.
    queryClient.setQueryData(["documents", "detail", document.id, "events"], [event(1, "document.stage_completed", "Extracted", "Validated"), event(2, "document.reprocessed", "NeedsReview", "Extracted")]);
    renderScreen(document, queryClient);
    await waitFor(() => expect(client.GET).toHaveBeenCalledWith("/api/v1/documents/{id}/events", expect.anything()));
    await waitFor(() => expect(screen.getByTestId("flags-empty")).toHaveTextContent("No flags: every validation rule passed."));
  });
});

describe("review screen reprocess", () => {
  it("offers only the stages a document that failed early reached, and defaults to the latest", async () => {
    routes.set("/api/v1/documents/{id}/events", () => ({ status: 200, data: [event(1, "document.received", null, "Received"), event(2, "document.stage_failed", "Received", "Failed")] }));
    renderScreen(documentOf({ status: "Failed", flags: [], canonical: null }));
    fireEvent.click(screen.getByTestId("reprocess-button"));
    const dialog = await screen.findByRole("alertdialog");
    const trigger = within(dialog).getByRole("combobox");
    await waitFor(() => expect(trigger).toHaveTextContent("Normalize"));
    fireEvent.keyDown(trigger, { key: "Enter" });
    const listbox = await screen.findByRole("listbox");
    const options = within(listbox)
      .getAllByRole("option")
      .map((option) => [option.textContent, option.getAttribute("aria-disabled") === "true"]);
    expect(options).toEqual([
      ["Normalize", false],
      ["Classify", true],
      ["Extract", true],
      ["Validate", true],
    ]);
  });
});
