import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@docproc/app-shell";

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

const { DocumentTypeEditor } = await import("./DocumentTypeEditor");

const version = (n: number, state: string) => ({ version: n, state, revision: 3, contentHash: null, llmSchema: null, createdAt: "2026-11-01T10:00:00Z", createdBy: "seed", updatedAt: "2026-11-01T10:00:00Z", publishedAt: null, publishedBy: null, changeNote: null });

const definition = {
  key: "acme-prices",
  version: 2,
  name: { en: "Acme prices" },
  ui: { sections: [{ id: "document", label: { en: "Document" } }] },
  fields: [{ id: "fld_no", key: "documentNumber", kind: "identifier", label: { en: "No." }, section: "document" }],
  rules: [{ id: "r_delta", kind: "deltaToPrevious", params: { path: "lines[*].unitPrice", joinOn: "supplierSku", maxChangePercent: 25 } }],
  processing: { review: { autoApprove: true, requirePassedRules: ["r_delta"] } },
};

const ruleKinds = [
  {
    kind: "deltaToPrevious",
    phase: "P5",
    description: "The values at path change by at most maxChangePercent.",
    paramsSchema: { type: "object", required: ["path", "joinOn"], properties: { path: { type: "string" }, joinOn: { type: "string" }, maxChangePercent: { type: "number", minimum: 0 } } },
    pathParams: ["path"],
    expressionParams: [],
  },
  {
    kind: "expression",
    phase: "P5",
    description: "The predicate holds.",
    paramsSchema: { type: "object", required: ["predicate"], properties: { predicate: { type: "string" }, forEach: { type: "string" } } },
    pathParams: ["forEach"],
    expressionParams: ["predicate"],
  },
];

function settings(supplierLayouts: boolean) {
  handlers.set("GET /api/v1/settings", () => ({ status: 200, data: { tenant: {}, settings: { features: { configurableTypes: true, customTypes: true, supplierLayouts, configurableSteps: true } } } }));
}

function renderEditor(canEdit = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <DocumentTypeEditor typeKey="acme-prices" canEdit={canEdit} customTypes onBack={() => undefined} />
    </QueryClientProvider>,
  );
}

async function choose(trigger: HTMLElement, option: string) {
  fireEvent.keyDown(trigger, { key: "Enter" });
  const listbox = await screen.findByRole("listbox");
  fireEvent.click(within(listbox).getByRole("option", { name: option }));
}

const tenantRule = (state: string, test: unknown = null) => ({
  id: "rule-1",
  documentTypeKey: "acme-prices",
  name: "Preisliste im Titel",
  priority: 100,
  activeVersion: state === "Active" ? 1 : null,
  status: "Active",
  createdFromDocumentId: null,
  matched: 12,
  overridden: 3,
  createdBy: "admin",
  createdAt: "2026-11-01T10:00:00Z",
  updatedAt: "2026-11-01T10:00:00Z",
  versions: [{ id: "ver-1", version: 1, state, rule: { all: [{ text: "Preisliste" }] }, describe: "All of: 'Preisliste'", ruleHash: "h", excludedSupplierIds: [], test, revision: 2, approvedBy: null, approvedAt: null, createdBy: "admin", createdAt: "2026-11-01T10:00:00Z" }],
});

beforeEach(async () => {
  await i18n.changeLanguage("en");
  handlers.clear();
  calls.length = 0;
  Object.values(toast).forEach((fn) => fn.mockClear());
  settings(true);
  handlers.set("GET /api/v1/document-types/{key}", () => ({ status: 200, data: { key: "acme-prices", name: { en: "Acme prices" }, description: null, origin: "Custom", status: "Active", baseTemplate: null, baseVersion: null, publishedVersion: 1, draftVersion: 2, createdAt: "x", updatedAt: "x", versions: [version(1, "Published"), version(2, "Draft")], indexedFields: [] } }));
  handlers.set("GET /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"3"', data: { key: "acme-prices", version: version(2, "Draft"), definition, formSpec: null, jsonSchema: null, llmSchema: null, descriptors: null } }));
  handlers.set("GET /api/v1/rule-kinds", () => ({ status: 200, data: ruleKinds }));
  handlers.set("GET /api/v1/documents", () => ({ status: 200, data: { items: [{ id: "doc-1", documentNumber: "PL-2027-02", supplierName: "Nordwind", createdAt: "2026-11-02T08:00:00Z" }], nextCursor: null } }));
  handlers.set("GET /api/v1/suppliers", () => ({ status: 200, data: { items: [{ id: "sup-1", name: "Nordwind" }], nextCursor: null } }));
});
afterEach(cleanup);

describe("type editor, Rules tab", () => {
  it("edits a P5 rule's parameters from its kind's schema and saves them with the definition", async () => {
    handlers.set("PUT /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"4"', data: version(2, "Draft") }));
    renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-rules"));

    const editor = await screen.findByTestId("rule-editor");
    expect(await within(editor).findByText("The values at path change by at most maxChangePercent.")).toBeInTheDocument();
    const percent = await within(editor).findByTestId("rule-param-maxChangePercent");
    expect(percent).toHaveValue("25");
    fireEvent.change(percent, { target: { value: "-1" } });
    expect(await within(editor).findByRole("alert")).toHaveTextContent("Between 0 and");
    fireEvent.change(percent, { target: { value: "30" } });

    // A rule of a new kind, with its expression parameter as a monospace area.
    await choose(screen.getByTestId("rule-new-kind"), "expression");
    fireEvent.click(screen.getByTestId("rule-add"));
    const predicate = await screen.findByTestId("rule-param-predicate");
    expect(predicate.tagName).toBe("TEXTAREA");
    fireEvent.change(predicate, { target: { value: "unitPrice > 0" } });

    fireEvent.click(screen.getByTestId("save-draft"));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const saved = calls.find((c) => c.method === "PUT")!.options.body as typeof definition;
    expect(saved.rules).toEqual([
      { id: "r_delta", kind: "deltaToPrevious", params: { path: "lines[*].unitPrice", joinOn: "supplierSku", maxChangePercent: 30 } },
      { id: "expression", kind: "expression", params: { predicate: "unitPrice > 0" } },
    ]);
    expect(saved.processing.review.requirePassedRules).toEqual(["r_delta"]);
  });

  it("declares a definition flag a rule raises and sets the gate's rows requirement", async () => {
    handlers.set("PUT /api/v1/document-types/{key}/versions/{version}", () => ({ status: 200, etag: '"4"', data: version(2, "Draft") }));
    renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-rules"));
    fireEvent.click(await screen.findByTestId("flag-add"));
    await choose(screen.getByTestId("rule-flag"), "X_FLAG");
    fireEvent.click(within(screen.getByTestId("review-gate-card")).getByRole("checkbox", { name: "Layout" }));
    fireEvent.click(screen.getByTestId("save-draft"));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const saved = calls.find((c) => c.method === "PUT")!.options.body as Record<string, unknown> & { rules: Array<Record<string, unknown>>; processing: { review: Record<string, unknown> } };
    expect(saved.flags).toEqual([{ code: "X_FLAG", severity: "warning", message: { en: "", de: "" } }]);
    expect(saved.rules[0]!.flag).toBe("X_FLAG");
    expect(saved.processing.review.lines).toEqual({ requireSource: ["Rule"] });
  });

  it("tests the saved version's rules against a document and shows each rule's outcome", async () => {
    handlers.set("POST /api/v1/document-types/{key}/versions/{version}:test-rules", () => ({
      status: 200,
      data: {
        documentId: "doc-1",
        typeKey: "acme-prices",
        version: 2,
        rules: [{ id: "r_delta", kind: "deltaToPrevious", outcome: "warned", flags: [{ code: "RULE_FAILED", severity: "Warning", message: "Article 104539 up 40 %.", field: "lines[3].unitPrice", params: {} }] }],
        flags: [{ code: "RULE_FAILED", severity: "Warning", message: "Article 104539 up 40 %.", field: "lines[3].unitPrice", params: {} }],
        blocking: false,
      },
    }));
    renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-rules"));
    await choose(await screen.findByTestId("rule-test-document"), "PL-2027-02 · Nordwind · " + new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short" }).format(new Date("2026-11-02T08:00:00Z")));
    fireEvent.click(screen.getByTestId("rule-test-run"));
    const result = await screen.findByTestId("rule-test-result");
    expect(calls.find((c) => c.path.endsWith(":test-rules"))!.options).toMatchObject({ params: { path: { key: "acme-prices", version: 2 } }, body: { documentId: "doc-1" } });
    expect(within(result).getByText("Warning")).toBeInTheDocument();
    expect(within(result).getByText(/Article 104539 up 40 %/)).toBeInTheDocument();
    expect(result).toHaveAttribute("data-blocking", "false");
  });
});

describe("type editor, Processing and Identification tabs", () => {
  it("shows the steps each phase runs for the type", async () => {
    handlers.set("GET /api/v1/document-types/{key}/resolved-pipeline", () => ({
      status: 200,
      data: {
        hash: "abc",
        configurable: true,
        overridden: false,
        steps: [
          { id: "classify.identification", kind: "classify.identification", phase: "Classify", params: {}, enabled: true, source: "default", fixed: true, runs: true },
          { id: "llm", kind: "llm", phase: "Extract", params: { lines: "perPage" }, enabled: true, source: "type", fixed: false, runs: true },
          { id: "code-list", kind: "code-list", phase: "Extract", params: {}, enabled: true, source: "type", fixed: false, runs: false },
        ],
        skipped: [],
        refused: [],
      },
    }));
    renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-processing"));
    const steps = await screen.findByTestId("pipeline-steps");
    expect(within(steps).getByText("lines: perPage")).toBeInTheDocument();
    expect(within(steps).getByText("Not run by this build")).toBeInTheDocument();
    expect(within(steps).getByLabelText("Fixed step")).toBeInTheDocument();
  });

  it("lists the tenant rules with their test and activates a draft, confirming when other types matched", async () => {
    const test = { ruleHash: "h", typeKey: "acme-prices", tested: 500, matched: 39, sameType: 37, otherTypes: 2, byType: {}, otherTypeMatches: [{ documentId: "doc-9", typeKey: "Invoice", supplierId: null, fileName: "rechnung.pdf" }], matches: [], summary: "would have matched 37 acme-prices documents, 2 other documents", testedAt: "x" };
    handlers.set("GET /api/v1/document-types/{key}/identification-rules", () => ({ status: 200, data: [tenantRule("Draft", test)] }));
    let activations = 0;
    handlers.set("POST /api/v1/identification-rules/{id}/versions/{version}:activate", (options) => {
      activations++;
      const confirm = (options.body as { confirmOtherTypes: boolean }).confirmOtherTypes;
      return confirm ? { status: 200, data: tenantRule("Active", test) } : { status: 409, error: { title: "Matches other types", status: 409, detail: "x", test } };
    });
    renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-identification"));
    const card = await screen.findByTestId("ident-rule-Preisliste im Titel");
    expect(within(card).getByTestId("ident-rule-test-summary")).toHaveTextContent("Would have matched 37 documents of this type, 2 other documents (of 500 tested).");
    expect(within(card).getByText("Overridden 25 %")).toBeInTheDocument();
    expect(within(card).getByText("rechnung.pdf")).toBeInTheDocument();
    fireEvent.click(within(card).getByTestId("ident-rule-activate"));
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent("would have matched 37 acme-prices documents, 2 other documents");
    fireEvent.click(within(dialog).getByRole("button", { name: "Activate anyway" }));
    await waitFor(() => expect(activations).toBe(2));
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith("Rule Preisliste im Titel is active."));
  });

  it("creates a rule from its JSON and hides the tab without supplierLayouts", async () => {
    handlers.set("GET /api/v1/document-types/{key}/identification-rules", () => ({ status: 200, data: [] }));
    handlers.set("POST /api/v1/document-types/{key}/identification-rules", () => ({ status: 201, data: tenantRule("Draft") }));
    const { unmount } = renderEditor();
    fireEvent.mouseDown(await screen.findByTestId("type-tab-identification"));
    fireEvent.click(await screen.findByTestId("ident-rule-new"));
    fireEvent.change(screen.getByTestId("ident-rule-name"), { target: { value: "Preisliste im Titel" } });
    fireEvent.change(screen.getByTestId("ident-rule-json"), { target: { value: "{" } });
    expect(screen.getByTestId("ident-rule-create")).toBeDisabled();
    fireEvent.change(screen.getByTestId("ident-rule-json"), { target: { value: '{"all":[{"text":"Preisliste","region":"title"}]}' } });
    fireEvent.click(await screen.findByRole("checkbox", { name: "Nordwind" }));
    fireEvent.click(screen.getByTestId("ident-rule-create"));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path.endsWith("identification-rules"))).toBe(true));
    expect(calls.find((c) => c.method === "POST")!.options.body).toEqual({ name: "Preisliste im Titel", priority: 100, rule: { all: [{ text: "Preisliste", region: "title" }] }, excludedSupplierIds: ["sup-1"] });
    unmount();

    settings(false);
    renderEditor();
    await screen.findByTestId("type-tab-rules");
    expect(screen.queryByTestId("type-tab-identification")).not.toBeInTheDocument();
  });
});
