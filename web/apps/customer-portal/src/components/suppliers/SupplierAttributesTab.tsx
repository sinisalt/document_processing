import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Trash2 } from "lucide-react";
import { Button, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Separator } from "@docproc/ui";
import { ApiError, EmptyState, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { attributeFields, mergeAttributes, parseAttribute, type AttributeField, type AttributeValues } from "@/lib/attributeSchema";
import { useEntityAttributes, useReceivingEntities } from "@/lib/routingQueries";
import { useTenantSettings, type Supplier } from "@/lib/queries";
import { AttributeForm } from "./AttributeForm";

/** The form's texts for stored attributes. */
function formValues(fields: AttributeField[], stored: Record<string, unknown> | null | undefined): Record<string, string | boolean> {
  return Object.fromEntries(fields.map((f) => [f.key, f.kind === "boolean" ? stored?.[f.key] === true : stored?.[f.key] === undefined || stored?.[f.key] === null ? "" : String(stored[f.key])]));
}

/** The form's texts as typed values, or the fields whose text is not a value of their kind. */
export function readForm(fields: AttributeField[], texts: Record<string, string | boolean>, t: (key: string) => string): { values: AttributeValues } | { errors: Record<string, string> } {
  const values: AttributeValues = {};
  const errors: Record<string, string> = {};
  for (const field of fields) {
    const parsed = parseAttribute(field, texts[field.key] ?? "");
    if ("error" in parsed) errors[field.key] = t(`suppliers.attributes.invalid.${parsed.error}`);
    else values[field.key] = parsed.value;
  }
  return Object.keys(errors).length > 0 ? { errors } : { values };
}

/**
 * The Suppliers sheet's Attributes tab (docs/11 §19 item 3, §6.4): the supplier's own attributes and its attributes per
 * receiving entity (the vendor number in company 1000), each a form generated from the tenant's attribute schema. The
 * values view merges group, then supplier, then the supplier's attributes in the document's entity.
 */
export function SupplierAttributesTab({ supplier, canEdit }: { supplier: Supplier; canEdit: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const settings = useTenantSettings();
  const fields = useMemo(() => attributeFields(settings.data?.settings?.supplierAttributeSchema), [settings.data]);
  const entities = useReceivingEntities();
  const perEntity = useEntityAttributes(supplier.id);
  const [own, setOwn] = useState(() => formValues(fields, supplier.attributes as Record<string, unknown>));
  const [ownErrors, setOwnErrors] = useState<Record<string, string>>({});
  // The schema may load after the sheet: the form then starts from the stored attributes.
  useEffect(() => setOwn(formValues(fields, supplier.attributes as Record<string, unknown>)), [fields, supplier.attributes]);
  const [entityId, setEntityId] = useState<string>("");
  const stored = (perEntity.data ?? []).find((e) => e.receivingEntityId === entityId);
  const [entityTexts, setEntityTexts] = useState<Record<string, string | boolean>>({});

  const saveOwn = useMutation({
    mutationFn: async () => {
      const read = readForm(fields, own, t);
      if ("errors" in read) {
        setOwnErrors(read.errors);
        throw new Error("invalid");
      }
      setOwnErrors({});
      return unwrap(
        await client.PATCH("/api/v1/suppliers/{id}", {
          params: { path: { id: supplier.id } },
          body: { name: null, vatId: null, taxId: null, ibans: null, emailDomains: null, aliases: null, defaultCurrency: null, alwaysReview: null, attributes: mergeAttributes(supplier.attributes as Record<string, unknown>, fields, read.values) as never },
        }),
      );
    },
    onSuccess: () => {
      toast.success(t("suppliers.attributes.saved"));
      void queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) setOwnErrors(Object.fromEntries(Object.entries(error.fieldErrors).map(([k, v]) => [k.replace(/^attributes\./, ""), v.join(" ")])));
      else if (error instanceof ApiError) toast.error(error.title, { description: error.problem?.detail ?? undefined });
    },
    meta: { silent: true },
  });

  const saveEntity = useMutation({
    mutationFn: async () => {
      const read = readForm(fields, entityTexts, t);
      if ("errors" in read) throw new Error(Object.values(read.errors).join(" "));
      return unwrap(
        await client.PUT("/api/v1/suppliers/{supplierId}/entity-attributes/{entityId}", {
          params: { path: { supplierId: supplier.id, entityId } },
          body: { attributes: mergeAttributes(stored?.attributes as Record<string, unknown>, fields, read.values) as never },
        }),
      );
    },
    onSuccess: () => {
      toast.success(t("suppliers.attributes.entitySaved"));
      void queryClient.invalidateQueries({ queryKey: queryKeys.routing.entityAttributes(supplier.id) });
    },
  });
  const removeEntity = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/suppliers/{supplierId}/entity-attributes/{entityId}", { params: { path: { supplierId: supplier.id, entityId: id } } })),
    onSuccess: () => {
      toast.success(t("suppliers.attributes.entityRemoved"));
      void queryClient.invalidateQueries({ queryKey: queryKeys.routing.entityAttributes(supplier.id) });
    },
  });

  if (fields.length === 0) {
    return <EmptyState title={t("suppliers.attributes.noSchemaTitle")} description={t("suppliers.attributes.noSchema")} />;
  }

  return (
    <div className="flex flex-col gap-4" data-testid="supplier-attributes-tab">
      <section className="flex flex-col gap-3">
        <h3 className="text-sm font-semibold">{t("suppliers.attributes.own")}</h3>
        <AttributeForm idPrefix="supplier" fields={fields} values={own} onChange={(key, value) => setOwn((c) => ({ ...c, [key]: value }))} disabled={!canEdit} errors={ownErrors} />
        {canEdit && (
          <div>
            <Button size="sm" onClick={() => saveOwn.mutate()} disabled={saveOwn.isPending} data-testid="supplier-attributes-save">
              {saveOwn.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("suppliers.attributes.save")}
            </Button>
          </div>
        )}
      </section>
      <Separator />
      <section className="flex flex-col gap-3">
        <h3 className="text-sm font-semibold">{t("suppliers.attributes.perEntity")}</h3>
        {(perEntity.data ?? []).length > 0 && (
          <ul className="flex flex-col gap-1 text-sm">
            {(perEntity.data ?? []).map((e) => (
              <li key={e.receivingEntityId} className="flex items-center gap-2">
                <span className="font-medium">{e.receivingEntityCode}</span>
                <span className="min-w-0 flex-1 truncate text-muted-foreground">{Object.entries(e.attributes).map(([k, v]) => `${k} = ${String(v)}`).join(", ")}</span>
                {canEdit && (
                  <Button size="icon-xs" variant="ghost" aria-label={t("suppliers.attributes.entityRemove")} onClick={() => removeEntity.mutate(e.receivingEntityId)}>
                    <Trash2 aria-hidden />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        )}
        {(entities.data ?? []).length === 0 ? (
          <p className="text-sm text-muted-foreground">{t("suppliers.attributes.noEntities")}</p>
        ) : (
          <>
            <div className="flex flex-col gap-1">
              <Label htmlFor="attributes-entity">{t("suppliers.attributes.entity")}</Label>
              <Select
                value={entityId}
                onValueChange={(value) => {
                  setEntityId(value);
                  setEntityTexts(formValues(fields, (perEntity.data ?? []).find((e) => e.receivingEntityId === value)?.attributes as Record<string, unknown>));
                }}
              >
                <SelectTrigger id="attributes-entity" className="sm:w-72">
                  <SelectValue placeholder={t("suppliers.attributes.pickEntity")} />
                </SelectTrigger>
                <SelectContent>
                  {(entities.data ?? []).map((e) => (
                    <SelectItem key={e.id} value={e.id}>
                      {e.code} · {e.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {entityId && (
              <>
                <AttributeForm idPrefix="entity" fields={fields} values={entityTexts} onChange={(key, value) => setEntityTexts((c) => ({ ...c, [key]: value }))} disabled={!canEdit} />
                {canEdit && (
                  <div>
                    <Button size="sm" variant="secondary" onClick={() => saveEntity.mutate()} disabled={saveEntity.isPending}>
                      {saveEntity.isPending && <Loader2 className="animate-spin" aria-hidden />}
                      {t("suppliers.attributes.entitySave")}
                    </Button>
                  </div>
                )}
              </>
            )}
          </>
        )}
      </section>
    </div>
  );
}
