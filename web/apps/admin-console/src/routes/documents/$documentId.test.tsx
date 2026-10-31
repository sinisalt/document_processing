import { screen, within } from "@testing-library/react";
import { afterEach, beforeAll, beforeEach, vi } from "vitest";
import type { components } from "@docproc/api-client";
import { i18n } from "@/i18n";
import { answer, renderScreen, resetShell, routeComponent, shell } from "@/test/shell";
import { documentEvent } from "@/test/fixtures";
import { Route } from "./$documentId";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

type DocumentDetail = components["schemas"]["DocumentDetail"];
type DocumentFlag = components["schemas"]["DocumentFlagDto"];

const DetailPage = routeComponent(Route);
const ID = "6f1c1d2e-0000-4000-8000-000000000001";

function flag(overrides: Partial<DocumentFlag>): DocumentFlag {
  return {
    id: "f1",
    code: "LOW_CONFIDENCE",
    severity: "Warning",
    field: "dueDate",
    message: "'dueDate' has confidence 0.50, below the threshold 0.80.",
    params: { field: "dueDate", confidence: "0.50", threshold: "0.80" },
    createdBy: "Pipeline",
    createdAt: "2026-10-30T10:00:00Z",
    resolvedAt: null,
    resolvedBy: null,
    ...overrides,
  };
}

function detail(flags: DocumentFlag[]): DocumentDetail {
  return {
    id: ID,
    status: "NeedsReview",
    type: "Invoice",
    typeConfidence: 0.99,
    supplierId: null,
    supplierName: "Nordsee Fisch GmbH",
    documentNumber: "INV-1001",
    issueDate: "2026-10-01",
    currency: "EUR",
    totalGross: 303.45,
    extractionMethod: "Llm",
    pageCount: 0,
    blockingFlagCount: 0,
    warningFlagCount: flags.length,
    createdAt: "2026-10-30T10:00:00Z",
    updatedAt: "2026-10-30T10:00:00Z",
    approvedAt: null,
    approvedBy: null,
    failureReason: null,
    canonical: null,
    canonicalSchemaVersion: null,
    flags,
    costs: { llmInputTokens: 0, llmOutputTokens: 0, llmCacheReadTokens: 0, ocrPages: 0, estimatedUsd: null, llmProvider: null, llmModel: null, keyOwner: null },
    ingestion: null,
    pageImageUrls: [],
  } as DocumentDetail;
}

function table(caption: string): HTMLElement {
  return screen.getAllByRole("table").find((element) => element.querySelector("caption")?.textContent === caption)!;
}

beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});

beforeEach(() => {
  resetShell();
  shell.params = { documentId: ID };
});

afterEach(async () => {
  await i18n.changeLanguage("en");
});

describe("/documents/$documentId", () => {
  it("shows the flag messages in the UI language, and the API's text for a code it does not know", async () => {
    await i18n.changeLanguage("de");
    answer("GET /api/v1/documents/{id}", detail([flag({}), flag({ id: "f2", code: "SOMETHING_NEW", message: "A check this build does not know failed.", params: {} })]));
    answer("GET /api/v1/documents/{id}/events", [documentEvent({ id: 1 })]);
    renderScreen(<DetailPage />);

    const flags = await screen.findByText(/wurde mit 50\s%\sSicherheit gelesen/);
    expect(flags.textContent?.replace(/[  ]/g, " ")).toBe("dueDate wurde mit 50 % Sicherheit gelesen, unter dem Schwellenwert von 80 %.");
    expect(screen.getByText("A check this build does not know failed.")).toBeInTheDocument();
    expect(screen.queryByText(/has confidence/)).not.toBeInTheDocument();
  });

  it("says the timeline did not load instead of calling it empty", async () => {
    answer("GET /api/v1/documents/{id}", detail([]));
    answer("GET /api/v1/documents/{id}/events", { title: "Boom", status: 500, detail: "The event store is down." }, 500);
    renderScreen(<DetailPage />);

    expect(await screen.findByText("The event store is down.")).toBeInTheDocument();
    const timeline = table("Timeline");
    expect(within(timeline).getByText("This list could not be loaded")).toBeInTheDocument();
    expect(within(timeline).queryByText("No events")).not.toBeInTheDocument();
  });

  it("says the flags did not load when the document request failed", async () => {
    answer("GET /api/v1/documents/{id}", { title: "Boom", status: 500, detail: "The database is down." }, 500);
    answer("GET /api/v1/documents/{id}/events", []);
    renderScreen(<DetailPage />);

    expect((await screen.findAllByText("The database is down.")).length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByText("No flags")).not.toBeInTheDocument();
  });
});
