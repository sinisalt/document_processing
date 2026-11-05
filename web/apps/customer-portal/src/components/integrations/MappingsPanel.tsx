import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, Loader2, Pencil, Plus } from "lucide-react";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
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
} from "@docproc/ui";
import { DataTable, createDataTableColumnHelper, isApiError, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { useDocumentTypes } from "@docproc/review";
import { EMPTY_ARRAY } from "@/lib/queries";
import { useOutputMappings, useOutputTemplates, type OutputMapping, type OutputTemplate } from "./api";
import { compileFieldMap } from "./fieldMap";
import { MappingEditor } from "./MappingEditor";

const helper = createDataTableColumnHelper<OutputMapping>();
const templateHelper = createDataTableColumnHelper<OutputTemplate>();
const ANY_TYPE = "__any";
const KEY_PATTERN = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/;

/** What the create dialog makes: a copy of a template, or a mapping of the tenant's own (starting as an empty visual map). */
type CreateRequest = { template: OutputTemplate } | { template: null };

/**
 * Settings > Integrations > Output mappings (docs/11 §13.9, §19): the tenant's mappings (open one in the editor), the
 * platform integration templates (`std.event`, `generic-ap-json`) to copy, and a new mapping of the tenant's own, which
 * starts as an empty visual field map.
 */
export function MappingsPanel({ canEdit, selectedKey, onSelectedKeyChange }: { canEdit: boolean; selectedKey: string | null; onSelectedKeyChange: (key: string | null) => void }) {
  const { t, i18n } = useTranslation();
  const mappings = useOutputMappings();
  const templates = useOutputTemplates();
  const [creating, setCreating] = useState<CreateRequest | null>(null);
  const own = useMemo(() => (mappings.data ?? EMPTY_ARRAY).filter((mapping) => mapping.origin !== "Platform"), [mappings.data]);

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("integrations.mappings.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("key", { header: t("integrations.mappings.columns.key"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor("origin", {
          header: t("integrations.mappings.columns.origin"),
          cell: ({ row }) => (
            <span>
              {t(`integrations.mappings.origins.${row.original.origin}`, { defaultValue: row.original.origin })}
              {row.original.baseTemplate ? <span className="text-muted-foreground"> · {row.original.baseTemplate}</span> : null}
            </span>
          ),
        }),
        helper.accessor("sourceTypeKey", { header: t("integrations.mappings.columns.sourceType"), cell: (info) => info.getValue() ?? t("integrations.anyType") }),
        helper.accessor("publishedVersion", { header: t("integrations.mappings.columns.published"), cell: (info) => (info.getValue() !== null ? <Badge variant="success">v{Number(info.getValue())}</Badge> : "—") }),
        helper.accessor("draftVersion", { header: t("integrations.mappings.columns.draft"), cell: (info) => (info.getValue() !== null ? <Badge variant="warning">v{Number(info.getValue())}</Badge> : "—") }),
        helper.accessor("updatedAt", { header: t("integrations.mappings.columns.updated"), cell: (info) => relativeTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <div className="flex justify-end">
              <Button size="xs" variant="ghost" onClick={() => onSelectedKeyChange(row.original.key)} data-testid={`open-mapping-${row.original.key}`}>
                <Pencil aria-hidden />
                {t("integrations.open")}
              </Button>
            </div>
          ),
        }),
      ]),
    [t, i18n.language, onSelectedKeyChange],
  );

  const templateColumns = useMemo(
    () =>
      templateHelper.columns([
        templateHelper.accessor("name", { header: t("integrations.mappings.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        templateHelper.accessor("key", { header: t("integrations.mappings.columns.key"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        templateHelper.accessor("description", { header: t("integrations.mappings.columns.description"), cell: (info) => <span className="text-xs text-muted-foreground">{info.getValue() ?? ""}</span> }),
        templateHelper.accessor("copiedAs", { header: t("integrations.mappings.columns.copiedAs"), cell: (info) => (info.getValue().length ? info.getValue().join(", ") : "—") }),
        templateHelper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <div className="flex justify-end gap-1">
              <Button size="xs" variant="ghost" onClick={() => onSelectedKeyChange(row.original.key)}>
                {t("integrations.view")}
              </Button>
              {canEdit && (
                <Button size="xs" variant="outline" onClick={() => setCreating({ template: row.original })} data-testid={`copy-template-${row.original.key}`}>
                  <Copy aria-hidden />
                  {t("integrations.mappings.copy")}
                </Button>
              )}
            </div>
          ),
        }),
      ]),
    [t, canEdit, onSelectedKeyChange],
  );

  if (selectedKey) return <MappingEditor mappingKey={selectedKey} canEdit={canEdit} onBack={() => onSelectedKeyChange(null)} />;

  return (
    <div className="grid gap-6" data-testid="mappings-panel">
      <Card>
        <CardHeader className="flex flex-row items-start justify-between gap-4">
          <div className="grid gap-1.5">
            <CardTitle>{t("integrations.mappings.title")}</CardTitle>
            <CardDescription>{t("integrations.mappings.description")}</CardDescription>
          </div>
          {canEdit && (
            <Button onClick={() => setCreating({ template: null })} data-testid="new-mapping">
              <Plus aria-hidden />
              {t("integrations.mappings.create")}
            </Button>
          )}
        </CardHeader>
        <CardContent>
          <DataTable columns={columns} data={own} getRowId={(row) => row.id} isLoading={mappings.isPending} query={mappings} caption={t("integrations.mappings.title")} emptyTitle={t("integrations.mappings.empty")} emptyDescription={t("integrations.mappings.emptyDescription")} rowAttributes={(row) => ({ "data-mapping": row.key })} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>{t("integrations.templates.title")}</CardTitle>
          <CardDescription>{t("integrations.templates.description")}</CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable columns={templateColumns} data={templates.data ?? EMPTY_ARRAY} getRowId={(row) => row.key} isLoading={templates.isPending} query={templates} caption={t("integrations.templates.title")} emptyTitle={t("integrations.templates.empty")} rowAttributes={(row) => ({ "data-template": row.key })} />
        </CardContent>
      </Card>
      <CreateMappingDialog request={creating} onClose={() => setCreating(null)} onCreated={(key) => onSelectedKeyChange(key)} />
    </div>
  );
}

function CreateMappingDialog({ request, onClose, onCreated }: { request: CreateRequest | null; onClose: () => void; onCreated: (key: string) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const types = useDocumentTypes(request !== null);
  const [key, setKey] = useState("");
  const [name, setName] = useState("");
  const [sourceType, setSourceType] = useState<string>(ANY_TYPE);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (request) {
      setKey(request.template ? `${request.template.key.replace(/^std\./, "")}` : "");
      setName(request.template ? request.template.name : "");
      setSourceType(request.template?.sourceTypeKey ?? ANY_TYPE);
      setError(null);
    }
  }, [request]);

  const create = useMutation({
    mutationFn: async () => {
      const body = request?.template
        ? { from: { template: request.template.key }, key: key.trim(), name: name.trim() || null, description: null, sourceTypeKey: null, engine: null, expression: null, contentType: null, targetSchema: null }
        : { from: null, key: key.trim(), name: name.trim(), description: null, sourceTypeKey: sourceType === ANY_TYPE ? null : sourceType, engine: "jsonata", expression: compileFieldMap({ version: 1, rows: [] }), contentType: null, targetSchema: null };
      const created = unwrap(await client.POST("/api/v1/output-mappings", { body: body as never }));
      if (request?.template && sourceType !== (request.template.sourceTypeKey ?? ANY_TYPE)) {
        unwrap(await client.PATCH("/api/v1/output-mappings/{key}", { params: { path: { key: created.key } }, body: { sourceTypeKey: sourceType === ANY_TYPE ? "" : sourceType, name: null, description: null, status: null } }));
      }
      return created;
    },
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("integrations.mappings.created", { key: created.key }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.outputs.all });
      onClose();
      onCreated(created.key);
    },
    onError: (failure) => setError(isApiError(failure) ? (Object.values(failure.fieldErrors)[0]?.join(" ") ?? failure.problem?.detail ?? failure.title) : String(failure)),
  });

  const keyValid = KEY_PATTERN.test(key.trim()) && !key.trim().startsWith("std.");
  return (
    <Dialog open={request !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{request?.template ? t("integrations.mappings.copyTitle", { name: request.template.name }) : t("integrations.mappings.createTitle")}</DialogTitle>
          <DialogDescription>{request?.template ? t("integrations.mappings.copyDescription") : t("integrations.mappings.createDescription")}</DialogDescription>
        </DialogHeader>
        <form
          id="create-mapping-form"
          className="grid gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            if (keyValid && (request?.template || name.trim())) create.mutate();
          }}
        >
          <div className="grid gap-1.5">
            <Label htmlFor="mapping-key">{t("integrations.mappings.fields.key")}</Label>
            <Input id="mapping-key" value={key} onChange={(event) => setKey(event.target.value)} className="font-mono" placeholder="article-price-import" autoFocus data-testid="mapping-key" />
            <p className={key && !keyValid ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>{t("integrations.mappings.fields.keyHint")}</p>
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="mapping-name">{t("integrations.mappings.fields.name")}</Label>
            <Input id="mapping-name" value={name} onChange={(event) => setName(event.target.value)} data-testid="mapping-name" />
          </div>
          <div className="grid gap-1.5">
            <Label>{t("integrations.mappings.fields.sourceType")}</Label>
            <Select value={sourceType} onValueChange={setSourceType}>
              <SelectTrigger data-testid="mapping-create-type">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY_TYPE}>{t("integrations.anyType")}</SelectItem>
                {(types.data ?? []).map((type) => (
                  <SelectItem key={type.key} value={type.key}>
                    {type.key}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <p className="text-xs text-muted-foreground">{t("integrations.mappings.fields.sourceTypeHint")}</p>
          </div>
          {error && (
            <p className="text-sm text-destructive" role="alert">
              {error}
            </p>
          )}
        </form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            {t("common:dialog.cancel")}
          </Button>
          <Button type="submit" form="create-mapping-form" disabled={create.isPending || !keyValid || (!request?.template && !name.trim())} data-testid="mapping-create-submit">
            {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {request?.template ? t("integrations.mappings.copy") : t("integrations.mappings.create")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
