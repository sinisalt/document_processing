import { useQuery } from "@tanstack/react-query";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";

/** `GET /step-kinds`: the step kinds per phase with their parameter schemas (docs/11 §10.2). They change only with a deployment. */
export function useStepKinds(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.pipelines.stepKinds(), queryFn: async () => unwrap(await client.GET("/api/v1/step-kinds")), enabled, staleTime: Infinity });
}

/** `GET /rule-kinds`: the rule kinds a definition may name, with their parameter schemas (docs/11 §11). */
export function useRuleKinds(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.pipelines.ruleKinds(), queryFn: async () => unwrap(await client.GET("/api/v1/rule-kinds")), enabled, staleTime: Infinity });
}

/** `GET /document-types/{key}/resolved-pipeline?supplierId=`: the steps a document of the type runs (for the supplier). */
export function useResolvedPipeline(typeKey: string | null, supplierId?: string | null, enabled = true) {
  const { client } = useApi();
  return useQuery({
    queryKey: queryKeys.pipelines.resolved(typeKey ?? "", supplierId ?? null),
    queryFn: async () =>
      unwrap(await client.GET("/api/v1/document-types/{key}/resolved-pipeline", { params: { path: { key: typeKey! }, query: supplierId ? { supplierId } : {} } })),
    enabled: enabled && !!typeKey,
  });
}
