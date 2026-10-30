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

/** Strips `cursor`/`limit` so a list key covers every page of one filter set (infinite queries page inside the key). */
function filterKey<T extends object | undefined>(query: T): Omit<NonNullable<T>, "cursor" | "limit"> | Record<string, never> {
  if (!query) return {};
  const { cursor: _cursor, limit: _limit, ...rest } = query as Record<string, unknown>;
  return rest as Omit<NonNullable<T>, "cursor" | "limit">;
}

/**
 * One key factory per resource so invalidation stays coarse where it should be (`queryKeys.documents.all` after a
 * review action) and exact where it can be (`detail(id)`). Keys are plain data; nothing else goes in them.
 */
export const queryKeys = {
  me: () => ["me"] as const,
  version: () => ["version"] as const,
  devUsers: () => ["dev", "users"] as const,

  tenants: {
    all: ["tenants"] as const,
    list: (query?: TenantListQuery) => ["tenants", "list", filterKey(query)] as const,
    detail: (id: string) => ["tenants", "detail", id] as const,
  },

  documents: {
    all: ["documents"] as const,
    list: (query?: DocumentListQuery) => ["documents", "list", filterKey(query)] as const,
    detail: (id: string) => ["documents", "detail", id] as const,
    events: (id: string) => ["documents", "detail", id, "events"] as const,
    text: (id: string) => ["documents", "detail", id, "text"] as const,
  },

  reviewTasks: {
    all: ["review-tasks"] as const,
    list: (query?: ReviewTaskListQuery) => ["review-tasks", "list", filterKey(query)] as const,
  },

  ingestions: {
    all: ["ingestions"] as const,
    list: (query?: IngestionListQuery) => ["ingestions", "list", filterKey(query)] as const,
    detail: (id: string) => ["ingestions", "detail", id] as const,
  },

  suppliers: {
    all: ["suppliers"] as const,
    list: (query?: SupplierListQuery) => ["suppliers", "list", filterKey(query)] as const,
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
    usage: (query?: LlmUsageQuery) => ["llm", "usage", filterKey(query)] as const,
    platformProviders: () => ["llm", "platform", "providers"] as const,
    platformUsage: (query?: PlatformLlmUsageQuery) => ["llm", "platform", "usage", filterKey(query)] as const,
  },
} as const;

export type QueryKeys = typeof queryKeys;
