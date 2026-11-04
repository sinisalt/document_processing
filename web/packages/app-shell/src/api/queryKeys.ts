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
export type DeliveryListQuery = ListQuery<"/api/v1/deliveries">;

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
    /** The stored table reports (the page strip, docs/11 §12.4) and the parts of the attachment (join, §12.7). */
    tableReports: (id: string) => ["documents", "detail", id, "table-reports"] as const,
    parts: (id: string) => ["documents", "detail", id, "parts"] as const,
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

  /** The type catalogue (docs/11 §19): platform templates, the tenant's types, their versions and one version's JSON. */
  documentTypes: {
    all: ["document-types"] as const,
    templates: () => ["document-types", "templates"] as const,
    list: () => ["document-types", "list"] as const,
    detail: (key: string) => ["document-types", "detail", key] as const,
    version: (key: string, version: number) => ["document-types", "detail", key, "version", version] as const,
  },

  /**
   * Outputs (docs/11 §13, §19 "Settings > Integrations"): the integration templates, the tenant's output mappings and
   * their versions (a version under its mapping), destinations, bindings and lookup tables.
   */
  outputs: {
    all: ["outputs"] as const,
    templates: () => ["outputs", "templates"] as const,
    mappings: () => ["outputs", "mappings"] as const,
    mapping: (key: string) => ["outputs", "mappings", key] as const,
    version: (key: string, version: number) => ["outputs", "mappings", key, "version", version] as const,
    destinations: () => ["outputs", "destinations"] as const,
    bindings: () => ["outputs", "bindings"] as const,
    lookupTables: () => ["outputs", "lookup-tables"] as const,
    lookupTable: (key: string) => ["outputs", "lookup-tables", key] as const,
  },

  /** Deliveries of the outputs (docs/11 §13.6): the log, one delivery with its attempts, and its payload snapshot. */
  deliveries: {
    all: ["deliveries"] as const,
    list: (query?: DeliveryListQuery) => ["deliveries", "list", plainKey(query)] as const,
    detail: (id: string) => ["deliveries", "detail", id] as const,
    payload: (id: string) => ["deliveries", "detail", id, "payload"] as const,
  },

  /**
   * Source routing and supplier configuration (docs/11 §6.4, §6.5, §7): a supplier's source rules and entity
   * attributes (under the supplier, so editing a supplier refreshes them), supplier groups and receiving entities.
   */
  routing: {
    all: ["routing"] as const,
    sourceRules: (supplierId: string) => ["suppliers", "detail", supplierId, "source-rules"] as const,
    entityAttributes: (supplierId: string) => ["suppliers", "detail", supplierId, "entity-attributes"] as const,
    groups: () => ["routing", "supplier-groups"] as const,
    entities: () => ["routing", "receiving-entities"] as const,
  },

  /**
   * Supplier layouts (docs/11 §8, §12.9, §12.10): a supplier's layouts, one version in full, a document's layout
   * suggestions and the documents matching a version, the MappingAuthors' notifications.
   */
  supplierLayouts: {
    all: ["supplier-layouts"] as const,
    ofSupplier: (supplierId: string) => ["supplier-layouts", "supplier", supplierId] as const,
    ofGroup: (groupId: string) => ["supplier-layouts", "group", groupId] as const,
    version: (layoutId: string, version: number) => ["supplier-layouts", "layout", layoutId, "version", version] as const,
    matching: (layoutId: string, version: number, state: string) => ["supplier-layouts", "layout", layoutId, "version", version, "matching", state] as const,
    suggestions: (documentId: string) => ["supplier-layouts", "suggestions", documentId] as const,
    notifications: () => ["supplier-layouts", "notifications"] as const,
  },

  /** Governance (docs/11 §15.3, §15.4): golden documents; sandbox and regression runs are mutations. */
  governance: {
    all: ["governance"] as const,
    golden: () => ["governance", "golden-documents"] as const,
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
