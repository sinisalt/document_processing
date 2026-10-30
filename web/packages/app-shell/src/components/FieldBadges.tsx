import { useTranslation } from "react-i18next";
import { Badge, cn } from "@docproc/ui";
import { formatPercent } from "../lib/format";
import {
  confidenceVariant,
  DEFAULT_CONFIDENCE_THRESHOLD,
  fieldStatusVariant,
  sourceMethodVariant,
  type FieldSource,
  type FieldStatus,
  type SourceMethod,
} from "../lib/fieldValue";

export function FieldStatusBadge({ status, className }: { status: FieldStatus; className?: string }) {
  const { t } = useTranslation("common");
  return (
    <Badge variant={fieldStatusVariant[status] ?? "outline"} className={className} data-field-status={status}>
      {t(`fieldStatus.${status}`, { defaultValue: status })}
    </Badge>
  );
}

export interface ConfidenceBadgeProps {
  confidence: number | null | undefined;
  /** The tenant's `autoApproveConfidenceThreshold`. */
  threshold?: number;
  className?: string;
}

export function ConfidenceBadge({ confidence, threshold = DEFAULT_CONFIDENCE_THRESHOLD, className }: ConfidenceBadgeProps) {
  const { t } = useTranslation("common");
  const known = typeof confidence === "number";
  const value = known ? formatPercent(confidence) : null;
  return (
    <Badge
      variant={confidenceVariant(confidence, threshold)}
      className={cn("tabular-nums", className)}
      aria-label={known ? t("confidence.label", { value }) : t("confidence.unknown")}
      title={known && confidence < threshold ? t("confidence.low") : undefined}
    >
      {value ?? "–"}
    </Badge>
  );
}

export function SourceBadge({ source, className }: { source: FieldSource | SourceMethod | null | undefined; className?: string }) {
  const { t } = useTranslation("common");
  if (!source) return null;
  const method = typeof source === "string" ? source : source.method;
  const page = typeof source === "string" ? null : source.page;
  return (
    <Badge variant={sourceMethodVariant[method] ?? "outline"} className={className} data-source={method}>
      {t(`sourceMethod.${method}`, { defaultValue: method })}
      {page != null ? ` · ${page}` : ""}
    </Badge>
  );
}
