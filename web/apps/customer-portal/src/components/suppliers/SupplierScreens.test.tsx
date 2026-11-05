import type { ReactNode } from "react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TooltipProvider } from "@docproc/ui";
import { i18n } from "@/i18n";
import type { Supplier } from "@/lib/queries";

/** The P4 supplier and settings screens (docs/11 §19 items 3, 5, 6) against a routed fake of the typed client. */
type Handler = (options: { params?: { path?: Record<string, string | number>; query?: Record<string, unknown> }; body?: unknown }) => { status?: number; body?: unknown };
const api = vi.hoisted(() => ({ handlers: new Map<string, Handler>(), calls: [] as Array<{ route: string; options: Parameters<Handler>[0] }> }));
function respond(method: string) {
  return async (path: string, options: Parameters<Handler>[0] = {}) => {
    const route = `${method} ${path}`;
    api.calls.push({ route, options });
    const handler = api.handlers.get(route);
    const result = handler ? handler(options) : { status: 200, body: [] };
    const status = result.status ?? 200;
    const ok = status < 400;
    return { data: ok ? result.body : undefined, error: ok ? undefined : result.body, response: new Response(null, { status }) };
  };
}
const client = vi.hoisted(() => ({}) as Record<string, unknown>);
const auth = vi.hoisted(() => ({ roles: ["TenantAdmin"] as string[] }));
vi.mock("@docproc/app-shell", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@docproc/app-shell")>();
  return { ...actual, useApi: () => ({ client }), useAuth: () => ({ user: { scope: "tenant", roles: auth.roles }, hasPolicy: (policy: Parameters<typeof actual.hasPolicy>[1]) => actual.hasPolicy(auth.roles, policy) }) };
});
vi.mock("@tanstack/react-router", async (importOriginal) => ({ ...(await importOriginal<typeof import("@tanstack/react-router")>()), useNavigate: () => vi.fn(), Link: ({ children }: { children: ReactNode }) => children }));

const { SupplierLayoutsTab } = await import("./SupplierLayoutsTab");
const { SupplierSettingsTab, settingsBody, settingsDraft } = await import("./SupplierSettingsTab");
const { SupplierRoutingTab } = await import("./SupplierRoutingTab");
const { SupplierAttributesTab } = await import("./SupplierAttributesTab");
const { RoutingTestBox, routingTestBody } = await import("@/components/routing/RoutingTestBox");
const { channelConfig, emptyChannel } = await import("@/components/settings/RoutingTab");
const { layoutSettingsBody } = await import("@/components/settings/LayoutSettingsCard");
const { bundleFileName } = await import("@/components/settings/GovernanceTab");

const supplier = {
  id: "sup-1",
  name: "Nordwind Großhandel GmbH",
  normalizedName: "nordwind",
  vatId: "DE312456789",
  taxId: null,
  ibans: [],
  emailDomains: ["nordwind.example"],
  aliases: [],
  defaultCurrency: "EUR",
  alwaysReview: false,
  globalSupplierId: null,
  createdAt: "2026-11-03T10:00:00Z",
  updatedAt: "2026-11-03T10:00:00Z",
  attributes: { erpVendorId: "V-10042", legacy: "x" },
  groupId: null,
  settings: { layouts: { required: true } },
} as unknown as Supplier;

const version = (patch: Record<string, unknown>) => ({ id: "v-1", version: 1, status: "Active", origin: "NewLayoutReview", identificationHash: "abcd1234", describe: 'all of: text "RECHNUNG / INVOICE"', reviewWarmup: 5, warmupSeen: 1, identified: 2, applied: 2, agreed: 0, corrected: 0, consecutiveFailures: 0, createdBy: "tenant-a-reviewer", createdAt: "2026-11-03T10:00:00Z", approvedBy: null, approvedAt: null, ...patch });
const layout = (key: string, v: Record<string, unknown>) => ({ id: `l-${key}`, scope: "Supplier", supplierId: "sup-1", groupId: null, documentTypeKey: "Invoice", layoutKey: key, name: key, receivingEntityId: null, itemKinds: [], priority: 100, activeVersion: v.status === "Active" ? 1 : null, shadowVersion: null, status: "Active", createdFromDocumentId: null, lastMatchedAt: null, createdAt: "2026-11-03T10:00:00Z", versions: [version(v)] });
const settings = { tenant: {}, settings: { autoApproveConfidenceThreshold: 0.8, retentionYears: 10, shareExtractorProfiles: false, alwaysReviewSupplierIds: [], layoutReviewWarmup: 5, layoutAgreementPercent: 90, layoutsRequiredByDefault: false, trustedForwarderDomains: [], supplierAttributeSchema: { properties: { erpVendorId: { type: "string", title: "ERP vendor id" }, paymentDays: { type: "integer", title: "Payment days" } } }, features: { configurableTypes: true, customTypes: false, outputs: true, sourceRouting: true, structuredSources: false, supplierLayouts: true } } };

function renderWith(node: ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>{node}</TooltipProvider>
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
  for (const method of ["GET", "POST", "PUT", "PATCH", "DELETE"]) client[method] = respond(method);
});
beforeEach(() => {
  api.handlers.clear();
  api.calls.length = 0;
  auth.roles = ["TenantAdmin"];
  api.handlers.set("GET /api/v1/settings", () => ({ body: settings }));
});
afterEach(cleanup);

describe("supplier sheet: Layouts (docs/11 §19 item 3)", () => {
  it("lists the layouts by type with their state, rule and warm-up, and activates a Candidate and its waiting documents", async () => {
    api.handlers.set("GET /api/v1/suppliers/{supplierId}/layouts", () => ({ body: [layout("nordwind-invoice-erp", { status: "Active", reviewWarmup: 0, warmupSeen: 0 }), layout("nordwind-invoice-webshop", { id: "v-2", status: "Candidate", reviewWarmup: 5, warmupSeen: 0 })] }));
    api.handlers.set("GET /api/v1/supplier-layouts/{layoutId}/versions/{version}/matching-documents", () => ({ body: [{ documentId: "d-9", fileName: "WS-2026-000342.pdf", status: "NeedsReview", typeKey: "Invoice", supplierLayoutVersionId: null, pendingLayoutVersionId: "v-2", holds: true, failed: [] }] }));
    api.handlers.set("POST /api/v1/supplier-layouts/{layoutId}/versions/{version}:activate", () => ({ body: { layout: {}, replay: {}, waiting: ["d-9"], reprocessed: ["d-9"] } }));
    renderWith(<SupplierLayoutsTab supplier={supplier} />);
    expect(await screen.findByText("Invoice: nordwind-invoice-erp, nordwind-invoice-webshop")).toBeInTheDocument();
    const erp = (await screen.findAllByTestId("layout-card")).find((c) => c.getAttribute("data-layout-key") === "nordwind-invoice-erp")!;
    expect(within(erp).getByTestId("layout-warmup")).toHaveTextContent("no warm-up");
    const webshop = screen.getAllByTestId("layout-card").find((c) => c.getAttribute("data-layout-key") === "nordwind-invoice-webshop")!;
    expect(within(webshop).getByTestId("layout-version-status")).toHaveTextContent("Candidate");
    expect(within(webshop).getByTestId("layout-rule")).toHaveTextContent('all of: text "RECHNUNG / INVOICE"');
    const activate = await within(webshop).findByRole("button", { name: "Activate and reprocess 1 document" });
    fireEvent.click(activate);
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.change(within(dialog).getByTestId("activate-warmup"), { target: { value: "0" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Activate" }));
    await waitFor(() => expect(api.calls.find((c) => c.route.endsWith(":activate"))?.options.body).toEqual({ reviewWarmup: 0, reprocessWaiting: true }));
  });

  it("offers a reviewer without the MappingAuthor grant no activation", async () => {
    auth.roles = ["Reviewer"];
    api.handlers.set("GET /api/v1/suppliers/{supplierId}/layouts", () => ({ body: [layout("nordwind-invoice-webshop", { status: "Candidate" })] }));
    renderWith(<SupplierLayoutsTab supplier={supplier} />);
    expect(await screen.findByTestId("layout-card")).toBeInTheDocument();
    expect(screen.queryByTestId("layout-activate")).toBeNull();
    expect(screen.queryByTestId("layout-retire")).toBeNull();
  });
});

describe("supplier sheet: Settings, Routing and Attributes", () => {
  it("reads and writes the supplier's settings, layouts required as a boolean (docs/05)", async () => {
    expect(settingsDraft(supplier).layoutsRequired).toBe("yes");
    expect(settingsDraft({ ...supplier, settings: { layouts: { required: "false" } } } as unknown as Supplier).layoutsRequired).toBe("no");
    expect(settingsBody({ ...settingsDraft(supplier), currency: "eur", reviewMode: "always" }, null)).toEqual({ layouts: { required: true }, defaults: { currency: "EUR" }, review: { mode: "always" } });
    api.handlers.set("PATCH /api/v1/suppliers/{id}", () => ({ body: supplier }));
    renderWith(<SupplierSettingsTab supplier={supplier} canEdit />);
    fireEvent.click(await screen.findByTestId("layouts-required-no"));
    fireEvent.click(screen.getByTestId("supplier-settings-save"));
    await waitFor(() => expect(api.calls.find((c) => c.route === "PATCH /api/v1/suppliers/{id}")?.options.body).toMatchObject({ groupId: "00000000-0000-0000-0000-000000000000", settings: { layouts: { required: false } } }));
  });

  it("lists the source rules in words and saves a sender rule pinned to a type", async () => {
    api.handlers.set("GET /api/v1/suppliers/{supplierId}/source-rules", () => ({ body: [{ id: "r-1", supplierId: "sup-1", channelId: null, recipient: null, sender: "rechnungsversand@nordwind.example", senderDomain: null, folderPrefix: null, subjectRegex: null, fileNameRegex: null, mode: "Exclusive", requireSenderAuth: true, priority: 0, pinTypeKey: "Invoice", receivingEntityId: null, enabled: false, createdFromDocumentId: "d-1", createdBy: "tenant-a-reviewer", createdAt: "", updatedAt: "", describe: "from rechnungsversand@nordwind.example (DMARC required) → Nordwind Großhandel GmbH, Exclusive, pins Invoice" }] }));
    api.handlers.set("POST /api/v1/suppliers/{supplierId}/source-rules", (o) => ({ status: 201, body: { describe: `from ${(o.body as { sender: string }).sender}` } }));
    renderWith(<SupplierRoutingTab supplier={supplier} canEdit />);
    const rule = await screen.findByTestId("source-rule");
    expect(rule).toHaveTextContent("(DMARC required)");
    expect(within(rule).getByText("Proposed in review")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("source-rule-add"));
    fireEvent.change(screen.getByTestId("rule-sender"), { target: { value: " billing@nordwind.example " } });
    fireEvent.click(screen.getByTestId("source-rule-save"));
    await waitFor(() => expect(api.calls.find((c) => c.route === "POST /api/v1/suppliers/{supplierId}/source-rules")?.options.body).toMatchObject({ sender: "billing@nordwind.example", mode: "Exclusive", requireSenderAuth: true, priority: 0, enabled: true, channelId: null }));
  });

  it("generates the attribute form from the tenant's schema and keeps keys the schema does not name", async () => {
    api.handlers.set("PATCH /api/v1/suppliers/{id}", () => ({ body: supplier }));
    renderWith(<SupplierAttributesTab supplier={supplier} canEdit />);
    const vendor = await screen.findByLabelText("ERP vendor id");
    await waitFor(() => expect(vendor).toHaveValue("V-10042"));
    fireEvent.change(screen.getByLabelText("Payment days"), { target: { value: "14,5" } });
    fireEvent.click(screen.getByTestId("supplier-attributes-save"));
    expect(await screen.findByText("Not a whole number.")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Payment days"), { target: { value: "14" } });
    fireEvent.click(screen.getByTestId("supplier-attributes-save"));
    await waitFor(() => expect(api.calls.find((c) => c.route === "PATCH /api/v1/suppliers/{id}")?.options.body).toMatchObject({ attributes: { legacy: "x", erpVendorId: "V-10042", paymentDays: 14 } }));
  });
});

describe("routing test box and settings bodies", () => {
  it("sends the source as ingest sees it and shows the winner, the explanation and the candidates", async () => {
    expect(routingTestBody({ sender: " a@b.example ", recipients: "x@y.local, z@y.local", channelId: "__none__", folderPath: "", subject: "", fileName: "", dmarc: "pass", spf: "none" })).toEqual({ sender: "a@b.example", recipients: ["x@y.local", "z@y.local"], channelId: null, folderPath: null, subject: null, fileName: null, senderAuth: { dmarc: "pass", spf: null } });
    expect(routingTestBody({ sender: "a@b.example", recipients: "", channelId: "c-1", folderPath: "", subject: "", fileName: "", dmarc: "none", spf: "none" }).senderAuth).toBeNull();
    api.handlers.set("POST /api/v1/routing:test", () => ({ body: { supplierId: "sup-1", supplierName: "Nordwind Großhandel GmbH", method: "sourceRule", ruleId: "r-1", mode: "Exclusive", confidence: 0.99, pinTypeKey: "Invoice", pinTypeFrom: "rule", receivingEntityId: null, ambiguous: false, ambiguousSupplierIds: [], senderVerified: true, unverifiedRuleIds: [], flags: [], candidates: [{ ruleId: "r-1", supplierId: "sup-1", supplierName: "Nordwind", matched: true, unverified: false, specificity: 1, priority: 0, mode: "Exclusive", held: ["sender"], failed: [], describe: "from rechnungsversand@nordwind.example" }], explanation: "The sender rule of Nordwind holds." } }));
    renderWith(<RoutingTestBox channels={[]} />);
    fireEvent.change(screen.getByTestId("routing-test-sender"), { target: { value: "rechnungsversand@nordwind.example" } });
    fireEvent.click(screen.getByTestId("routing-test-submit"));
    expect(await screen.findByTestId("routing-test-winner")).toHaveTextContent("Routed to Nordwind Großhandel GmbH");
    expect(screen.getByTestId("routing-test-explanation")).toHaveTextContent("The sender rule of Nordwind holds.");
    expect(screen.getByTestId("routing-test-candidate")).toHaveTextContent("held: sender");
  });

  it("builds the typed channel configs, leaving out what is empty", () => {
    expect(channelConfig({ ...emptyChannel("Mailbox"), address: " invoices@tenant-a.local ", port: "993", unwrapForwards: false, trustedForwarderDomains: "bureau.example" })).toEqual({ address: "invoices@tenant-a.local", port: 993, unwrapForwards: false, trustedForwarderDomains: ["bureau.example"] });
    expect(channelConfig({ ...emptyChannel("WatchedFolder"), path: "scanner", stableSeconds: "5" })).toEqual({ path: "scanner", stableSeconds: 5 });
  });

  it("sends the layout settings with the general policy as stored, each only with its switch", () => {
    const data = settings as never;
    const draft = { layoutReviewWarmup: "0", layoutAgreementPercent: "95", layoutsRequiredByDefault: true, trustedForwarderDomains: "a.example\nb.example", supplierAttributeSchema: "" };
    expect(layoutSettingsBody(data, draft, { supplierLayouts: true, sourceRouting: false })).toEqual({ autoApproveConfidenceThreshold: 0.8, alwaysReviewSupplierIds: [], retentionYears: 10, shareExtractorProfiles: false, layoutReviewWarmup: 0, layoutAgreementPercent: 95 });
    expect(layoutSettingsBody(data, draft, { supplierLayouts: false, sourceRouting: true })).toMatchObject({ layoutsRequiredByDefault: true, trustedForwarderDomains: ["a.example", "b.example"], supplierAttributeSchema: {} });
    expect(() => layoutSettingsBody(data, { ...draft, supplierAttributeSchema: "{oops" }, { supplierLayouts: false, sourceRouting: true })).toThrow();
  });

  it("names an exported bundle after the tenant and the day", () => {
    expect(bundleFileName("tenant-a", new Date("2026-11-04T10:00:00Z"))).toBe("docproc-config-tenant-a-2026-11-04.json");
    expect(bundleFileName(null, new Date("2026-11-04T10:00:00Z"))).toBe("docproc-config-tenant-2026-11-04.json");
  });
});
