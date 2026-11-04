import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus, Trash2 } from "lucide-react";
import { Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input, Label } from "@docproc/ui";
import { ApiError, ConfirmDialog, DataTable, createDataTableColumnHelper, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { attributeFields } from "@/lib/attributeSchema";
import { EMPTY_ARRAY, useTenantSettings } from "@/lib/queries";
import { useSupplierGroups, type SupplierGroup } from "@/lib/routingQueries";
import { AttributeForm } from "./AttributeForm";
import { readForm } from "./SupplierAttributesTab";
import { mergeAttributes } from "@/lib/attributeSchema";

const helper = createDataTableColumnHelper<SupplierGroup>();

/**
 * Supplier groups (docs/11 §6.4, §19 item 3): a group's attributes are inherited by its members (a member's own win),
 * and a group can own layouts used by members without their own. Create, rename, edit the attributes (a form from the
 * tenant's attribute schema), delete an empty group.
 */
export function SupplierGroupsPanel({ canEdit }: { canEdit: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const groups = useSupplierGroups();
  const settings = useTenantSettings();
  const fields = useMemo(() => attributeFields(settings.data?.settings?.supplierAttributeSchema), [settings.data]);
  const [editing, setEditing] = useState<SupplierGroup | "new" | null>(null);
  const [deleting, setDeleting] = useState<SupplierGroup | null>(null);
  const [name, setName] = useState("");
  const [texts, setTexts] = useState<Record<string, string | boolean>>({});
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.routing.groups() });

  const open = (group: SupplierGroup | "new") => {
    setEditing(group);
    setName(group === "new" ? "" : group.name);
    const stored = group === "new" ? {} : (group.attributes as Record<string, unknown>);
    setTexts(Object.fromEntries(fields.map((f) => [f.key, f.kind === "boolean" ? stored[f.key] === true : stored[f.key] == null ? "" : String(stored[f.key])])));
  };

  const save = useMutation({
    mutationFn: async () => {
      const read = readForm(fields, texts, t);
      if ("errors" in read) throw new Error(Object.values(read.errors).join(" "));
      const stored = editing && editing !== "new" ? (editing.attributes as Record<string, unknown>) : {};
      const attributes = mergeAttributes(stored, fields, read.values) as never;
      if (editing === "new") return unwrap(await client.POST("/api/v1/supplier-groups", { body: { name: name.trim(), attributes } }));
      return unwrap(await client.PATCH("/api/v1/supplier-groups/{id}", { params: { path: { id: (editing as SupplierGroup).id } }, body: { name: name.trim(), attributes } }));
    },
    onSuccess: (group) => {
      toast.success(t("suppliers.groups.saved", { name: group.name }));
      setEditing(null);
      invalidate();
    },
    onError: (error) => {
      toast.error(error instanceof ApiError ? error.title : t("suppliers.groups.saveFailed"), { description: error instanceof ApiError ? (error.problem?.detail ?? undefined) : error.message });
    },
    meta: { silent: true },
  });
  const remove = useMutation({
    mutationFn: async (group: SupplierGroup) => unwrap(await client.DELETE("/api/v1/supplier-groups/{id}", { params: { path: { id: group.id } } })),
    onSuccess: () => {
      toast.success(t("suppliers.groups.deleted"));
      invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("suppliers.groups.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor((g) => Number(g.supplierCount), { id: "members", header: t("suppliers.groups.columns.members"), cell: (info) => info.getValue() }),
        helper.accessor((g) => Object.entries(g.attributes).map(([k, v]) => `${k} = ${String(v)}`).join(", "), { id: "attributes", header: t("suppliers.groups.columns.attributes"), cell: (info) => info.getValue() || "—" }),
        ...(canEdit
          ? [
              helper.display({
                id: "actions",
                header: "",
                cell: ({ row }) => (
                  <Button size="icon-xs" variant="ghost" aria-label={t("suppliers.groups.delete")} onClick={(event) => { event.stopPropagation(); setDeleting(row.original); }} disabled={Number(row.original.supplierCount) > 0}>
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
    <div className="flex flex-col gap-3" data-testid="supplier-groups">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <p className="max-w-prose text-sm text-muted-foreground">{t("suppliers.groups.description")}</p>
        {canEdit && (
          <Button size="sm" onClick={() => open("new")} data-testid="supplier-group-add">
            <Plus aria-hidden />
            {t("suppliers.groups.add")}
          </Button>
        )}
      </div>
      <DataTable columns={columns} data={groups.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={groups.isPending} query={groups} caption={t("suppliers.groups.caption")} onRowClick={canEdit ? (row) => open(row) : undefined} emptyTitle={t("suppliers.groups.empty")} emptyDescription={t("suppliers.groups.emptyDescription")} />
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>{editing === "new" ? t("suppliers.groups.createTitle") : t("suppliers.groups.editTitle")}</DialogTitle>
            <DialogDescription>{t("suppliers.groups.dialogDescription")}</DialogDescription>
          </DialogHeader>
          <div className="flex flex-col gap-3">
            <div className="flex flex-col gap-1">
              <Label htmlFor="group-name">{t("suppliers.groups.columns.name")}</Label>
              <Input id="group-name" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
            </div>
            {fields.length > 0 && <AttributeForm idPrefix="group" fields={fields} values={texts} onChange={(key, value) => setTexts((c) => ({ ...c, [key]: value }))} />}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditing(null)}>
              {t("common:dialog.cancel")}
            </Button>
            <Button onClick={() => save.mutate()} disabled={save.isPending || !name.trim()}>
              {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("suppliers.save")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <ConfirmDialog open={deleting !== null} onOpenChange={(o) => !o && setDeleting(null)} title={t("suppliers.groups.deleteTitle", { name: deleting?.name ?? "" })} confirmLabel={t("suppliers.groups.delete")} destructive onConfirm={() => (deleting ? remove.mutateAsync(deleting).then(() => setDeleting(null)) : undefined)} />
    </div>
  );
}
