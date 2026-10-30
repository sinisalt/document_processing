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

/** Every page of `GET /documents` for one filter set; polls every 3 s while any loaded row is still in the pipeline. */
export function useDocumentList(query: DocumentListQuery, enabled = true) {
  const { client } = useApi();
  const [polling, setPolling] = useState(false);
  const list = useCursorList<DocumentSummary>({
    queryKey: queryKeys.documents.list(query),
    queryFn: async (cursor) => unwrap(await client.GET("/api/v1/documents", { params: { query: { ...query, cursor: cursor ?? undefined } } })),
    enabled,
    refetchInterval: polling ? PROCESSING_POLL_MS : false,
  });
  const inFlight = list.items.some((d) => isInFlight(d.status));
  useEffect(() => setPolling(inFlight), [inFlight]);
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

export function useDocumentEvents(id: string, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documents.events(id),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/events", { params: { path: { id } } })),
    enabled,
  });
}

/** The first page of suppliers matching `q` (200 is the API's page cap), for pickers and the supplier screen. */
export function useSuppliers(query: SupplierListQuery = {}, enabled = true) {
  const { client } = useApi();
  return useCursorList<Supplier>({
    queryKey: queryKeys.suppliers.list(query),
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
