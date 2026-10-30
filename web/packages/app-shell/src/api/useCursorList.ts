import { useMemo } from "react";
import { useInfiniteQuery, type QueryKey } from "@tanstack/react-query";

/** Every list of the API: `{ items, nextCursor }`, `nextCursor` null on the last page (docs/05 "cursor pagination"). */
export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
}

export interface UseCursorListOptions<T> {
  queryKey: QueryKey;
  /** Fetches one page; receives the cursor of the previous page, null for the first. */
  queryFn: (cursor: string | null) => Promise<CursorPage<T>>;
  enabled?: boolean;
  staleTime?: number;
  refetchInterval?: number | false;
}

export interface UseCursorListResult<T> {
  /** All loaded pages flattened, in server order. */
  items: T[];
  pageCount: number;
  hasNextPage: boolean;
  fetchNextPage: () => Promise<unknown>;
  isFetchingNextPage: boolean;
  /** No data yet (first page in flight or disabled). */
  isLoading: boolean;
  /** Any fetch in flight, including background refetches. */
  isFetching: boolean;
  isError: boolean;
  error: unknown;
  refetch: () => Promise<unknown>;
}

/**
 * Cursor pagination over TanStack Query's infinite query, for every `{items, nextCursor}` list. Screens render `items`
 * and call `fetchNextPage` from a "Load more" button or an intersection observer.
 */
export function useCursorList<T>(options: UseCursorListOptions<T>): UseCursorListResult<T> {
  const query = useInfiniteQuery({
    queryKey: options.queryKey,
    queryFn: ({ pageParam }) => options.queryFn(pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    enabled: options.enabled,
    staleTime: options.staleTime,
    refetchInterval: options.refetchInterval,
  });

  const items = useMemo(() => query.data?.pages.flatMap((page) => page.items) ?? [], [query.data]);

  return {
    items,
    pageCount: query.data?.pages.length ?? 0,
    hasNextPage: query.hasNextPage,
    fetchNextPage: query.fetchNextPage,
    isFetchingNextPage: query.isFetchingNextPage,
    isLoading: query.isPending,
    isFetching: query.isFetching,
    isError: query.isError,
    error: query.error,
    refetch: query.refetch,
  };
}
