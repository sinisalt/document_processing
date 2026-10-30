import { describe, expect, it, vi } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { useCursorList, type CursorPage } from "./useCursorList";

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const pages: Record<string, CursorPage<{ id: number }>> = {
  first: { items: [{ id: 1 }, { id: 2 }], nextCursor: "c2" },
  c2: { items: [{ id: 3 }], nextCursor: "c3" },
  c3: { items: [{ id: 4 }], nextCursor: null },
};

describe("useCursorList", () => {
  it("loads the first page with a null cursor and follows nextCursor", async () => {
    const queryFn = vi.fn(async (cursor: string | null) => pages[cursor ?? "first"]!);
    const { result } = renderHook(() => useCursorList({ queryKey: ["test", "list"], queryFn }), { wrapper: wrapper() });

    expect(result.current.isLoading).toBe(true);
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(queryFn).toHaveBeenCalledWith(null);
    expect(result.current.items.map((i) => i.id)).toEqual([1, 2]);
    expect(result.current.hasNextPage).toBe(true);
    expect(result.current.pageCount).toBe(1);

    await act(() => result.current.fetchNextPage());
    await waitFor(() => expect(result.current.items.map((i) => i.id)).toEqual([1, 2, 3]));
    expect(queryFn).toHaveBeenLastCalledWith("c2");

    await act(() => result.current.fetchNextPage());
    await waitFor(() => expect(result.current.items.map((i) => i.id)).toEqual([1, 2, 3, 4]));
    expect(queryFn).toHaveBeenLastCalledWith("c3");
    expect(result.current.hasNextPage).toBe(false);
    expect(result.current.pageCount).toBe(3);
  });

  it("reports an empty last page as the end", async () => {
    const queryFn = vi.fn(async () => ({ items: [], nextCursor: null }));
    const { result } = renderHook(() => useCursorList({ queryKey: ["test", "empty"], queryFn }), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.items).toEqual([]);
    expect(result.current.hasNextPage).toBe(false);
  });

  it("surfaces errors", async () => {
    const queryFn = vi.fn(async () => {
      throw new Error("boom");
    });
    const { result } = renderHook(() => useCursorList({ queryKey: ["test", "error"], queryFn }), { wrapper: wrapper() });
    await waitFor(() => expect(result.current.isError).toBe(true));
    expect((result.current.error as Error).message).toBe("boom");
    expect(result.current.items).toEqual([]);
  });

  it("does nothing while disabled", async () => {
    const queryFn = vi.fn(async () => pages.first!);
    const { result } = renderHook(() => useCursorList({ queryKey: ["test", "disabled"], queryFn, enabled: false }), { wrapper: wrapper() });
    await new Promise((r) => setTimeout(r, 20));
    expect(queryFn).not.toHaveBeenCalled();
    expect(result.current.isLoading).toBe(true);
    expect(result.current.items).toEqual([]);
  });
});
