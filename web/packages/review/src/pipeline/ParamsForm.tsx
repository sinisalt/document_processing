import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Textarea } from "@docproc/ui";
import { REVIEW_NS } from "../lib/labels";
import { displayValue, paramFields, readValue, takesNoParams, withParam, type JsonSchema, type ParamField, type ParamValues } from "./paramsSchema";

const UNSET = "__unset__";

export interface ParamsFormProps {
  /** The kind's `paramsSchema`. */
  schema: JsonSchema | null | undefined;
  /** Rule kinds: the parameters that hold a rule expression. */
  expressionParams?: readonly string[];
  values: ParamValues;
  onChange: (values: ParamValues) => void;
  disabled?: boolean;
  /** Prefix of the inputs' ids and test ids, unique on the page. */
  idPrefix: string;
  /** A control of the caller's own for a parameter (the destination picker of an `http-lookup`). */
  renderField?: (field: ParamField, value: unknown, set: (value: unknown) => void) => ReactNode | undefined;
}

/**
 * A step or rule kind's parameters as a form (docs/11 §19 "Parameter forms"), drawn from its JSON Schema: text,
 * numbers, switches as default / yes / no, enums, comma lists, rule expressions in a monospace area and anything else
 * as JSON. A parameter left empty is left out, so the kind's default applies. The caller remounts it (`key`) for another
 * step or rule.
 */
export function ParamsForm({ schema, expressionParams = [], values, onChange, disabled, idPrefix, renderField }: ParamsFormProps) {
  const { t } = useTranslation(REVIEW_NS);
  const fields = paramFields(schema, expressionParams);
  const [texts, setTexts] = useState<Record<string, string>>(() => Object.fromEntries(fields.map((f) => [f.name, displayValue(f, values[f.name])])));
  const [errors, setErrors] = useState<Record<string, string>>({});

  if (fields.length === 0) {
    return <p className="text-sm text-muted-foreground">{takesNoParams(schema) ? t("params.none") : t("params.free")}</p>;
  }

  const setText = (field: ParamField, text: string) => {
    setTexts((current) => ({ ...current, [field.name]: text }));
    const read = readValue(field, text);
    if ("error" in read) {
      setErrors((current) => ({ ...current, [field.name]: t(`params.errors.${read.error}`, { min: field.min ?? "", max: field.max ?? "" }) }));
      return;
    }
    setErrors((current) => {
      const next = { ...current };
      delete next[field.name];
      return next;
    });
    onChange(withParam(values, field.name, read.value));
  };

  return (
    <div className="grid gap-3" data-testid={`${idPrefix}-params`}>
      {fields.map((field) => {
        const id = `${idPrefix}-${field.name}`;
        const own = renderField?.(field, values[field.name], (value) => onChange(withParam(values, field.name, value)));
        return (
          <div key={field.name} className="grid gap-1.5">
            <Label htmlFor={id} className="font-mono text-xs">
              {field.name}
              {field.required && <span className="text-destructive"> *</span>}
            </Label>
            {own !== undefined ? (
              own
            ) : field.control === "enum" || field.control === "boolean" ? (
              <Select
                value={values[field.name] === undefined ? UNSET : String(values[field.name])}
                onValueChange={(value) => onChange(withParam(values, field.name, value === UNSET ? undefined : field.control === "boolean" ? value === "true" : value))}
                disabled={disabled}
              >
                <SelectTrigger id={id} data-testid={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={UNSET}>{t("params.default")}</SelectItem>
                  {(field.control === "boolean" ? ["true", "false"] : (field.options ?? [])).map((option) => (
                    <SelectItem key={option} value={option}>
                      {field.control === "boolean" ? t(`params.${option}`) : option}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : field.control === "expression" || field.control === "json" ? (
              <Textarea
                id={id}
                data-testid={id}
                className="min-h-20 font-mono text-xs"
                spellCheck={false}
                disabled={disabled}
                value={texts[field.name] ?? displayValue(field, values[field.name])}
                onChange={(event) => setText(field, event.target.value)}
                aria-invalid={errors[field.name] ? true : undefined}
              />
            ) : (
              <Input
                id={id}
                data-testid={id}
                className={field.control === "text" ? "font-mono text-xs" : undefined}
                inputMode={field.control === "number" || field.control === "integer" ? "decimal" : undefined}
                disabled={disabled}
                value={texts[field.name] ?? displayValue(field, values[field.name])}
                placeholder={field.control === "list" ? t("params.listHint") : undefined}
                onChange={(event) => setText(field, event.target.value)}
                aria-invalid={errors[field.name] ? true : undefined}
              />
            )}
            {errors[field.name] && (
              <p className="text-xs text-destructive" role="alert">
                {errors[field.name]}
              </p>
            )}
          </div>
        );
      })}
    </div>
  );
}
