import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Copy, GitFork, Pencil, Plus, Download } from "lucide-react";
import {
  Alert,
  AlertDescription,
  AlertTitle,
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
  Skeleton,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@docproc/ui";
import { isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { REVIEW_NS, localized } from "../lib/labels";
import { useDocumentTemplates, useDocumentTypes, type DocumentTemplateSummary, type DocumentTypeDetail } from "./api";
import { DocumentTypeEditor } from "./DocumentTypeEditor";
import { TYPE_KEY, newTypeDefinition } from "./model";

export interface DocumentTypesPanelProps {
  /** ConfigAdmin (TenantAdmin, SuperAdmin): adopt, copy, create, edit and publish. Everyone else browses. */
  canEdit: boolean;
  /** The type open in the editor; controlled by the host (a URL search parameter), or kept here when left out. */
  selectedKey?: string | null;
  onSelectedKeyChange?: (key: string | null) => void;
}

/**
 * The tenant's feature switches (`GET /settings`); they decide which catalogue writes the API takes. `enabled` false
 * leaves the request out (a platform account or a signed-out shell has no tenant settings).
 */
export function useTenantFeatures(enabled = true) {
  const { client } = useApi();
  return useQuery({ queryKey: queryKeys.settings.get(), queryFn: async () => unwrap(await client.GET("/api/v1/settings")), select: (data) => data.settings.features ?? null, enabled });
}

/**
 * Settings > Document types (docs/11 §19): the platform templates with Adopt and Copy, the tenant's types with Fork
 * (an adopted type becomes the tenant's copy in place, keeping its key) and Edit, and New type for a type of the tenant's
 * own; the type editor opens in place. Writes need the tenant's `configurableTypes` switch, a type of its own also
 * `customTypes` (the admin console switches them).
 */
export function DocumentTypesPanel({ canEdit, selectedKey, onSelectedKeyChange }: DocumentTypesPanelProps) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [ownSelection, setOwnSelection] = useState<string | null>(null);
  const selected = selectedKey !== undefined ? selectedKey : ownSelection;
  const select = (key: string | null) => (onSelectedKeyChange ? onSelectedKeyChange(key) : setOwnSelection(key));

  const features = useTenantFeatures();
  const types = useDocumentTypes();
  const templates = useDocumentTemplates();
  const [copyOf, setCopyOf] = useState<DocumentTemplateSummary | null>(null);
  const [creating, setCreating] = useState(false);

  const configurable = features.data?.configurableTypes === true;
  const custom = features.data?.customTypes === true;
  const writable = canEdit && configurable;

  const create = useMutation({
    mutationFn: async (body: { from?: { template: string; mode: "adopt" | "copy" }; key?: string; definition?: unknown }) =>
      unwrap(await client.POST("/api/v1/document-types", { body: { from: body.from ?? null, key: body.key ?? null, definition: (body.definition ?? null) as never } })),
    meta: { silent: true },
    onSuccess: async (type, body) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.documentTypes.all });
      toast.success(body.from?.mode === "adopt" ? t("types.adopted", { key: type.key }) : t("types.created", { key: type.key }));
      setCopyOf(null);
      setCreating(false);
      if (body.from?.mode !== "adopt") select(type.key);
    },
    onError: (error) => {
      toast.error(isApiError(error) ? error.title : t("types.failed"), { description: isApiError(error) ? (Object.values(error.fieldErrors)[0]?.join(" ") ?? error.problem?.detail ?? undefined) : undefined });
    },
  });

  if (selected) {
    return <DocumentTypeEditor typeKey={selected} canEdit={writable} customTypes={custom} onBack={() => select(null)} />;
  }

  const name = (names: Record<string, string> | null | undefined, fallback: string) => localized(names, i18n.language) ?? fallback;

  return (
    <div className="grid gap-6" data-testid="document-types">
      {features.data && !configurable && (
        <Alert>
          <AlertTitle>{t("types.offTitle")}</AlertTitle>
          <AlertDescription>{t("types.off")}</AlertDescription>
        </Alert>
      )}

      <Card>
        <CardHeader className="flex flex-row items-start justify-between gap-4">
          <div className="grid gap-1.5">
            <CardTitle>{t("types.yoursTitle")}</CardTitle>
            <CardDescription>{t("types.yoursDescription")}</CardDescription>
          </div>
          {writable && custom && (
            <Button type="button" onClick={() => setCreating(true)} data-testid="new-type">
              <Plus aria-hidden />
              {t("types.newType")}
            </Button>
          )}
        </CardHeader>
        <CardContent>
          {types.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("types.columns.name")}</TableHead>
                  <TableHead>{t("types.columns.key")}</TableHead>
                  <TableHead>{t("types.columns.origin")}</TableHead>
                  <TableHead>{t("types.columns.published")}</TableHead>
                  <TableHead>{t("types.columns.draft")}</TableHead>
                  <TableHead className="text-right">{t("types.columns.actions")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(types.data ?? []).map((type: DocumentTypeDetail) => (
                  <TableRow key={type.key} data-type={type.key}>
                    <TableCell className="font-medium">{name(type.name, type.key)}</TableCell>
                    <TableCell className="font-mono text-xs">{type.key}</TableCell>
                    <TableCell>
                      <Badge variant={type.origin === "Adopted" ? "outline" : "secondary"}>{t(`types.origin.${type.origin}`, { defaultValue: type.origin })}</Badge>
                      {type.baseTemplate && <span className="ml-1 text-xs text-muted-foreground">{type.baseTemplate}</span>}
                    </TableCell>
                    <TableCell className="tabular-nums">{type.publishedVersion ? `v${type.publishedVersion}` : "—"}</TableCell>
                    <TableCell className="tabular-nums">{type.draftVersion ? `v${type.draftVersion}` : "—"}</TableCell>
                    <TableCell className="text-right">
                      <span className="inline-flex gap-1">
                        {writable && type.origin === "Adopted" && type.baseTemplate && (
                          <Button type="button" variant="outline" size="sm" disabled={create.isPending} onClick={() => create.mutate({ from: { template: type.baseTemplate!, mode: "copy" }, key: type.key })} title={t("types.forkHint")}>
                            <GitFork aria-hidden />
                            {t("types.fork")}
                          </Button>
                        )}
                        <Button type="button" variant="ghost" size="sm" onClick={() => select(type.key)} aria-label={t("types.open", { name: name(type.name, type.key) })}>
                          <Pencil aria-hidden />
                          {writable && type.origin !== "Adopted" ? t("types.edit") : t("types.view")}
                        </Button>
                      </span>
                    </TableCell>
                  </TableRow>
                ))}
                {types.data?.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={6} className="text-muted-foreground">
                      {t("types.noTypes")}
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("types.templatesTitle")}</CardTitle>
          <CardDescription>{t("types.templatesDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          {templates.isLoading ? (
            <Skeleton className="h-24 w-full" />
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>{t("types.columns.name")}</TableHead>
                  <TableHead>{t("types.columns.template")}</TableHead>
                  <TableHead>{t("types.columns.adoptedAs")}</TableHead>
                  <TableHead className="text-right">{t("types.columns.actions")}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(templates.data ?? []).map((template) => (
                  <TableRow key={template.key} data-template={template.key}>
                    <TableCell className="whitespace-normal">
                      <div className="font-medium">{name(template.name, template.key)}</div>
                      {template.description && <div className="max-w-md text-xs text-muted-foreground">{template.description}</div>}
                    </TableCell>
                    <TableCell className="font-mono text-xs">
                      {template.key} v{template.version}
                    </TableCell>
                    <TableCell className="text-sm">{template.adoptedAs.length > 0 ? template.adoptedAs.join(", ") : "—"}</TableCell>
                    <TableCell className="text-right">
                      {writable && (
                        <span className="inline-flex gap-1">
                          {!template.adoptedAs.includes(template.defaultKey) && (
                            <Button type="button" variant="outline" size="sm" disabled={create.isPending} onClick={() => create.mutate({ from: { template: template.key, mode: "adopt" } })} aria-label={t("types.adoptNamed", { name: name(template.name, template.key) })}>
                              <Download aria-hidden />
                              {t("types.adopt")}
                            </Button>
                          )}
                          <Button type="button" variant="ghost" size="sm" onClick={() => setCopyOf(template)} aria-label={t("types.copyNamed", { name: name(template.name, template.key) })}>
                            <Copy aria-hidden />
                            {t("types.copy")}
                          </Button>
                        </span>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      <KeyDialog
        open={copyOf !== null}
        title={t("types.copyTitle", { name: copyOf ? name(copyOf.name, copyOf.key) : "" })}
        description={t("types.copyDescription")}
        initialKey={copyOf ? `${copyOf.defaultKey}-copy` : ""}
        withName={false}
        pending={create.isPending}
        onOpenChange={(open) => !open && setCopyOf(null)}
        onSubmit={({ key }) => copyOf && create.mutate({ from: { template: copyOf.key, mode: "copy" }, key })}
      />
      <KeyDialog
        open={creating}
        title={t("types.newTitle")}
        description={t("types.newDescription")}
        initialKey=""
        withName
        pending={create.isPending}
        onOpenChange={setCreating}
        onSubmit={({ key, nameEn, nameDe }) => create.mutate({ definition: newTypeDefinition(key, { en: nameEn, ...(nameDe ? { de: nameDe } : {}) }) })}
      />
    </div>
  );
}

interface KeyDialogProps {
  open: boolean;
  title: string;
  description: string;
  initialKey: string;
  withName: boolean;
  pending: boolean;
  onOpenChange: (open: boolean) => void;
  onSubmit: (values: { key: string; nameEn: string; nameDe: string }) => void;
}

function KeyDialog({ open, title, description, initialKey, withName, pending, onOpenChange, onSubmit }: KeyDialogProps) {
  const { t } = useTranslation(REVIEW_NS);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>{open && <KeyForm title={title} description={description} initialKey={initialKey} withName={withName} pending={pending} onCancel={() => onOpenChange(false)} onSubmit={onSubmit} t={t} />}</DialogContent>
    </Dialog>
  );
}

function KeyForm({ title, description, initialKey, withName, pending, onCancel, onSubmit, t }: Omit<KeyDialogProps, "open" | "onOpenChange"> & { onCancel: () => void; t: (key: string) => string }) {
  const [key, setKey] = useState(initialKey);
  const [nameEn, setNameEn] = useState("");
  const [nameDe, setNameDe] = useState("");
  const keyInvalid = key !== "" && !TYPE_KEY.test(key);
  const ready = TYPE_KEY.test(key) && (!withName || nameEn.trim() !== "");
  return (
    <form
      className="grid gap-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (ready) onSubmit({ key, nameEn: nameEn.trim(), nameDe: nameDe.trim() });
      }}
    >
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>{description}</DialogDescription>
      </DialogHeader>
      <div className="grid gap-1.5">
        <Label htmlFor="type-key">{t("types.key")}</Label>
        <Input id="type-key" value={key} onChange={(event) => setKey(event.target.value)} aria-invalid={keyInvalid || undefined} aria-describedby="type-key-hint" />
        <p id="type-key-hint" className={keyInvalid ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>
          {t("types.keyHint")}
        </p>
      </div>
      {withName && (
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="grid gap-1.5">
            <Label htmlFor="type-name-en">{t("types.nameEn")}</Label>
            <Input id="type-name-en" value={nameEn} onChange={(event) => setNameEn(event.target.value)} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="type-name-de">{t("types.nameDe")}</Label>
            <Input id="type-name-de" value={nameDe} onChange={(event) => setNameDe(event.target.value)} />
          </div>
        </div>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onCancel}>
          {t("types.cancel")}
        </Button>
        <Button type="submit" disabled={!ready || pending} aria-busy={pending}>
          {t("types.create")}
        </Button>
      </DialogFooter>
    </form>
  );
}
