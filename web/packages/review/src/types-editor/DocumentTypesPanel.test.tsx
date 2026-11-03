import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@docproc/app-shell";
import { templates as compiled } from "../test/templates";

/** The fake API: one handler per "METHOD path"; calls are recorded with their options. */
type Answer = { status: number; data?: unknown; error?: unknown; etag?: string };
const handlers = new Map<string, (options: Record<string, unknown>) => Answer>();
const calls: Array<{ method: string; path: string; options: Record<string, unknown> }> = [];
function call(method: string) {
  return vi.fn(async (path: string, options: Record<string, unknown> = {}) => {
    calls.push({ method, path, options });
    const handler = handlers.get(`${method} ${path}`);
    const answer = handler ? handler(options) : { status: 404, error: { title: "Not Found", status: 404 } };
    const ok = answer.status < 400;
    const headers = new Headers(answer.etag ? { ETag: answer.etag } : {});
    return { data: ok ? answer.data : undefined, error: ok ? undefined : answer.error, response: new Response(null, { status: answer.status, headers }) };
  });
}
const client = { GET: call("GET"), POST: call("POST"), PUT: call("PUT"), PATCH: call("PATCH") };
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));
const toast = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock("sonner", async (importOriginal) => ({ ...(await importOriginal<typeof import("sonner")>()), toast }));

const { DocumentTypesPanel } = await import("./DocumentTypesPanel");

const version = (n: number, state: string, extra: Record<string, unknown> = {}) => ({
  version: n,
  state,
  revision: 3,
  contentHash: null,
  llmSchema: state === "Published" ? "invoice_extraction@1" : null,
  createdAt: "2026-11-01T10:00:00Z",
  createdBy: "seed",
  updatedAt: "2026-11-01T10:00:00Z",
  publishedAt: state === "Published" ? "2026-11-01T10:00:00Z" : null,
  publishedBy: state === "Published" ? "seed" : null,
  changeNote: null,
  ...extra,
});

const type = (key: string, origin: string, extra: Record<string, unknown> = {}) => ({
  key,
  name: { en: key === "CreditNote" ? "Credit note" : key, de: key === "CreditNote" ? "Gutschrift" : key },
  description: null,
  origin,
  status: "Active",
  baseTemplate: key === "CreditNote" ? "std.credit-note" : null,
  baseVersion: 1,
  publishedVersion: 1,
  draftVersion: null,
  createdAt: "2026-11-01T10:00:00Z",
  updatedAt: "2026-11-01T10:00:00Z",
  versions: [version(1, "Published")],
  indexedFields: [],
  ...extra,
});

const creditNoteDefinition = {
  key: "CreditNote",
  version: 2,
  name: { en: "Credit note", de: "Gutschrift" },
  base: { template: "std.credit-note", version: 1, mode: "copy" },
  ui: { sections: [{ id: "document", label: { en: "Document", de: "Beleg" } }] },
  fields: [
    { id: "fld_inv_no", key: "invoiceNumber", kind: "identifier", label: { en: "Credit note no.", de: "Gutschrift-Nr." }, indexed: true, required: true, section: "document" },
    { id: "fld_l_no", key: "lineNumber", kind: "integer", readOnly: true, provenance: false },
  ],
};

function settings(features: { configurableTypes: boolean; customTypes: boolean }) {
  handlers.set("GET /api/v1/settings", () => ({ status: 200, data: { tenant: {}, settings: { features: { outputs: false, sourceRouting: false, structuredSources: false, supplierLayouts: false, ...features } } } }));
}

function renderPanel(canEdit = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <DocumentTypesPanel canEdit={canEdit} />
    </QueryClientProvider>,
  );
}

beforeEach(async () => {
  await i18n.changeLanguage("en");
  handlers.clear();
  calls.length = 0;
  Object.values(toast).forEach((fn) => fn.mockClear());
  settings({ configurableTypes: true, customTypes: true });
  handlers.set("GET /api/v1/document-types", () => ({ status: 200, data: [type("Invoice", "Adopted", { baseTemplate: "std.invoice" }), type("CreditNote", "Adopted")] }));
  handlers.set("GET /api/v1/document-templates", () => ({
    status: 200,
    data: [
      { key: "std.credit-note", version: 1, name: { en: "Credit note" }, description: null, legacyKey: "CreditNote", defaultKey: "CreditNote", adoptedAs: ["CreditNote"] },
      { key: "std.price-list", version: 1, name: { en: "Price list", de: "Preisliste" }, description: "A supplier's price list.", legacyKey: null, defaultKey: "price-list", adoptedAs: [] },
    ],
  }));
});
afterEach(cleanup);

describe("document types catalogue", () => {
  it("lists the tenant's types and the templates, and adopts a template", async () => {
    handlers.set("POST /api/v1/document-types", () => ({ status: 201, data: type("price-list", "Adopted") }));
    renderPanel();
    expect(await screen.findByText("Credit note", { selector: "td" })).toBeInTheDocument();
    expect(screen.getByText("std.price-list v1")).toBeInTheDocument();
    // Adopted already: no second Adopt for the credit note.
    expect(screen.queryByRole("button", { name: "Adopt Credit note" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Adopt Price list" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST")).toBe(true));
    expect(calls.find((c) => c.method === "POST")!.options.body).toEqual({ from: { template: "std.price-list", mode: "adopt" }, key: null, definition: null });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("price-list adopted."));
  });

  it("copies a template under a key of the tenant's choice", async () => {
    handlers.set("POST /api/v1/document-types", () => ({ status: 201, data: type("acme-prices", "Copied", { draftVersion: 1, publishedVersion: null, versions: [version(1, "Draft")] }) }));
    handlers.set("GET /api/v1/document-types/{key}", () => ({ status: 200, data: type("acme-prices", "Copied", { draftVersion: 1, publishedVersion: null, versions: [version(1, "Draft")] }) }));
    handlers.set("GET /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"3"', data: { key: "acme-prices", version: version(1, "Draft"), definition: { ...creditNoteDefinition, key: "acme-prices", version: 1 }, formSpec: null, jsonSchema: null, llmSchema: null, descriptors: null } }));
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Copy Price list" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText("Key"), { target: { value: "acme prices" } });
    expect(within(dialog).getByRole("button", { name: "Create" })).toBeDisabled();
    fireEvent.change(within(dialog).getByLabelText("Key"), { target: { value: "acme-prices" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.options.body).toEqual({ from: { template: "std.price-list", mode: "copy" }, key: "acme-prices", definition: null }));
    // The copy opens in the editor.
    expect(await screen.findByTestId("type-editor")).toHaveAttribute("data-type", "acme-prices");
  });

  it("creates a type of the tenant's own only with customTypes", async () => {
    settings({ configurableTypes: true, customTypes: false });
    const { unmount } = renderPanel();
    await screen.findByText("Credit note", { selector: "td" });
    expect(screen.queryByTestId("new-type")).not.toBeInTheDocument();
    unmount();

    settings({ configurableTypes: true, customTypes: true });
    handlers.set("POST /api/v1/document-types", () => ({ status: 201, data: type("return-slip", "Custom", { draftVersion: 1 }) }));
    renderPanel();
    fireEvent.click(await screen.findByTestId("new-type"));
    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText("Key"), { target: { value: "return-slip" } });
    fireEvent.change(within(dialog).getByLabelText("Name (English)"), { target: { value: "Return slip" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST")).toBe(true));
    const body = calls.find((c) => c.method === "POST")!.options.body as { definition: { key: string; name: Record<string, string> }; from: unknown };
    expect(body.from).toBeNull();
    expect(body.definition).toMatchObject({ key: "return-slip", version: 1, name: { en: "Return slip" } });
  });

  it("offers no writes with configurableTypes off or without ConfigAdmin", async () => {
    settings({ configurableTypes: false, customTypes: false });
    const { unmount } = renderPanel();
    expect(await screen.findByText("Document types are switched off")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Adopt Price list" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Fork to edit" })).not.toBeInTheDocument();
    unmount();
    settings({ configurableTypes: true, customTypes: true });
    renderPanel(false);
    await screen.findByText("Credit note", { selector: "td" });
    expect(screen.queryByRole("button", { name: /^Copy / })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^Open / })).toHaveLength(2);
  });
});

describe("type editor", () => {
  function editorRoutes(state: "Draft" | "Published" = "Draft") {
    const detail = type("CreditNote", "Copied", { draftVersion: state === "Draft" ? 2 : null, publishedVersion: 1, versions: [version(1, "Published"), ...(state === "Draft" ? [version(2, "Draft")] : [])] });
    handlers.set("GET /api/v1/document-types", () => ({ status: 200, data: [detail] }));
    handlers.set("GET /api/v1/document-types/{key}", () => ({ status: 200, data: detail }));
    handlers.set("GET /api/v1/document-types/{key}/versions/{version}", (options) => {
      const n = ((options.params as { path: { version: number } }).path.version);
      return { status: 200, etag: '"3"', data: { key: "CreditNote", version: version(n, n === 2 ? "Draft" : "Published"), definition: { ...creditNoteDefinition, version: n }, formSpec: compiled.creditNote, jsonSchema: null, llmSchema: null, descriptors: null } };
    });
  }

  async function openCreditNote() {
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Open Credit note" }));
    return screen.findByTestId("field-form");
  }

  it("adds a custom field with labels per language, required and indexed, and saves the draft with If-Match", async () => {
    editorRoutes();
    handlers.set("PUT /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"4"', data: version(2, "Draft", { revision: 4 }) }));
    await openCreditNote();
    expect(screen.getByTestId("version-badge")).toHaveTextContent("v2 · Draft");
    fireEvent.click(screen.getByTestId("add-field"));
    const form = await screen.findByTestId("field-form");
    expect(form).toHaveAttribute("data-field-id", "fld_c1");
    fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "supplierCustomerRef" } });
    fireEvent.change(within(form).getByLabelText("Label (EN)"), { target: { value: "Our customer no. at supplier" } });
    fireEvent.change(within(form).getByLabelText("Label (DE)"), { target: { value: "Unsere Kundennummer beim Lieferanten" } });
    fireEvent.click(within(form).getByRole("switch", { name: "Indexed" }));
    fireEvent.click(within(form).getByRole("switch", { name: "Required" }));
    fireEvent.click(screen.getByTestId("save-draft"));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.method === "PUT")!;
    expect(put.options.headers).toEqual({ "If-Match": '"3"' });
    const saved = put.options.body as typeof creditNoteDefinition;
    expect(saved.base).toEqual(creditNoteDefinition.base);
    expect(saved.fields.at(-1)).toEqual({ id: "fld_c1", key: "supplierCustomerRef", kind: "string", label: { en: "Our customer no. at supplier", de: "Unsere Kundennummer beim Lieferanten" }, section: "document", indexed: true, required: true });
  });

  it("edits the printed labels and the pattern of a field as its extraction hints (docs/11 §5.3)", async () => {
    editorRoutes();
    handlers.set("PUT /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"4"', data: version(2, "Draft", { revision: 4 }) }));
    await openCreditNote();
    fireEvent.click(screen.getByTestId("add-field"));
    const form = await screen.findByTestId("field-form");
    fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "supplierCustomerRef" } });
    // Typed as a list: a trailing comma stays in the box until the next label follows.
    const de = within(form).getByLabelText("Printed as (DE)");
    fireEvent.change(de, { target: { value: "Kunden-Nr.," } });
    expect(de).toHaveValue("Kunden-Nr.,");
    fireEvent.change(de, { target: { value: "Kunden-Nr., Kd.-Nr.,  Kundennummer" } });
    fireEvent.change(within(form).getByLabelText("Printed as (EN)"), { target: { value: "Customer No." } });
    const pattern = within(form).getByLabelText("Pattern");
    fireEvent.change(pattern, { target: { value: "^([0-9]{3}" } });
    expect(within(form).getByRole("alert")).toHaveTextContent("Not a valid regular expression.");
    fireEvent.change(pattern, { target: { value: "^[0-9]{3}\\s?[0-9]{6}$" } });
    expect(within(form).queryByRole("alert")).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId("save-draft"));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const saved = calls.find((c) => c.method === "PUT")!.options.body as typeof creditNoteDefinition;
    expect(saved.fields.at(-1)).toMatchObject({
      key: "supplierCustomerRef",
      extraction: { labels: { de: ["Kunden-Nr.", "Kd.-Nr.", "Kundennummer"], en: ["Customer No."] }, pattern: "^[0-9]{3}\\s?[0-9]{6}$" },
    });

    // Cleared again, the hints leave no empty extraction object behind.
    fireEvent.change(within(form).getByLabelText("Printed as (DE)"), { target: { value: "" } });
    fireEvent.change(within(form).getByLabelText("Printed as (EN)"), { target: { value: " " } });
    fireEvent.change(within(form).getByLabelText("Pattern"), { target: { value: "" } });
    fireEvent.click(screen.getByTestId("save-draft"));
    await waitFor(() => expect(calls.filter((c) => c.method === "PUT").length).toBe(2));
    expect(JSON.parse(JSON.stringify(calls.filter((c) => c.method === "PUT")[1]!.options.body)).fields.at(-1)).not.toHaveProperty("extraction");
  });

  it("shows the meta-schema errors of a refused save", async () => {
    editorRoutes();
    handlers.set("PUT /api/v1/document-types/{key}/versions/{version}", () => ({ status: 400, error: { title: "Invalid", status: 400, errors: { "fields[2].kind": ["Not a kind."] } } }));
    await openCreditNote();
    fireEvent.click(screen.getByTestId("add-field"));
    fireEvent.click(screen.getByTestId("save-draft"));
    expect(await screen.findByTestId("save-errors")).toHaveTextContent("fields[2].kind: Not a kind.");
  });

  it("does not save a key the field grammar refuses", async () => {
    editorRoutes();
    await openCreditNote();
    fireEvent.click(screen.getByTestId("add-field"));
    const form = await screen.findByTestId("field-form");
    fireEvent.change(within(form).getByLabelText("Key"), { target: { value: "invoiceNumber" } });
    expect(within(form).getByRole("alert")).toHaveTextContent("Another field here has the same key.");
    expect(screen.getByTestId("save-draft")).toBeDisabled();
  });

  it("validates, and shows the dependents of a publish the checks refuse", async () => {
    editorRoutes();
    const report = {
      canPublish: false,
      problems: [{ where: "fields", message: "The indexed field invoiceNumber is removed." }],
      warnings: [{ where: "fields[0]", message: "Renamed." }],
      changes: [{ change: "Removed", fieldId: "fld_inv_no", oldPath: "invoiceNumber", newPath: null, oldKind: "identifier", newKind: null }],
      blockingDependents: [{ kind: "index", name: "DocumentFieldIndex", fieldId: "fld_inv_no", path: "invoiceNumber" }],
      llmSchema: "invoice_extraction@1+x.0418fb60",
      contentHash: null,
    };
    handlers.set("POST /api/v1/document-types/{key}/versions/{version}:validate", () => ({ status: 200, data: { ...report, canPublish: true, problems: [], blockingDependents: [] } }));
    handlers.set("POST /api/v1/document-types/{key}/versions/{version}:publish", () => ({ status: 422, error: report }));
    await openCreditNote();
    fireEvent.click(screen.getByRole("button", { name: "Validate" }));
    const passed = await screen.findByTestId("publish-report");
    expect(passed).toHaveAttribute("data-can-publish", "true");
    expect(passed).toHaveTextContent("invoice_extraction@1+x.0418fb60");

    fireEvent.change(screen.getByLabelText("Change note (optional)"), { target: { value: "customer number" } });
    fireEvent.click(screen.getByTestId("publish"));
    await waitFor(() => expect(screen.getByTestId("publish-report")).toHaveAttribute("data-can-publish", "false"));
    const blocked = screen.getByTestId("publish-report");
    expect(blocked).toHaveTextContent("index DocumentFieldIndex uses invoiceNumber");
    expect(blocked).toHaveTextContent("The indexed field invoiceNumber is removed.");
    expect(within(blocked).getByText("Removed")).toBeInTheDocument();
    const publish = calls.find((c) => c.path.endsWith(":publish"))!;
    expect(publish.options.body).toEqual({ changeNote: "customer number" });
    expect(publish.options.headers).toEqual({ "If-Match": '"3"' });
    expect(toast.error).toHaveBeenCalledWith("Not published: see the report.");
  });

  it("publishes a draft and lists the versions", async () => {
    editorRoutes();
    handlers.set("POST /api/v1/document-types/{key}/versions/{version}:publish", () => ({ status: 200, data: type("CreditNote", "Copied") }));
    await openCreditNote();
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Versions" }));
    fireEvent.click(await screen.findByTestId("publish"));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("v2 published; new documents of this type use it."));
    expect(document.querySelectorAll("[data-version]")).toHaveLength(2);
  });

  it("previews the generated form and shows the definition as JSON", async () => {
    editorRoutes();
    await openCreditNote();
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Preview" }));
    const preview = await screen.findByTestId("type-preview");
    expect(within(preview).getByLabelText("Credit note no.")).toBeDisabled();
    fireEvent.mouseDown(screen.getByRole("tab", { name: "JSON" }));
    const json = await screen.findByLabelText("Definition (JSON)");
    expect((json as HTMLTextAreaElement).value).toContain('"invoiceNumber"');
    fireEvent.change(json, { target: { value: "{ not json" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply JSON" }));
    expect(await screen.findByRole("alert")).toBeInTheDocument();
  });

  it("starts a new version of a published copy, and keeps bare members fixed", async () => {
    editorRoutes("Published");
    handlers.set("POST /api/v1/document-types/{key}/versions", () => ({ status: 201, data: version(2, "Draft") }));
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Open Credit note" }));
    await screen.findByTestId("field-form");
    expect(screen.queryByTestId("save-draft")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "New version" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/api/v1/document-types/{key}/versions")).toBe(true));

    cleanup();
    editorRoutes();
    await openCreditNote();
    fireEvent.click(document.querySelector<HTMLElement>('[data-field-key="lineNumber"]')!);
    expect(await screen.findByText(/cannot be changed/)).toBeInTheDocument();
    expect(within(screen.getByTestId("field-form")).getByLabelText("Key")).toBeDisabled();
  });

  it("forks an adopted type in place", async () => {
    handlers.set("GET /api/v1/document-types/{key}", () => ({ status: 200, data: type("CreditNote", "Adopted") }));
    handlers.set("GET /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, data: { key: "CreditNote", version: version(1, "Published"), definition: { ...creditNoteDefinition, version: 1 }, formSpec: null, jsonSchema: null, llmSchema: null, descriptors: null } }));
    handlers.set("POST /api/v1/document-types", () => ({ status: 200, data: type("CreditNote", "Copied", { draftVersion: 2 }) }));
    renderPanel();
    fireEvent.click(await screen.findByRole("button", { name: "Open Credit note" }));
    expect(await screen.findByText("Adopted from a template")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Fork to edit" }));
    await waitFor(() => expect(calls.find((c) => c.method === "POST")?.options.body).toEqual({ from: { template: "std.credit-note", mode: "copy" }, key: "CreditNote", definition: null }));
  });
});
