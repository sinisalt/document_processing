import { useTranslation } from "react-i18next";
import { CircleAlert, Lock } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Skeleton, Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@docproc/ui";
import { REVIEW_NS } from "../lib/labels";
import { stepsByPhase, type ResolvedPipeline } from "./overrides";

/** The parameters of a step in one line (`effort: high · maxCostPerDocument: 0.5`); empty for none. */
export function paramsText(params: unknown): string {
  if (!params || typeof params !== "object") return "";
  return Object.entries(params as Record<string, unknown>)
    .map(([name, value]) => `${name}: ${typeof value === "string" ? (value.length > 60 ? `${value.slice(0, 57)}…` : value) : JSON.stringify(value)}`)
    .join(" · ");
}

/**
 * The steps each phase runs (docs/11 §10.1, §19 Processing): per phase every step with its kind, whether it runs, the
 * layer that last changed it (the type, the group, the supplier, the layout), fixed steps locked and steps this build
 * does not run marked; the overrides that were skipped and, when the layers were refused, why.
 */
export function PipelineSteps({ pipeline, isLoading }: { pipeline: ResolvedPipeline | undefined; isLoading?: boolean }) {
  const { t } = useTranslation(REVIEW_NS);
  if (isLoading || !pipeline) return <Skeleton className="h-40 w-full" aria-busy />;
  return (
    <div className="grid gap-3" data-testid="pipeline-steps" data-hash={pipeline.hash}>
      {!pipeline.configurable && (
        <Alert>
          <AlertTitle>{t("pipeline.offTitle")}</AlertTitle>
          <AlertDescription>{t("pipeline.off")}</AlertDescription>
        </Alert>
      )}
      {pipeline.refused.length > 0 && (
        <Alert variant="destructive" data-testid="pipeline-refused">
          <CircleAlert aria-hidden />
          <AlertTitle>{t("pipeline.refusedTitle")}</AlertTitle>
          <AlertDescription>
            <ul className="list-disc pl-4">
              {pipeline.refused.map((problem) => (
                <li key={problem.path + problem.message}>
                  <span className="font-mono text-xs">{problem.path}</span>: {problem.message}
                </li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      )}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("pipeline.columns.phase")}</TableHead>
            <TableHead>{t("pipeline.columns.step")}</TableHead>
            <TableHead>{t("pipeline.columns.kind")}</TableHead>
            <TableHead>{t("pipeline.columns.state")}</TableHead>
            <TableHead>{t("pipeline.columns.source")}</TableHead>
            <TableHead>{t("pipeline.columns.params")}</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {stepsByPhase(pipeline.steps).flatMap((group) =>
            group.steps.map((step, index) => (
              <TableRow key={`${group.phase}:${step.id}`} data-step={step.id} data-enabled={step.enabled} data-source={step.source}>
                <TableCell className="text-xs font-medium">{index === 0 ? t(`pipeline.phases.${group.phase}`, { defaultValue: group.phase }) : ""}</TableCell>
                <TableCell className="font-mono text-xs">
                  <span className="flex items-center gap-1">
                    {step.fixed && <Lock className="size-3 text-muted-foreground" aria-label={t("pipeline.fixed")} />}
                    {step.id}
                  </span>
                </TableCell>
                <TableCell className="font-mono text-xs">{step.kind}</TableCell>
                <TableCell>
                  {!step.runs ? (
                    <Badge variant="outline">{t("pipeline.notRun")}</Badge>
                  ) : step.enabled ? (
                    <Badge variant="success">{t("pipeline.on")}</Badge>
                  ) : (
                    <Badge variant="secondary">{t("pipeline.disabled")}</Badge>
                  )}
                </TableCell>
                <TableCell>
                  <Badge variant={step.source === "default" || step.source === "type" ? "outline" : "warning"}>{t(`pipeline.sources.${step.source}`, { defaultValue: step.source })}</Badge>
                </TableCell>
                <TableCell className="max-w-xs font-mono text-[11px] break-words text-muted-foreground">{paramsText(step.params)}</TableCell>
              </TableRow>
            )),
          )}
        </TableBody>
      </Table>
      {pipeline.skipped.length > 0 && (
        <div className="text-xs text-muted-foreground" data-testid="pipeline-skipped">
          <p className="font-medium">{t("pipeline.skipped")}</p>
          <ul className="list-disc pl-4">
            {pipeline.skipped.map((note) => (
              <li key={note}>{note}</li>
            ))}
          </ul>
        </div>
      )}
      <p className="font-mono text-[11px] text-muted-foreground">{t("pipeline.hash", { hash: pipeline.hash })}</p>
    </div>
  );
}
