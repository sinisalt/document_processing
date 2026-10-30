import { Controller, useFormContext, type FieldValues } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { Input, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea, cn } from "@docproc/ui";
import { ConfidenceBadge, FieldStatusBadge, SourceBadge, isLowConfidence, type FieldValue } from "@docproc/app-shell";
import { fieldDomId, formName, type FieldSpec } from "@/lib/canonical";

export interface FieldInputProps {
  spec: FieldSpec;
  field: FieldValue | undefined;
  /** Shown after a money amount. */
  currency?: string | null;
  disabled?: boolean;
  threshold?: number;
  /** Called with the field's `fieldPath` when the input is hovered or focused, null when left. */
  onHighlight?: (fieldPath: string | null) => void;
  /** Compact variant for grid cells: no badges, dense input. */
  compact?: boolean;
  /** Hide the badges (a nested property of a block field shows them once, at the block). */
  showBadges?: boolean;
  className?: string;
  "aria-label"?: string;
}

const CLEAR = "__clear__";

/**
 * One editable canonical value bound to the review form by its canonical path: a text/number/date input, a select for
 * enums, with the shell's status, confidence and source badges beside it.
 */
export function FieldInput({ spec, field, currency, disabled = false, threshold, onHighlight, compact = false, showBadges = true, className, "aria-label": ariaLabel }: FieldInputProps) {
  const { t } = useTranslation();
  const form = useFormContext<FieldValues>();
  const name = formName(spec.path);
  const id = fieldDomId(spec.path);
  const error = form.getFieldState(name, form.formState).error;
  const low = isLowConfidence(field, threshold);
  const hover = {
    onMouseEnter: () => onHighlight?.(spec.fieldPath),
    onMouseLeave: () => onHighlight?.(null),
    onFocus: () => onHighlight?.(spec.fieldPath),
  };
  const common = {
    id,
    disabled,
    "aria-invalid": error ? true : undefined,
    "aria-describedby": error ? `${id}-error` : undefined,
    "aria-label": ariaLabel,
    "data-path": spec.path,
    "data-low-confidence": low || undefined,
    className: cn(compact && "h-8 text-sm", low && "border-destructive/60", className),
    ...hover,
  };

  let control;
  if (spec.kind === "enum") {
    control = (
      <Controller
        control={form.control}
        name={name}
        render={({ field: f }) => (
          <Select value={f.value ? String(f.value) : CLEAR} onValueChange={(value) => f.onChange(value === CLEAR ? "" : value)} disabled={disabled}>
            <SelectTrigger {...common} ref={f.ref} onBlur={f.onBlur} size={compact ? "sm" : "default"}>
              <SelectValue placeholder={t("review.form.none")} />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={CLEAR}>{t("review.form.none")}</SelectItem>
              {(spec.options ?? []).map((option) => (
                <SelectItem key={option} value={option}>
                  {t(`enums.${option}`, { defaultValue: option })}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      />
    );
  } else if (spec.kind === "text") {
    control = <Textarea {...common} {...form.register(name)} rows={compact ? 1 : 3} />;
  } else {
    control = (
      <Input
        {...common}
        {...form.register(name)}
        type={spec.kind === "date" ? "date" : "text"}
        inputMode={spec.kind === "money" || spec.kind === "number" ? "decimal" : undefined}
        className={cn(common.className, (spec.kind === "money" || spec.kind === "number") && "text-right tabular-nums")}
      />
    );
  }

  return (
    <div className={cn("flex min-w-0 flex-col gap-1", compact && "gap-0.5")} data-field={spec.path} data-field-status={field?.status}>
      <div className="flex items-center gap-1">
        <div className="min-w-0 flex-1">{control}</div>
        {spec.kind === "money" && currency && (
          <span className="shrink-0 text-xs text-muted-foreground" aria-hidden>
            {currency}
          </span>
        )}
      </div>
      {!compact && showBadges && field && (
        <div className="flex flex-wrap items-center gap-1">
          <FieldStatusBadge status={field.status} />
          <ConfidenceBadge confidence={field.confidence} threshold={threshold} />
          <SourceBadge source={field.source} />
        </div>
      )}
      {error && (
        <p id={`${id}-error`} role="alert" className="text-xs text-destructive">
          {String(error.message ?? t("common:errors.validation"))}
        </p>
      )}
    </div>
  );
}
