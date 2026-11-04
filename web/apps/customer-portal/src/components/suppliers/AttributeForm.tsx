import { useTranslation } from "react-i18next";
import { Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Switch } from "@docproc/ui";
import { attributeText, type AttributeField } from "@/lib/attributeSchema";

const NONE = "__none__";

/**
 * A form generated from the tenant's attribute schema (docs/11 §6.4, §19 "Attributes: … with a form generated from
 * SupplierAttributeSchema"): a text or number input per property, a select for an enum, a switch for a boolean. The
 * values are the form's texts (booleans as booleans); the caller parses them with `parseAttribute`.
 */
export function AttributeForm({ idPrefix, fields, values, onChange, disabled, errors = {} }: { idPrefix: string; fields: AttributeField[]; values: Record<string, string | boolean>; onChange: (key: string, value: string | boolean) => void; disabled?: boolean; errors?: Record<string, string> }) {
  const { t } = useTranslation();
  return (
    <div className="grid gap-3 sm:grid-cols-2" data-testid={`${idPrefix}-attribute-form`}>
      {fields.map((field) => {
        const id = `${idPrefix}-attr-${field.key}`;
        const value = values[field.key];
        return (
          <div key={field.key} className="flex flex-col gap-1">
            <Label htmlFor={id}>
              {field.title}
              {field.required && <span aria-hidden> *</span>}
            </Label>
            {field.kind === "boolean" ? (
              <Switch id={id} checked={value === true} onCheckedChange={(checked) => onChange(field.key, checked)} disabled={disabled} />
            ) : field.options ? (
              <Select value={typeof value === "string" && value !== "" ? value : NONE} onValueChange={(next) => onChange(field.key, next === NONE ? "" : next)} disabled={disabled}>
                <SelectTrigger id={id}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={NONE}>{t("suppliers.attributes.none")}</SelectItem>
                  {field.options.map((option) => (
                    <SelectItem key={option} value={option}>
                      {option}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : (
              <Input
                id={id}
                value={attributeText(value)}
                onChange={(event) => onChange(field.key, event.target.value)}
                inputMode={field.kind === "string" ? undefined : "decimal"}
                maxLength={field.maxLength}
                disabled={disabled}
                aria-invalid={!!errors[field.key]}
              />
            )}
            {field.description && <span className="text-xs text-muted-foreground">{field.description}</span>}
            {errors[field.key] && <span className="text-xs text-destructive">{errors[field.key]}</span>}
          </div>
        );
      })}
    </div>
  );
}
