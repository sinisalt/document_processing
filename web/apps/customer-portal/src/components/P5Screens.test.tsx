import { Suspense, type ComponentType, type ReactNode } from "react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TooltipProvider } from "@docproc/ui";
import { i18n } from "@/i18n";
import type { Supplier } from "@/lib/queries";

/**
 * The screens of P5 part D (docs/11 §19) against a routed fake of the typed client: the supplier's Processing tab
 * (overrides and the http-lookup test call), the master-data import with its report, a destination's acknowledgements,
 * "use for all suppliers without own layouts", the Deliveries tab's rejection and Insights > Onboarding.
 */
type Handler = (options: { params?: { path?: Record<string, string | number>; query?: Record<string, unknown> }; body?: unknown }) => { status?: number; body?: unknown };
const api = vi.hoisted(() => ({ handlers: new Map<string, Handler>(), calls: [] as Array<{ route: string; options: Record<string, unknown> }> }));
function respond(method: string) {
  return async (path: string, options: Record<string, unknown> = {}) => {
    const route = `${method} ${path}`;
    api.calls.push({ route, options });
    const handler = api.handlers.get(route);
    const result = handler ? handler(options as Parameters<Handler>[0]) : { status: 404, body: { title: "Not found", status: 404 } };
    const status = result.status ?? 200;
    const ok = status < 400;
    return { data: ok ? result.body : undefined, error: ok ? undefined : result.body, response: new Response(null, { status }) };
  };
}
const client = vi.hoisted(() => ({}) as Record<string, unknown>);
const routeSearch = vi.hoisted(() => ({ current: {} as Record<string, unknown> }));
vi.mock("@docproc/app-shell", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@docproc/app-shell")>();
  return { ...actual, useApi: () => ({ client }), useAuth: () => ({ user: { scope: "tenant", roles: ["TenantAdmin"] }, hasPolicy: (policy: Parameters<typeof actual.hasPolicy>[1]) => actual.hasPolicy(["TenantAdmin"], policy) }) };
});
vi.mock("@tanstack/react-router", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-router")>()),
  createFileRoute: () => (options: Record<string, unknown>) => ({ options, fullPath: "/", useSearch: () => routeSearch.current, useParams: () => ({}) }),
  useNavigate: () => vi.fn(),
  Link: ({ children, to, search, params: _params, ...rest }: { children?: ReactNode; to?: string; search?: Record<string, string>; params?: unknown }) => (
    <a {...rest} href={`${to}${search ? "?" + new URLSearchParams(search).toString() : ""}`}>
      {children}
    </a>
  ),
}));
const toast = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("sonner", async (importOriginal) => ({ ...(await importOriginal<typeof import("sonner")>()), toast }));

const { SupplierProcessingTab } = await import("./suppliers/SupplierProcessingTab");
const { MasterDataPanel } = await import("./integrations/MasterDataPanel");
const { AcknowledgementsDialog } = await import("./integrations/AcknowledgementsDialog");
const { TenantRuleAction } = await import("./review/TenantRuleAction");
const { DeliveriesTab } = await import("./review/DeliveriesTab");
const insightsRoute = await import("@/routes/insights");
const inboxRoute = await import("@/routes/documents.index");
const queueRoute = await import("@/routes/review");
const { RevisionPanel } = await import("./review/RevisionPanel");
const { useIdentify } = await import("./review/useIdentify");
const { renderHook, act } = await import("@testing-library/react");

async function renderRoute(route: { Route: unknown }) {
  const Component = (route.Route as { options: { component: ComponentType & { preload?: () => Promise<void> } } }).options.component;
  await Component.preload?.();
  return renderWith(<Component />);
}

function renderWith(node: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <Suspense fallback={<p>loading</p>}>{node}</Suspense>
      </TooltipProvider>
    </QueryClientProvider>,
  );
}
const callsTo = (route: string) => api.calls.filter((c) => c.route === route);

async function choose(trigger: HTMLElement, option: string | RegExp) {
  fireEvent.keyDown(trigger, { key: "Enter" });
  const listbox = await screen.findByRole("listbox");
  fireEvent.click(within(listbox).getByRole("option", { name: option }));
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
  for (const method of ["GET", "POST", "PUT", "PATCH", "DELETE"]) client[method] = vi.fn(respond(method));
});
beforeEach(() => {
  api.handlers.clear();
  api.calls.length = 0;
  Object.values(toast).forEach((fn) => fn.mockClear());
});
afterEach(cleanup);

const step = (id: string, kind: string, phase: string, extra: Record<string, unknown> = {}) => ({ id, kind, phase, params: {}, enabled: true, source: "default", fixed: false, runs: true, ...extra });
const supplier = { id: "sup-1", name: "Nordwind Großhandel GmbH", groupId: null, settings: { review: { mode: "always" } }, updatedAt: "2026-11-05T08:00:00Z" } as unknown as Supplier;
const lookupSchema = {
  type: "object",
  required: ["destinationId", "response"],
  additionalProperties: false,
  properties: { destinationId: { type: "string", format: "uuid" }, method: { enum: ["GET", "POST"] }, request: { type: "string", maxLength: 16384 }, response: { type: "string", maxLength: 16384 }, onFailure: { enum: ["warn", "block", "ignore"] } },
};

describe("the supplier's Processing tab", () => {
  beforeEach(() => {
    api.handlers.set("GET /api/v1/document-types", () => ({ body: [{ key: "Invoice", name: { en: "Invoice" }, publishedVersion: 1 }] }));
    api.handlers.set("GET /api/v1/step-kinds", () => ({
      body: [
        { kind: "validate.duplicate", phase: "Validate", description: "", paramsSchema: { type: "object", additionalProperties: false }, fixed: false, insertable: false, runs: true, defaultId: "duplicate" },
        { kind: "llm", phase: "Extract", description: "", paramsSchema: { type: "object", properties: { effort: { enum: ["low", "high"] }, maxCostPerDocument: { type: "number", minimum: 0, maximum: 1000 } } }, fixed: false, insertable: false, runs: true, defaultId: "llm" },
        { kind: "http-lookup", phase: "Extract", description: "", paramsSchema: lookupSchema, fixed: false, insertable: true, runs: true, defaultId: "lookup" },
      ],
    }));
    api.handlers.set("GET /api/v1/document-types/{key}/resolved-pipeline", () => ({
      body: {
        hash: "h1",
        configurable: true,
        overridden: false,
        steps: [step("classify.identification", "classify.identification", "Classify", { fixed: true }), step("llm", "llm", "Extract"), step("duplicate", "validate.duplicate", "Validate"), step("review-gate", "review-gate", "Validate", { fixed: true })],
        skipped: [],
        refused: [],
      },
    }));
    api.handlers.set("GET /api/v1/destinations", () => ({ body: [{ id: "00000000-0000-4000-8000-0000000000d1", name: "erp-vendors", transport: "http" }] }));
    api.handlers.set("GET /api/v1/documents", () => ({ body: { items: [{ id: "doc-1", documentNumber: "RE-1", createdAt: "2026-11-05T07:00:00Z" }], nextCursor: null } }));
    api.handlers.set("PATCH /api/v1/suppliers/{id}", (options) => ({ body: { ...supplier, settings: (options.body as { settings: unknown }).settings } }));
  });

  it("shows the steps, switches one off and saves the overrides with the other settings kept", async () => {
    renderWith(<SupplierProcessingTab supplier={supplier} canEdit />);
    const steps = await screen.findByTestId("pipeline-steps");
    expect(within(steps).getByText("duplicate")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("processing-add-disable"));
    await choose(screen.getByTestId("processing-step"), "duplicate");
    fireEvent.click(screen.getByTestId("processing-step-add"));
    expect(screen.getByTestId("processing-overrides")).toHaveTextContent("Switch off duplicate");
    // A fixed step cannot be switched off: it is not offered.
    fireEvent.click(screen.getByTestId("processing-add-disable"));
    fireEvent.keyDown(screen.getByTestId("processing-step"), { key: "Enter" });
    expect(within(await screen.findByRole("listbox")).queryByRole("option", { name: "review-gate" })).toBeNull();
    fireEvent.keyDown(screen.getByRole("listbox"), { key: "Escape" });

    fireEvent.click(screen.getByTestId("processing-save"));
    await waitFor(() => expect(callsTo("PATCH /api/v1/suppliers/{id}")).toHaveLength(1));
    const body = callsTo("PATCH /api/v1/suppliers/{id}")[0]!.options.body as { settings: Record<string, unknown> };
    expect(body.settings).toEqual({ review: { mode: "always" }, pipelineOverrides: [{ op: "disable", step: "duplicate" }] });
  });

  it("configures an http lookup, tries it against a document and adds it after an Extract step", async () => {
    api.handlers.set("POST /api/v1/pipelines/lookups:test", () => ({ body: { succeeded: true, assignments: { "ext.vendorId": "V-4711" }, reason: null, message: null, httpStatus: 200, attempts: 1 } }));
    renderWith(<SupplierProcessingTab supplier={supplier} canEdit />);
    await screen.findByTestId("pipeline-steps");
    fireEvent.click(screen.getByTestId("processing-add-lookup"));
    const editor = await screen.findByTestId("processing-lookup-editor");
    fireEvent.change(within(editor).getByTestId("lookup-id"), { target: { value: "erpVendor" } });
    await choose(within(editor).getByTestId("lookup-destination"), "erp-vendors");
    fireEvent.change(within(editor).getByTestId("lookup-param-response"), { target: { value: '{ "ext.vendorId": response.id }' } });
    await choose(within(editor).getByTestId("lookup-test-document"), /RE-1/);
    fireEvent.click(within(editor).getByTestId("lookup-test-run"));
    const result = await within(editor).findByTestId("lookup-test-result");
    expect(result).toHaveAttribute("data-succeeded", "true");
    expect(result).toHaveTextContent("V-4711");
    expect(callsTo("POST /api/v1/pipelines/lookups:test")[0]!.options.body).toEqual({ documentId: "doc-1", stepId: "erpVendor", params: { destinationId: "00000000-0000-4000-8000-0000000000d1", response: '{ "ext.vendorId": response.id }' } });

    fireEvent.click(within(editor).getByTestId("lookup-apply"));
    expect(screen.getByTestId("processing-overrides")).toHaveTextContent("http-lookup erpVendor after llm");
  });
});

describe("master-data import", () => {
  it("guesses the column map, dry-runs, shows the report and then imports", async () => {
    const run = (dryRun: boolean) => ({
      id: "run-1",
      sourceId: null,
      trigger: "Import",
      dryRun,
      status: "Succeeded",
      created: 1,
      updated: 1,
      unchanged: 0,
      conflicts: 1,
      errors: 0,
      report: { created: 1, updated: 1, unchanged: 0, conflicts: 1, errors: 0, rows: [{ row: 1, action: "create", name: "Südhof Feinkost", matchedBy: null, changes: [], conflicts: [] }, { row: 2, action: "conflict", name: "Nordwind", matchedBy: "vatId", changes: [], conflicts: ["ibans"] }] },
      at: "2026-11-05T09:00:00Z",
      by: "admin",
    });
    api.handlers.set("POST /api/v1/suppliers:import", (options) => ({ body: run((options.body as { dryRun: boolean }).dryRun) }));
    api.handlers.set("GET /api/v1/master-data-sources", () => ({ body: [] }));
    api.handlers.set("GET /api/v1/destinations", () => ({ body: [] }));
    renderWith(<MasterDataPanel />);
    fireEvent.change(await screen.findByTestId("import-content"), { target: { value: "Lieferant;USt-IdNr;Kreditorennummer\nSüdhof Feinkost;DE111;70002" } });
    expect(await screen.findByTestId("import-columns")).toBeInTheDocument();
    expect(screen.getByTestId("import-apply")).toBeDisabled();
    fireEvent.click(screen.getByTestId("import-dry-run"));
    const report = await screen.findByTestId("master-data-report");
    expect(report).toHaveAttribute("data-dry-run", "true");
    expect(within(report).getByText("1 conflict")).toBeInTheDocument();
    expect(within(report).getByText("conflict on ibans")).toBeInTheDocument();
    expect(callsTo("POST /api/v1/suppliers:import")[0]!.options.body).toMatchObject({ format: "csv", dryRun: true, columns: { name: "Lieferant", vatId: "USt-IdNr", "attributes.erpVendorId": "Kreditorennummer" }, matchKeys: ["attributes.erpVendorId", "vatId", "iban"] });

    fireEvent.click(screen.getByTestId("import-apply"));
    await waitFor(() => expect(callsTo("POST /api/v1/suppliers:import")).toHaveLength(2));
    expect(callsTo("POST /api/v1/suppliers:import")[1]!.options.body).toMatchObject({ dryRun: false });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Imported: 1 created, 1 updated."));
  });
});

describe("destination acknowledgements", () => {
  it("saves a bearer callback and a status poll, and sets the callback secret", async () => {
    api.handlers.set("PUT /api/v1/destinations/{id}/acknowledgements", () => ({ body: {} }));
    api.handlers.set("PUT /api/v1/destinations/{id}/callback-secret", () => ({ body: {} }));
    const destination = { id: "dest-1", name: "erp-ap", transport: "http", acknowledgements: null, hasCallbackSecret: false, hasPreviousCallbackSecret: false } as never;
    renderWith(<AcknowledgementsDialog destination={destination} open onOpenChange={() => undefined} />);
    fireEvent.click(await screen.findByTestId("ack-callback"));
    await choose(screen.getByTestId("ack-auth"), "Bearer token");
    expect(screen.getByTestId("ack-callback-url")).toHaveTextContent("/api/v1/integrations/callbacks/dest-1");
    fireEvent.change(screen.getByTestId("ack-secret"), { target: { value: "short" } });
    expect(screen.getByTestId("ack-secret-set")).toBeDisabled();
    fireEvent.change(screen.getByTestId("ack-secret"), { target: { value: "a-callback-secret-of-32-characters" } });
    fireEvent.click(screen.getByTestId("ack-secret-set"));
    await waitFor(() => expect(callsTo("PUT /api/v1/destinations/{id}/callback-secret")[0]?.options.body).toEqual({ secret: "a-callback-secret-of-32-characters" }));
    fireEvent.click(screen.getByTestId("ack-poll"));
    fireEvent.change(screen.getByTestId("ack-url"), { target: { value: "https://erp.example/api/ap/invoices/{externalId}/status" } });
    fireEvent.click(screen.getByTestId("ack-save"));
    await waitFor(() => expect(callsTo("PUT /api/v1/destinations/{id}/acknowledgements")).toHaveLength(1));
    expect(callsTo("PUT /api/v1/destinations/{id}/acknowledgements")[0]!.options.body).toEqual({ callback: { auth: "bearer" }, statusPoll: { urlTemplate: "https://erp.example/api/ap/invoices/{externalId}/status", intervalMinutes: 15 } });
  });
});

describe("use for all suppliers without own layouts", () => {
  it("saves the marked rule for the document's type and activates it, confirming the other types", async () => {
    const test = { summary: "would have matched 37 price-list documents, 1 other documents", sameType: 37, otherTypes: 1, tested: 120 };
    const rule = (state: string) => ({ id: "r-1", name: "Preisliste im Titel", status: "Active", activeVersion: state === "Active" ? 1 : null, versions: [{ id: "v-1", version: 1, state, test }] });
    api.handlers.set("POST /api/v1/documents/{id}/identification-rules", () => ({ status: 201, body: rule("Draft") }));
    api.handlers.set("POST /api/v1/identification-rules/{id}/versions/{version}:activate", (options) =>
      (options.body as { confirmOtherTypes: boolean }).confirmOtherTypes ? { body: rule("Active") } : { status: 409, body: { title: "Matches other types", status: 409, test } },
    );
    const { rerender } = renderWith(<TenantRuleAction documentId="doc-7" typeKey="price-list" rule={{ all: [{ text: "Preisliste" }] }} holds={false} />);
    expect(screen.getByTestId("use-for-all-suppliers")).toBeDisabled();
    rerender(
      <QueryClientProvider client={new QueryClient()}>
        <TenantRuleAction documentId="doc-7" typeKey="price-list" rule={{ all: [{ text: "Preisliste" }] }} holds />
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getByTestId("use-for-all-suppliers"));
    const saved = await screen.findByTestId("tenant-rule-saved");
    expect(callsTo("POST /api/v1/documents/{id}/identification-rules")[0]!.options).toMatchObject({ params: { path: { id: "doc-7" } }, body: { rule: { all: [{ text: "Preisliste" }] }, typeKey: "price-list", name: null } });
    expect(within(saved).getByTestId("ident-rule-test-summary")).toHaveTextContent("Would have matched 37 documents of this type, 1 other documents (of 120 tested).");
    fireEvent.click(within(saved).getByTestId("tenant-rule-activate"));
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Activate anyway" }));
    await waitFor(() => expect(screen.getByTestId("tenant-rule-saved")).toHaveAttribute("data-state", "Active"));
  });
});

describe("the Deliveries tab", () => {
  it("names the receiver's rejection of a settled document", async () => {
    api.handlers.set("GET /api/v1/deliveries", () => ({ body: { items: [], nextCursor: null } }));
    const flag = { id: "f", code: "EXTERNAL_REJECTED", severity: "Warning", field: null, message: "", params: { destination: "erp-ap", message: "Vendor blocked" }, createdBy: "Pipeline", createdAt: "x", resolvedAt: null, resolvedBy: null } as never;
    renderWith(<DeliveriesTab documentId="doc-1" enabled canRedeliver={false} rejection={flag} />);
    const alert = await screen.findByTestId("deliveries-rejected");
    expect(alert).toHaveTextContent("Rejected by erp-ap");
    expect(alert).toHaveTextContent("Vendor blocked");
  });
});

describe("Insights > Onboarding", () => {
  it("ranks suppliers without layouts with their actions and lists what needs a look", async () => {
    const row = (name: string, documents: number, layouts: unknown[] = []) => ({ supplierId: `id-${name}`, name, documents, autoApproved: 1, autoApprovalRate: 0.5, correctionsPerDocument: 1.25, llmCostUsd: 0.42, openNewLayout: 0, layouts, sampleDocumentId: `doc-${name}` });
    api.handlers.set("GET /api/v1/insights/automation", () => ({
      body: {
        from: "2026-10-07",
        to: "2026-11-05",
        documents: 10,
        autoApproved: 4,
        unknown: 1,
        suppliers: [row("Südhof Feinkost", 6), row("Nordwind", 4, [{ layoutId: "l-1", typeKey: "Invoice", layoutKey: "nordwind-invoice", name: "x", version: 2, state: "Degraded", lastMatchedAt: null }])],
        suppliersWithoutLayouts: [row("Südhof Feinkost", 6)],
        unknownByChannel: [{ id: "c-1", name: "Mailbox", supplierId: null, documents: 10, unknown: 1, rate: 0.1 }],
        unknownBySourceRule: [],
        degradedLayouts: [{ layoutId: "l-1", ownerId: "id-Nordwind", supplierName: "Nordwind", typeKey: "Invoice", layoutKey: "nordwind-invoice", version: 2, state: "Degraded", lastMatchedAt: null, waiting: 0 }],
        staleLayouts: [],
        candidatesWaiting: [],
        overriddenRules: [],
        destinations: [{ destinationId: "d-1", name: "erp-ap", total: 5, failed: 1, deadLettered: 1, failureRate: 0.4 }],
        masterDataConflicts: [{ sourceId: null, name: "suppliers:import", runId: "run-1", at: "2026-11-05T09:00:00Z", conflicts: 2 }],
      },
    }));
    const Component = (insightsRoute.Route as unknown as { options: { component: ComponentType & { preload?: () => Promise<void> } } }).options.component;
    await Component.preload?.();
    renderWith(<Component />);
    const next = await screen.findByTestId("insights-onboard-next");
    const sample = within(next).getByTestId("insights-open-sample");
    expect(sample).toHaveAttribute("href", "/documents/$documentId");
    expect(within(next).getByTestId("insights-create-layout")).toHaveAttribute("href", "/suppliers?supplier=id-S%C3%BCdhof+Feinkost&tab=layouts");
    expect(within(next).getByTestId("insights-add-source-rule")).toHaveAttribute("href", "/suppliers?supplier=id-S%C3%BCdhof+Feinkost&tab=routing");
    expect(screen.getByText("40%")).toBeInTheDocument();
    expect(within(screen.getByTestId("insights-layouts")).getByText("Degraded", { selector: "span,div" })).toBeInTheDocument();
    expect(within(screen.getByTestId("insights-master-data")).getByText("2 conflicts")).toBeInTheDocument();
    const query = callsTo("GET /api/v1/insights/automation")[0]!.options as { params: { query: { from: string; to: string } } };
    expect(Date.parse(query.params.query.to) - Date.parse(query.params.query.from)).toBe(29 * 86_400_000);
  });
});

describe("the inbox and the queue with the dueBy priority", () => {
  const summary = (id: string, extra: Record<string, unknown>) => ({
    id,
    tenantId: "t",
    tenantName: null,
    status: "NeedsReview",
    type: "daily-specials",
    typeConfidence: 1,
    supplierId: "s",
    supplierName: "Nordwind",
    documentNumber: id,
    issueDate: null,
    currency: null,
    totalGross: null,
    extractionMethod: null,
    pageCount: 2,
    blockingFlagCount: 0,
    warningFlagCount: 0,
    createdAt: "2026-11-05T06:00:00Z",
    updatedAt: "2026-11-05T06:00:00Z",
    approvedAt: null,
    validFrom: null,
    validUntil: null,
    lineCount: null,
    reviewPriority: null,
    reviewDueAt: null,
    supersededBy: null,
    ...extra,
  });

  it("puts urgent lists first with ?sort=reviewDue and greys a superseded list with a link to its replacement", async () => {
    api.handlers.set("GET /api/v1/documents", () => ({
      body: {
        items: [
          summary("TA-old", { status: "Approved", supersededBy: "TA-new" }),
          summary("PL-1", { type: "price-list" }),
          summary("TA-new", { reviewPriority: "Urgent", reviewDueAt: "2026-11-05T22:59:59Z" }),
        ],
        nextCursor: null,
      },
    }));
    routeSearch.current = { sort: "reviewDue", tab: "all" };
    await renderRoute(inboxRoute);
    await screen.findAllByText("TA-new");
    const rows = screen.getAllByRole("row").filter((row) => row.getAttribute("data-superseded") !== null || row.getAttribute("data-priority") !== null || within(row).queryByText(/^(TA-old|PL-1|TA-new)$/));
    const numbers = rows.map((row) => within(row).queryByText(/^(TA-old|PL-1|TA-new)$/)?.textContent).filter(Boolean);
    expect(numbers).toEqual(["TA-new", "TA-old", "PL-1"]);
    const old = rows.find((row) => within(row).queryByText("TA-old"))!;
    expect(old).toHaveAttribute("data-superseded", "true");
    expect(old.className).toContain("opacity-60");
    expect(within(old).getByTestId("inbox-superseded")).toHaveAttribute("href", "/documents/$documentId");
    expect(screen.getByTestId("inbox-sort-due")).toHaveAttribute("data-state", "on");
    routeSearch.current = {};
  });

  it("badges an urgent task and an integration issue in the queue", async () => {
    const task = (id: string, extra: Record<string, unknown>) => ({ id, tenantId: "t", tenantName: null, documentId: `d-${id}`, reason: "x", assignedTo: null, status: "Open", createdAt: "2026-11-05T06:00:00Z", slaDueAt: null, completedAt: null, document: summary(`N-${id}`, {}), priority: "Normal", kind: "Review", ...extra });
    api.handlers.set("GET /api/v1/review-tasks", () => ({ body: { items: [task("1", { priority: "Urgent", slaDueAt: "2026-11-05T22:59:59Z" }), task("2", { kind: "IntegrationIssue" })], nextCursor: null } }));
    await renderRoute(queueRoute);
    expect(await screen.findByTestId("queue-priority")).toHaveTextContent("Urgent");
    expect(screen.getByTestId("queue-integration-issue")).toHaveTextContent("Integration issue");
  });
});

describe("the REVISION_OF panel", () => {
  it("shows the earlier document side by side and sends the note with the resolution", async () => {
    const earlier = { id: "e-1", documentNumber: "TA-0511", status: "Approved", createdAt: "2026-11-05T06:00:00Z", approvedAt: "2026-11-05T06:30:00Z", validFrom: "2026-11-05", validUntil: "2026-11-05", lineCount: 24, flags: [] };
    const later = { ...earlier, id: "l-1", status: "NeedsReview", approvedAt: null, lineCount: 26, flags: [{ id: "f", code: "REVISION_OF", severity: "Warning", field: null, message: "", params: { documentId: "e-1" }, createdBy: "Pipeline", createdAt: "x", resolvedAt: null, resolvedBy: null }] };
    api.handlers.set("GET /api/v1/documents/{id}", () => ({ body: earlier }));
    api.handlers.set("POST /api/v1/documents/{id}:resolve-revision", () => ({ body: later }));
    renderWith(<RevisionPanel document={later as never} canReview />);
    const table = await screen.findByTestId("revision-comparison");
    expect(within(table).getByText("24")).toBeInTheDocument();
    const rows = within(table).getAllByRole("row");
    expect(rows.find((r) => r.getAttribute("data-fact") === "lineCount")).toHaveAttribute("data-differs", "true");
    expect(rows.find((r) => r.getAttribute("data-fact") === "number")).toHaveAttribute("data-differs", "false");
    fireEvent.change(screen.getByTestId("revision-note"), { target: { value: "Nachtrag des Mittags" } });
    fireEvent.click(screen.getByTestId("revision-additional"));
    await waitFor(() => expect(callsTo("POST /api/v1/documents/{id}:resolve-revision")).toHaveLength(1));
    expect(callsTo("POST /api/v1/documents/{id}:resolve-revision")[0]!.options.body).toEqual({ resolution: "additional", note: "Nachtrag des Mittags" });
  });
});

describe("Identify mode for a tenant rule", () => {
  it("proposes and tests in the tenant's scope, without a supplier", async () => {
    api.handlers.set("POST /api/v1/documents/{id}/identifiers:propose", () => ({ body: { identifier: { text: "Preisliste", page: 1, region: "title" }, describe: "'Preisliste' in the title", holds: true, warnings: [] } }));
    api.handlers.set("POST /api/v1/identification-rules:test", () => ({ body: { documents: [], overlaps: [] } }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let draft = { connective: "all" as const, chips: [] as never[] };
    const { result, rerender } = renderHook(() => useIdentify("doc-9", null, draft as never, (next) => (draft = next as never), "price-list", "tenant"), {
      wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
    });
    await act(async () => {
      result.current.pick(1, { kind: "word", index: 3, box: [0.1, 0.1, 0.2, 0.12], additive: false } as never);
    });
    await waitFor(() => expect(result.current.proposal?.identifier).not.toBeNull());
    expect(callsTo("POST /api/v1/documents/{id}/identifiers:propose")[0]!.options.body).toEqual({ page: 1, words: [3], scope: "tenant" });
    act(() => result.current.add());
    rerender();
    await waitFor(() => expect(callsTo("POST /api/v1/identification-rules:test").length).toBeGreaterThan(0));
    expect(callsTo("POST /api/v1/identification-rules:test")[0]!.options.body).toMatchObject({ scope: { tenant: true } });
  });
});
