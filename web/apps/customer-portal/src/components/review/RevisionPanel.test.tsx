import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { i18n } from "@/i18n";
import type { DocumentDetail, DocumentFlag } from "@/lib/queries";

/** The REVISION_OF and EXTERNAL_REJECTED panels of the review screen (docs/11 §11, §13.8). */
const client = {
  POST: vi.fn(async (path: string, init: { body: unknown }) => ({ data: { ...documentOf([]), lastBody: init.body, lastPath: path }, error: undefined, response: new Response(null, { status: 200 }) })),
};
vi.mock("@docproc/app-shell", async (importOriginal) => ({ ...(await importOriginal<typeof import("@docproc/app-shell")>()), useApi: () => ({ client }) }));

const { IntegrationIssuePanel, RevisionPanel, openFlag } = await import("./RevisionPanel");

const EARLIER = "11111111-0000-4000-8000-000000000001";

const flag = (code: string, params: Record<string, string>, resolvedAt: string | null = null): DocumentFlag =>
  ({ id: `f-${code}`, code, severity: "Warning", field: null, message: code, params, createdBy: "Pipeline", createdAt: "2026-11-05T06:41:00Z", resolvedAt, resolvedBy: null }) as DocumentFlag;

function documentOf(flags: DocumentFlag[]): DocumentDetail {
  return { id: "22222222-0000-4000-8000-000000000002", status: "NeedsReview", flags } as unknown as DocumentDetail;
}

function renderWith(node: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{node}</QueryClientProvider>);
}

beforeAll(async () => {
  await i18n.changeLanguage("en");
});

afterEach(() => {
  cleanup();
  client.POST.mockClear();
});

describe("RevisionPanel", () => {
  it("offers the three resolutions and posts the one chosen", async () => {
    renderWith(<RevisionPanel document={documentOf([flag("REVISION_OF", { documentId: EARLIER, status: "Approved" })])} canReview />);

    expect(screen.getByText("Revision of an earlier document")).toBeTruthy();
    expect(screen.getByText("Open the earlier document").getAttribute("href")).toBe(`/documents/${EARLIER}`);
    fireEvent.click(screen.getByTestId("revision-supersedes"));

    await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
    expect(client.POST.mock.calls[0]![0]).toBe("/api/v1/documents/{id}:resolve-revision");
    expect(client.POST.mock.calls[0]![1]).toMatchObject({ body: { resolution: "supersedes" } });
  });

  it("shows nothing for a resolved flag and no buttons to a viewer", () => {
    const { container } = renderWith(<RevisionPanel document={documentOf([flag("REVISION_OF", { documentId: EARLIER }, "2026-11-05T07:00:00Z")])} canReview />);
    expect(container.textContent).toBe("");
    cleanup();
    renderWith(<RevisionPanel document={documentOf([flag("REVISION_OF", { documentId: EARLIER })])} canReview={false} />);
    expect(screen.queryByTestId("revision-duplicate")).toBeNull();
    expect(openFlag(documentOf([flag("REVISION_OF", {})]), "REVISION_OF")).not.toBeNull();
  });
});

describe("IntegrationIssuePanel", () => {
  it("names the receiver and its answer and resolves the issue with a note", async () => {
    renderWith(<IntegrationIssuePanel document={documentOf([flag("EXTERNAL_REJECTED", { destination: "erp", message: "Vendor blocked" })])} canReview />);

    expect(screen.getByText("erp rejected the document")).toBeTruthy();
    expect(screen.getByText("Its answer: Vendor blocked")).toBeTruthy();
    const resolve = screen.getByTestId("integration-issue-resolve") as HTMLButtonElement;
    expect(resolve.disabled).toBe(true);
    fireEvent.change(screen.getByTestId("integration-issue-note"), { target: { value: "Booked by hand." } });
    fireEvent.click(resolve);

    await waitFor(() => expect(client.POST).toHaveBeenCalledTimes(1));
    expect(client.POST.mock.calls[0]![0]).toBe("/api/v1/documents/{id}:resolve-integration-issue");
    expect(client.POST.mock.calls[0]![1]).toMatchObject({ body: { note: "Booked by hand." } });
  });

  it("speaks German too", async () => {
    await i18n.changeLanguage("de");
    renderWith(<IntegrationIssuePanel document={documentOf([flag("EXTERNAL_REJECTED", { destination: "erp" })])} canReview={false} />);
    expect(screen.getByText("erp hat das Dokument abgelehnt")).toBeTruthy();
    expect(screen.getByText("Das Zielsystem hat keinen Grund genannt.")).toBeTruthy();
    await i18n.changeLanguage("en");
  });
});
