import { fireEvent, screen, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, vi } from "vitest";
import { ApiError, queryKeys } from "@docproc/app-shell";
import { renderScreen, resetShell, shell } from "@/test/shell";
import { INITIAL_THUMBNAILS, PageThumbnails, retryPageImage } from "./PageThumbnails";

vi.mock("@docproc/app-shell", async (original) => (await import("@/test/shell")).mockShell(await original()));
vi.mock("@tanstack/react-router", async (original) => (await import("@/test/shell")).mockRouter(await original()));

const ID = "d0000000-0000-0000-0000-000000000001";

describe("PageThumbnails", () => {
  beforeAll(() => {
    URL.createObjectURL ??= () => "blob:test";
    URL.revokeObjectURL ??= () => {};
  });
  beforeEach(() => {
    resetShell();
    shell.handlers.set("GET /api/v1/documents/{id}/pages/{n}.png", () => ({ body: new Blob(["png"], { type: "image/png" }) }));
  });

  it("requests at most the first twelve pages of a 200-page document until 'Show all'", async () => {
    renderScreen(<PageThumbnails documentId={ID} pageCount={200} />);

    await waitFor(() => expect(shell.calls).toHaveLength(INITIAL_THUMBNAILS));
    expect(screen.getAllByRole("listitem")).toHaveLength(INITIAL_THUMBNAILS);
    expect(screen.getByText("Showing 12 of 200 pages.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Show all 200 pages" }));
    expect(screen.getAllByRole("listitem")).toHaveLength(200);
  });

  it("keys page images outside the documents prefix, so a document action does not refetch them", async () => {
    renderScreen(<PageThumbnails documentId={ID} pageCount={2} />);
    await waitFor(() => expect(shell.queryClient.getQueryData(queryKeys.documentPages.page(ID, 2))).toBeInstanceOf(Blob));

    await shell.queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
    expect(shell.calls).toHaveLength(2);
    expect(shell.queryClient.getQueryState(queryKeys.documentPages.page(ID, 1))?.isInvalidated).toBe(false);
  });

  it("does not retry a page that is not rendered (404)", () => {
    expect(retryPageImage(0, new ApiError(404, undefined, "/x"))).toBe(false);
    expect(retryPageImage(0, new ApiError(503, undefined, "/x"))).toBe(true);
    expect(retryPageImage(2, new ApiError(503, undefined, "/x"))).toBe(false);
  });

  it("says in words that a missing page is unavailable, after one request", async () => {
    shell.handlers.set("GET /api/v1/documents/{id}/pages/{n}.png", () => ({ status: 404, body: { title: "Not found", status: 404 } }));
    renderScreen(<PageThumbnails documentId={ID} pageCount={1} />);
    expect(await screen.findByText("Page image not available")).toBeVisible();
    expect(shell.calls).toHaveLength(1);
  });
});
