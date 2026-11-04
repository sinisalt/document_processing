import { useQuery } from "@tanstack/react-query";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";

/** `GET /suppliers/{id}/layouts`: the supplier's layouts with their versions, newest first (docs/11 §8, P4). */
export function useSupplierLayouts(supplierId: string | null | undefined, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.supplierLayouts.ofSupplier(supplierId ?? ""),
    queryFn: async () => unwrap(await client.GET("/api/v1/suppliers/{supplierId}/layouts", { params: { path: { supplierId: supplierId! } } })),
    enabled: enabled && !!supplierId,
  });
}

/** `GET /supplier-layouts/{id}/versions/{v}`: one version in full (rule, mapping, coverage, replay). */
export function useLayoutVersion(layoutId: string | null | undefined, version: number | null | undefined, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.supplierLayouts.version(layoutId ?? "", version ?? 0),
    queryFn: async () => unwrap(await client.GET("/api/v1/supplier-layouts/{layoutId}/versions/{version}", { params: { path: { layoutId: layoutId!, version: version! } } })),
    enabled: enabled && !!layoutId && !!version,
  });
}

/** `GET /supplier-layouts/{id}/versions/{v}/matching-documents?state=`: the sibling re-test (pending) or the documents a rule no longer holds on. */
export function useMatchingDocuments(layoutId: string | null | undefined, version: number | null | undefined, state: "pending" | "approved", enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.supplierLayouts.matching(layoutId ?? "", version ?? 0, state),
    queryFn: async () =>
      unwrap(
        await client.GET("/api/v1/supplier-layouts/{layoutId}/versions/{version}/matching-documents", {
          params: { path: { layoutId: layoutId!, version: version! }, query: state === "pending" ? { state } : { state, holds: false } },
        }),
      ),
    enabled: enabled && !!layoutId && !!version,
  });
}

/** `GET /documents/{id}/layout-suggestions`: the supplier's layouts ranked by similarity to the document (suggestions only). */
export function useLayoutSuggestions(documentId: string, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.supplierLayouts.suggestions(documentId),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/layout-suggestions", { params: { path: { id: documentId } } })),
    enabled,
    retry: false,
    meta: { silent: true },
  });
}

/** `GET /notifications`: the open notifications of the policies the caller holds (Candidates waiting for a MappingAuthor). */
export function useNotifications(enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.supplierLayouts.notifications(),
    queryFn: async () => unwrap(await client.GET("/api/v1/notifications")),
    enabled,
    retry: false,
    meta: { silent: true },
  });
}
