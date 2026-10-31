import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { isInFlight, queryKeys, unwrap, useApi, useCursorList, type DocumentListQuery, type SupplierListQuery } from "@docproc/app-shell";

export type DocumentSummary = components["schemas"]["DocumentSummary"];
export type DocumentDetail = components["schemas"]["DocumentDetail"];
export type DocumentEvent = components["schemas"]["DocumentEventDto"];
export type DocumentFlag = components["schemas"]["DocumentFlagDto"];
export type Supplier = components["schemas"]["SupplierResponse"];
export type SupplierActivityItem = components["schemas"]["SupplierActivityItem"];
export type ReviewTask = components["schemas"]["ReviewTaskDto"];
export type Member = components["schemas"]["MemberResponse"];
export type ApiKey = components["schemas"]["ApiKeySummary"];
export type Channel = components["schemas"]["ChannelResponse"];
export type LlmProvider = components["schemas"]["LlmProviderResponse"];
export type LlmPolicy = components["schemas"]["LlmPolicyResponse"];
export type LlmUsage = components["schemas"]["LlmUsageResponse"];
export type TenantSettings = components["schemas"]["TenantSettingsResponse"];

/** The inbox poll (docs/05 "while documents are Processing, poll"). */
export const PROCESSING_POLL_MS = 3_000;

export const EMPTY_ARRAY: never[] = [];

/** How long the lists keep polling after an upload, so its documents show up in a tab that did not list them yet. */
export const UPLOAD_POLL_WINDOW_MS = 60_000;

let lastUploadAt = 0;
const uploadListeners = new Set<() => void>();

/** Called by the upload dialog: every mounted document list polls for the next minute. */
export function notifyUploaded(): void {
  lastUploadAt = Date.now();
  for (const listener of uploadListeners) listener();
}

/** True for {@link UPLOAD_POLL_WINDOW_MS} after the last {@link notifyUploaded}. */
export function useRecentUpload(): boolean {
  const remaining = () => Math.max(0, UPLOAD_POLL_WINDOW_MS - (Date.now() - lastUploadAt));
  const [recent, setRecent] = useState(() => remaining() > 0);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const arm = () => {
      clearTimeout(timer);
      const left = remaining();
      setRecent(left > 0);
      if (left > 0) timer = setTimeout(() => setRecent(false), left);
    };
    arm();
    uploadListeners.add(arm);
    return () => {
      uploadListeners.delete(arm);
      clearTimeout(timer);
    };
  }, []);
  return recent;
}

/**
 * Every page of `GET /documents` for one filter set; polls every 3 s while any loaded row is still in the pipeline,
 * and for a minute after an upload (the new documents may belong to a tab that shows nothing in flight).
 */
export function useDocumentList(query: DocumentListQuery, enabled = true) {
  const { client } = useApi();
  const [inFlight, setInFlight] = useState(false);
  const recentUpload = useRecentUpload();
  const list = useCursorList<DocumentSummary>({
    queryKey: queryKeys.documents.pages(query),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/documents", { params: { query: { ...query, cursor: cursor ?? undefined } } })),
    enabled,
    refetchInterval: inFlight || recentUpload ? PROCESSING_POLL_MS : false,
  });
  const anyInFlight = list.items.some((d) => isInFlight(d.status));
  useEffect(() => setInFlight(anyInFlight), [anyInFlight]);
  return list;
}

/** `GET /documents/{id}`; polls while the pipeline still writes it. */
export function useDocument(id: string) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documents.detail(id),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}", { params: { path: { id } } })),
    refetchInterval: (query) => (query.state.data && isInFlight(query.state.data.status) ? PROCESSING_POLL_MS : false),
  });
}

export function useDocumentEvents(id: string, enabled = true, options: { refetchOnMount?: boolean | "always" } = {}) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documents.events(id),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/events", { params: { path: { id } } })),
    enabled,
    refetchOnMount: options.refetchOnMount,
  });
}

/** The first page of suppliers matching `q` (200 is the API's page cap), for pickers and the supplier screen. */
export function useSuppliers(query: SupplierListQuery = {}, enabled = true) {
  const { client } = useApi();
  return useCursorList<Supplier>({
    queryKey: queryKeys.suppliers.pages(query),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/suppliers", { params: { query: { ...query, cursor: cursor ?? undefined } } })),
    enabled,
    staleTime: 60_000,
  });
}

export function useTenantSettings(enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.settings.get(),
    queryFn: async () => unwrap(await client.GET("/api/v1/settings")),
    enabled,
    staleTime: 5 * 60_000,
  });
}
