import { useTranslation } from "react-i18next";
import { Alert, AlertDescription, AlertTitle, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@docproc/ui";
import { isApiError } from "@docproc/app-shell";
import { REVIEW_NS } from "../lib/labels";
import { useResolvedPipeline } from "../pipeline/api";
import { PipelineSteps } from "../pipeline/PipelineSteps";

/**
 * The type editor's Processing tab (docs/11 §10, §19 item 2): the steps each phase runs for a document of the published
 * type, before any supplier's overrides (`GET /document-types/{key}/resolved-pipeline`). A supplier's or a group's
 * overrides, http lookups included, are set on the supplier's Processing tab; the review gate is on the Rules tab.
 */
export function ProcessingTab({ typeKey }: { typeKey: string }) {
  const { t } = useTranslation(REVIEW_NS);
  const pipeline = useResolvedPipeline(typeKey);
  return (
    <Card data-testid="type-processing">
      <CardHeader>
        <CardTitle className="text-base">{t("pipeline.typeTitle")}</CardTitle>
        <CardDescription>{t("pipeline.typeDescription")}</CardDescription>
      </CardHeader>
      <CardContent>
        {pipeline.isError ? (
          <Alert variant="destructive">
            <AlertTitle>{t("pipeline.unavailable")}</AlertTitle>
            <AlertDescription>{isApiError(pipeline.error) ? (pipeline.error.problem?.detail ?? pipeline.error.title) : String(pipeline.error)}</AlertDescription>
          </Alert>
        ) : (
          <PipelineSteps pipeline={pipeline.data} isLoading={pipeline.isPending} />
        )}
      </CardContent>
    </Card>
  );
}
