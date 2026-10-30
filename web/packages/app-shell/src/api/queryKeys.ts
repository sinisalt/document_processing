import type { paths } from "@docproc/api-client";

/** The query-string parameters of a list operation, straight from the generated contract. */
export type ListQuery<P extends keyof paths> = paths[P] extends { get: { parameters: { query?: infer Q } } } ? NonNullable<Q> : never;

export type DocumentListQuery = ListQuery<"/api/v1/documents">;
export type ReviewTaskListQuery = ListQuery<"/api/v1/review-tasks">;
export type IngestionListQuery = ListQuery<"/api/v1/ingestions">;
export type SupplierListQuery = ListQuery<"/api/v1/suppliers">;
export type TenantListQuery = ListQuery<"/api/v1/tenants">;
export type LlmUsageQuery = ListQuery<"/api/v1/llm-usage">;
export type PlatformLlmUsageQuery = ListQuery<"/api/v1/llm/usage">;

/**
 * A plain (single-page) query's key part: every parameter but `cursor`. `limit` stays, because `{status, limit: 25}`
 * and `{status, limit: 200}` are different answers.
 */
function plainKey<T extends object | undefined>(query: T): Omit<NonNullable<T>, "cursor"> | Record<string, never> {
  if (!query) return {};
  const { cursor: _cursor, ...rest } = query as Record<string, unknown>;
  return rest as Omit<NonNullable<T>, "cursor">;
}

/** An infinite list's key part: the filters only. `cursor` and `limit` are paging, which happens inside the entry. */
function filterKey<T extends object | undefined>(query: T): Omit<NonNullable<T>, "cursor" | "limit"> | Record<string, never> {
  if (!query) return {};
  const { cursor: _cursor, limit: _limit, ...rest } = query as Record<string, unknown>;
  return rest as Omit<NonNullable<T>, "cursor" | "limit">;
}

/**
 * One key factory per resource so invalidation stays coarse where it should be (`queryKeys.documents.all` after a
 * review action) and exact where it can be (`detail(id)`). Keys are plain data; nothing else goes in them.
 *
 * A list has two shapes: `list(query)` for a single `useQuery` page (a lookup, a count; `limit` is part of the key) and
 * `pages(query)` for `useCursorList` (an infinite query whose entry holds every page of one filter set). They never
 * share an entry, so a `{limit: 200}` lookup cannot be read as the first page of an infinite list or the other way.
 */
export const queryKeys = {
  me: () => ["me"] as const,
  version: () => ["version"] as const,
  devUsers: () => ["dev", "users"] as const,

  tenants: {
    all: ["tenants"] as const,
    list: (query?: TenantListQuery) => ["tenants", "list", plainKey(query)] as const,
    pages: (query?: TenantListQuery) => ["tenants", "pages", filterKey(query)] as const,
    detail: (id: string) => ["tenants", "detail", id] as const,
  },

  documents: {
    all: ["documents"] as const,
    list: (query?: DocumentListQuery) => ["documents", "list", plainKey(query)] as const,
    pages: (query?: DocumentListQuery) => ["documents", "pages", filterKey(query)] as const,
    detail: (id: string) => ["documents", "detail", id] as const,
    events: (id: string) => ["documents", "detail", id, "events"] as const,
    text: (id: string) => ["documents", "detail", id, "text"] as const,
  },

  /**
   * Rendered page PNGs, deliberately outside the `documents` prefix: invalidating `documents.all` after an action must
   * not refetch every page image (the images of a page never change once rendered).
   */
  documentPages: {
    all: ["document-pages"] as const,
    page: (id: string, page: number) => ["document-pages", id, page] as const,
  },

  reviewTasks: {
    all: ["review-tasks"] as const,
    list: (query?: ReviewTaskListQuery) => ["review-tasks", "list", plainKey(query)] as const,
    pages: (query?: ReviewTaskListQuery) => ["review-tasks", "pages", filterKey(query)] as const,
  },

  ingestions: {
    all: ["ingestions"] as const,
    list: (query?: IngestionListQuery) => ["ingestions", "list", plainKey(query)] as const,
    pages: (query?: IngestionListQuery) => ["ingestions", "pages", filterKey(query)] as const,
    detail: (id: string) => ["ingestions", "detail", id] as const,
  },

  suppliers: {
    all: ["suppliers"] as const,
    list: (query?: SupplierListQuery) => ["suppliers", "list", plainKey(query)] as const,
    pages: (query?: SupplierListQuery) => ["suppliers", "pages", filterKey(query)] as const,
    detail: (id: string) => ["suppliers", "detail", id] as const,
    activity: (id: string) => ["suppliers", "detail", id, "activity"] as const,
  },

  channels: {
    all: ["channels"] as const,
    list: () => ["channels", "list"] as const,
  },

  members: {
    all: ["members"] as const,
    list: () => ["members", "list"] as const,
  },

  apiKeys: {
    all: ["api-keys"] as const,
    list: () => ["api-keys", "list"] as const,
  },

  settings: {
    all: ["settings"] as const,
    get: () => ["settings", "get"] as const,
  },

  llm: {
    all: ["llm"] as const,
    policy: () => ["llm", "policy"] as const,
    providers: () => ["llm", "providers"] as const,
    usage: (query?: LlmUsageQuery) => ["llm", "usage", plainKey(query)] as const,
    platformProviders: () => ["llm", "platform", "providers"] as const,
    platformUsage: (query?: PlatformLlmUsageQuery) => ["llm", "platform", "usage", plainKey(query)] as const,
  },
} as const;

export type QueryKeys = typeof queryKeys;
