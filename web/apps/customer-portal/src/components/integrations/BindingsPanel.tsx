import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Pencil, Plus, Trash2 } from "lucide-react";
import {
  Button,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useDocumentTypes } from "@docproc/review";
import { EMPTY_ARRAY } from "@/lib/queries";
import { SectionIntro } from "@/components/settings/SectionIntro";
import { bindingEvents, reapprovalModes, useBindings, useDestinations, useOutputMappings, type Binding } from "./api";

const helper = createDataTableColumnHelper<Binding>();
const ANY = "__any";
const FOLLOW = "__follow";

/** The binding form: every setting of `POST /output-bindings` the UI offers. */
export interface BindingFormValues {
  name: string;
  events: string[];
  documentTypeKey: string;
  supplierId: string;
  channelId: string;
  condition: string;
  mappingKey: string;
  pinnedMappingVersion: string;
  destinationId: string;
  exclusiveGroup: string;
  priority: string;
  onReapproval: (typeof reapprovalModes)[number];
  updateMappingKey: string;
  enabled: boolean;
}

export function bindingFormOf(binding: Binding | null): BindingFormValues {
  return {
    name: binding?.name ?? "",
    events: binding?.events ?? ["document.approved"],
    documentTypeKey: binding?.documentTypeKey ?? "",
    supplierId: binding?.supplierId ?? "",
    channelId: binding?.channelId ?? "",
    condition: binding?.condition ?? "",
    mappingKey: binding?.mappingKey ?? "",
    pinnedMappingVersion: binding?.pinnedMappingVersion !== null && binding?.pinnedMappingVersion !== undefined ? String(binding.pinnedMappingVersion) : "",
    destinationId: binding?.destinationId ?? "",
    exclusiveGroup: binding?.exclusiveGroup ?? "",
    priority: binding ? String(binding.priority) : "0",
    onReapproval: (binding?.onReapproval as BindingFormValues["onReapproval"]) ?? "Resend",
    updateMappingKey: binding?.updateMappingKey ?? "",
    enabled: binding?.enabled ?? true,
  };
}

/**
 * The body of `POST /output-bindings` (new) or `PATCH /output-bindings/{id}` (an edit, where an empty text clears a
 * setting and the `clear*` flags clear the supplier, channel and pinned version).
 */
export function bindingBodyOf(values: BindingFormValues, existing: Binding | null) {
  const pinned = values.pinnedMappingVersion.trim() ? Number(values.pinnedMappingVersion) : null;
  const common = {
    name: values.name.trim(),
    events: values.events,
    condition: values.condition.trim(),
    mappingKey: values.mappingKey,
    destinationId: values.destinationId,
    exclusiveGroup: values.exclusiveGroup.trim(),
    priority: Number(values.priority) || 0,
    onReapproval: values.onReapproval,
    updateMappingKey: values.onReapproval === "Update" ? values.updateMappingKey : "",
    enabled: values.enabled,
    documentTypeKey: values.documentTypeKey,
  };
  if (!existing) {
    return {
      ...common,
      condition: common.condition || null,
      exclusiveGroup: common.exclusiveGroup || null,
      updateMappingKey: common.updateMappingKey || null,
      documentTypeKey: common.documentTypeKey || null,
      supplierId: values.supplierId || null,
      channelId: values.channelId || null,
      pinnedMappingVersion: pinned,
      mode: null,
    };
  }
  return {
    ...common,
    supplierId: values.supplierId || null,
    clearSupplier: !values.supplierId && !!existing.supplierId,
    channelId: values.channelId || null,
    clearChannel: !values.channelId && !!existing.channelId,
    pinnedMappingVersion: pinned,
    clearPinnedVersion: pinned === null && existing.pinnedMappingVersion !== null,
  };
}

/**
 * Settings > Integrations > Bindings (docs/11 §13.5, §19): which documents go where. A binding names the events
 * (approved, rejected), the scope (document type, supplier, channel), an optional condition, the mapping (the published
 * version, or a pinned one) and the destination; exclusive groups send one binding per group, and re-approval resends,
 * skips or updates. So price lists go to the article-price import while invoices keep going to the AP import.
 */
export function BindingsPanel() {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const bindings = useBindings();
  const destinations = useDestinations();
  const [editing, setEditing] = useState<Binding | "new" | null>(null);
  const [deleting, setDeleting] = useState<Binding | null>(null);
  const destinationName = useMemo(() => new Map((destinations.data ?? []).map((d) => [d.id, d.name])), [destinations.data]);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: queryKeys.outputs.bindings() });

  const toggle = useMutation({
    mutationFn: async ({ id, enabled }: { id: string; enabled: boolean }) => unwrap(await client.PATCH("/api/v1/output-bindings/{id}", { params: { path: { id } }, body: { enabled } as never })),
    onSuccess: invalidate,
  });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/output-bindings/{id}", { params: { path: { id } } })),
    onSuccess: async () => {
      toast.success(t("integrations.bindings.deleted"));
      setDeleting(null);
      await invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("integrations.bindings.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("documentTypeKey", { header: t("integrations.bindings.columns.type"), cell: (info) => info.getValue() ?? t("integrations.anyType") }),
        helper.accessor("events", { header: t("integrations.bindings.columns.events"), cell: (info) => info.getValue().map((event) => t(`integrations.events.${event}`, { defaultValue: event })).join(", ") }),
        helper.accessor("mappingKey", { header: t("integrations.bindings.columns.mapping"), cell: ({ row }) => <span className="font-mono text-xs">{row.original.mappingKey}{row.original.pinnedMappingVersion !== null ? ` v${Number(row.original.pinnedMappingVersion)}` : ""}</span> }),
        helper.accessor("destinationId", { header: t("integrations.bindings.columns.destination"), cell: (info) => destinationName.get(info.getValue()) ?? info.getValue() }),
        helper.accessor("condition", { header: t("integrations.bindings.columns.condition"), cell: (info) => (info.getValue() ? <span className="font-mono text-xs">{info.getValue()}</span> : "—") }),
        helper.accessor("onReapproval", { header: t("integrations.bindings.columns.onReapproval"), cell: (info) => t(`integrations.bindings.reapproval.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("enabled", {
          header: t("integrations.bindings.columns.enabled"),
          cell: ({ row }) => <Switch checked={row.original.enabled} disabled={toggle.isPending} onCheckedChange={(enabled) => toggle.mutate({ id: row.original.id, enabled })} aria-label={t("integrations.bindings.toggle", { name: row.original.name })} />,
        }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <div className="flex justify-end gap-1">
              <Button size="xs" variant="ghost" onClick={() => setEditing(row.original)}>
                <Pencil aria-hidden />
                {t("integrations.edit")}
              </Button>
              <Button size="xs" variant="ghost" onClick={() => setDeleting(row.original)} aria-label={t("integrations.bindings.deleteLabel", { name: row.original.name })}>
                <Trash2 aria-hidden />
              </Button>
            </div>
          ),
        }),
      ]),
    [t, destinationName, toggle],
  );

  return (
    <div className="flex flex-col gap-4" data-testid="bindings-panel">
      <SectionIntro
        title={t("integrations.bindings.title")}
        description={t("integrations.bindings.description")}
        action={
          <Button onClick={() => setEditing("new")} data-testid="new-binding">
            <Plus aria-hidden />
            {t("integrations.bindings.create")}
          </Button>
        }
      />
      <DataTable columns={columns} data={bindings.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={bindings.isPending} query={bindings} caption={t("integrations.bindings.title")} emptyTitle={t("integrations.bindings.empty")} emptyDescription={t("integrations.bindings.emptyDescription")} rowAttributes={(row) => ({ "data-binding": row.name })} />
      <BindingDialog binding={editing === "new" ? null : editing} open={editing !== null} onClose={() => setEditing(null)} onSaved={invalidate} />
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("integrations.bindings.deleteTitle", { name: deleting?.name ?? "" })}
        description={t("integrations.bindings.deleteDescription")}
        confirmLabel={t("integrations.delete")}
        destructive
        isPending={remove.isPending}
        onConfirm={() => remove.mutateAsync(deleting!.id).then(() => undefined)}
      />
    </div>
  );
}

function BindingDialog({ binding, open, onClose, onSaved }: { binding: Binding | null; open: boolean; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const [values, setValues] = useState<BindingFormValues>(bindingFormOf(binding));
  const [error, setError] = useState<string | null>(null);
  const types = useDocumentTypes(open);
  const mappings = useOutputMappings(open);
  const destinations = useDestinations(open);
  const suppliers = useQuery({ queryKey: queryKeys.suppliers.list({ limit: 200 }), queryFn: async () => unwrap(await client.GET("/api/v1/suppliers", { params: { query: { limit: 200 } } })), enabled: open });
  const channels = useQuery({ queryKey: queryKeys.channels.list(), queryFn: async () => unwrap(await client.GET("/api/v1/channels")), enabled: open });

  useEffect(() => {
    if (open) {
      setValues(bindingFormOf(binding));
      setError(null);
    }
  }, [open, binding]);

  const set = <K extends keyof BindingFormValues>(key: K, value: BindingFormValues[K]) => setValues((v) => ({ ...v, [key]: value }));
  const mapping = (mappings.data ?? []).find((m) => m.key === values.mappingKey);
  const publishedVersions = (mapping?.versions ?? []).filter((v) => v.state !== "Draft");

  const save = useMutation({
    mutationFn: async () => {
      const body = bindingBodyOf(values, binding);
      return binding
        ? unwrap(await client.PATCH("/api/v1/output-bindings/{id}", { params: { path: { id: binding.id } }, body: body as never }))
        : unwrap(await client.POST("/api/v1/output-bindings", { body: body as never }));
    },
    meta: { silent: true },
    onSuccess: async (saved) => {
      toast.success(binding ? t("integrations.bindings.updated", { name: saved.name }) : t("integrations.bindings.created", { name: saved.name }));
      await onSaved();
      onClose();
    },
    onError: (failure) => setError(isApiError(failure) ? Object.entries(failure.fieldErrors).map(([field, messages]) => `${field}: ${messages.join(" ")}`).join(" ") || (failure.problem?.detail ?? failure.title) : String(failure)),
  });

  const valid = values.name.trim() && values.mappingKey && values.destinationId && values.events.length > 0 && (values.onReapproval !== "Update" || values.updateMappingKey);

  return (
    <Dialog open={open} onOpenChange={(next) => !next && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{binding ? t("integrations.bindings.editTitle", { name: binding.name }) : t("integrations.bindings.createTitle")}</DialogTitle>
          <DialogDescription>{t("integrations.bindings.formDescription")}</DialogDescription>
        </DialogHeader>
        <form
          id="binding-form"
          className="grid gap-4"
          data-testid="binding-form"
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) save.mutate();
          }}
        >
          <div className="grid gap-1.5">
            <Label htmlFor="binding-name">{t("integrations.bindings.fields.name")}</Label>
            <Input id="binding-name" value={values.name} onChange={(event) => set("name", event.target.value)} placeholder="article-prices" data-testid="binding-name" />
          </div>
          <fieldset className="grid gap-1.5">
            <legend className="text-sm font-medium">{t("integrations.bindings.fields.events")}</legend>
            <div className="flex flex-wrap gap-4">
              {bindingEvents.map((event) => (
                <Label key={event} className="flex items-center gap-2 font-normal">
                  <Checkbox checked={values.events.includes(event)} onCheckedChange={(on) => set("events", on === true ? [...values.events, event] : values.events.filter((e) => e !== event))} />
                  {t(`integrations.events.${event}`)}
                </Label>
              ))}
            </div>
          </fieldset>
          <div className="grid gap-4 sm:grid-cols-3">
            <SelectField label={t("integrations.bindings.fields.type")} value={values.documentTypeKey} onChange={(v) => set("documentTypeKey", v)} anyLabel={t("integrations.anyType")} options={(types.data ?? []).map((type) => ({ value: type.key, label: type.key }))} testId="binding-type" />
            <SelectField label={t("integrations.bindings.fields.supplier")} value={values.supplierId} onChange={(v) => set("supplierId", v)} anyLabel={t("integrations.anySupplier")} options={(suppliers.data?.items ?? []).map((s) => ({ value: s.id, label: s.name }))} testId="binding-supplier" />
            <SelectField label={t("integrations.bindings.fields.channel")} value={values.channelId} onChange={(v) => set("channelId", v)} anyLabel={t("integrations.anyChannel")} options={(channels.data ?? []).map((c) => ({ value: c.id, label: c.name }))} testId="binding-channel" />
          </div>
          <div className="grid gap-4 sm:grid-cols-[1fr_9rem]">
            <SelectField label={t("integrations.bindings.fields.mapping")} value={values.mappingKey} onChange={(v) => setValues((current) => ({ ...current, mappingKey: v, pinnedMappingVersion: "" }))} options={(mappings.data ?? []).map((m) => ({ value: m.key, label: `${m.name} (${m.key})${m.publishedVersion === null ? ` · ${t("integrations.bindings.unpublished")}` : ""}` }))} testId="binding-mapping" placeholder={t("integrations.bindings.chooseMapping")} />
            <div className="grid gap-1.5">
              <Label>{t("integrations.bindings.fields.version")}</Label>
              <Select value={values.pinnedMappingVersion || FOLLOW} onValueChange={(v) => set("pinnedMappingVersion", v === FOLLOW ? "" : v)}>
                <SelectTrigger data-testid="binding-version">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={FOLLOW}>{t("integrations.bindings.followPublished")}</SelectItem>
                  {publishedVersions.map((v) => (
                    <SelectItem key={String(v.version)} value={String(v.version)}>
                      v{Number(v.version)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
          <SelectField label={t("integrations.bindings.fields.destination")} value={values.destinationId} onChange={(v) => set("destinationId", v)} options={(destinations.data ?? []).map((d) => ({ value: d.id, label: `${d.name} (${t(`integrations.transports.${d.transport}`, { defaultValue: d.transport })})` }))} testId="binding-destination" placeholder={t("integrations.bindings.chooseDestination")} />
          <div className="grid gap-1.5">
            <Label htmlFor="binding-condition">{t("integrations.bindings.fields.condition")}</Label>
            <Input id="binding-condition" value={values.condition} onChange={(event) => set("condition", event.target.value)} placeholder="roles.validUntil >= $today()" className="font-mono text-xs" data-testid="binding-condition" />
            <p className="text-xs text-muted-foreground">{t("integrations.bindings.hints.condition")}</p>
          </div>
          <div className="grid gap-4 sm:grid-cols-3">
            <div className="grid gap-1.5">
              <Label>{t("integrations.bindings.fields.onReapproval")}</Label>
              <Select value={values.onReapproval} onValueChange={(v) => set("onReapproval", v as BindingFormValues["onReapproval"])}>
                <SelectTrigger data-testid="binding-reapproval">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {reapprovalModes.map((mode) => (
                    <SelectItem key={mode} value={mode}>
                      {t(`integrations.bindings.reapproval.${mode}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="binding-group">{t("integrations.bindings.fields.exclusiveGroup")}</Label>
              <Input id="binding-group" value={values.exclusiveGroup} onChange={(event) => set("exclusiveGroup", event.target.value)} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="binding-priority">{t("integrations.bindings.fields.priority")}</Label>
              <Input id="binding-priority" type="number" value={values.priority} onChange={(event) => set("priority", event.target.value)} />
            </div>
          </div>
          {values.onReapproval === "Update" && (
            <SelectField label={t("integrations.bindings.fields.updateMapping")} value={values.updateMappingKey} onChange={(v) => set("updateMappingKey", v)} options={(mappings.data ?? []).map((m) => ({ value: m.key, label: `${m.name} (${m.key})` }))} testId="binding-update-mapping" placeholder={t("integrations.bindings.chooseMapping")} />
          )}
          <Label className="flex items-center gap-2 font-normal">
            <Switch checked={values.enabled} onCheckedChange={(on) => set("enabled", on)} />
            {t("integrations.bindings.fields.enabled")}
          </Label>
          {error && (
            <p className="text-sm text-destructive" role="alert" data-testid="binding-problem">
              {error}
            </p>
          )}
        </form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t("common:dialog.cancel")}
          </Button>
          <Button type="submit" form="binding-form" disabled={!valid || save.isPending} data-testid="binding-save">
            {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {binding ? t("integrations.save") : t("integrations.bindings.create")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SelectField({ label, value, onChange, options, anyLabel, placeholder, testId }: { label: string; value: string; onChange: (value: string) => void; options: Array<{ value: string; label: string }>; anyLabel?: string; placeholder?: string; testId: string }) {
  return (
    <div className="grid gap-1.5">
      <Label>{label}</Label>
      <Select value={value || (anyLabel ? ANY : undefined)} onValueChange={(v) => onChange(v === ANY ? "" : v)}>
        <SelectTrigger data-testid={testId}>
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {anyLabel && <SelectItem value={ANY}>{anyLabel}</SelectItem>}
          {options.map((option) => (
            <SelectItem key={option.value} value={option.value}>
              {option.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}
