import type { ReactNode } from "react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TooltipProvider } from "@docproc/ui";
import { i18n } from "@/i18n";
import { articlePriceImportMap, compileFieldMap } from "./fieldMap";

/** The Integrations tab and the Deliveries tab against a routed fake of the typed client (docs/11 §19, P3). */
type Handler = (options: { params?: { path?: Record<string, string | number>; query?: Record<string, unknown> }; body?: unknown; headers?: Record<string, string> }) => { status?: number; body?: unknown; headers?: Record<string, string> };
const api = vi.hoisted(() => ({ handlers: new Map<string, Handler>(), calls: [] as Array<{ route: string; options: Parameters<Handler>[0] }> }));

function respond(method: string) {
  return async (path: string, options: Parameters<Handler>[0] = {}) => {
    const route = `${method} ${path}`;
    api.calls.push({ route, options });
    const handler = api.handlers.get(route);
    const result = handler ? handler(options) : { status: 404, body: { title: "Not found" } };
    const status = result.status ?? 200;
    const ok = status < 400;
    return { data: ok ? result.body : undefined, error: ok ? undefined : result.body, response: new Response(null, { status, headers: result.headers }) };
  };
}
const client = vi.hoisted(() => ({}) as Record<string, unknown>);
const auth = vi.hoisted(() => ({ roles: ["TenantAdmin"] as string[] }));

vi.mock("@docproc/app-shell", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@docproc/app-shell")>();
  return { ...actual, useApi: () => ({ client }), useAuth: () => ({ user: { scope: "tenant", roles: auth.roles }, hasPolicy: (policy: Parameters<typeof actual.hasPolicy>[1]) => actual.hasPolicy(auth.roles, policy) }) };
});
vi.mock("@tanstack/react-router", async (importOriginal) => ({ ...(await importOriginal<typeof import("@tanstack/react-router")>()), useNavigate: () => vi.fn() }));

const { IntegrationsTab } = await import("./IntegrationsTab");
const { MappingEditor } = await import("./MappingEditor");
const { DeliveriesTab } = await import("@/components/review/DeliveriesTab");
const { bindingBodyOf, bindingFormOf } = await import("./BindingsPanel");

const settings = (outputs: boolean) => ({ tenant: {}, settings: { features: { configurableTypes: true, customTypes: false, outputs, sourceRouting: false, structuredSources: false, supplierLayouts: false } } });

const destination = {
  id: "dest-1",
  name: "erp-articles",
  transport: "http",
  config: { method: "POST", urlTemplate: "http://mock-erp:8080/api/article-prices" },
  hasSecret: false,
  hasPreviousSecret: false,
  secretSetAt: null,
  status: "Active",
  lastTestAt: null,
  lastTestResult: null,
  createdAt: "2026-11-03T10:00:00Z",
  updatedAt: "2026-11-03T10:00:00Z",
};

function renderWith(node: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>{node}</TooltipProvider>
    </QueryClientProvider>,
  );
}

const callsTo = (route: string) => api.calls.filter((c) => c.route === route);

/**
 * The wait for the tab's first content. Before it shows, the tab reads the settings, then the destinations, then renders
 * every section (tables, Radix selects and dialogs); the first such render in this file takes about 0.5 s on an idle
 * machine, and 1.4 s when the .NET suite ran on the same cores, past the 1 s that findBy waits by default. That wait is
 * for this load, not a speed check, so it allows for a loaded machine.
 */
const tabLoaded = { timeout: 5_000 };

beforeAll(async () => {
  await i18n.changeLanguage("en");
  Element.prototype.scrollIntoView = vi.fn();
  // Radix Select and Checkbox use pointer capture and ResizeObserver, which jsdom lacks.
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
  auth.roles = ["TenantAdmin"];
  api.handlers.set("GET /api/v1/settings", () => ({ body: settings(true) }));
  api.handlers.set("GET /api/v1/destinations", () => ({ body: [destination] }));
  api.handlers.set("GET /api/v1/output-bindings", () => ({ body: [] }));
  api.handlers.set("GET /api/v1/output-mappings", () => ({ body: [] }));
  api.handlers.set("GET /api/v1/output-templates", () => ({ body: [] }));
  api.handlers.set("GET /api/v1/lookup-tables", () => ({ body: [{ key: "erp-uom", description: null, caseSensitive: false, source: "Manual", platform: false, entryCount: 3, revision: 1, updatedAt: "2026-11-03T10:00:00Z" }] }));
  api.handlers.set("GET /api/v1/document-types", () => ({ body: [{ key: "price-list" }, { key: "Invoice" }] }));
  api.handlers.set("GET /api/v1/suppliers", () => ({ body: { items: [{ id: "sup-1", name: "Nordwind Großhandel GmbH" }], nextCursor: null } }));
  api.handlers.set("GET /api/v1/channels", () => ({ body: [] }));
});

afterEach(cleanup);

describe("Settings > Integrations", () => {
  it("says the outputs switch is off and asks for nothing else", async () => {
    api.handlers.set("GET /api/v1/settings", () => ({ body: settings(false) }));
    renderWith(<IntegrationsTab section="destinations" onSectionChange={vi.fn()} mappingKey={null} onMappingKeyChange={vi.fn()} />);
    expect(await screen.findByTestId("integrations-off", undefined, tabLoaded)).toHaveTextContent("Outputs are off for this tenant");
    expect(callsTo("GET /api/v1/destinations")).toHaveLength(0);
  });

  it("lists destinations and creates an http destination with an API key header and its secret", async () => {
    api.handlers.set("POST /api/v1/destinations", ({ body }) => ({ status: 201, body: { ...destination, id: "dest-2", ...(body as object) } }));
    renderWith(<IntegrationsTab section="destinations" onSectionChange={vi.fn()} mappingKey={null} onMappingKeyChange={vi.fn()} />);
    const row = await screen.findByText("erp-articles", undefined, tabLoaded);
    expect(row.closest("tr")).toHaveTextContent("POST http://mock-erp:8080/api/article-prices");

    fireEvent.click(screen.getByTestId("new-destination"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByTestId("destination-name"), { target: { value: "erp-ap" } });
    fireEvent.change(within(dialog).getByTestId("destination-urlTemplate"), { target: { value: "http://mock-erp:8080/api/ap/invoices" } });
    fireEvent.change(within(dialog).getByTestId("destination-responseMapping"), { target: { value: "erpId" } });
    fireEvent.click(within(dialog).getByTestId("destination-save"));

    await waitFor(() => expect(callsTo("POST /api/v1/destinations")).toHaveLength(1));
    expect(callsTo("POST /api/v1/destinations")[0]!.options.body).toEqual({ name: "erp-ap", transport: "http", config: { method: "POST", urlTemplate: "http://mock-erp:8080/api/ap/invoices", responseMapping: "erpId" }, secret: null });
  });

  it("sends a new secret with the change in one request and shows the API's demand for it on the secret field", async () => {
    const bearer = { ...destination, config: { method: "POST", urlTemplate: "https://erp.example/api/article-prices", auth: { type: "bearer" } }, hasSecret: true };
    api.handlers.set("GET /api/v1/destinations", () => ({ body: [bearer] }));
    let attempt = 0;
    api.handlers.set("PATCH /api/v1/destinations/{id}", ({ body }) =>
      ++attempt === 1
        ? { status: 400, body: { title: "One or more validation errors occurred.", status: 400, errors: { secret: ["This change sends the stored secret somewhere else (the host of urlTemplate changed): give the secret again in the same request (secret)."] } } }
        : { body: { ...bearer, ...(body as object) } },
    );
    renderWith(<IntegrationsTab section="destinations" onSectionChange={vi.fn()} mappingKey={null} onMappingKeyChange={vi.fn()} />);
    fireEvent.click(await screen.findByTestId("edit-destination-erp-articles", undefined, tabLoaded));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/needs the secret again/)).toBeInTheDocument();
    fireEvent.change(within(dialog).getByTestId("destination-urlTemplate"), { target: { value: "https://erp-new.example/api/article-prices" } });
    fireEvent.click(within(dialog).getByTestId("destination-save"));
    expect(await within(dialog).findByText(/sends the stored secret somewhere else/)).toBeInTheDocument();
    expect(callsTo("PATCH /api/v1/destinations/{id}")[0]!.options.body).toMatchObject({ secret: null });

    fireEvent.change(within(dialog).getByTestId("destination-secret"), { target: { value: "new-token" } });
    fireEvent.click(within(dialog).getByTestId("destination-save"));
    await waitFor(() => expect(callsTo("PATCH /api/v1/destinations/{id}")).toHaveLength(2));
    expect(callsTo("PATCH /api/v1/destinations/{id}")[1]!.options.body).toMatchObject({ config: { urlTemplate: "https://erp-new.example/api/article-prices", auth: { type: "bearer" } }, secret: "new-token" });
    expect(callsTo("PUT /api/v1/destinations/{id}/secret")).toHaveLength(0);
  });

  it("shows the API's config error on its field", async () => {
    api.handlers.set("POST /api/v1/destinations", () => ({ status: 400, body: { title: "One or more validation errors occurred.", status: 400, errors: { "config.urlTemplate": ["urlTemplate is an absolute http(s) URL."] } } }));
    renderWith(<IntegrationsTab section="destinations" onSectionChange={vi.fn()} mappingKey={null} onMappingKeyChange={vi.fn()} />);
    fireEvent.click(await screen.findByTestId("new-destination", undefined, tabLoaded));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByTestId("destination-name"), { target: { value: "bad" } });
    fireEvent.change(within(dialog).getByTestId("destination-urlTemplate"), { target: { value: "ftp://x" } });
    fireEvent.click(within(dialog).getByTestId("destination-save"));
    expect(await within(dialog).findByText("urlTemplate is an absolute http(s) URL.")).toBeInTheDocument();
  });

  it("creates a binding of price lists to a destination", async () => {
    api.handlers.set("GET /api/v1/output-mappings", () => ({ body: [{ id: "m1", key: "article-price-import", name: "Article prices", origin: "Custom", publishedVersion: 1, draftVersion: null, versions: [{ id: "v1", version: 1, state: "Published" }] }] }));
    api.handlers.set("POST /api/v1/output-bindings", ({ body }) => ({ status: 201, body: { id: "b1", ...(body as object) } }));
    renderWith(<IntegrationsTab section="bindings" onSectionChange={vi.fn()} mappingKey={null} onMappingKeyChange={vi.fn()} />);
    fireEvent.click(await screen.findByTestId("new-binding", undefined, tabLoaded));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByTestId("binding-name"), { target: { value: "article-prices" } });
    // The selects are Radix; their values are set through the same state the form submits, so drive the body builder too.
    expect(within(dialog).getByTestId("binding-save")).toBeDisabled();
    const body = bindingBodyOf({ ...bindingFormOf(null), name: "article-prices", documentTypeKey: "price-list", mappingKey: "article-price-import", destinationId: "dest-1", onReapproval: "Update", updateMappingKey: "article-price-import" }, null);
    expect(body).toEqual({
      name: "article-prices",
      events: ["document.approved"],
      condition: null,
      mappingKey: "article-price-import",
      destinationId: "dest-1",
      exclusiveGroup: null,
      priority: 0,
      onReapproval: "Update",
      updateMappingKey: "article-price-import",
      enabled: true,
      documentTypeKey: "price-list",
      supplierId: null,
      channelId: null,
      pinnedMappingVersion: null,
      mode: null,
    });
  });

  it("clears scope settings of a binding on edit instead of leaving them", () => {
    const existing = { ...bindingFormOf(null), id: "b1", name: "x", events: ["document.approved"], documentTypeKey: "Invoice", supplierId: "sup-1", channelId: "ch-1", condition: "true", mappingKey: "m", pinnedMappingVersion: 2, destinationId: "d", exclusiveGroup: "g", priority: 1, onReapproval: "Resend", updateMappingKey: null, enabled: true, mode: "Immediate", createdAt: "", updatedAt: "" };
    const values = { ...bindingFormOf(existing), documentTypeKey: "", supplierId: "", channelId: "", condition: "", pinnedMappingVersion: "", exclusiveGroup: "" };
    expect(bindingBodyOf(values, existing)).toMatchObject({ documentTypeKey: "", condition: "", exclusiveGroup: "", clearSupplier: true, clearChannel: true, clearPinnedVersion: true, supplierId: null, channelId: null, pinnedMappingVersion: null });
  });
});

describe("the mapping editor", () => {
  const expression = compileFieldMap({ version: 1, rows: articlePriceImportMap.rows.slice(0, 3) });

  beforeEach(() => {
    api.handlers.set("GET /api/v1/output-mappings/{key}", () => ({ body: { id: "m1", key: "article-price-import", name: "Article prices", description: null, sourceTypeKey: "price-list", origin: "Custom", baseTemplate: null, baseVersion: null, status: "Active", publishedVersion: null, draftVersion: 1, createdAt: "", updatedAt: "", versions: [{ id: "v1", version: 1, state: "Draft", engine: "jsonata", contentType: "application/json", revision: 3, hasTargetSchema: false }] } }));
    api.handlers.set("GET /api/v1/output-mappings/{key}/versions/{version}", () => ({ body: { key: "article-price-import", version: { id: "v1", version: 1, state: "Draft", engine: "jsonata", contentType: "application/json", revision: 3 }, expression, fileNameTemplate: null, targetSchema: null, referencedPaths: [], sampleDocumentIds: ["doc-1"] }, headers: { ETag: '"3"' } }));
    api.handlers.set("GET /api/v1/documents", () => ({ body: { items: [{ id: "doc-1", documentNumber: "PL-2027-01", supplierName: "Nordwind Großhandel GmbH", type: "price-list", status: "Approved", lineCount: 412 }], nextCursor: null } }));
    api.handlers.set("POST /api/v1/output-mappings:preview", ({ body }) => {
      const request = body as { includeValues?: boolean; expression: string };
      if (request.includeValues) return { body: { output: {}, undefined: false, outputBytes: 2, errors: [], schemaErrors: [], referencedPaths: [], tables: [], elapsedMs: 3, values: { doc: { lines: [{ supplierSku: "4711", tiers: [{ minQuantity: 10 }] }] }, roles: { validFrom: "2027-01-01" }, supplier: { attributes: { erpVendorId: "V-10042" } } } } };
      return { body: { output: { header: { vendorNo: "V-10042" }, prices: Array.from({ length: 412 }, (_, i) => ({ scales: i < 41 ? [{}, {}] : [] })) }, undefined: false, outputBytes: 110_000, errors: [], schemaErrors: [{ path: "prices[379].price", location: "/prices/379/price", keyword: "type", message: "Value is null but should be number" }], referencedPaths: [], tables: [], elapsedMs: 41, values: null } };
    });
    api.handlers.set("PUT /api/v1/output-mappings/{key}/versions/{version}", () => ({ body: { id: "v1", version: 1, state: "Draft", revision: 4 } }));
  });

  it("shows the visual map of the draft, renders it over the chosen document and saves what it compiles to", async () => {
    renderWith(<MappingEditor mappingKey="article-price-import" canEdit onBack={vi.fn()} />);
    const map = await screen.findByTestId("field-map");
    expect(within(map).getByTestId("fm-target-0")).toHaveValue("header.vendorNo");
    expect(within(map).getByTestId("fm-source-2")).toHaveValue("roles.validFrom");

    // The server-rendered preview: list sizes and the schema error naming the row.
    expect(await screen.findByText("prices: 412 items")).toBeInTheDocument();
    expect(screen.getByText("prices[].scales: 82 items in 41 rows")).toBeInTheDocument();
    expect(screen.getByTestId("preview-schema-errors")).toHaveTextContent("prices[379].price");
    const preview = callsTo("POST /api/v1/output-mappings:preview").find((c) => !(c.options.body as { includeValues?: boolean }).includeValues)!;
    expect(preview.options.body).toMatchObject({ expression, documentId: "doc-1", engine: "jsonata" });

    // A row added in the visual map is compiled into the expression that is saved.
    fireEvent.click(screen.getByTestId("fm-add"));
    fireEvent.change(screen.getByTestId("fm-target-3"), { target: { value: "header.currency" } });
    fireEvent.change(screen.getByTestId("fm-source-3"), { target: { value: "roles.currency" } });
    expect(screen.getByTestId("mapping-publish")).toBeDisabled();
    // "Render again" right after an edit renders the edit, without waiting for the pause in typing.
    fireEvent.click(screen.getByTestId("preview-refresh"));
    await waitFor(() => expect(callsTo("POST /api/v1/output-mappings:preview").some((c) => (c.options.body as { expression: string }).expression.includes('"currency": roles.currency'))).toBe(true), { timeout: 400 });
    fireEvent.click(screen.getByTestId("mapping-save"));
    await waitFor(() => expect(callsTo("PUT /api/v1/output-mappings/{key}/versions/{version}")).toHaveLength(1));
    const put = callsTo("PUT /api/v1/output-mappings/{key}/versions/{version}")[0]!;
    expect(put.options.headers).toEqual({ "If-Match": '"3"' });
    const saved = (put.options.body as { expression: string }).expression;
    expect(saved).toBe(compileFieldMap({ version: 1, rows: [...articlePriceImportMap.rows.slice(0, 3), { kind: "value", target: "header.currency", source: "roles.currency" }] }));
    expect(put.options.body).toMatchObject({ engine: "jsonata", sampleDocumentIds: ["doc-1"] });
  });

  it("puts a field picked in the source tree into the active row, relative to its list", async () => {
    api.handlers.set("GET /api/v1/output-mappings/{key}/versions/{version}", () => ({
      body: { key: "article-price-import", version: { id: "v1", version: 1, state: "Draft", engine: "jsonata", contentType: "application/json", revision: 3 }, expression: compileFieldMap({ version: 1, rows: [{ kind: "list", target: "prices", source: "doc.lines", rows: [{ kind: "value", target: "", source: "" }, { kind: "value", target: "x", source: "y" }] }] }), fileNameTemplate: null, targetSchema: null, referencedPaths: [], sampleDocumentIds: ["doc-1"] },
      headers: { ETag: '"3"' },
    }));
    renderWith(<MappingEditor mappingKey="article-price-import" canEdit onBack={vi.fn()} />);
    const target = await screen.findByTestId("fm-target-0.0");
    act(() => target.focus());
    const tree = await screen.findByTestId("source-tree");
    await within(tree).findByText("lines");
    // Open the list and pick a field of its rows.
    const expanders = within(tree).getAllByRole("button", { name: "Expand" });
    for (const button of expanders) fireEvent.click(button);
    fireEvent.click(await within(tree).findByText("supplierSku"));
    await waitFor(() => expect(screen.getByTestId("fm-source-0.0")).toHaveValue("supplierSku"));
    expect(screen.getByTestId("fm-target-0.0")).toHaveValue("supplierSku");
  });

  it("keeps a template's code in code mode and offers a visual map only by replacing it", async () => {
    api.handlers.set("GET /api/v1/output-mappings/{key}/versions/{version}", () => ({ body: { key: "article-price-import", version: { id: "v1", version: 1, state: "Draft", engine: "jsonata", contentType: "application/json", revision: 3 }, expression: '{ "docNo": roles.documentNumber }', fileNameTemplate: null, targetSchema: null, referencedPaths: [], sampleDocumentIds: [] }, headers: { ETag: '"3"' } }));
    renderWith(<MappingEditor mappingKey="article-price-import" canEdit onBack={vi.fn()} />);
    expect(await screen.findByTestId("mapping-code")).toHaveValue('{ "docNo": roles.documentNumber }');
    fireEvent.mouseDown(screen.getByTestId("mapping-tab-visual"));
    fireEvent.click(screen.getByTestId("mapping-tab-visual"));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(screen.queryByTestId("field-map")).not.toBeInTheDocument();
  });

  it("is read-only for a reviewer", async () => {
    auth.roles = ["Reviewer"];
    renderWith(<MappingEditor mappingKey="article-price-import" canEdit={false} onBack={vi.fn()} />);
    expect(await screen.findByTestId("mapping-read-only")).toHaveTextContent("Only an integration admin changes mappings.");
    expect(screen.queryByTestId("mapping-save")).not.toBeInTheDocument();
    expect(screen.queryByTestId("fm-add")).not.toBeInTheDocument();
  });
});

describe("the document's Deliveries tab", () => {
  const delivery = (patch: Record<string, unknown>) => ({
    id: "del-1",
    documentId: "doc-1",
    documentVersion: 3,
    event: "document.approved",
    bindingId: "b1",
    bindingName: "article-prices",
    destinationId: "dest-1",
    destinationName: "erp-articles",
    transport: "http",
    mappingVersionId: "v1",
    mappingVersion: 1,
    idempotencyKey: "doc-1:b1:3",
    status: "Delivered",
    attempts: 2,
    nextAttemptAt: null,
    lastStatusCode: 202,
    lastError: null,
    contentType: "application/json",
    payloadSha256: "abc",
    feedSequence: null,
    redeliveryOfId: null,
    createdAt: "2026-11-03T10:00:00Z",
    deliveredAt: "2026-11-03T10:00:02Z",
    ...patch,
  });

  it("lists the document's deliveries and opens one to its attempts, external id, payload and redelivery", async () => {
    api.handlers.set("GET /api/v1/deliveries", () => ({ body: { items: [delivery({}), delivery({ id: "del-2", bindingName: "not-for-lists", destinationName: "erp-ap", status: "Skipped", attempts: 0, lastStatusCode: null, lastError: "not applicable", payloadSha256: null })], nextCursor: null } }));
    api.handlers.set("GET /api/v1/deliveries/{id}", () => ({ body: { delivery: delivery({}), attempts: [{ at: "2026-11-03T10:00:00Z", durationMs: 12, statusCode: 503, errorClass: "retryable:status", responseExcerpt: '{"error":"unavailable"}', step: null }, { at: "2026-11-03T10:00:02Z", durationMs: 30, statusCode: 202, errorClass: null, responseExcerpt: '{"importId":"AP-IMP-1"}', step: null }], externalRef: { externalId: "AP-IMP-1", status: "Accepted", message: null, updatedAt: "2026-11-03T10:00:02Z" } } }));
    api.handlers.set("GET /api/v1/deliveries/{id}/payload", () => ({ body: '{"header":{"vendorNo":"V-10042"}}' }));
    api.handlers.set("POST /api/v1/deliveries/{id}:redeliver", () => ({ status: 202, body: delivery({ id: "del-3", idempotencyKey: "doc-1:b1:3:r1", status: "Pending" }) }));

    renderWith(<DeliveriesTab documentId="doc-1" enabled canRedeliver />);
    const row = (await screen.findByText("erp-articles")).closest("tr")!;
    expect(row).toHaveTextContent("Delivered");
    expect(screen.getByText("erp-ap").closest("tr")).toHaveTextContent("not applicable");
    expect(callsTo("GET /api/v1/deliveries")[0]!.options.params?.query).toEqual({ documentId: "doc-1", limit: 200 });

    fireEvent.click(within(row).getAllByRole("button")[0]!);
    const detail = await screen.findByTestId("delivery-detail");
    const attempts = await within(detail).findByTestId("delivery-attempts");
    expect(attempts).toHaveTextContent("503");
    expect(attempts).toHaveTextContent("Will be retried · status");
    expect(attempts).toHaveTextContent("202");
    expect(within(detail).getByTestId("delivery-external-id")).toHaveTextContent("AP-IMP-1");

    fireEvent.click(within(detail).getByTestId("delivery-redeliver"));
    await waitFor(() => expect(callsTo("POST /api/v1/deliveries/{id}:redeliver")).toHaveLength(1));
    expect(callsTo("POST /api/v1/deliveries/{id}:redeliver")[0]!.options).toMatchObject({ params: { path: { id: "del-1" } }, body: { mode: "snapshot", mappingVersion: null } });
  });

  it("keeps reading an empty list for a while after the decision, until the fan-out shows up", async () => {
    let fannedOut = false;
    api.handlers.set("GET /api/v1/deliveries", () => ({ body: { items: fannedOut ? [delivery({})] : [], nextCursor: null } }));
    renderWith(<DeliveriesTab documentId="doc-1" enabled canRedeliver settledAt={new Date().toISOString()} />);
    expect(await screen.findByText("No deliveries")).toBeInTheDocument();

    fannedOut = true;
    expect(await screen.findByText("erp-articles", undefined, { timeout: 4000 })).toBeInTheDocument();
  });

  it("does not poll an empty list of a document decided long ago", async () => {
    api.handlers.set("GET /api/v1/deliveries", () => ({ body: { items: [], nextCursor: null } }));
    renderWith(<DeliveriesTab documentId="doc-1" enabled canRedeliver settledAt="2026-01-01T00:00:00Z" />);
    expect(await screen.findByText("No deliveries")).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 2000));
    expect(callsTo("GET /api/v1/deliveries")).toHaveLength(1);
  });

  it("offers no redelivery to a reviewer", async () => {
    auth.roles = ["Reviewer"];
    api.handlers.set("GET /api/v1/deliveries", () => ({ body: { items: [delivery({ status: "DeadLettered", lastStatusCode: 422, lastError: "422 from the receiver" })], nextCursor: null } }));
    api.handlers.set("GET /api/v1/deliveries/{id}", () => ({ body: { delivery: delivery({}), attempts: [], externalRef: null } }));
    renderWith(<DeliveriesTab documentId="doc-1" enabled canRedeliver={false} />);
    const row = (await screen.findByText("erp-articles")).closest("tr")!;
    expect(row).toHaveTextContent("Dead letter");
    fireEvent.click(within(row).getAllByRole("button")[0]!);
    await screen.findByTestId("delivery-detail");
    expect(screen.queryByTestId("delivery-redeliver")).not.toBeInTheDocument();
  });
});
