import { useQuery } from "@tanstack/react-query";
import type { components } from "@docproc/api-client";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";

export type SourceRule = components["schemas"]["SourceRuleResponse"];
export type SourceRuleRequest = components["schemas"]["SourceRuleRequest"];
export type RoutingTestRequest = components["schemas"]["RoutingTestRequest"];
export type RoutingTestResponse = components["schemas"]["RoutingTestResponse"];
export type SupplierGroup = components["schemas"]["SupplierGroupResponse"];
export type ReceivingEntity = components["schemas"]["ReceivingEntityResponse"];
export type EntityAttributes = components["schemas"]["SupplierEntityAttributesResponse"];

/** `GET /suppliers/{id}/source-rules` (docs/11 §6.5): the supplier's source rules by priority, each with its sentence. */
export function useSourceRules(supplierId: string | null | undefined, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.routing.sourceRules(supplierId ?? ""),
    queryFn: async () => unwrap(await client.GET("/api/v1/suppliers/{supplierId}/source-rules", { params: { path: { supplierId: supplierId! } } })),
    enabled: enabled && !!supplierId,
  });
}

/** `GET /suppliers/{id}/entity-attributes`: the supplier's attributes per receiving entity. */
export function useEntityAttributes(supplierId: string | null | undefined, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.routing.entityAttributes(supplierId ?? ""),
    queryFn: async () => unwrap(await client.GET("/api/v1/suppliers/{supplierId}/entity-attributes", { params: { path: { supplierId: supplierId! } } })),
    enabled: enabled && !!supplierId,
  });
}

/** `GET /supplier-groups`. */
export function useSupplierGroups(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.routing.groups(), queryFn: async () => unwrap(await client.GET("/api/v1/supplier-groups")), enabled });
}

/** `GET /receiving-entities`: the tenant's own companies or branches, by code. */
export function useReceivingEntities(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.routing.entities(), queryFn: async () => unwrap(await client.GET("/api/v1/receiving-entities")), enabled });
}

/** The lines of a textarea list ("one per line or comma-separated"), trimmed and without duplicates. */
export function splitLines(text: string): string[] {
  return Array.from(new Set(text.split(/[\n,;]+/).map((s) => s.trim()).filter(Boolean)));
}

/** The source-rule form's values as the API's request: empty texts are left out (a condition not set). */
export function sourceRuleBody(values: Record<string, string | boolean | number | null | undefined>): SourceRuleRequest {
  const text = (key: string) => {
    const value = values[key];
    return typeof value === "string" && value.trim() !== "" ? value.trim() : null;
  };
  return {
    channelId: text("channelId"),
    recipient: text("recipient"),
    sender: text("sender"),
    senderDomain: text("senderDomain"),
    folderPrefix: text("folderPrefix"),
    subjectRegex: text("subjectRegex"),
    fileNameRegex: text("fileNameRegex"),
    mode: text("mode"),
    requireSenderAuth: typeof values.requireSenderAuth === "boolean" ? values.requireSenderAuth : null,
    priority: typeof values.priority === "number" && Number.isFinite(values.priority) ? values.priority : null,
    pinTypeKey: text("pinTypeKey"),
    receivingEntityId: text("receivingEntityId"),
    enabled: typeof values.enabled === "boolean" ? values.enabled : null,
  };
}
