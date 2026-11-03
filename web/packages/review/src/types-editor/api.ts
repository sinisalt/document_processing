import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";

export type DocumentTypeDetail = components["schemas"]["DocumentTypeDetail"];
export type DocumentTemplateSummary = components["schemas"]["DocumentTemplateSummary"];
export type DocumentTypeVersionSummary = components["schemas"]["DocumentTypeVersionSummary"];
export type DocumentTypeVersionDetail = components["schemas"]["DocumentTypeVersionDetail"];
export type PublishReport = components["schemas"]["PublishReportDto"];
export type IndexedField = components["schemas"]["IndexedFieldDto"];

/** The legacy type keys every tenant has (the `DocumentType` names before P1), in the inbox's order. */
export const legacyTypeKeys = ["Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other", "Unknown"] as const;

/** `GET /document-types`: the tenant's types (tenant scope only; a platform account has none). */
export function useDocumentTypes(enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documentTypes.list(),
    queryFn: async () => unwrap(await client.GET("/api/v1/document-types")),
    enabled,
    staleTime: 60_000,
  });
}

/** `GET /document-templates`: the platform templates with the tenant's types made from each. */
export function useDocumentTemplates(enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documentTypes.templates(),
    queryFn: async () => unwrap(await client.GET("/api/v1/document-templates")),
    enabled,
  });
}

/** `GET /document-types/{key}`: one type with its versions. */
export function useDocumentType(key: string) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documentTypes.detail(key),
    queryFn: async () => unwrap(await client.GET("/api/v1/document-types/{key}", { params: { path: { key } } })),
  });
}

export interface VersionWithRevision {
  detail: DocumentTypeVersionDetail;
  /** The draft revision from the ETag, for `If-Match` on save and publish. */
  etag: string | null;
}

/** `GET /document-types/{key}/versions/{v}?include=formSpec`: the definition, its form spec, and the ETag of a draft. */
export function useDocumentTypeVersion(key: string, version: number | null) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.documentTypes.version(key, version ?? 0),
    enabled: version !== null,
    queryFn: async (): Promise<VersionWithRevision> => {
      const result = await client.GET("/api/v1/document-types/{key}/versions/{version}", { params: { path: { key, version: version! }, query: { include: "formSpec" } } });
      return { detail: unwrap(result), etag: result.response.headers.get("ETag") };
    },
  });
}
