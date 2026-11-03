import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";

export type Destination = components["schemas"]["DestinationResponse"];
export type Binding = components["schemas"]["OutputBindingResponse"];
export type OutputMapping = components["schemas"]["OutputMappingDetail"];
export type OutputMappingVersionSummary = components["schemas"]["OutputMappingVersionSummary"];
export type OutputMappingVersion = components["schemas"]["OutputMappingVersionDetail"];
export type OutputTemplate = components["schemas"]["OutputTemplateSummary"];
export type Delivery = components["schemas"]["DeliverySummary"];
export type DeliveryDetail = components["schemas"]["DeliveryDetail"];
export type PreviewResponse = components["schemas"]["OutputPreviewResponse"];
export type RenderError = components["schemas"]["RenderErrorDto"];
export type LookupTable = components["schemas"]["LookupTableSummary"];
export type LookupEntry = components["schemas"]["LookupEntryDto"];

/** The transports this build delivers with (docs/11 §24 decision 4: http JSON, file drop and pull feed; sftp and e-mail in P6). */
export const transports = ["http", "blob", "pull"] as const;
export type Transport = (typeof transports)[number];

/** The statuses of a delivery (docs/05 "Output endpoints as built"). */
export const deliveryStatuses = ["Pending", "Sending", "Delivered", "Failed", "DeadLettered", "Skipped", "Cancelled"] as const;

/** The events a binding fires on (docs/11 §13.5 as built). */
export const bindingEvents = ["document.approved", "document.rejected"] as const;
export const reapprovalModes = ["Resend", "Skip", "Update"] as const;

/** `GET /settings`'s feature switches, which the Integrations tab needs (`outputs`). */
export function useOutputsEnabled() {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.settings.get(), queryFn: async () => unwrap(await client.GET("/api/v1/settings")), select: (data) => data.settings.features?.outputs === true });
}

export function useDestinations(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.destinations(), queryFn: async () => unwrap(await client.GET("/api/v1/destinations")), enabled });
}

export function useBindings(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.bindings(), queryFn: async () => unwrap(await client.GET("/api/v1/output-bindings")), enabled });
}

export function useOutputMappings(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.mappings(), queryFn: async () => unwrap(await client.GET("/api/v1/output-mappings")), enabled });
}

export function useOutputTemplates(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.templates(), queryFn: async () => unwrap(await client.GET("/api/v1/output-templates")), enabled });
}

export function useOutputMapping(key: string) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.mapping(key), queryFn: async () => unwrap(await client.GET("/api/v1/output-mappings/{key}", { params: { path: { key } } })) });
}

export interface VersionWithRevision {
  detail: OutputMappingVersion;
  /** The draft's revision from the ETag, for `If-Match` on save. */
  etag: string | null;
}

export function useOutputMappingVersion(key: string, version: number | null) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.outputs.version(key, version ?? 0),
    enabled: version !== null,
    queryFn: async (): Promise<VersionWithRevision> => {
      const result = await client.GET("/api/v1/output-mappings/{key}/versions/{version}", { params: { path: { key, version: version! } } });
      return { detail: unwrap(result), etag: result.response.headers.get("ETag") };
    },
  });
}

export function useLookupTables(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.outputs.lookupTables(), queryFn: async () => unwrap(await client.GET("/api/v1/lookup-tables")), enabled });
}

export interface LookupTableWithRevision {
  table: LookupTable;
  entries: LookupEntry[];
  etag: string | null;
}

export function useLookupTable(key: string | null) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.outputs.lookupTable(key ?? ""),
    enabled: key !== null,
    queryFn: async (): Promise<LookupTableWithRevision> => {
      const result = await client.GET("/api/v1/lookup-tables/{key}", { params: { path: { key: key! } } });
      const detail = unwrap(result);
      return { table: detail.table, entries: detail.entries, etag: result.response.headers.get("ETag") };
    },
  });
}

/** Deliveries still on their way: the list is polled while one is. */
export const inFlightStatuses = new Set(["Pending", "Sending", "Failed"]);

/**
 * `GET /deliveries` with filters (a document's deliveries, or the log), newest first, one page of up to 200; polled
 * every few seconds while a delivery is pending, being sent or waiting for its retry.
 */
export function useDeliveries(query: { documentId?: string; destinationId?: string; status?: string }, enabled = true) {
  const { client } = useApi();
  const params = Object.fromEntries(Object.entries({ ...query, limit: 200 }).filter(([, value]) => value !== undefined && value !== ""));
  return useQuery({
    queryKey: queryKeys.deliveries.list(params),
    queryFn: async () => unwrap(await client.GET("/api/v1/deliveries", { params: { query: params } })),
    enabled,
    refetchInterval: (query) => (query.state.data?.items.some((d) => inFlightStatuses.has(d.status)) ? 3000 : false),
  });
}

export function useDelivery(id: string, enabled: boolean) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.deliveries.detail(id), queryFn: async () => unwrap(await client.GET("/api/v1/deliveries/{id}", { params: { path: { id } } })), enabled });
}

/** The payload snapshot as text (pretty-printed when it is JSON), loaded when asked for. */
export function useDeliveryPayload(id: string, enabled: boolean) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.deliveries.payload(id),
    enabled,
    queryFn: async () => {
      const result = await client.GET("/api/v1/deliveries/{id}/payload", { params: { path: { id } }, parseAs: "text" });
      const text = unwrap(result) as unknown as string;
      const type = result.response.headers.get("Content-Type") ?? "";
      return { contentType: type, text: prettyJson(text) };
    },
  });
}

/** JSON text indented, anything else as it is. */
export function prettyJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

/** A JSON value as indented text, cut at `limit` characters with a note (a 412-row price list is about 110 KB). */
export function jsonPreview(value: unknown, limit = 200_000): { text: string; cut: boolean } {
  const text = JSON.stringify(value, null, 2) ?? "undefined";
  return text.length > limit ? { text: text.slice(0, limit), cut: true } : { text, cut: false };
}
