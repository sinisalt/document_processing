import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus, Trash2 } from "lucide-react";
import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input, Label, Textarea } from "@docproc/ui";
import { ApiError, ConfirmDialog, DataTable, createDataTableColumnHelper, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { AttributeForm } from "@/components/suppliers/AttributeForm";
import { readForm } from "@/components/suppliers/SupplierAttributesTab";
import { attributeFields, mergeAttributes } from "@/lib/attributeSchema";
import { EMPTY_ARRAY, useTenantSettings } from "@/lib/queries";
import { splitLines, useReceivingEntities, type ReceivingEntity } from "@/lib/routingQueries";
import { SectionIntro } from "./SectionIntro";

const helper = createDataTableColumnHelper<ReceivingEntity>();

/**
 * Settings > Receiving entities (docs/11 §6.4): the tenant's own companies or branches that documents are addressed to,
 * by code, with their VAT ids and attributes (a form from the attribute schema). The values view's `entity` is the
 * document's receiving entity; source rules and channels can name one.
 */
export function EntitiesTab({ canEdit }: { canEdit: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const entities = useReceivingEntities();
  const settings = useTenantSettings();
  const fields = useMemo(() => attributeFields(settings.data?.settings?.supplierAttributeSchema), [settings.data]);
  const [editing, setEditing] = useState<ReceivingEntity | "new" | null>(null);
  const [deleting, setDeleting] = useState<ReceivingEntity | null>(null);
  const [draft, setDraft] = useState({ code: "", name: "", vatIds: "" });
  const [texts, setTexts] = useState<Record<string, string | boolean>>({});
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.routing.entities() });

  const open = (entity: ReceivingEntity | "new") => {
    setEditing(entity);
    setDraft(entity === "new" ? { code: "", name: "", vatIds: "" } : { code: entity.code, name: entity.name, vatIds: entity.vatIds.join("\n") });
    const stored = entity === "new" ? {} : (entity.attributes as Record<string, unknown>);
    setTexts(Object.fromEntries(fields.map((f) => [f.key, f.kind === "boolean" ? stored[f.key] === true : stored[f.key] == null ? "" : String(stored[f.key])])));
  };

  const save = useMutation({
    mutationFn: async () => {
      const read = readForm(fields, texts, t);
      if ("errors" in read) throw new Error(Object.values(read.errors).join(" "));
      const stored = editing && editing !== "new" ? (editing.attributes as Record<string, unknown>) : {};
      const body = { code: draft.code.trim(), name: draft.name.trim(), vatIds: splitLines(draft.vatIds), attributes: mergeAttributes(stored, fields, read.values) as never };
      if (editing === "new") return unwrap(await client.POST("/api/v1/receiving-entities", { body }));
      return unwrap(await client.PATCH("/api/v1/receiving-entities/{id}", { params: { path: { id: (editing as ReceivingEntity).id } }, body }));
    },
    onSuccess: (entity) => {
      toast.success(t("settings.entities.saved", { code: entity.code }));
      setEditing(null);
      invalidate();
    },
    onError: (error) => toast.error(error instanceof ApiError ? error.title : t("settings.entities.saveFailed"), { description: error instanceof ApiError ? (error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? undefined)) : error.message }),
    meta: { silent: true },
  });
  const remove = useMutation({
    mutationFn: async (entity: ReceivingEntity) => unwrap(await client.DELETE("/api/v1/receiving-entities/{id}", { params: { path: { id: entity.id } } })),
    onSuccess: () => {
      toast.success(t("settings.entities.deleted"));
      invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("code", { header: t("settings.entities.columns.code"), cell: (info) => <span className="font-mono">{info.getValue()}</span> }),
        helper.accessor("name", { header: t("settings.entities.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor((e) => e.vatIds.join(", "), { id: "vatIds", header: t("settings.entities.columns.vatIds"), cell: (info) => info.getValue() || "—" }),
        helper.accessor((e) => Object.entries(e.attributes).map(([k, v]) => `${k} = ${String(v)}`).join(", "), { id: "attributes", header: t("settings.entities.columns.attributes"), cell: (info) => info.getValue() || "—" }),
        ...(canEdit
          ? [
              helper.display({
                id: "actions",
                header: "",
                cell: ({ row }) => (
                  <Button size="icon-xs" variant="ghost" aria-label={t("settings.entities.delete")} onClick={(event) => { event.stopPropagation(); setDeleting(row.original); }}>
                    <Trash2 aria-hidden />
                  </Button>
                ),
              }),
            ]
          : []),
      ]),
    [t, canEdit],
  );

  return (
    <div className="flex flex-col gap-4" data-testid="entities-tab">
      <SectionIntro
        title={t("settings.entities.title")}
        description={t("settings.entities.description")}
        action={
          canEdit && (
            <Button onClick={() => open("new")} data-testid="entity-add">
              <Plus aria-hidden />
              {t("settings.entities.add")}
            </Button>
          )
        }
      />
      <DataTable columns={columns} data={entities.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={entities.isPending} query={entities} caption={t("settings.entities.caption")} onRowClick={canEdit ? (row) => open(row) : undefined} emptyTitle={t("settings.entities.empty")} emptyDescription={t("settings.entities.emptyDescription")} />
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>{editing === "new" ? t("settings.entities.createTitle") : t("settings.entities.editTitle")}</DialogTitle>
            <DialogDescription>{t("settings.entities.dialogDescription")}</DialogDescription>
          </DialogHeader>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1">
              <Label htmlFor="entity-code">{t("settings.entities.columns.code")}</Label>
              <Input id="entity-code" value={draft.code} onChange={(e) => setDraft((d) => ({ ...d, code: e.target.value }))} autoFocus />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="entity-name">{t("settings.entities.columns.name")}</Label>
              <Input id="entity-name" value={draft.name} onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))} />
            </div>
            <div className="flex flex-col gap-1 sm:col-span-2">
              <Label htmlFor="entity-vat">{t("settings.entities.columns.vatIds")}</Label>
              <Textarea id="entity-vat" value={draft.vatIds} onChange={(e) => setDraft((d) => ({ ...d, vatIds: e.target.value }))} rows={2} />
            </div>
          </div>
          {fields.length > 0 && <AttributeForm idPrefix="entity-edit" fields={fields} values={texts} onChange={(key, value) => setTexts((c) => ({ ...c, [key]: value }))} />}
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditing(null)}>
              {t("common:dialog.cancel")}
            </Button>
            <Button onClick={() => save.mutate()} disabled={save.isPending || !draft.code.trim() || !draft.name.trim()} data-testid="entity-save">
              {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("settings.save")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <ConfirmDialog open={deleting !== null} onOpenChange={(o) => !o && setDeleting(null)} title={t("settings.entities.deleteTitle", { code: deleting?.code ?? "" })} description={t("settings.entities.deleteDescription")} confirmLabel={t("settings.entities.delete")} destructive onConfirm={() => (deleting ? remove.mutateAsync(deleting).then(() => setDeleting(null)) : undefined)} />
    </div>
  );
}
