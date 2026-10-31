import { Controller, useFormContext, type FieldValues } from "react-hook-form";
import { useTranslation } from "react-i18next";
import { Input, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea, cn } from "@docproc/ui";
import { ConfidenceBadge, FieldStatusBadge, LocalizedDateInput, SourceBadge, isLowConfidence, type FieldValue } from "@docproc/app-shell";
import { fieldDomId, formName, type FieldSpec } from "@/lib/canonical";

/** Why a field is highlighted: the pointer is over it, or it has the keyboard focus. */
export type HighlightReason = "hover" | "focus";

/**
 * Called with the field's `fieldPath` and why, or with null and the reason that ended: `(null, "hover")` when the
 * pointer leaves, `(null, "focus")` when the focus leaves. The two are kept apart, so a pointer crossing another field
 * does not take the highlight from the field being edited.
 */
export type HighlightHandler = (fieldPath: string | null, reason?: HighlightReason) => void;

export interface FieldInputProps {
  spec: FieldSpec;
  field: FieldValue | undefined;
  /** Shown after a money amount. */
  currency?: string | null;
  disabled?: boolean;
  threshold?: number;
  /** Called with the field's `fieldPath` when the input is hovered or focused, null when left. */
  onHighlight?: HighlightHandler;
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
    onMouseEnter: () => onHighlight?.(spec.fieldPath, "hover"),
    onMouseLeave: () => onHighlight?.(null, "hover"),
    onFocus: () => onHighlight?.(spec.fieldPath, "focus"),
    // Capture phase: register() and the Controllers put their own onBlur on the control, which would replace this one.
    onBlurCapture: () => onHighlight?.(null, "focus"),
  };
  const common = {
    id,
    disabled,
    "aria-invalid": error ? true : undefined,
    "aria-describedby": error ? `${id}-error` : undefined,
    "aria-label": ariaLabel,
    "data-path": spec.path,
    "data-low-confidence": low || undefined,
    // Read-only (a viewer, a decided document): the values are what the screen is for, so they keep full contrast
    // instead of the 50 % opacity of a disabled control.
    className: cn(compact && "h-8 text-sm", low && "border-amber-500/70 dark:border-amber-400/60", disabled && "disabled:cursor-default disabled:bg-muted/40 disabled:opacity-100", className),
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
            <SelectTrigger {...common} className={cn(!compact && "w-full", common.className)} ref={f.ref} onBlur={f.onBlur} size={compact ? "sm" : "default"}>
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
  } else if (spec.kind === "date") {
    // Not a native date input (type="date"): Chromium writes that in the browser's locale, so a German UI showed 10/28/2026.
    control = (
      <Controller
        control={form.control}
        name={name}
        render={({ field: f }) => (
          <LocalizedDateInput
            {...common}
            className={undefined}
            inputClassName={common.className}
            ref={f.ref}
            name={f.name}
            value={typeof f.value === "string" ? f.value : ""}
            onValueChange={f.onChange}
            onBlur={f.onBlur}
          />
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
        type="text"
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
